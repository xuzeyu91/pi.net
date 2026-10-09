# coding-agent 移植状态（packages/coding-agent → src/Pi.CodingAgent）

本文记录 `packages/coding-agent`（**85,536 行 TS / 299 文件**，不含 `*.test.ts`）到
`src/Pi.CodingAgent` 的分阶段移植进度与设计差异。包级进度总览见
[porting-status.md](porting-status.md)，tui 见 [tui-porting-status.md](tui-porting-status.md)。

> 参照源码：`D:\AI\参考项目\pi\packages\coding-agent\src`

## 规模与现状

| 目录 | 行数 | 文件 | 内容 |
|---|---:|---:|---|
| `src/core/` | 35,148 | 94 | 会话 / 模型运行时 / 设置 / 工具系统 / 扩展 / compaction / export-html |
| `src/modes/` | 23,753 | 67 | 交互模式（TUI 主界面）与 RPC 模式 |
| `src/experimental/` | 9,950 | 46 | durable 集成、插件、services、vacation |
| `src/extensions/` | 7,303 | 25 | codemode / llama / mcp / tool-search 扩展 |
| `src/utils/` | 3,737 | 37 | 通用工具 |
| `src/cli/` | 1,959 | 17 | CLI 参数解析与子命令 |
| `src/bun/` + `src/client/` | 62 | 6 | 单文件二进制入口、客户端 |
| **合计** | **85,536** | **299** | |

> 早期 `docs/migration-plan.md` 记的「19,427 行」是错的（少算了一个数量级），已按实测更正。

## 分阶段计划

按依赖顺序推进，每个子阶段以「构建 0 警告 0 错误 + 相关测试全绿 + 一致性核对」收口。

| 子阶段 | 范围 | 行数 | 状态 |
|---|---|---:|---|
| **4a** | `src/utils/*`（37 文件）+ 无依赖的根级模块（`config.ts` / `migrations.ts` / `core/defaults.ts` 等） | ~4,500 | 🚧 进行中（26/36 utils + `config.ts`） |
| **4b** | 配置 / 信任 / 模型层：settings-manager、trust-manager、project-trust、auth-storage、model-config/registry/resolver、models-store、radius、virtual-models、mcp-servers、keybindings | ~12,000 | ⏳ |
| **4c** | 工具系统：`core/tools/*` + `core/tools/renderers/*` | ~9,000 | ⏳ |
| **4d** | 扩展系统：`core/extensions/*`（types / runner / loader）+ `extensions/*`（codemode / llama / mcp / tool-search） | ~12,000 | ⏳ |
| **4e** | 会话与资源：agent-session、session-manager、resource-loader、package-manager、compaction、export-html、system-prompt、telemetry、sdk | ~20,000 | ⏳ |
| **4f** | 模式层：`modes/rpc/*`、`modes/interactive/*`（含 7,082 行的 `interactive-mode.ts` 与 components / theme） | ~23,700 | ⏳ |
| **4g** | 入口与实验层：`cli/*`、`main.ts`、`cli.ts`、`rpc-entry.ts`、`package-manager-cli.ts`、`bun/*`、`client/*`、`experimental/*` | ~12,000 | ⏳ |

## 4a 进度（utils 层）

### ✅ 已完成（26/36 个 utils 代码文件 + 根级 `config.ts`）

