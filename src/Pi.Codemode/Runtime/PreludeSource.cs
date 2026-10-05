namespace Pi.Codemode.Runtime;

/// <summary>
/// 在 QuickJS VM 里、脚本之前求值的 JavaScript 前奏。对应 TS <c>runtime/prelude-source.ts</c>。<para/>
/// VM 是独立的 wasm 实例，这里不守卫 realm 边界：前奏把 host 桥保持在闭包里使脚本无法直接调用，
/// 并在其上构建 <c>tools</c>、<c>ALL_TOOLS</c>、输出助手（<c>text</c>、<c>image</c>、<c>exit</c>、<c>console</c>）
/// 与全局函数。工具参数与结果以 JSON 字符串跨界，并在这一侧解析。<para/>
/// 输出助手：<c>text(value)</c> 追加文本项（非字符串被 JSON 序列化）、<c>image(urlOrItem)</c> 从 base64
/// <c>data:</c> URL、<c>{ image_url }</c> 对象或 MCP <c>ImageContent</c> 块追加图片、<c>exit()</c> 成功结束脚本；
/// <c>console.*</c> 像 <c>text()</c> 一样追加文本项。<para/>
/// <c>store(key, value)</c> 与 <c>load(key)</c> 是同步的：作用于随 <c>storeJson</c> 传入的 JSON 文本快照，
/// 脚本写入的键在一次成功的 "done" 里上报。<para/>
/// 求值结果为函数 <c>(bridge, toolsJson, globalsJson, storeJson) =&gt; { settle, run, stalled }</c>：
/// <c>toolsJson</c> 列出 <c>{ name, jsName, description }</c>——<c>tools[jsName]</c> 与 <c>tools[name]</c>
/// 调用工具，<c>ALL_TOOLS</c> 列出 <c>{ name: jsName, description }</c>；<c>stalled()</c> 上报「未结束且无
/// host 调用挂起」的脚本（VM 里没有定时器与 I/O，这样的脚本永远无法被恢复）；<c>globalsJson</c> 列出
/// <c>{ name, spread }</c>，<c>a.b</c> 形式的名字归入一个冻结的 <c>a</c> 对象。<c>bridge(kind, a, b, c)</c>
/// 的 kind 为 "call"/"global"（id, name, argsJson）、"output"（"text", text）或（"image", data, mimeType）、
/// "done"（ok, valueJsonOrErrorJson, writesJson）。
/// </summary>
public static class CodemodePrelude
{
    /// <summary>单个 store 值的 JSON 字符上限。对应 TS <c>MAX_STORE_VALUE_CHARS</c>。</summary>
    public const int MaxStoreValueChars = 256 * 1024;

    /// <summary>store 全部键值合计的 JSON 字符上限。对应 TS <c>MAX_STORE_TOTAL_CHARS</c>。</summary>
    public const int MaxStoreTotalChars = 1024 * 1024;

    /// <summary>
    /// 单个脚本可用 <c>text()</c>、<c>image()</c> 与 <c>console.*</c> 产出的上限：文本字符数与 base64
    /// 图片数据字符数，以及项数。host 会保留全部输出直到脚本结束，因此没有上限的话，循环打印的脚本
    /// 会把 host 内存拖到崩溃；项数上限覆盖打印空串的循环。对应 TS <c>MAX_OUTPUT_CHARS</c>。
    /// </summary>
    public const int MaxOutputChars = 16 * 1024 * 1024;

    /// <summary>输出项数上限。对应 TS <c>MAX_OUTPUT_ITEMS</c>。</summary>
    public const int MaxOutputItems = 100_000;

