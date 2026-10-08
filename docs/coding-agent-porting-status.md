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
| **4a** | `src/utils/*`（37 文件）+ 无依赖的根级模块（`config.ts` / `migrations.ts` / `core/defaults.ts` 等） | ~4,500 | 🚧 进行中（12/37 utils） |
| **4b** | 配置 / 信任 / 模型层：settings-manager、trust-manager、project-trust、auth-storage、model-config/registry/resolver、models-store、radius、virtual-models、mcp-servers、keybindings | ~12,000 | ⏳ |
| **4c** | 工具系统：`core/tools/*` + `core/tools/renderers/*` | ~9,000 | ⏳ |
| **4d** | 扩展系统：`core/extensions/*`（types / runner / loader）+ `extensions/*`（codemode / llama / mcp / tool-search） | ~12,000 | ⏳ |
| **4e** | 会话与资源：agent-session、session-manager、resource-loader、package-manager、compaction、export-html、system-prompt、telemetry、sdk | ~20,000 | ⏳ |
| **4f** | 模式层：`modes/rpc/*`、`modes/interactive/*`（含 7,082 行的 `interactive-mode.ts` 与 components / theme） | ~23,700 | ⏳ |
| **4g** | 入口与实验层：`cli/*`、`main.ts`、`cli.ts`、`rpc-entry.ts`、`package-manager-cli.ts`、`bun/*`、`client/*`、`experimental/*` | ~12,000 | ⏳ |

## 4a 进度（utils 层）

### ✅ 已完成

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

C# 侧另有三个「JS 语义」辅助（TS 无对应文件，供全部子阶段复用）：

| 文件 | 作用 |
|---|---|
| `Utils/JsRegex.cs` | JS `\s` / `\d` 字符类常量 + `Number.parseInt` 的忠实实现（0x 前缀剥离、大整数单次正确舍入，见差异 C7） |
| `Utils/ProcessInfo.cs` | `process.platform` / `process.arch` 的 Node 拼写映射（保持用户可见字符串不变） |
| `Utils/Chalk.cs` | chalk 的 16 色子集：嵌套重开（`close` → `close+open`）与 CRLF 感知的逐行包裹 |

### ⏳ 待移植

`utils/` 余下 25 个文件（约 2,900 行），按依赖分组：

| 分组 | 文件 | 备注 |
|---|---|---|
| 路径 / 进程 | `paths.ts`、`child-process.ts`、`shell.ts`、`fs-watch.ts` | 依赖 `config.ts` 的 `getBinDir()`；进程树杀死与 stdio 宽限同 durable 的 `Env` 层处理 |
| Git / 变更日志 | `git.ts`、`changelog.ts`、`version-check.ts` | git 用 `Process` 调外部命令 |
| 剪贴板 | `clipboard.ts`、`clipboard-command.ts`、`clipboard-image.ts` | 平台命令分派 |
| 图像处理 | `photon.ts`、`image-convert.ts`、`image-process.ts`、`image-resize.ts`、`image-resize-core.ts`、`image-resize-worker.ts`、`exif-orientation.ts`、`tool-result-images.ts` | 依赖 `@silvia-odwyer/photon-node`（Rust/wasm）。需选型：托管图像库（SkiaSharp / ImageSharp）或注入点外置 |
| 其他 | `frontmatter.ts`、`zip.ts`、`syntax-highlight.ts`、`html.ts`✅、`tools-manager.ts`、`management-http.ts`、`windows-self-update.ts`、`abort.ts`✅ | `frontmatter` 依赖 `yaml`、`syntax-highlight` 依赖 `highlight.js`、`zip` 需归档库 |

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

## 差分验证（utils 层）

`tests/Pi.CodingAgent.Tests/utils-corpus.json` 由 `tools/gen-coding-agent-utils-corpus.mjs`
驱动**原始 TypeScript 实现**实跑生成（`tools/` 已 gitignore）。当前 **409 条**向量：

| 区段 | 条数 | 内容 |
|---|---:|---|
| `stripJsonComments` | 23 | 注释 / 尾随逗号 / 字符串内的 `//` 与 `,}` / 嵌套 / 未闭合字符串 |
| `text` | 7 | BOM 的有无、重复、非 BOM 的零宽字符 |
| `decodeHtmlEntity` | 48 | 具名实体、十进制 / 十六进制（含大小写前缀）、符号、越界、NaN、孤立代理项 |
| `decodeHtmlEntityAt` | 19 | 分号缺失 / 距离超 16 / 连续实体 / 越界索引 |
| `stripAnsi` | 23 | CSI / OSC（BEL、ESC\\、0x9C 三种终止符）/ 8 位 CSI / 真彩 / 超链接 / 多行 |
| `parseInt` | 110 | 2 个 radix × 55 串：空白集、符号、`0x` 前缀、小数、指数、非法字符、17–400 位大整数 |
| `chalk` | 150 | 10 种样式 × 15 个文本：空串、多行、CRLF、嵌套、已有 close、Unicode |
| `deprecation` | 4 → 3 | 去重语义 + chalk 包裹的确切输出行 |
| `mime` | 29 | JPEG（含 JFIF-F7 特例）/ PNG（含 APNG、坏 chunk 长度）/ GIF / WebP / BMP（DIB 12/40/124/125、planes、bits） |

生成器还带一条守卫：任何会被记成 JSON 字符串的字段若含**孤立代理项**就直接抛错，避免
`System.Text.Json` 在 C# 侧报出难懂的解析失败。

## 测试覆盖

`tests/Pi.CodingAgent.Tests`（11 项，已禁用并行化）：

| 测试 | 覆盖 |
|---|---|
| `UtilsCorpusTests.StripJsonComments_MatchesTypeScriptReference` | 23 条 |
| `UtilsCorpusTests.TextBom_MatchesTypeScriptReference` | 7 条（`splitBom` + `stripBom` 双断言） |
| `UtilsCorpusTests.DecodeHtmlEntity_MatchesTypeScriptReference` | 48 条 |
| `UtilsCorpusTests.DecodeHtmlEntityAt_MatchesTypeScriptReference` | 19 条 |
| `UtilsCorpusTests.StripAnsi_MatchesTypeScriptReference` | 23 条 |
| `UtilsCorpusTests.ParseInt_MatchesTypeScriptReference` | 110 条 |
| `UtilsCorpusTests.Chalk_MatchesTypeScriptReference` | 150 条（另断言禁用时原样返回） |
| `UtilsCorpusTests.Deprecation_MatchesTypeScriptReference` | 去重 + 输出行 |
| `UtilsCorpusTests.Mime_MatchesTypeScriptReference` | 29 条 |
| `UtilsCorpusTests.CorpusIsComplete` | 各分区条数守卫 |
| `UtilsCorpusTests.CorpusCoversTheInterestingOutcomes` | 覆盖分布守卫（字符串内注释、越界码点、8 位 CSI、CRLF、BMP DIB 边界…） |