| TS 文件 | 行数 | .NET | 说明 |
|---|---:|---|---|
| `utils/json.ts` | 6 | `Utils/Json.cs` | `stripJsonComments`：先剥 `//` 行注释再剥尾随逗号，两趟都用「先匹配字符串字面量」的择一正则，避免动到字符串内容 |
| `utils/text.ts` | 9 | `Utils/Text.cs` | `splitBom` / `stripBom` |
| `utils/pi-user-agent.ts` | 4 | `Utils/PiUserAgent.cs` | User-Agent 组装（运行时令牌见差异 C1） |
| `utils/sleep.ts` | 18 | `Utils/Sleep.cs` | `sleep(ms, signal)` → `SleepAsync(ms, ct)`（差异 C2） |
| `utils/deprecation.ts` | 14 | `Utils/Deprecation.cs` | 去重告警 + chalk 黄；错误流可注入（差异 C3） |
| `utils/wsl.ts` | 15 | `Utils/Wsl.cs` | `WSL_DISTRO_NAME` / `WSLENV` / `/proc/version` 探测，环境查找可注入 |
| `utils/ansi.ts` | 60 | `Utils/AnsiText.cs` | `stripAnsi`（ansi-regex 派生），**类名改为 `AnsiText`**（见差异 C4） |
| `utils/html.ts` | 51 | `Utils/Html.cs` | HTML 实体解码；`String.fromCodePoint` 接受孤立代理项（差异 C5） |
| `utils/abort.ts` | 48 | `Utils/Abort.cs` | `operationSignal` / `raceWithAbortSignal` → `CancellationToken`（差异 C2） |
| `utils/output-files.ts` | 34 | `Utils/OutputFiles.cs` | 临时输出文件（`0o600` + `wx`，差异 C6） |
| `utils/mime.ts` | 116 | `Utils/Mime.cs` | 图像 MIME 嗅探：JPEG / PNG（含 APNG 排除）/ GIF / WebP / BMP 头部校验 |
| `utils/open-browser.ts` | 24 | `Utils/OpenBrowser.cs` | 平台启动器（不用 shell，防注入） |
| `utils/fs-watch.ts` | 30 | `Utils/FsWatch.cs` | `fs.watch` 的 `FileSystemWatcher` 等价物 + 抖动去重 |
| `utils/paths.ts` | 140 | `Utils/Paths.cs` + `Utils/FileUrl.cs` + `Utils/NodePath.cs` | 核心路径语义。`FileUrl.cs` 与 `NodePath.cs` 是新增的忠实底层：`node:url` 的 `fileURLToPath`（WHATWG 状态机）与 `node:path` 的 `normalize`/`join`/`dirname`/`basename`/`isAbsolute`/`resolve`/`relative`（见差异 C9–C11） |
| `utils/child-process.ts` | 137 | `Utils/ChildProcess.cs` | `utils/child-process.ts` + `cross-spawn`：`parseNonShell` 的 cmd-shim 判定、`.com`/`.exe` 快路径、`comspec` 回退，以及 `escapeCommand`/`escapeArgument`（差异 C12） |
| `utils/shell.ts` | 218 | `Utils/Shell.cs` | `getShellConfig` / `getPowerShellConfig` / `getShellEnv` / `sanitizeBinaryOutput` / `killProcessTree` + 分离子进程 PID 追踪 |
| `utils/clipboard.ts` | 144 | `Utils/Clipboard.cs` | 平台命令分派（Termux → Wayland → X11 → pbcopy/clip）、远程会话 OSC 52 回退、原生剪贴板注入缝（差异 C13） |
| `utils/clipboard-command.ts` | 44 | `Utils/ClipboardCommand.cs` | 带超时与输出上限的剪贴板命令执行器 |
| `utils/version-check.ts` | 109 | `Utils/VersionCheck.cs` | `formatVersionCheckError` / `comparePackageVersions` / `getLatestPiRelease` / `checkForNewPiVersion`（差异 C14） |
| `utils/management-http.ts` | 78 | `Utils/ManagementHttp.cs` | 带重试的 fetch（`{408,425,429,500,502,503,504}`），`HandlerOverride` 注入缝 |
| `utils/changelog.ts` | 196 | `Utils/Changelog.cs` | `parseChangelog` / `normalizeChangelogLinks`（把仓库内相对链接改写到 tag）/ `compareVersions` / `getNewEntries`；需要 JS 的 `encodeURI`（差异 C15） |
| `utils/git.ts` | 226 | `Utils/Git.cs` + `Utils/HostedGit.cs` + `Utils/JsUrl.cs` | `splitRef`（scp / 显式协议 / 裸 `host/path` 三态）/ `hasUnsafeGitInstallPart` / `buildGitSource` / `parseGenericGitUrl` / `parseGitUrl`。`HostedGit.cs` 是 `hosted-git-info@8.1.0` 的**识别半边**（`parse-url` + `from-url` + `hosts` 的 `domain`/`protocols`/`extract`），`JsUrl.cs` 是它依赖的 WHATWG `new URL()` 子集（差异 C16–C17） |
| `utils/tools-manager.ts` | 366 | `Utils/ToolsManager.cs` | `getToolPath` / `getLatestVersion` / `ensureTool`：托管 `fd`/`rg` 的下载、解压（tar.gz / zip）、就位。五处注入缝（`EnvOverride` / `ToolsDirOverride` / `SpawnOverride` / `PlatformOverride` / `ArchOverride`），平台与架构**不经 `ProcessInfo`** 以免扰动 `NodePath`（差异 C19–C20） |
| `utils/windows-self-update.ts` | 118 | `Utils/WindowsSelfUpdate.cs` | `cleanupQuarantine` / `quarantineNativeDependencies`：把仍被加载的 `.node` 挪进 `.pi-native-quarantine` 再删。加载列表经 `LoadedSharedObjectsOverride` 注入——`process.report.getReport().sharedObjects` 在 .NET 无对应物（差异 C21） |
| `utils/zip.ts` | 72 | `Utils/Zip.cs` | `writeZipArchive`：手写经典 ZIP（版本 20 / UTF-8 名标志 `0x0800` / 方法 8 / 无数据描述符 / 无扩展字段），供 bug report 打包。压缩流用 `DeflateStream`（裸 RFC 1951），CRC-32 自实现（与 `zlib.crc32` 逐位一致）。`ZipEntry.FromText` / `FromBytes` 对应 TS 的 `string \| Uint8Array` 两态（差异 C22） |
| `utils/syntax-highlight.ts` | 219 | `Utils/SyntaxHighlight.cs` | `renderHighlightedHtml`（把 `hljs-*` span 栈映射到主题格式化器：精确 → `.` 前缀 → `-` 前缀 → `default`，逐行格式化且**空行不套格式**，实体在出口解码）为逐行忠实移植；`highlight` / `supportsLanguage` / `loadAllHighlightLanguages` 的 tokenizer 收进 `IHighlighter` 缝（差异 C23） |
| `config.ts`（根级） | 656 | `Config.cs` | 安装方式判定、自更新命令拼装、分享链接、资产路径。`ConfigSeams` 集中 9 个注入点 |