    /// <summary>
    /// 前奏源码，对应 TS <c>PRELUDE_SOURCE</c>（模板常量已代入）：求值得到
    /// <c>(bridge, toolsJson, globalsJson, storeJson) =&gt; { settle, run, stalled }</c>，
    /// 由注入的 <see cref="ICodemodeJsEngine"/> 在 VM 里求值（对应 TS 的 worker.ts）。<para/>
    /// 文本中的 262144 / 1048576 / 16777216 / 100000 即上述四个常量——TS 模板在模块加载时代入，
    /// C# 原始字符串不插值故写字面量，改动时需同步。闭合分隔符须独占一行而带入的结尾换行由
    /// <c>TrimEnd</c> 去掉，取值与 TS 常量逐字节一致。
    /// </summary>
    public static readonly string Source = """
(function (bridge, toolsJson, globalsJson, storeJson) {
	"use strict";
	const stringify = JSON.stringify;
	const parse = JSON.parse;
	const promiseThen = Promise.prototype.then;
	const ErrorCtor = Error;
	const TypeErrorCtor = TypeError;
	const RangeErrorCtor = RangeError;
	const pending = new Map();
	let nextId = 1;
	let finished = false;
	// Thrown by exit() to unwind the script after it already reported success.
	const EXIT = Object.freeze({});

	function done(ok, payload, writes) {
		if (finished) return;
		finished = true;
		bridge("done", ok, payload, writes);
	}

	function serialize(value) {
		return value === undefined ? undefined : stringify(value);
	}

	// QuickJS stacks list frames only. Prefix "Name: message" like V8 so the
	// text reads the same as a Node error, and drop this prelude's frames.
	function errorText(error) {
		const head = error.message ? error.name + ": " + error.message : String(error.name);
		const frames =
			typeof error.stack === "string"
				? error.stack.split("\n").filter((line) => line.trim() && !line.includes("codemode-prelude.js"))
				: [];
		return [head, ...frames].join("\n");
	}

	function format(value) {
		if (typeof value === "string") return value;
		if (value instanceof ErrorCtor) return errorText(value);
		try {
			const json = stringify(value);
			return json === undefined ? String(value) : json;
		} catch {
			return String(value);
		}
	}

	function describeError(error) {
		if (error instanceof ErrorCtor) {
			return stringify({ name: error.name, message: error.message, stack: errorText(error) });
		}
		return stringify({ message: format(error) });
	}

	function caller(kind, name, spread) {
		return (...args) =>
			new Promise((resolve, reject) => {
				let json;
				try {
					json = serialize(spread ? args : args[0]);
				} catch (error) {
					reject(error);
					return;
				}
				const id = nextId++;
				pending.set(id, { resolve, reject });
				bridge(kind, id, name, json);
			});
	}

	const tools = Object.create(null);
	const allTools = [];
	for (const { name, jsName, description } of parse(toolsJson)) {
		const fn = caller("call", name);
		// The first tool wins when two names normalize to the same identifier.
		if (!(jsName in tools)) {
			tools[jsName] = fn;
			allTools.push(Object.freeze({ name: jsName, description }));
		}
		if (!(name in tools)) tools[name] = fn;
	}
	Object.freeze(tools);
	Object.freeze(allTools);

	// Reading a member that does not exist throws an error that names the close matches, instead of
	// a later "not a function". \`in\` checks still work.
	const comparable = (name) => name.toLowerCase().replace(/[^a-z0-9]/g, "");
	function guard(target, label, names, hint) {
		return new Proxy(target, {
			get(object, property, receiver) {
				if (typeof property !== "string" || property in object || property in Object.prototype || property === "then" || property === "toJSON") {
					return Reflect.get(object, property, receiver);
				}
				const wanted = comparable(property);
				const exact = names.filter((name) => comparable(name) === wanted);
				const close = exact.length > 0 ? exact : names.filter((name) => wanted && (comparable(name).includes(wanted) || wanted.includes(comparable(name))));
				let message = label + "." + property + " does not exist.";
				if (close.length > 0) message += " Did you mean " + close.slice(0, 5).map((name) => label + "." + name).join(", ") + "?";
				else if (names.length <= 20) message += " Available: " + names.join(", ") + ".";
				if (hint) message += " " + hint;
				message += ' Check for a member with "' + property + '" in ' + label + ".";
				throw new TypeErrorCtor(message);
			},
		});
	}
	const toolsProxy = guard(
		tools,
		"tools",
		allTools.map((tool) => tool.name),
		"ALL_TOOLS lists every tool; searchTools(query) finds tools by topic.",
	);

	const namespaces = new Map();
	for (const { name, spread } of parse(globalsJson)) {
		const fn = caller("global", name, spread);
		const dot = name.indexOf(".");
		if (dot === -1) {
			Object.defineProperty(globalThis, name, { value: fn, enumerable: true });
			continue;
		}
		const namespace = name.slice(0, dot);
		if (!namespaces.has(namespace)) namespaces.set(namespace, Object.create(null));
		namespaces.get(namespace)[name.slice(dot + 1)] = fn;
	}
	for (const [namespace, members] of namespaces) {
		Object.freeze(members);
		const value = guard(members, namespace, Object.keys(members));
		Object.defineProperty(globalThis, namespace, { value, enumerable: true });
	}

	// key -> JSON text. Sizes count key and JSON characters.
	const stored = new Map(Object.entries(parse(storeJson)));
	const writes = new Map();
	let storedChars = 0;
	for (const [key, json] of stored) storedChars += key.length + json.length;

	const STORE_HINT =
		"store() is for small state such as IDs or summaries. Show images with image(), keep large data in variables, or write it to a file with a tool.";

	function checkKey(name, key) {
		if (typeof key !== "string") throw new TypeError(name + "() key must be a string");
	}

	function store(key, value) {
		checkKey("store", key);
		const previous = stored.has(key) ? key.length + stored.get(key).length : 0;
		if (value === undefined) {
			stored.delete(key);
			storedChars -= previous;
			writes.set(key, undefined);
			return;
		}
		let json;
		try {
			json = stringify(value);
		} catch (error) {
			throw new TypeError("store(" + stringify(key) + ") value is not JSON-serializable: " + format(error));
		}
		if (json === undefined) {
			throw new TypeError("store(" + stringify(key) + ") value is not JSON-serializable");
		}
		if (json.length > 262144) {
			throw new RangeError(
				"store(" + stringify(key) + ") value has " + json.length + " characters of JSON, more than the limit of 262144. " +
					STORE_HINT,
			);
		}
		const next = storedChars - previous + key.length + json.length;
		if (next > 1048576) {
			throw new RangeError(
				"store is full: stored values would exceed 1048576 characters of JSON. Delete keys with store(key, undefined). " +
					STORE_HINT,
			);
		}
		stored.set(key, json);
		storedChars = next;
		writes.set(key, json);
	}

	function load(key) {
		checkKey("load", key);
		const json = stored.get(key);
		return json === undefined ? undefined : parse(json);
	}

	function serializeWrites() {
		const entries = [];
		for (const [key, json] of writes) entries.push(json === undefined ? [key] : [key, json]);
		return stringify(entries);
	}

	Object.defineProperty(globalThis, "store", { value: store, enumerable: true });
	Object.defineProperty(globalThis, "load", { value: load, enumerable: true });

	let outputChars = 0;
	let outputItems = 0;

	// Past the output limits the script fails: done() reports the error, so catching it does not
	// resume output, and the host ends the script.
	function output(kind, data, mimeType) {
		if (finished) return;
		outputChars += data.length;
		outputItems++;
		if (outputChars > 16777216 || outputItems > 100000) {
			const error = new RangeErrorCtor(
				"script output exceeded the limit of 16777216 characters or 100000 text(), image(), and console calls. " +
					"Print a summary instead, or write large data to a file with a tool.",
			);
			done(false, describeError(error));
			throw error;
		}
		bridge("output", kind, data, mimeType);
	}

	// Primitives become their string form, everything else JSON.
	function outputText(value) {
		if (value === undefined || value === null || typeof value !== "object" && typeof value !== "function") {
			return String(value);
		}
		const json = stringify(value);
		return json === undefined ? String(value) : json;
	}

	function text(value) {
		let rendered;
		try {
			rendered = outputText(value);
		} catch (error) {
			throw new TypeErrorCtor(error instanceof ErrorCtor ? error.message : String(error));
		}
		output("text", rendered);
	}

	function imageUrl(value) {
		if (typeof value === "string") return value;
		if (typeof value !== "object" || value === null || Array.isArray(value)) {
			throw new TypeErrorCtor("image expects a non-empty image URL string, an object with image_url, or a raw MCP image block");
		}
		if (value.image_url !== undefined) {
			if (typeof value.image_url !== "string") throw new TypeErrorCtor("image expects a non-empty image URL string, an object with image_url, or a raw MCP image block");
			return value.image_url;
		}
		if (typeof value.type !== "string") throw new TypeErrorCtor("image expects a non-empty image URL string, an object with image_url, or a raw MCP image block");
		if (value.type !== "image") {
			throw new TypeErrorCtor('image only accepts MCP image blocks, got "' + value.type + '"');
		}
		if (typeof value.data !== "string" || value.data === "") throw new TypeErrorCtor("image expected MCP image data");
		if (value.data.toLowerCase().startsWith("data:")) return value.data;
		return "data:;base64," + value.data;
	}

	// Base64 of the signatures of the formats providers accept inline (PNG, JPEG except
	// JPEG-LS, GIF, "RIFF....WEBP"). Signatures start at byte 0, so their encodings are prefixes.
	const IMAGE_SIGNATURES = [
		["image/png", /^iVBORw0KGg/],
		["image/jpeg", /^[/]9j[/](?!9)/],
		["image/gif", /^R0lGOD[dl]h/],
		["image/webp", /^UklG.{8}RUJQ/],
	];

	function image(value) {
		const url = imageUrl(value);
		if (url === "") throw new TypeErrorCtor("image expects a non-empty image URL string, an object with image_url, or a raw MCP image block");
		const colon = url.indexOf(":");
		const scheme = colon === -1 ? "" : url.slice(0, colon).toLowerCase();
		if (scheme === "http" || scheme === "https") {
			throw new TypeErrorCtor("remote image URLs are not supported in tool outputs. Pass a base64 data URI instead");
		}
		const comma = url.indexOf(",");
		const header = comma === -1 ? [] : url.slice(colon + 1, comma).split(";");
		if (scheme !== "data" || comma === -1 || header.slice(1).every((part) => part.toLowerCase() !== "base64")) {
			throw new TypeErrorCtor("invalid image output. Pass a base64 data URI instead");
		}
		// Providers reject the whole request on a bad image, and a persisted image block would be
		// resent on every later turn. Line breaks from wrapped base64 are dropped. The declared type
		// is ignored in favor of the detected one, as providers also reject mismatches.
		const data = url.slice(comma + 1).replace(/\s+/g, "");
		if (data.length % 4 !== 0 || !/^[A-Za-z0-9+/]+={0,2}$/.test(data)) {
			throw new TypeErrorCtor("invalid image output. The image data is not valid base64 (truncated or corrupted?)");
		}
		const head = data.slice(0, 16);
		const signature = IMAGE_SIGNATURES.find(([, pattern]) => pattern.test(head));
		if (!signature) {
			throw new TypeErrorCtor("invalid image output. The image data is not a PNG, JPEG, GIF, or WebP image");
		}
		output("image", data, signature[0]);
	}

	function exit() {
		let writesJson;
		try {
			writesJson = serializeWrites();
		} catch (error) {
			done(false, describeError(error));
			throw EXIT;
		}
		done(true, undefined, writesJson);
		throw EXIT;
	}

	const console = {};
	for (const level of ["log", "info", "warn", "error", "debug"]) {
		console[level] = (...args) => {
			output("text", args.map(format).join(" "));
		};
	}
	Object.freeze(console);

	Object.defineProperty(globalThis, "tools", { value: toolsProxy, enumerable: true });
	Object.defineProperty(globalThis, "ALL_TOOLS", { value: allTools, enumerable: true });
	Object.defineProperty(globalThis, "console", { value: console, enumerable: true });
	Object.defineProperty(globalThis, "text", { value: text, enumerable: true });
	Object.defineProperty(globalThis, "image", { value: image, enumerable: true });
	Object.defineProperty(globalThis, "exit", { value: exit, enumerable: true });

	return {
		settle(id, ok, payload) {
			const entry = pending.get(id);
			if (!entry) return;
			pending.delete(id);
			if (!ok) {
				entry.reject(new ErrorCtor(payload));
				return;
			}
			let value;
			try {
				value = payload === undefined ? undefined : parse(payload);
			} catch (error) {
				entry.reject(error);
				return;
			}
			entry.resolve(value);
		},
		run(fn) {
			let promise;
			try {
				promise = fn(toolsProxy, console);
			} catch (error) {
				done(false, describeError(error));
				return;
			}
			promiseThen.call(
				promise,
				(value) => {
					let json;
					try {
						json = serialize(value);
					} catch (error) {
						done(false, describeError(error));
						return;
					}
					done(true, json, serializeWrites());
				},
				(error) => {
					done(false, describeError(error));
				},
			);
		},
		stalled() {
			if (finished || pending.size > 0) return false;
			done(
				false,
				stringify({
					name: "Error",
					message:
						"The script is waiting on a promise that can never settle: no tool call is pending, and timers do not exist here.",
				}),
			);
			return true;
		},
	};
})
""".TrimEnd();
}
