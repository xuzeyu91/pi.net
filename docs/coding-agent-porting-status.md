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
| **4a** | `src/utils/*`（37 文件）+ 无依赖的根级模块（`config.ts` / `migrations.ts` / `core/defaults.ts` 等） | ~4,500 | 🚧 进行中（21/36 utils + `config.ts`） |
| **4b** | 配置 / 信任 / 模型层：settings-manager、trust-manager、project-trust、auth-storage、model-config/registry/resolver、models-store、radius、virtual-models、mcp-servers、keybindings | ~12,000 | ⏳ |
| **4c** | 工具系统：`core/tools/*` + `core/tools/renderers/*` | ~9,000 | ⏳ |
| **4d** | 扩展系统：`core/extensions/*`（types / runner / loader）+ `extensions/*`（codemode / llama / mcp / tool-search） | ~12,000 | ⏳ |
| **4e** | 会话与资源：agent-session、session-manager、resource-loader、package-manager、compaction、export-html、system-prompt、telemetry、sdk | ~20,000 | ⏳ |
| **4f** | 模式层：`modes/rpc/*`、`modes/interactive/*`（含 7,082 行的 `interactive-mode.ts` 与 components / theme） | ~23,700 | ⏳ |
| **4g** | 入口与实验层：`cli/*`、`main.ts`、`cli.ts`、`rpc-entry.ts`、`package-manager-cli.ts`、`bun/*`、`client/*`、`experimental/*` | ~12,000 | ⏳ |

## 4a 进度（utils 层）

### ✅ 已完成（21/36 个 utils 代码文件 + 根级 `config.ts`）

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
| `config.ts`（根级） | 656 | `Config.cs` | 安装方式判定、自更新命令拼装、分享链接、资产路径。`ConfigSeams` 集中 9 个注入点 |

C# 侧另有若干「JS 语义」辅助（TS 无对应文件，供全部子阶段复用）：

| 文件 | 作用 |
|---|---|
| `Utils/JsRegex.cs` | JS `\s` / `\d` 字符类常量 + `Number.parseInt` 的忠实实现（0x 前缀剥离、大整数单次正确舍入，见差异 C7） |
| `Utils/ProcessInfo.cs` | `process.platform` / `process.arch` 的 Node 拼写映射（保持用户可见字符串不变） |
| `Utils/Chalk.cs` | chalk 的 16 色子集：嵌套重开（`close` → `close+open`）与 CRLF 感知的逐行包裹 |
| `Utils/NodeError.cs` | `INodeError { string? Code }` + `NodeIoException`——Node 的 `err.code`（`ECONNREFUSED`、`ERR_INVALID_URL`…）在 C# 侧的落点 |
| `Utils/Semver.cs` | semver 7.8.5 的 `valid` / `parse` / `compare`；把 `internal/re.js` 的 `MAX_LENGTH`/`MAX_SAFE_INTEGER` 安全边界原样搬进正则 |
| `Utils/NodePath.cs` | `lib/path.js` 的逐行移植（含 `\\.\`/`\\?\` 设备根、UNC 根、Windows 保留名、CVE-2024-36139 补丁块） |
| `Utils/FileUrl.cs` | `fileURLToPath` 的 WHATWG 解析器（scheme/authority/path 三态、IPv4 归一化、禁止域名码点） |
| `Utils/JsUri.cs` | JS 的 `encodeURI` / `encodeURIComponent`。**不能**用 `Uri.EscapeDataString`：未转义集合不同，且两者都不保留 `%`（`encodeURI("%41")` 是 `"%2541"`） |

### ⏳ 待移植

`utils/` 余下 **15 个文件 / 2,206 行**：

| 分组 | 文件 | 行数 | 备注 |
|---|---|---:|---|
| Git | `git.ts` | 226 | 依赖 `hosted-git-info`（658 行纯字符串库）。**需移植**：`parse-url.js`（WHATWG `new URL()` 子集）+ `from-url.js` + `hosts.js` 的 `extract`/`protocols`/`domain`；模板函数无人调用可略 |
| 图像处理 | `clipboard-image.ts`、`exif-orientation.ts`、`image-convert.ts`、`image-process.ts`、`image-resize-core.ts`、`image-resize-worker.ts`、`image-resize.ts`、`photon.ts`、`tool-result-images.ts` | 1,265 | 依赖 `@silvia-odwyer/photon-node`（Rust/wasm）。**需选型**：托管图像库（SkiaSharp / ImageSharp）或注入点外置 |
| 工具管理 | `tools-manager.ts`、`windows-self-update.ts` | 484 | 依赖 `config.ts`（已完成）与子进程 |
| 需第三方库 | `frontmatter.ts`、`syntax-highlight.ts`、`zip.ts` | 331 | 分别依赖 `yaml`、`highlight.js`、归档库；**需选型** |

另 `utils/highlight-js.d.ts` 是纯类型声明，无运行时代码，不需要移植。

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

## 差分验证（utils 层）

两份语料都由**原始 TypeScript 实现**实跑生成（生成器在 `tools/`，该目录已 gitignore）：

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
| `hostedGitInfoFromUrl` | 92 | hosted-git-info 的 `fromUrl`：github / gitlab / bitbucket / gist / sourcehut、短写、凭据、端口、百分号编码、畸形输入。**待消费**（`git.ts` 那一批） |
| `parseGitUrl` | 107 | `git.ts` 的 `parseGitUrl`：`git:` 前缀的各种历史短写、显式协议、scp 形式、`#ref`、不安全片段（`..`、反斜杠、前导 `/`、`%00`）。**待消费** |
| `normalizeChangelogLinks` | 52 | 相对链接改写、`blob`/`tree` 判定、浮动分支改写到 tag、旧仓库改名、编码 |
| `parseChangelog` | 19 | 版本头形态（`## x.y.z` / `## [x.y.z]` / `## v…`）、无版本头截断、CRLF、缺失尾换行 |
| `parseChangelogMissing` / `parseChangelogUnreadable` | 2 | 不存在的路径 / 目录（后者走 catch 分支） |
| `compareChangelogVersions` | 7 | 三元比较 |
| `getNewChangelogEntries` | 13 | `lastVersion` 畸形时的 `Number() \|\| 0` 语义 |

## 测试覆盖

`tests/Pi.CodingAgent.Tests`（51 项，已禁用并行化）：

| 测试类 | 项数 | 覆盖 |
|---|---:|---|
| `UtilsCorpusTests` | 11 | `utils-corpus.json` 的 416 条 + 两条语料守卫 |
| `PathCorpusTests` | 16 | `nodePath*` / `paths*` / `fileUrlToPath` / `semver` / `versionCheck` 的路径与版本语义；含 `FileUrlToPath_DivergencesAreAsDocumented` 双向断言与 `CwdScope` 回放 |
| `CoreUtilsCorpusTests` | 18 | 其余全部区段：semver、版本检查、剪贴板、config、shell、cross-spawn 转义（含 448 条随机扫描） |
| `ChangelogCorpusTests` | 6 | `git-corpus.json` 的 changelog 区段（`git.ts` 与 `hostedGitInfoFromUrl` 区段待下一批消费） |