C# 侧另有若干「JS 语义」辅助（TS 无对应文件，供全部子阶段复用）：

| 文件 | 作用 |
|---|---|
| `Utils/JsRegex.cs` | JS `\s` / `\d` 字符类常量 + `Number.parseInt` 的忠实实现（0x 前缀剥离、大整数单次正确舍入，见差异 C7） |
| `Utils/ProcessInfo.cs` | `process.platform` / `process.arch` 的 Node 拼写映射（保持用户可见字符串不变） |
| `Utils/Chalk.cs` | chalk 的 16 色子集：嵌套重开（`close` → `close+open`）与 CRLF 感知的逐行包裹 |
| `Utils/NodeError.cs` | `INodeError { string? Code }` + `NodeIoException`——Node 的 `err.code`（`ECONNREFUSED`、`ERR_INVALID_URL`…）在 C# 侧的落点 |
| `Utils/Semver.cs` | semver 7.8.5 的 `valid` / `parse` / `compare`；把 `internal/re.js` 的 `MAX_LENGTH`/`MAX_SAFE_INTEGER` 安全边界原样搬进正则 |
| `Utils/NodePath.cs` | `lib/path.js` 的逐行移植（含 `\\.\`/`\\?\` 设备根、UNC 根、Windows 保留名、CVE-2024-36139 补丁块）。`toNamespacedPath` 也在这里（`WindowsSelfUpdate` 要它把路径规范成 `\\?\` 长路径形式），源码同样取自 `process.binding("natives")["path"]` |
| `Utils/FileUrl.cs` | `fileURLToPath` 的 WHATWG 解析器（scheme/authority/path 三态、IPv4 归一化、禁止域名码点） |
| `Utils/JsUri.cs` | JS 的 `encodeURI` / `encodeURIComponent`。**不能**用 `Uri.EscapeDataString`：未转义集合不同，且两者都不保留 `%`（`encodeURI("%41")` 是 `"%2541"`）。`decodeURIComponent` 也在这里——它要**抛**（`URIError`），`HostedGit` 与 `Git` 都靠这个信号判定畸形转义 |
| `Utils/JsUrl.cs` | WHATWG `new URL()` 的子集 + 序列化器。**不能**用 `System.Uri`：非特殊协议没有 `//` 时是**不透明路径**（`github:user/repo` 的主机是空串），特殊协议的 authority 会吃掉**任意个**前导 `/`（`https:////host/x` 的主机仍是 `host`）。序列化器只有 `Git.SplitRef` 用得到——它靠「改 `pathname` 再 `toString()`」把 ref 从 clone URL 里摘掉。`Resolve(base, ref)` 是 `ToolsManager` 解析 `Location` 头用的相对引用解析（绝对 → 协议相对 → 空 → `?`/`#` → 目录拼接） |
| `Utils/HostedGit.cs` | `hosted-git-info` 的 `fromUrl`：5 个主机的 `domain`/`protocols`/`extract` 表 + `parse-url` 的 scp 修正。返回 `HostedGitInfo`（`type`/`domain`/`user`/`project`/`committish`/`default`） |

### ⏳ 待移植

`utils/` 余下 **10 个文件 / 1,205 行**：

| 分组 | 文件 | 行数 | 备注 |
|---|---|---:|---|
| 图像处理 | `clipboard-image.ts`、`exif-orientation.ts`、`image-convert.ts`、`image-process.ts`、`image-resize-core.ts`、`image-resize-worker.ts`、`image-resize.ts`、`photon.ts`、`tool-result-images.ts` | 1,165 | 已选型 **SkiaSharp**（MIT）。`photon.ts` 是 Bun 的 wasm 路径烘焙垫片，.NET 无对应物，不逐行移植 |
| 需第三方库 | `frontmatter.ts` | 40 | 已选型 **YamlDotNet**（MIT） |

另 `utils/highlight-js.d.ts` 是纯类型声明，无运行时代码，不需要移植。

> 计数口径：上表两行相加 = 1,165 + 40 = **1,205**，与 `wc -l` 实测一致。早前记的「图像处理 1,265」多算了 100 行（导致 1,265+484+331=2,080 与总数 1,980 对不上），本批已按实测更正。

## 关键设计差异（TS → C#）

| # | 差异 | 说明 |
|---|---|---|
| C1 | **User-Agent 的运行时令牌改为 `dotnet/<version>`** | TS 报 `bun/<ver>` 或 `node/<ver>`；C# 两者都没有，故 `pi/<version> (<platform>; dotnet/<version>; <arch>)`。`platform` / `arch` 仍用 Node 的拼写（`win32` / `x64` …）以免改变用户可见格式。 |
| C2 | **`AbortSignal` → `CancellationToken`** | 与全仓既定结论一致（见 `porting-status.md` 差异第 1 条）。`sleep` 的 `Error("Aborted")` 与 `raceWithAbortSignal` 的 `signal.reason` 都归一为 `OperationCanceledException`（携带 token），调用方用 `IsCancellationRequested` 判别。 |
| C3 | **`console.warn` / `console.error` → 可注入的 `TextWriter`** | `Deprecation` 与后续 CLI 输出都走 `TextWriter`，测试用 `StringWriter` 观测，避免改进程级 `Console`。 |
| C4 | **`utils/ansi.ts` 的类名取 `AnsiText`** | `Pi.Tui.Ansi` 已存在且会被同一文件同时 `using`，同名会二义。TS 侧本来就是两套独立实现（tui 的 `utils.ts` vs coding-agent 的 `utils/ansi.ts`），拆开是忠实的。 |
| C5 | **`String.fromCodePoint` 需自行实现** | .NET 的 `char.ConvertFromUtf32` 对代理码点**抛异常**，而 JS 的 `String.fromCodePoint(0xD800)` 返回孤立代理项。`Html.FromCodePoint` 对 `0xD800–0xDFFF` 直接产出该码元。差分语料因此把解码结果记成 **UTF-16 码元数组**——`System.Text.Json` 无法把 `"\uD800"` 读回字符串。 |
| C6 | **`0o600` + `flag: "wx"` → `FileStreamOptions`** | `wx` = `FileMode.CreateNew`（不跟随他人预置的链接），权限用 `UnixCreateMode`。注意 `UnixCreateMode` 在 Windows 上会抛，故只在 `!OperatingSystem.IsWindows()` 分支设置（否则 CA1416 报错）。 |
| C7 | **`Number.parseInt` 需要两处非显然的处理** | ①规范在**显式 radix 为 16** 时设置 `stripPrefix`，故 `parseInt("0x1f", 16) === 31`；②V8 用 bignum 累加后**只做一次正确舍入**，而 .NET 的 `(double)BigInteger` 是**截断**（`11111111111111111` → `...110`，JS 是 `...112`），故改走 `double.Parse`（.NET Core 3.0+ 的解析器是正确舍入的）。110 条差分向量锁定，含 `1e308`、`Infinity`、`9`×24 等边界。 |
| C8 | **chalk 的嵌套重开是 `close → close + open`** | chalk v6 的 `stringReplaceAll(string, substring, postfix)` **保留匹配并追加** `postfix`，所以已存在的 `\e[39m` 变成 `\e[39m\e[33m`（而不是被替换成 `\e[33m`）。首版按旧版 chalk 的「替换」实现，6 条嵌套向量失败。 |
| C9 | **`node:path` 不能交给 BCL** | `Path.GetFullPath` / `Path.GetRelativePath` 只是近似：`relative` 对相等路径 .NET 返回 `"."` 而 Node 返回**空串**；`resolve` 的逐盘符 cwd 回退、`normalize` 的 `\\.\`/`\\?\` 设备根与 Windows 保留名（`CON`、`COM¹`…）都没有对应物。首版用 BCL 实现，13 条差分向量失败，最终改为逐行移植 `lib/path.js`（源码经 `process.binding("natives")["path"]` 取出，不靠猜）。 |
| C10 | **`fileURLToPath` 需自行实现 WHATWG 状态机** | `new URL()` 无法直接复用：驱动器字母可以从 authority 态「逃逸」（`file:///C:/x` vs `file://C:/x`），`localhost` 的折叠发生在**大小写折叠之后**，凭据/端口要报 `ERR_INVALID_URL`，主机名的「以数字结尾」判定是**语法性**的（`08` 会走进 IPv4 分支然后失败，而不是保持为主机名）。全部规则先用约 200 条探针向量实测，再落代码。 |
| C11 | **两处刻意的 `fileURLToPath` 分歧** | ①ICU 的 `domainToUnicode` 未移植（`internal/idna` 不在 `process.binding("natives")` 里，是 ICU 后端），含非 ASCII 主机名时行为不同；②IPv4 内嵌的 IPv6 字面量用 .NET 的点分十进制序列化。两者都写进 XML `<remarks>`，并用 `fileUrlToPathDivergence` 语料段**双向断言**——若将来 ICU/.NET 的变化让分歧收窄或扩大，测试会失败并提示删除条目。 |
| C12 | **cross-spawn `escapeArgument` 的正则不能照搬** | 原式 `/(?=(\\+?)?)\1"/g` 与 `/(?=(\\+?)?)\1$/` 把反斜杠串放在**前瞻里的惰性量词**中，而 V8 不会为一个已经成功的前瞻回溯放大该捕获——真正被倍增的只有反斜杠串的**最后一个**反斜杠。实测规则：串尾的 n 个反斜杠变 n+1，引号前的 n 个变 n+2（n=0 时变 1）。照搬正则会得到 2n / 2n+1，**是错的**。已用 448 条随机向量锁定。 |
| C13 | **需要注入缝的三处平台耦合** | `NodePath.CwdOverride`（`resolve`/`relative` 读 cwd，而语料在不同目录采录）、`Clipboard.NativeClipboardOverride`（TS 测试会 stub 模块级单例）、`Clipboard.PlatformOverride` / `EnvOverride` / `CommandRunnerOverride` / `Osc52WriterOverride`。`resolve`/`relative` 是纯字符串运算，注入的路径不必真实存在。 |
| C14 | **`AggregateException.Message` 不是 JS 的 `error.message`** | .NET 的 `AggregateException.Message` 会把每条内层消息**追加**成 `" (m1) (m2)"`，而 JS 的 `error.message` 只有外层文本；同时 `String(error)` 取的是 JS 的 `error.name`（`"Error"` / `"AggregateError"`），不是 .NET 类型名。`FormatVersionCheckError` 因此要把后缀剥回去，并把类型映射成 JS 名称。 |
| C15 | **`existsSync` 对目录返回 true** | Node 的 `existsSync(dir)` 是 true，随后 `readFileSync` 抛 `EISDIR` 走 catch；而 .NET 的 `File.Exists(dir)` 是 false，会静默跳过 catch。`Changelog.ParseChangelog` 因此用 `File.Exists(path) \|\| Directory.Exists(path)`。同理 `Uri.EscapeDataString` 不能当 `encodeURI`（差异见 `JsUri.cs`）。 |
| C16 | **`JsUrl` 刻意省略两处 WHATWG 行为** | ①IDNA / domain-to-ASCII：非 ASCII 的特殊协议主机名原样保留而不转 punycode（ICU 的 `domainToUnicode` 与 TS 侧同样不完整，见 C11）；②默认端口折叠：`https://h:443/x` 保留显式 `:443` 而不丢。两者都不改变本仓可达路径上的 `hostname` / `pathname`，已写进 `JsUrl.cs` 的 XML `<remarks>`。**首版还有一处真错**：特殊协议只吃了两个前导 `/`，导致 `https:////host/x` 的主机被判成空串。WHATWG 的 "special authority ignore slashes" 会吃掉**任意个**，`git://github.com/user/repo.git` 因此得到 `null` 而不是 `https:////github.com/user/repo.git`；语料把它抓出来了。 |
| C17 | **`hosted-git-info` 只移植「识别」半边** | `git.ts` 只读 `domain`/`user`/`project`/`committish`（`type`/`default` 也一并保留以对齐 `fromUrl` 的返回值），故不移植 URL **格式化**半边（`ssh()`/`https()`/`browse()`/`tarball()` 与背后十几个模板）、1000 条 LRU 缓存、以及 `auth` 分量。判断依据是 grep：全仓只有 `git.ts` 引它，且只调 `fromUrl`；缓存是纯记忆化，唯一可观测差异是返回对象的引用相等，而调用方只读字段。 |
| C18 | **`Git.SplitRef` 的 `://` 分支需要 URL 序列化** | 原实现是「`new URL(url)` → 改 `parsed.pathname` → `parsed.toString()`」，序列化结果会带上协议规范化、主机小写、点段折叠。这不是能省的一步：`https://github.com/user/repo@ref` 的 `repo` 就来自它。为此给 `JsUrlValue` 补了 `Port`/`Query`/`HasAuthority`/`IsIpv6` 四个分量并写了 `JsUrl.Serialize`。用户信息与不透明主机按解析结果原样输出（解析时也没有编码它们），ASCII 输入下与 WHATWG 一致。 |
| C19 | **`ToolsManager` 的平台/架构读本类私有缝，不经 `ProcessInfo`** | 语料要在同一台 Windows 上回放 darwin / linux / win32 × x64 / arm64 的全矩阵，所以平台与架构必须可注入。若把缝挂在 `ProcessInfo` 上，`NodePath`（它按真实宿主平台选 `win32`/`posix` 分支）就会被一起改写，路径答案随即失真——`ToolsManager` 里所有平台判断都走 `Platform` / `Arch` 两个私有属性，`ProcessInfo` 保持只读宿主常量。 |
| C20 | **`redirect: "manual"` → `FetchRetryOptions.AllowAutoRedirect`** | `getLatestVersion` 读的是 302 的 `Location` 头本身，必须关掉自动跟随。`ManagementHttp` 原来只有一个 `HttpClient`，现拆成 `_client` / `_manualRedirectClient` 两个（`HandlerOverride` 的 setter 同时处置两者），选项默认 `true` 以保持既有调用点行为不变。`Location` 的解析用 `JsUrl.Resolve("https://github.com", location)` 再取 `pathname` 最后一段——与 `new URL(location, "https://github.com")` 一致，相对 `Location` 也能解析。 |
| C21 | **`process.report.getReport().sharedObjects` 在 .NET 没有对应物** | `windows-self-update.ts` 靠它拿到「当前进程已加载的原生模块」，据此判断哪些 `.node` 仍被占用。.NET 侧的近似物是 `Process.GetCurrentProcess().Modules`，但语义不同（它列的是模块而非 dlopen 的共享对象，且在非 Windows 上不可用）。因此 `DefaultLoadedSharedObjects()` 只在 Windows 上返回模块列表、其他平台返回 `null`，真实判定交给 `LoadedSharedObjectsOverride` 注入——语料用注入值回放全部 8 条向量。另外 `renameSync` 在同卷上是原子改名，落点用 `File.Move(overwrite: true)`。 |
| C22 | **`Zip` 的压缩字节不逐字节复刻，接受标准改为「结构等价 + 双向互操作」** | Node 的 zlib 与 .NET 的 zlib-ng 在小载荷上**逐字节相同**，在较大载荷上会选出不同的匹配编码——1000 个 `'A'` 时 Node 出 11 字节、.NET 出 12 字节。因此 `compressedSize` 以及由它派生的 `localOffset` / `centralDirectoryOffset` **不能**与 TS 对齐，语料改为：①逐字段比对全部编码无关字段（签名、版本 20、flags `0x0800`、method 8、DOS 时间/日期、CRC-32、**未压缩**长度、名字节、中央目录尺寸、条目数）；②把 `localOffset` 断言成「指向同名局部记录」的**链接关系**而非具体数字；③断言 deflate 往返回原字节；④用 BCL `ZipArchive` 读 Node 写的归档。另外**独立**用 Python `zipfile`（第三方实现，读取时校验 CRC）双向验证了 12 组归档 × 2 侧全部通过，含 UTF-8 名 `报告-📄.md`。副作用：`zip.ts` 原本是「需第三方归档库」的待选型项，实际只用 `crc32` + `deflateRawSync`，`System.IO.Compression` 全覆盖，无需新增依赖。 |
| C23 | **`syntax-highlight.ts` 的 tokenizer 收进 `IHighlighter` 缝** | 原文件 eager 注册 21 种语言、懒加载其余约 180 种，`hljs.highlight` 是一台约 1 MB 的状态机。属于本仓的那半——把 `hljs-*` span 栈改写成主题格式化器——逐行移植并用 `syntax-highlight-corpus.json` 覆盖（含 `.`/`-` 前缀回退、空行不套格式、未闭合 span、`<spanner>` 不算标签、实体解码）；tokenizer 本身改为 `SyntaxHighlight.HighlighterOverride` 可注入。默认实现 `PlainTextHighlighter` 输出 highlight.js 的 plaintext 结果（同一套 `escapeHTML`），`SupportsLanguage` 对一切返回 `false`。**后果是未配置注入时没有任何语法配色**，但转义与 `theme.default` 仍然正确；TUI 调用点本来就用 `supportsLanguage(lang) ? lang : undefined` 守卫，因此会走 `highlightAuto` 而不是抛错。语料里的 `plaintext` 向量取自真实 highlight.js，用来把回退实现钉在真实行为上。 |
| C24 | **本解决方案首次引入 NuGet 依赖（SkiaSharp / YamlDotNet）** | 此前 `src/` 只有 `Pi.Durable` 引了 `DiffPlex`，其余全部手写（`NodePath` / `FileUrl` / `JsUrl` / `Semver` / `JsRegex` / `JsUri` / `Chalk` / `Zip` 都是零依赖）。本批为 4a 余下 1,205 行引入两个库：**SkiaSharp**（MIT，JPEG/PNG/GIF/WebP 编解码，图像处理 1,165 行必需——手写 JPEG 解码器不现实）与 **YamlDotNet**（MIT，`frontmatter.ts` 的 `yaml@2.9.0` 的 `parse` 对应物）。选 MIT 而非 ImageSharp 是避开 Six Labors 的分裂授权。SkiaSharp 带按 RID 的原生资产，这与原项目 `photon.ts` 折腾的「Bun 单文件二进制里 wasm 路径被烘焙」是同一类问题的 .NET 形态，但 .NET 对原生资产有正规分发机制，不需要路径补丁。 |

## 差分验证（utils 层）

各份语料都由**原始 TypeScript 实现**实跑生成（生成器在 `tools/`，该目录已 gitignore）：

> **生成器的依赖根**：生成器直接 `import` 原 TS 源文件并以 Node 运行，原源码从**自身位置**向上解析裸包，
> 所以必须能从 `D:\AI\参考项目\node_modules` 找到它们：`chalk@6.0.0`、`cross-spawn@7.0.6`、
> `semver@7.8.5`、`hosted-git-info@8.1.0`（**不是** package.json 声明的 9.0.3——语料与移植都按 8.1.0
> 采录）、`highlight.js@10.7.3`、`yaml@2.9.0`、`diff@8.0.4`、`ignore@7.0.8`、`minimatch@10.2.6`、
> `jiti@2.7.0`、`proper-lockfile@4.1.2`、`quickjs-wasi@3.6.2`、`typebox@1.3.27`、`undici@8.10.2`、
> `grok-mermaid@0.2.3`、`@silvia-odwyer/photon-node@0.3.4`。
> 两条已踩过的坑：①**不要**在 `tools/` 里裸跑 `npm install`——npm 会向上把 `D:\AI\参考项目` 当成项目根，
> 并把它那里的 `node_modules`（原本是指向依赖存储的 junction）替换成普通目录，导致所有生成器立刻
> `ERR_MODULE_NOT_FOUND`；要装就用 `npm install --prefix "D:/AI/参考项目" --no-save --no-package-lock`。
> ②同一个包**不能**同时存在于 `tools/node_modules` 与父目录——`highlight.js` 会变成两个实例，生成器注册的
> 语言落在另一个实例上，原模块仍报 `Unknown language`。

| 语料 | 生成器 | 条数 | 覆盖 |
|---|---|---:|---|
| `tests/Pi.CodingAgent.Tests/utils-corpus.json` | `tools/gen-coding-agent-utils-corpus.mjs` | 416 | json / text / html / ansi / parseInt / chalk / deprecation / mime |
| `tests/Pi.CodingAgent.Tests/core-utils-corpus.json` | `tools/gen-coding-agent-core-utils-corpus.mjs` | 1,526 | 见下表（38 个区段） |

`core-utils-corpus.json` 的区段：

| 区段 | 条数 | 内容 |
|---|---:|---|
| `nodePathNormalize` / `Join` / `Dirname` / `Basename` / `BasenameWithExt` / `IsAbsolute` | 237 | win32 与 posix 双平台：设备根、UNC、保留名、`.`/`..` 折叠、尾随分隔符 |
| `nodePathResolve` / `nodePathRelative` | 60 | 逐盘符 cwd 回退、跨盘、相对路径在 cwd 之外的情形（配合 `hostCwd` 回放） |
| `pathsNormalizeWindowsShellPath` / `IsLocalPath` / `NormalizePath` / `ResolvePath` / `CwdRelative` / `Platform` | 103 | `utils/paths.ts` 的公开面 |
| `fileUrlToPath` | 254 | WHATWG 路径状态机、百分号编码的点段、authority/主机/凭据/端口、禁止域名码点、IPv4 归一化 |
| `fileUrlToPathDivergence` | 4 | 差异 C11 的双向断言 |
| `semverValid` / `semverCompare` | 95 | semver 7.8.5 的 `valid` / `compare`，含长度与安全整数边界 |
| `versionCheckCompare` / `IsNewer` / `FormatError` / `Release` / `Check` | 49 | 版本比较、错误格式化（含 `AggregateError`）、重试次数（404→1、503/429→3、418→1、408→3） |
| `clipboardRead` / `clipboardCopy` / `clipboardCopyText` | 34 | 平台分派顺序、远程会话 OSC 52、原生剪贴板三态（无 / 空 / 有文本） |
| `configUpdate` / `Classify` / `CommandStep` / `NormalizeTarget` / `ShareUrl` | 55 | 安装方式判定、自更新命令拼装、包目标归一化 |
| `hostPlatform` / `hostCwd` | 2 | 宿主常量，供回放使用 |
| `shellSanitizeBinaryOutput` / `IsLegacyWslBashPath` | 45 | 二进制输出清洗、WSL bash 路径判定 |
| `escapeCmdCommand` / `escapeCmdArgument` | 140 | cross-spawn 转义的定值向量 |
| `escapeCmdArgumentRandom` | 448 | 反斜杠 / 引号 / 元字符的确定性随机扫描 + 全部 n≤8 的串形（差异 C12） |

`tests/Pi.CodingAgent.Tests/git-corpus.json`（由 `tools/gen-coding-agent-git-corpus.mjs` 生成，共 292 条）：

| 区段 | 条数 | 内容 |
|---|---:|---|
| `hostedGitInfoFromUrl` | 92 | hosted-git-info 的 `fromUrl`：github / gitlab / bitbucket / gist / sourcehut、短写、凭据、端口、百分号编码、畸形输入 |
| `parseGitUrl` | 107 | `git.ts` 的 `parseGitUrl`：`git:` 前缀的各种历史短写、显式协议、scp 形式、`#ref`、不安全片段（`..`、反斜杠、前导 `/`、`%00`） |
| `normalizeChangelogLinks` | 52 | 相对链接改写、`blob`/`tree` 判定、浮动分支改写到 tag、旧仓库改名、编码 |
| `parseChangelog` | 19 | 版本头形态（`## x.y.z` / `## [x.y.z]` / `## v…`）、无版本头截断、CRLF、缺失尾换行 |
| `parseChangelogMissing` / `parseChangelogUnreadable` | 2 | 不存在的路径 / 目录（后者走 catch 分支） |
| `compareChangelogVersions` | 7 | 三元比较 |
| `getNewChangelogEntries` | 13 | `lastVersion` 畸形时的 `Number() \|\| 0` 语义 |

`tests/Pi.CodingAgent.Tests/tools-corpus.json`（由 `tools/gen-coding-agent-tools-corpus.mjs` 生成，共 84 条 + 2 个常量对象）：

| 区段 | 条数 | 内容 |
|---|---:|---|
| `nodePathToNamespacedPath` | 22 | `path.toNamespacedPath` 的 win32 / posix 双列：驱动器相对、UNC、设备根、`\\?\` 幂等、超短路径 |
| `toolsLatestVersion` | 17 | `getLatestVersion` 的状态码 / `Location` 组合：302 正常、无 `Location`、404、相对 `Location`、畸形 tag |
| `toolsToolPath` | 9 | `getToolPath` 的三态（托管副本 / 系统二进制 / `null`）× 平台 × 系统命令可用性 |
| `toolsEnsureTool` | 16 | `ensureTool` 的四种 spawn 模式（`all-missing` / `all-present` / `fdfind-only` / `system-missing-extract-ok`）× 平台 × 架构 × 离线，逐条记录状态消息序列与最终路径 |
| `toolsOfflineMode` | 12 | `PI_OFFLINE` 的取值形态（`1` / `true` / `0` / 空串 / 未设…）与「离线」判定 |
| `windowsSelfUpdate` | 8 | 隔离目录建/挪/删、二次执行幂等、包外文件不碰、清理后布局 |
| `hostCwd` / `windowsSelfUpdateCleanupMissing` | 2 | 宿主 cwd 常量；隔离目录缺失时清理不抛 |

`tests/Pi.CodingAgent.Tests/zip-corpus.json`（由 `tools/gen-coding-agent-zip-corpus.mjs` 生成，共 12 组归档）：

| 区段 | 条数 | 内容 |
|---|---:|---|
| `fixedLocal` / `dosDateTime` | 2 | 生成时钉住的本地时刻（`2024-03-15T14:23:47`）与它应编出的 DOS 字（time 29431 / day 22639） |
| `archives[].locals` / `centrals` / `eocd` | 12 组 | 逐字段解码出的归档结构；`compressedSize` 与偏移只做一致性检查（差异 C22） |
| `archives[].archiveBase64` | 12 组 | Node 写出的原始归档字节，用于「C# 能否读外部归档」这一向 |

归档用例覆盖：单条小文本、bug report 的四文件形态（`report.json` / `diagnostics.json` / `session.jsonl` / `summary.md`）、空归档、空内容条目、空与非空混合、含高位字节的二进制、1000 字节近不可压、4096 字节高可压、UTF-8 名（`报告-📄.md`）、嵌套路径名、CRLF 内容、重名条目。

`tests/Pi.CodingAgent.Tests/syntax-highlight-corpus.json`（由 `tools/gen-coding-agent-syntax-highlight-corpus.mjs` 生成，共 323 条）：

| 区段 | 条数 | 内容 |
|---|---:|---|
| `crafted` | 210 | 手工构造的 highlight.js 形态 HTML × 7 套主题：嵌套 span、单引号 class、多类名、无 class、`>` 出现在属性值里、`<spanner>`、未闭合 span、游离 `</span>`、跨行 span、空行、CRLF、`.`/`-` 前缀作用域、标签内制表符/换行 |
| `highlighted` | 85 | 9 种语言（含 `plaintext`）× 7 套主题的真实 highlight.js 输出与渲染结果；另含空源码、CRLF 源码、无尾换行、未知语言（记 `threw`） |
| `auto` | 28 | `highlightAuto` 路径：无 language / **空字符串 language**（JS 真值测试会落到 auto）/ 带 subset / 空 subset |
| `formatterIds` / `supportsLanguage` | 11 | 具名格式化器 id（`upper`/`bracket`/`len`/`star`，`len` 用来暴露逐行切分）与真实 `supportsLanguage` 答案（含大小写不敏感：`Python` / `JSON` 均为 true） |

主题格式化器无法序列化，故语料存 id，两侧把同一 id 映射到同一变换。

## 测试覆盖

`tests/Pi.CodingAgent.Tests`（67 项，已禁用并行化）：

| 测试类 | 项数 | 覆盖 |
|---|---:|---|
| `UtilsCorpusTests` | 11 | `utils-corpus.json` 的 416 条 + 两条语料守卫 |
| `PathCorpusTests` | 16 | `nodePath*` / `paths*` / `fileUrlToPath` / `semver` / `versionCheck` 的路径与版本语义；含 `FileUrlToPath_DivergencesAreAsDocumented` 双向断言与 `CwdScope` 回放 |
| `CoreUtilsCorpusTests` | 18 | 其余全部区段：semver、版本检查、剪贴板、config、shell、cross-spawn 转义（含 448 条随机扫描） |
| `ChangelogCorpusTests` | 6 | `git-corpus.json` 的 changelog 区段 |
| `GitCorpusTests` | 2 | `git-corpus.json` 的 `hostedGitInfoFromUrl`（92 条）与 `parseGitUrl`（107 条）区段 |
| `ToolsCorpusTests` | 7 | `tools-corpus.json` 的全部区段：`toNamespacedPath`、`getLatestVersion`、`getToolPath`、`ensureTool`、离线判定、`WindowsSelfUpdate` 布局回放（含清理缺失路径） |
| `ZipCorpusTests` | 2 | `zip-corpus.json` 的 12 组归档：逐字段结构比对 + BCL `ZipArchive` 读 Node 归档 |
| `SyntaxHighlightCorpusTests` | 5 | `syntax-highlight-corpus.json` 的 323 条：手工 HTML、真实 highlight.js 输出、`highlight` 的委派与分支选择、`supportsLanguage` 委派、plaintext 回退与真实 highlight.js 等价 |
