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
| **4a** | `src/utils/*`（37 文件）+ 无依赖的根级模块（`config.ts` / `migrations.ts` / `core/defaults.ts` 等） | ~4,500 | ✅ utils 36/36 + `config.ts` 完成（2026-10-09） |
| **4b** | 配置 / 信任 / 模型层：settings-manager、trust-manager、project-trust、auth-storage、model-config/registry/resolver、models-store、radius、virtual-models、mcp-servers、keybindings | 6,844（实测） | ✅ 16/16 文件（6,844 行）完成（2026-10-09） |
| **4c** | 工具系统：`core/tools/*` + `core/tools/renderers/*` | ~9,000 | ✅ 8/8 工具 + 6 个支撑文件完成（2026-10-09）；renderers 的 renderCall/renderResult 依赖 Theme，随 4f 落地 |
| **4d** | 扩展系统：`core/extensions/*`（types / runner / loader）+ `extensions/*`（codemode / llama / mcp / tool-search） | ~12,000 | ⏳ 4d-1 契约层 + 4d-2a 事件总线 + 4d-2b 加载器完成（2026-10-10）；4d-3 加载器实现 / runner 待办 |
| **4e** | 会话与资源：agent-session、session-manager、resource-loader、package-manager、compaction、export-html、system-prompt、telemetry、sdk | ~20,000 | ⏳ |
| **4f** | 模式层：`modes/rpc/*`、`modes/interactive/*`（含 7,082 行的 `interactive-mode.ts` 与 components / theme） | ~23,700 | ⏳ |
| **4g** | 入口与实验层：`cli/*`、`main.ts`、`cli.ts`、`rpc-entry.ts`、`package-manager-cli.ts`、`bun/*`、`client/*`、`experimental/*` | ~12,000 | ⏳ |

## 4a 进度（utils 层）

### ✅ 已完成（36/36 个 utils 代码文件 + 根级 `config.ts`，4a 收口）

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
| `utils/frontmatter.ts` | 40 | `Utils/Frontmatter.cs` | `parseFrontmatter` / `stripFrontmatter`：定界符处理逐行移植（仅识别起始 `---`、首个 `\n---` 收口、空块短路），YAML 走 YamlDotNet（差异 C25），936 条差分语料 |
| `utils/exif-orientation.ts` | 183 | `Utils/Image/ExifOrientation.cs` | 字节级 EXIF 解析逐行移植（JPEG APP1 扫描跳过 XMP 段、WebP RIFF EXIF 块含 `Exif\0\0` 前缀、TIFF IFD tag 0x0112）；`rotate90` 按原 dstIndex 映射做像素拷贝并交换宽高，fliph/flipv 原地翻转（差异 C26/C28） |
| `utils/photon.ts` | 140 | `Utils/Image/SkiaImage.cs` | photon wasm 模块的 SkiaSharp 等价层：`new_from_byteslice` → 解码钉死 Rgba8888/Unpremul、`get_bytes` → PNG 编码、`get_bytes_jpeg(quality)` → JPEG 编码、`resize(..., Lanczos3)` → `SKPixmap.ScalePixels` + CatmullRom。wasm 路径烘焙垫片与 `.node` 隔离不适用（差异 C26） |
| `utils/image-convert.ts` | 90 | `Utils/Image/ImageConvert.cs` | `convertToPng`（PNG 透传，其余解码→EXIF→重编码）、`loadPngTranscoder`（pi-tui 的 `ImageTranscoder` 委托）、`ensurePngTranscoder`（Kitty 能力检查 + 单次注册 + 注册后回调） |
| `utils/image-resize-core.ts` | 165 | `Utils/Image/ImageResizeCore.cs` | 缩放算法全量：尺寸限额 → PNG/JPEG 质量阶梯（去重保序）取首个达标者 → 0.75 渐进收缩至 1×1；`Math.round` 语义钉 AwayFromZero（差异 C28） |
| `utils/image-resize.ts` | 124 | `Utils/Image/ImageResize.cs` | worker 编排 → 线程池卸载（差异 C27）；`formatDimensionNote` 的 `toFixed(2)` 语义 |
| `utils/image-resize-worker.ts` | 43 | — | worker 入口文件：只做「消息端口后面跑 `resizeImageInProcess`」，C# 由 `Task.Run` 承载，无对应物（差异 C27） |
| `utils/image-process.ts` | 120 | `Utils/Image/ImageProcess.cs` | `processImage`：归一（png/jpeg+jpg/gif/webp）→ 转换提示 → 自动缩放 + 维度注记；不可转换/不可缩放的省略文案逐字保留 |
| `utils/clipboard-image.ts` | 241 | `Utils/Image/ClipboardImage.cs` | 类名取 `ClipboardImageApi`（与 payload record `ClipboardImage` 撞名）。三态探测（TS undefined=后端失败继续回退 / null=无图像停止）→ `ImageProbe`；wl-paste / xclip / PowerShell(wsl) / 原生剪贴板全矩阵；BMP 等不支持格式转 PNG（不做 EXIF，与 TS 同）；命令与原生剪贴板经注入缝（差异 C29/C31） |
| `utils/tool-result-images.ts` | 68 | `Utils/Image/ToolResultImages.cs` | 工具产出图像块归一：无变化返回原数组实例、不可处理保留原块、提示落 text 块；`ModelImageResizeOptions` → `ImageResizeOptions` 显式桥接（差异 C30）。`Buffer.from(x,"base64")` 宽容解码见差异 C29 |
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

**4a 无剩余文件**（36/36 个 utils 代码文件 + 根级 `config.ts`，2026-10-09 收口）。图像批次的
`photon.ts` 不逐行移植（Bun wasm 路径烘焙垫片，.NET 的 SkiaSharp NuGet 走正规原生资产分发，差异 C26）；
`utils/highlight-js.d.ts` 是纯类型声明，无运行时代码，不需要移植。

> 计数口径：图像处理 1,165 行 + frontmatter 40 行 = 1,205 行，与 `wc -l` 实测一致。

## 4b 进度（配置 / 信任 / 模型层）

### ✅ 已完成（16/16 文件，6,844 行）

| TS 文件 | 行数 | .NET | 说明 |
|---|---:|---|---|
| `core/settings-manager.ts` | 1,533 | `Core/SettingsManager.cs`（1,555）+ `Core/Settings.cs` / `Core/ResolveConfigValue.cs` / `Core/PiManifest.cs` / `Core/Defaults.cs` | 全局/项目分层设置、逐字段 dirty 跟踪、串行写队列、每次文件操作都加锁。三层内部保持 `JsonObject`（与 TS 保留 plain object 一致），深合并 / 已改字段回放到磁盘文件 / 畸形值宽容这三件事才等价；具名 `Settings` 只出现在公开边界。写队列见差异 C46。 |
| `core/trust-manager.ts` | 246 | `Core/TrustManager.cs` | 项目信任的授予/撤销/查询，`NodeLock` 文件锁（`Core/NodeLock.cs`）。 |
| `core/project-trust.ts` | 96 | `Core/ProjectTrust.cs` | 信任状态判定（受信根、子路径继承、显式拒绝）。 |
| `core/auth-storage.ts` | 506 | `Core/AuthStorage.cs` | `ModeFile`（`0o600`/`0o700`，仅非 Windows 生效）、`FileAuthStorageBackend`、`ReadOnlyAuthStorage`、`InMemoryAuthStorageBackend`、`AuthStorage`（共享读取状态 + reload 去重 + readers 计数）。 |
| `core/model-config.ts` | 346 | `Core/ModelConfig.cs`（595）+ `Core/JsonSchema.cs` | `models.json` 的 schema 校验与具名快照。schema 用**手写 JSON-schema 树**替代 TypeBox `Compile()`（差异 C38）；错误路径格式化（`required` 特例 + `instancePath` → 点分路径）与 TypeBox 的 `formatValidationPath` 同形。**已修缺陷**：首版迭代 JSON 根对象而非 `config.providers`，使整份 `models.json` 静默失效（见「测试覆盖」）。 |
| `core/models-store.ts` | 147 | `Core/ModelsStore.cs` | `InMemoryCodingAgentModelsStore` / `FileModelsStore`（文件后端以**解析后的 JSON 对象**为准，忠实复刻 TS 的整体 parse→re-serialize，未识别条目不丢）；配合 Pi.Ai 的 `ModelSpecJson` 完成「模型对象 ↔ JSON」往返。 |
| `core/radius.ts` | 11 | `Core/Radius.cs` | `ProviderId = "radius"`、`PI_RADIUS_GATEWAY`、`McpUrl`、可注入 `Env`。 |
| `core/virtual-models.ts` | 238 | `Core/VirtualModels.cs` | 虚拟模型路由：`VirtualModels`（`Api = "pi-virtual"`、`StateEntry`、`CreateVirtualModel`、`WithVirtualModels`、`UnroutedStream`、`RouteReasons`）、`VirtualModelDefinition`、`VirtualOnlyProvider`、`VirtualModelsProvider`。router state 见差异 C40；`findLatestResponse` 已按 `ChatMessage` 重载移植（`ModelRuntime` 的路由需要它），`getBranchSelection` / `getVirtualModelState` 仍待 4e（gap **G-4b-VM**）。 |
| `core/mcp-servers.ts` | 319 | `Core/McpServers.cs` | 暴露面常量与别名（`exposures` / `allExposures` / `exposureAliases`）、`Namespace`、`IsLoopbackRedirectUri`、配置校验、OAuth 校验、别名解析、`McpServerRegistry`。`McpServerConfig` 见差异 C41。 |
| `core/keybindings.ts` | 401 | `Core/Keybindings.cs` | 键位绑定解析/校验/冲突检测与用户覆盖。 |
| `core/runtime-credentials.ts` | 52 | `Core/RuntimeCredentials.cs` | 非持久化运行时 API key 的凭据存储叠加层（`--api-key` 路径）。override 用有序列表复刻 JS `Map` 语义（差异 C49）。 |
| `core/remote-catalog-provider.ts` | 158 | `Core/RemoteCatalogProvider.cs` | 给静态内置 provider 叠加持久化的 pi.dev 目录：三态目录体（数组 / `{models:[]}` / 对象 map）、新鲜度窗口、ETag 条件请求、404/501 与瞬时失败的差异化持久化策略。TS 用对象展开包装 provider，C# 显式转发全部成员并实现 `IImagesProvider` / `IClassifierProvider`（差异 C48）。 |
| `core/provider-composer.ts` | 732 | `Core/ProviderComposer.cs`（1,213）+ `Core/ProviderConfig.cs`（190） | 组合内置 / `models.json` / 扩展三层，产出不读凭据的 `IProvider`。公开面：`ComposeModelProvider`、`ValidateExtensionProvider`、`ResolveConfiguredModelHeaders`、`ResolveCompatibilityRequestConfig`、`ConfiguredRequestAuthStatus`、`ClearApiKeyCache`；类型面：`ProviderModelConfig`（抽象基类 + chat/image/classifier 三态）、`ProviderConfigInput`、`ExtensionOAuthConfig`、`AuthStatus` / `AuthStatusSource`。差异 C50–C56。 |
| `core/model-runtime.ts` | 1,032 | `Core/ModelRuntime.cs`（约 1,180） | 配置 pi-ai `Models` 集合、组合 provider（内置 → `models.json` → 扩展 → 虚拟模型）、维护可用性快照、串行化凭据操作、虚拟模型路由与错误上报。`Models` 在 C# 是 sealed class（非接口），故改为**组合**一个 `Models` 实例而非继承（差异 C57）；其余差异 C58–C61。 |
| `core/model-registry.ts` | 244 | `Core/ModelRegistry.cs`（约 300）+ `ResolvedRequestAuth` | 面向扩展的**同步兼容门面**：全部成员转发到 `ModelRuntime`，自身不持状态。手写部分只有两处——目录访问器返回副本（对齐 TS 的展开），以及 `getApiKeyAndHeaders` 的四条分支（已配置认证 / 未配置时回落到兼容头 / `authHeader` 缺 key 报错 / 把组合器抛出的 `authHeader requires a resolved API key` 翻译成用户可见文案）。差异 C62–C65。 |
| `core/model-resolver.ts` | 783 | `Core/ModelResolver.cs`（约 780）+ Pi.Ai 的 `ModelOperations.ModelsAreEqual` | 模型串解析：`defaultModelPerProvider`（41 项，`Object.keys` 顺序 load-bearing，用有序 `KeyValuePair` 列表承载）、`findExactModelReferenceMatch`、`parseModelPattern`（严格/宽松两模式，`:thinking` 后缀）、`resolveModelScopeFromModels`（glob + 大小写折叠 + `modelsAreEqual` 去重）、`resolveCliModel`、`findInitialModel`、`restoreModelFromSession`、`resolveModelScope`。新增 `IModelResolverRuntime` 把 resolver 实际读到的五个成员切片出来（差异 C81）。差异 C74–C81。 |
| 根级 `migrations.ts` | 315 | `Migrations.cs` | 启动时一次性迁移（auth → auth.json、会话目录、commands → prompts、keybindings、tools → bin、扩展系统）。TS 归 4a 的「根级模块」，随 4b 一并收口。差异 C42。 |

### minimatch 依赖（`Utils/Glob/`，为 `model-resolver` 而移植）

`core/model-resolver.ts` 用 `minimatch(fullId, globPattern, { nocase: true })` 解析用户的模型通配，而 C# 没有等价物。这是**面向用户**的能力（`docs/cli.md` 承诺大小写不敏感的 glob），近似实现会静默改变用户可见行为，因此按 `minimatch@10.2.6` 逐行移植，并用真实包采出的差分语料锁定（见「差分验证」）。

| .NET | 对应 JS | 说明 |
|---|---|---|
| `Utils/Glob/Glob.cs` | `index.js` 的导出面 + `escape.js` / `unescape.js` / `assert-valid-pattern.js` | `GlobOptions`、`Match(p, pattern, options)`、`Filter`、`MakeRe`、`MatchList`、`BraceExpand`、`Escape` / `Unescape`、`AssertValidPattern`、`Sep`、`GlobStar`。 |
| `Utils/Glob/Minimatch.cs` | `index.js` 的 `Minimatch` 类 | `make()` 全流程、`preprocess` 五件套（`adjascentGlobstarOptimize` / `levelOneOptimize` / `levelTwoFileOptimize` / `firstPhasePreProcess` / `secondPhasePreProcess`）、`parseNegate`、`parse`（含 8 个 fast-test 短路）、`makeRe`、`slashSplit`、`match`、`matchOne` / `matchGlobstar` / `matchGlobStarBodySections`。 |
| `Utils/Glob/GlobAst.cs` | `ast.js` | `#parts`（`string \| AST` 联合）、`#flatten`（adopt / adoptWithSpace / usurp，最多 10 轮）、`#fillNegs`（把 `!` extglob 的尾部复制进后续兄弟，使 `!(a)b` ≡ `!(a\|ab)`）、`#parseAST`、`#parseGlob`、`#partsToRegExp`、`toMMPattern`。 |
| `Utils/Glob/GlobClass.cs` | `brace-expressions.js` | `parseClass` 与 POSIX 类表。**插入顺序 load-bearing**：`[[:alpha:]]` 与 `[[:alnum:]]` 的前缀关系决定了谁先命中。 |
| `Utils/Glob/BraceExpansion.cs` | `brace-expansion@5` | `{a,b}` 集合与 `{1..5}` 序列，含 Bash 的 `{a},b}` 重启怪癖与四个 DoS 上限（`max` / `maxLength` / `maxDepth` / `maxRewrites`）。 |
| `Utils/Glob/BalancedMatch.cs` | `balanced-match` | 最外层成对定界符定位；`brace-expansion` 用它走 `{...}`。 |

> `windowsPathsNoEscape` 的取值链值得单独记一笔：`Minimatch` 把它算成 `!!options.windowsPathsNoEscape \|\| options.allowWindowsEscape === false`（上游用 `'allowWindow' + 'sEscape'` 拼出已废弃的键名来绕开类型检查），而 `escape` / `unescape` 只读前者。平台默认取 `process.platform`（可被 `__MINIMATCH_TESTING_PLATFORM__` 覆盖，本移植沿用该覆盖点以便在任何宿主上跑 Windows 分支）。
> `brace-expansion` 的哨兵串由 `Math.random()` 改为定值 NUL 字面量；`NumericToString` 只覆盖 JS 打印为纯数字的整数范围（`|n| < 1e21`），指数形态不复制（glob 不可达）。另：上游注释称 `{},a}b` 会「展开成空」，但实现返回原串——本移植跟实现走。

### Pi.Ai 侧补齐（4b 的运行时依赖）

`core/model-runtime.ts` 依赖 Pi.Ai 的 `Models` 运行时层，而此前 Pi.Ai 只移植了编排核心与图片/分类分发。本阶段补齐：

| .NET | 内容 |
|---|---|
| `Pi.Ai/Models/ModelsRefresh.cs` | `ModelsPublication`（`PersistDeleted` / `Persist` / `Update`，用两个成员替代 TS 的三态 `persist`）、`RefreshModelsContext`、`ModelsRefreshOptions`、`ModelsRefreshResult`。 |
| `Pi.Ai/Models/ModelsAuth.cs`（`Models` 的第二个 partial） | `CreateModelsOptions`、`ModelsAuthOverrides`、`Credentials` / `Store`、刷新代次与控制器（`SupersedeProviderRefresh` / `BeginProviderRefresh`）、带代次校验的串行发布（`PublishProviderModelsAsync`）、`RefreshAsync`（先离网恢复缓存再联网）、`CheckAuthAsync` / `GetAuthenticatedProvidersAsync`、`GetAvailable*`、`GetAuthAsync(providerId)` / `GetAuthAsync(model)`、`LoginAsync` / `LogoutAsync`、`MergeHeaders` / `MergeEnv` / `ApplyAuthAsync`。差异 C43 / C44 / C45。 |
| `Pi.Ai/Models/ModelsAuth.cs`（`ModelRuntime` 用到的补口） | `ModelsAuthOverrides.MinOAuthValidityMs` 新增并透传进 `AuthResolutionOverrides`——`ModelRuntime` 的 `minOAuthValidityMs` 选项此前没有落点（`GetAuthAsync` 只能透传 `ApiKey` / `Env` / `Signal`）。 |
| `Pi.Ai/Models/ModelsRequestOptions.cs` | `ModelsImagesOptions` / `ModelsClassifierOptions`：`ImagesOptions` / `ClassifierOptions` 的子类型，各带 `TransformHeaders`。TS 侧是 `ModelsImagesOptions = ImagesOptions & ModelsRequestTransforms`，而 C# 的 provider 边界类型是非 nullable 的具体类，结构类型无法就地扩展，故用子类型承载。 |
| `Pi.Ai/Models/ModelSpecJson.cs` | `ModelSpec` ↔ JSON 往返（TS 里「模型对象本身就是 JSON」，C# 是具名记录，需要显式映射）。 |
| `Pi.Ai/Models/Models.cs`（改） | `Stream` / `StreamSimple` / `StreamDeferred` 改经 `LazyStream.Run` **延迟执行**（修正原实现的同步抛出——TS 的 `models.stream()` 同步返回流、把错误编码成 error 终态）；`IProvider` 增 `RefreshModels` / `FilterAllModels` / `SupportsFetchDeferred` / `SupportsCancelDeferred`（差异 C47）；`SetProvider` / `DeleteProvider` / `ClearProviders` 现在会作废进行中的刷新。**另修一处真实缺陷**：`GetModelsOfType` 误用 chat-only 的 `SafeModels`（TS 是 `getAllModels(provider).filter(isModelType)`），见「测试覆盖」。 |
| `Pi.Ai/Auth/AuthResolve.cs`（改） | 抽出 `RefreshStoredOAuthCredentialAsync`；语义修正：**已开始的刷新不随调用方 signal 取消**，只受超时约束（保证轮换的 refresh token 一定落盘）。 |
| `Pi.Ai/Utils/AbortSignals.cs`（改） | 新增 `RaceWithAsync<T>` 与非泛型重载 + `Observe(Task)`（被放弃的操作仍被观察，不留未观察异常）。 |

### 测试覆盖

`tests/Pi.CodingAgent.Tests/CoreRuntimeTests.cs`（19 项）：`RuntimeCredentials` 的覆盖/回落/列表顺序/取消语义；`RemoteCatalogProvider` 的空叠加、类型+id 合并与顺序、`localGeneratedAt` 门槛、发布被取代、新鲜度窗口跳过、强制刷新的 `?types=` 与 `If-None-Match`、304 / 404 / 瞬时失败三条分支、三种目录体形状、畸形目录报错、能力转发。HTTP 经 `ManagementHttp.HandlerOverride` 注入（`InternalsVisibleTo` 已开）。

`tests/Pi.CodingAgent.Tests/CoreProviderComposerTests.cs`（39 项）：TS 侧 `provider-composer` 的辅助函数全部未导出，没有可复刻的参考用例，因此断言针对 `ComposeModelProvider` 的**端到端可观察行为**——五层叠加顺序（内置 → models.json baseUrl/compat → 自定义模型 upsert → 扩展整体替换 → OAuth `modifyModels`（chat-only）→ 顶层 `modelOverrides`）、`streamSimple` 的 api 分流与 `getApiProvider` 回退（含未注册 api 的 error 终态）、`refreshModels` 的**条件存在性**与「先校验再发布」、取消时跳过发布、图片/分类的「扩展 → base → error 结果」优先序、api-key/OAuth 组合与 `authHeader` 约束、三层请求头合并、`configuredRequestAuthStatus` 的六种来源、以及全部结构性错误文案。

`tests/Pi.CodingAgent.Tests/CoreModelRuntimeTests.cs`（62 项）：`model-runtime.ts` 未导出任何辅助函数，同样只能做端到端断言。覆盖 `CreateAsync` 的门面（42 个内置 provider、`models.json` 加载、`PI_OFFLINE` 的**存在性**语义）、注册/重注册/反注册（native ↔ extension 互斥、`RegisterProvider` 的逐字段合并、校验先于写入）、虚拟模型（空 id、与物理模型冲突、纯虚拟 provider 立即 configured、注销）、`GetProviderAuthStatus` 的五级回退（runtime → stored → models.json → environment → 未配置）、`IsUsingOAuth` / `IsUsingSubscription`、`GetAuthAsync(providerId)` / `GetAuthAsync(model)` 的模型头合并、`StreamSimple` 的认证合并（头名大小写不敏感）/ `transformHeaders`（并在请求前摘除该选项）/ `baseUrl` 覆盖 / `apiKey` 优先、分派与三类错误终态、延后能力报错、图片/分类的认证边界与 error 结果、虚拟路由（`maxTokens` 封顶、无上限时保留调用方预算、跨 provider 丢凭据 / 同 provider 透传、三类路由错误、`Previous` / `Failed` 上报）、`RefreshAsync` 的错误收集 / 取消 / 配置重载 / 组合错误消除、凭据串行化与 `CredentialSynchronizationError` 包装、`LoginAsync` / `LogoutAsync`，以及各访问器。

`tests/Pi.CodingAgent.Tests/CoreModelRegistryTests.cs`（29 项）：`ModelRegistry` 是纯转发门面，故断言分两半——每个成员是否落到同一个 `ModelRuntime` 入口，以及手写部分是否守住 TS 的控制流。覆盖目录访问器返回**副本**（`GetAll` / `GetAvailable` 不与运行时共享列表实例）、`Find` / `FindOfType` / `GetModelsOfType` / `GetModelOfType` / `GetAvailableOfTypeAsync` 的类别与凭据过滤（含 `Find` 只达 chat 模型、非 chat 类型必须走 `FindOfType` 这条 TS 语义）、`HasConfiguredAuth` / `GetProviderAuthStatus` / `GetProviderDisplayName`（未知 provider 回落成 id）/ `IsUsingOAuth`、`GetApiKeyAndHeadersAsync` 的四条分支（已配置认证含 headers；**空串 baseUrl 视作缺席**；未配置时回落到兼容头；`authHeader` 缺 key 报 `No API key found for "p"`；组合器抛出的 `authHeader requires a resolved API key` 被翻译成同一文案；其他失败保留 cause 原文）、`GetProviderAuthAsync` / `GetApiKeyForProviderAsync`（失败与未知 provider 都吞成 null）、两类 provider 与虚拟模型的注册/注销与可见性（含虚拟模型**不**进 `GetRegisteredProviderIds`）、`RefreshAsync` 重载 `models.json`、`GetError` 暴露组合错误、以及 `Stream` / `StreamSimple` / `CompleteAsync` / `ClassifyAsync` / `GenerateImagesAsync` 的分派。

> **本轮测试发现并修复的真实缺陷**：`Models.GetModelsOfType` 用的是 chat-only 的 `SafeModels`，而 TS 是 `getAllModels(provider).filter(isModelType)`。后果是 image / classifier 模型一律取不到（`classifier` 更糟——`getModels` 的默认实现会过滤掉非 chat 模型）。已按 TS 修正（改为遍历 `SafeAllModels`），`Pi.Ai.Tests` 全量 286 项仍全绿。顺带记一笔：既有用例 `GetModelsOfTypeFiltersByType` 用的替身同时实现了 `GetModels()` 与 `GetAllModels()`，而 TS 的默认 `getAllModels` 会把后者退回成前者，因此该用例本身与 TS 不符——本移植按 TS 收紧后它仍然通过，属「假阴性恰好被修正的语义掩盖」，无需改动。

> **本轮测试发现并修复的真实缺陷**：`ModelConfig.LoadAsync` 迭代的是 JSON **根对象**而非 `config.providers`（TS 为 `Object.entries(config.providers)`），导致任何 `models.json` 都解析出空 provider 表——`models` / `modelOverrides` / `headers` / `oauth` / `authHeader` 全部静默失效。已修正，并新增「无认证方式时组合出的 api-key 认证 resolve 为未配置」用例（TS 的 `no authentication method configured` 分支在两侧都实际不可达）。

`tests/Pi.CodingAgent.Tests/ModelResolverCorpusTests.cs`（9 项）：`model-resolver-corpus.json` 的七个区段逐条回放——`defaultModelPerProvider` 的 41 项顺序与逐项查找、`findExactModelReferenceMatch`（记命中索引，`-1` 表示未命中）、`parseModelPattern`（1,206 条，含空 pattern、`:thinking` 后缀、非法思考级别告警）、`resolveModelScopeFromModels`（279 条 scope）、`resolveCliModel`（1,440 条）、`findInitialModel`（24 条）、`restoreModelFromSession`（12 条）、`resolveModelScope`（含 12 条 stderr 诊断）。回放经 `FixtureRuntime`（`IModelResolverRuntime` 的鸭子实现）注入语料里的模型集与认证集，stdout/stderr 用 `Chalk.EnabledOverride` + `ModelResolver.OutWriterOverride/ErrorWriterOverride` 捕获。

`tests/Pi.CodingAgent.Tests/ModelResolverTests.cs`（28 项）：语料表达不了的部分。①`ModelRuntime` 真的满足 C81 的五个成员（`GetModels()` 恒带 42 个内置 provider——这正是抽接口的原因）；②`GetModels(provider)` 过滤仍在；③真实 runtime 上跑 `ResolveCliModel` / `FindInitialModel` / `RestoreModelFromSession`；④**「第一可用模型」回退**（语料故意省略，见「差分验证」的顺序不可复现说明）；⑤`ModelResolutionException` 携带未上色的消息；⑥别名启发式；⑦日期后缀 `-YYYYMMDD` 的四个反例（7 位 / 9 位 / 全角数字 / 带分隔符）；⑧glob 大小写折叠、去重、`modelsAreEqual` 的**类型维度**（同 id 不同类别都保留）；⑨超大 pattern；⑩stderr 一行一诊断；⑪取消传播。

> 其余 4b 文件（settings-manager / trust-manager / auth-storage / model-config / models-store / mcp-servers / keybindings / virtual-models）目前**只有编译与人工核对**，尚无自动化测试；记为待办。

## 4c 进度（工具系统）

### ✅ 已完成（8/8 工具 + 6 个支撑文件，约 5,600 行 C# + 900 行测试）

提交 `c003cf1`（已推送到 `origin/main`）。

> 推送方式说明：沙箱内 git 无凭据（`could not read Username for 'https://github.com'`），
> 本地提交后由用户在宿主机执行 `git push`。`0f59bc3`（4c 收尾修复）目前为本地提交，待推送。

| 文件 | 对应 TS | 说明 |
|---|---|---|
| `Core/Tools/Truncate.cs` | `core/tools/truncate.ts` | head/tail/middle/line 截断，JS 兼容的 UTF-8 字节计数与 `F1` 体积格式 |
| `Core/Tools/JsDiff.cs` | `diff@8.0.4` `dist/diff.js` | 移植 Myers 行 diff + `createTwoFilesPatch`（见 C86） |
| `Core/Tools/EditDiff.cs` | `core/tools/edit-diff.ts` | BOM 处理、精确/模糊多编辑、行尾探测与还原、统一 patch、带行号展示 diff |
| `Core/Tools/OutputAccumulator.cs` | `core/tools/output-accumulator.ts` | 有界流式输出，`Decoder` 复刻 `TextDecoder(stream:true)`，临时文件溢出与全量回读 |
| `Core/Tools/ToolPathUtils.cs` | `core/tools/path-utils.ts` | `~` 展开、macOS 截图文件名变体（NBSP / NFD / 弯引号） |
| `Core/Tools/FileMutationQueue.cs` | `core/tools/file-mutation-queue.ts` | 按解析后路径串行化同文件写 |
| `Core/Tools/ToolDefinitionWrapper.cs` | `core/tools/tool-definition-wrapper.ts` | `ToolDefinition` ↔ 核心 `AgentTool` 桥接 + `ToolArgs` 参数读取 |
| `Core/Tools/ReadTool.cs` / `WriteTool.cs` / `EditTool.cs` | `read.ts` / `write.ts` / `edit.ts` | 含 `operations` 注入点（远程文件系统） |
| `Core/Tools/BashTool.cs` / `PowerShellTool.cs` | `bash.ts` / `powershell.ts` | 共用 shell 执行路径，节流更新、`PI_*` 环境变量、spawn hook |
| `Core/Tools/GrepTool.cs` / `FindTool.cs` / `LsTool.cs` | `grep.ts` / `find.ts` / `ls.ts` | ripgrep / fd 子进程 + 截断与限额提示 |

**顺带修复的真实缺陷**：

1. `ChildProcess.cs` 事件重放解引用了可空委托（CS8602，`f78608b` 遗留）。
2. `AgentTool` 没有 `PromptSnippet` / `PromptGuidelines`，导致包装后的工具丢失 TS 用 `Object.assign` 保留的提示元数据。
3. `.gitignore` 的 `tools/` 规则未锚定到仓库根，把 `src/Pi.CodingAgent/Core/Tools` 整个目录从 git 里藏掉了（已改为 `/tools/`）。

**测试**：`tests/Pi.CodingAgent.Tests/CoreToolsTests.cs`（47 项）复刻 TS `test/tools.test.ts` 的断言——read 的截断/offset/limit 分支、write 建目录、edit 的多编辑/重叠/失败不部分应用/模糊匹配、truncate 的四个入口、mutation queue 的同文件串行与跨文件并行、wrapper 桥接、edit 参数兼容垫片；另加 jsdiff/patch 对齐用例。解决方案构建 0 警告 0 错误。

### 4c 收尾修复（本轮，提交前本地验证）

`Pi.CodingAgent.Tests` 全量 432 项通过（0 失败）。本轮修了 5 个问题：

1. **`ImageTests.cs` 的 `LargePng200` 固件转录损坏**（1772 → 1588 字符，PNG 结构乱码，Skia 解码失败）：用脚本从 TS `test/image-processing.test.ts` 原值整体替换，5 个图片固件（Tiny/Medium/Large/TinyJpeg/TinyJpeg2X1）现已逐一比对一致。它同时是 `ResizeImage_ResizesBeyondByteLimit` 与 `OversizedPngIsResizedAndAnnotated` 两个失败的根因。
2. **`ClipboardImage.cs` 的 `ImageProbe.Undefined = default`**：record struct 的默认值 `IsUndefined == false`，导致「后端失败」被当成「确无图片」，linux 分支的 xclip / native 回退被跳过（10 个 ClipboardImageTests 失败）。改为 `new(null, true)`。
3. **`ImageTests.cs` EXIF 测试公式笔误**：orientation 7/8 的期望映射写成 `(2 - x) * 3 + y`，TS 实现是 `(w - 1 - x) * h + y`（w=2 时应为 `(1 - x) * 3 + y`），越界且与生产代码不一致；生产代码本身是对的。
4. **`ImageTests.cs` `CommandLog.RunAsync` 硬转型 `(string[])args`**：生产代码用集合表达式传 `IReadOnlyList<string>`，运行时不是 `string[]`（14 个 InvalidCastException）。改为 `args.ToArray()`。
5. **`Frontmatter.cs` 块标量丢失末尾换行**：yaml@2.9.0 把 EOF 当行终止，`slice(4, endIndex)` 取出的 YAML 块永不以 `\n` 结尾，故 `|` / `>` 块标量在 clip 语义下总带末尾换行；YamlDotNet 只在源码有换行时保留。`Parse` 增加 `TerminateDocument`（缺失时补一个 `\n`），对其余构造是 no-op（47 条语料用例全绿）。

**沙箱环境限制（非代码缺陷，勿追）**：本沙箱阻止 `HttpListener`（`句柄无效`），`Pi.Mcp.Tests` 4 项与 `Pi.Ai.Tests` 1 项因此失败；沙箱 `TMP` 路径超长（>108 字符）使 AF_UNIX socket 路径越界，`Pi.Server.Tests` 1 项失败。另 `Pi.Ai.Tests` 的 `BrowserLoginReceivesRealLoopbackCallback` 在回环服务器起不来时 `while (authUrl is null)` 无限空转（既有测试健壮性问题，非本轮引入）。`Pi.Durable.Tests` 的 2 项 bash 用例按设计在无 Git Bash 时跳过。

### 4d 已确定的决策（尚未实现）

1. **llama 拆分**：4d 只移植 `client.ts` / `provider.ts` / `huggingface.ts` / `index.ts`（963 行，HTTP 客户端 + 提供者注册）；`ui.ts`（503 行）依赖 4f 的 Theme 与交互组件，随 4f 落地。
2. **扩展加载走 C# 原生插件路线**（与 `migration-plan.md` 一致）：扩展为 .NET 程序集，用 `AssemblyLoadContext` 加载。功能等价，但分发形式从「丢一个 `.ts` 文件」变为「放一个 `.dll`」。

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
| C25 | **YamlDotNet 不是 `yaml@2.9.0` 的逐字节替身** | YamlDotNet 没有 schema 选择，标量解析更接近 YAML 1.1：`yes`/`no`、时间戳、前导零数字、merge key 等输入会产生不同结果。定界符处理（起始 `---`、首个 `\n---` 收口、空块短路、BOM/换行归一）逐行移植，重复键按 `yaml@2.9.0` 的语义拒绝（`WithDuplicateKeyChecking`）。分歧由 `frontmatter-corpus.json`（936 条）钉住。 |
| C26 | **photon → SkiaSharp 是编码器级替换，不是字节级移植** | 两边的行为契约（解码成功/失败、尺寸、格式选择算法、预算循环）一致，但 Skia 的 PNG/JPEG 编码器与 photon 的 Rust image crate 不逐字节相同：`encodedSize` 不同，恰好在 `maxBytes` 边界上的图像可能翻转 PNG/JPEG 选择；缩放像素值有细微差异（CatmullRom ≈ Lanczos3 类，Skia 无 Lanczos）；解码支持矩阵随 Skia codec registry。photon 的 RGBA8 unpremul 光栅形状由 `SkiaImage.Decode` 钉死（Rgba8888/Unpremul）保证旋转/翻转逐像素语义一致。 |
| C27 | **`worker_threads` → 线程池卸载** | TS 把缩放跑在 worker 里避免 WASM 阻塞 TUI 事件循环，Bun 编译产物加载失败时回退进程内。C# 没有 worker 文件概念：同一目标用 `Task.Run` 达成，「worker 加载失败」没有失败模式可复现，`image-resize-worker.ts` 无对应物。 |
| C28 | **JS `Math.round` / `toFixed` 是「半值远离零」，.NET 默认是银行家舍入** | 缩放目标尺寸的 `Math.round` 用 `MidpointRounding.AwayFromZero`（.5 比例处是真实行为分歧）；`formatDimensionNote` 的 `toFixed(2)` 用 Round(AwayFromZero)+Invariant `F2`。 |
| C29 | **`Buffer.from(x, "base64")` 是宽容解码，.NET 会抛** | JS 跳过字母表外字符（含空白）、1 mod 4 的悬尾字符丢弃、容忍缺 padding；`Convert.FromBase64String` 全都要抛。`LenientBase64.Decode` 复刻宽容前半，再交给 BCL。工具产出的 payload 不保证干净，宽容语义保证「不可解码图像 → 保留原块」而不是异常冒泡。 |
| C30 | **`ModelImageResizeOptions` 是 Pi.Ai 的名义类型，与 coding-agent 的 `ImageResizeOptions` 结构相同但语义独立** | TS 靠结构类型把模型档案直接传进 `processImage`；C# 在 `ToolResultImages` 里显式字段映射桥接，两个类型都保留（模型目录下发档位 vs 通用默认，后续 4b/4c 消费）。`types.ts` 的 `ModelInputLimits` / `ModelImageInputLimits` / `BaseModel.inputLimits` 一并补进 Pi.Ai（此前无消费者）。 |
| C31 | **C# 原生剪贴板接口把 TS 的 `undefined`/`null` 折叠成 `null`** | pi-tui TS 的 `getImage()` 三态（undefined=不可用、null=无图像、数组=有图像）；C# `INativeClipboard.GetImageAsync()` 的契约是 null=不可用、空数组=无图像。`ClipboardImageApi` 的 `ImageProbe` 保留三态语义：null→继续回退、空→停止。原生剪贴板恒为最后一站，折叠不可观察；命令行后端的三态照原样保留（Wayland 空剪贴板不得回退到陈旧 X11 内容）。 |

### 4b 阶段差异（C32–C81）

> 编号说明：C33–C36 是 4b 期间预留但最终未使用的空号（相关结论并入了 C37 与 C39），此处不跳号补位以免与代码注释失配。C66 起是 `minimatch` 移植带来的差异。

| # | 差异 | 说明 |
|---|---|---|
| C32 | **`http-dispatcher.ts` 只移植 provider 中立的半边** | TS 装 Undici 的 `EnvHttpProxyAgent` 为全局 dispatcher 并替换 `globalThis.fetch`；.NET 没有对应物——`HttpClient` 自己管连接池、代理解析与超时，coding-agent 在 `Utils/ManagementHttp.cs` 与 Pi.Ai 传输层逐 client 配置。故保留超时解析/格式化契约与代理环境变量播种，**丢弃 dispatcher 安装**。 |
| C37 | **`models-store.ts` 的条目解析提前丢弃未知 `type`** | TS 的存储原样保留 JSON，直到 `createModels` 才按已知类别过滤；C# 在 `FromJsonObject` 就丢弃（`ModelSpecJson` 需要具名记录）。已发布的 provider 都不产出未知类别，因此不可观察。 |
| C38 | **手写 JSON-schema 树替代 TypeBox `Compile()`** | `model-config.ts` 用 TypeBox 编译 `models.json` 的 schema 并读 `Check()`/`Errors()`；C# 无对应物，改为手写 `JsonSchema` 子集（Any / Null / Bool / Str / Num / Int / Lit / Arr / Obj / Rec / Union）与同形错误对象（`keyword` / `instancePath` / `message` / `requiredProperties`）。两处差异：TypeBox 报节点的**每个**失败关键字，本移植每节点只报第一个；错误文案对齐 TypeBox 但不保证逐字节相同。 |
| C39 | **`ModelConfig` 不做深冻结** | TS 深冻结每个 provider，调用方无法改动快照；C# 无冻结机制，`GetProvider` 直接交出存储实例，调用方须自行视为只读。另：I/O 错误文案内嵌平台自身的消息（不再有 Node 的 `ENOENT: no such file or directory, open '…'`）。 |
| C40 | **`virtual-models.ts` 的 router state 用 `JsonNode`** | TS 的 `TState = unknown`（文档要求 JSON 可序列化），C# 收敛为 `JsonNode`。另：TS 用「成员缺失」表达的 provider 能力检查，C# 以同一错误消息呈现。`findLatestResponse` 需要一个「最后一条非 error/aborted 的助手消息」判定，TS 收 `AgentMessage` 联合，C# 在 `ModelRuntime` 落地前无 `AgentMessage`，故本阶段先补一个收 `ChatMessage` 的重载（`StopReason` 非 `Error`/`Aborted` 即命中）——`ModelRuntime` 的路由正好只用到这一面。**仍缺**：`getBranchSelection` / `getVirtualModelState`（需 `SessionEntry`），记为 gap **G-4b-VM**，待 4e 会话层。 |
| C41 | **`McpServerConfig` 包装 aliases 解析后的 JSON** | TS 的 MCP 配置是结构型 JSON 对象；C# 用 `McpServerConfig(JsonObject)` 包装并投影核心读取的字段，配置里未识别的键**不丢**。 |
| C42 | **`Migrations.ShowDeprecationWarningsAsync` 在 stdin 被重定向时读一行** | TS 在关闭的 stdin 上会永久等待；C# 在重定向时读一行（EOF 即继续），交互式仍读单个按键。 |
| C43 | **`IProvider.Auth` 为 null 即「无认证语义」** | TS 的 `Provider.auth` 是必填成员，不存在无认证 provider；C# 的 `Auth` 是默认接口成员（null），`ApplyAuthAsync` 视 null 为无认证并**跳过解析**——测试替身与 faux provider 不必构造认证，所有真实 provider 都设有 `Auth`，走与 TS 完全一致的路径。 |
| C44 | **取消以 `OperationCanceledException` 呈现** | TS 的 `AbortSignal` 取消携带 `signal.reason`；C# 统一抛 `OperationCanceledException`（携带 token），调用方用 `IsCancellationRequested` 判别。 |
| C45 | **`transformHeaders` 走选项字典的键** | TS 是 `ModelsRequestTransforms` 的具名成员；C# 以选项字典的 `"transformHeaders"` 键承载（`Models.TransformHeadersOptionKey`），与本解决方案其余流选项的字典约定一致；`ApplyAuthAsync` 在返回前移除该键。 |
| C46 | **`settings-manager.ts` 的写队列是 `Task` 续接链** | TS 用 promise 链串行化同一文件的写入；C# 用 `Task` continuation 链达成同一语义（前一次写入失败不阻断后续）。副作用：排队的写入可能在线程池线程上运行，而**入队的 setter 仍在栈上**——因此每个被排队工作触碰的值都在入队前快照（与 TS 同），dirty 字段集合用锁保护。 |
| C47 | **`IProvider` 用显式能力属性替代 TS 的可选成员存在性** | TS 的 `Provider.fetchDeferred` / `cancelDeferred` 是可选成员，`Models` 靠「成员是否存在」决定报错时机；C# 的默认接口成员**总是存在**，故补 `SupportsFetchDeferred` / `SupportsCancelDeferred` 两个布尔属性承载「存在性」，使 `StreamDeferred` / `CancelDeferredAsync` 能在**解析认证之前**判定能力——与 TS 的检查顺序一致（否则「认证未配置」会先于「不支持延后」报出）。 |
| C48 | **响应头要跨 `HttpResponseHeaders` / `HttpContentHeaders` 两处查** | TS 的 `response.headers` 是统一视图，`response.headers.get("last-modified")` 一定拿得到；.NET 把实体头（`Last-Modified`、`Content-Type` 等）路由到 `HttpContentHeaders`，`response.Headers.TryGetValues("last-modified")` 对**真实响应**返回 false（`ETag` 属响应头所以正常）。`RemoteCatalogProvider.ReadHeader` 因此先查响应头再查内容头。**这是移植时踩到的真实缺陷**：首版只查 `response.Headers`，`lastModified` 恒为 0，会让远端目录每次刷新都全量下载（新鲜度门槛失效）。 |
| C49 | **`runtime-credentials.ts` 的 override 用有序列表而非字典** | TS 的 override 是 JS `Map`：迭代顺序即插入顺序，且「删除后重插」会移到末尾。.NET `Dictionary` 可能复用空槽，顺序会不同。`RuntimeCredentials` 因此用 `List<KeyValuePair<…>>` 复刻，`ListAsync` 的合并结果与 TS 逐项一致（存储顺序在前、同 id 就地替换、新 id 追加到末尾）。 |
| C50 | **`adaptOAuth` 退化为恒等转发** | TS 的扩展 OAuth 面向 `OAuthLoginCallbacks`（notify/prompt 风格），由 `adaptOAuth` 翻译成规范的 `AuthInteraction`；C# 的两个面本就是同一类型 `ProviderAuthInteraction`（`IAuthInteraction` + `Signal`），故 `ExtensionOAuthConfig.Login` 直接接收它，`ExtensionOAuthAuth` 只做 `IOAuthAuth` 适配。TS 的 `{...credential, type: "oauth"}` 打标在 C# 由 `Credential.OAuth` 的静态类型承载。 |
| C51 | **`ApiKeyAuth.check` 未移植** | TS 的 `composeApiKeyAuth` 实现 `check`（命令配置值不执行命令、环境值只判存在）；C# 的 `IApiKeyAuth` 没有 `check` 成员——可用性一律经 `ResolveAsync` 判定（见 `ModelsAuth.CheckProviderAuthAsync`）。因此组合出的 api-key 认证只暴露 `name` / `login` / `resolve`。可观察差异：命令配置值在检查阶段就会执行，失败经 `AuthResolve` 包成 `ModelsError` 抛出。这是本移植对**所有** provider 一致的既有取舍。 |
| C52 | **api 注册表回退统一走 `StreamSimple`** | TS 的 `streamWith` 在回退到 `getApiProvider(model.api)` 时按 `simple` 分流 `api.stream` / `api.streamSimple`；C# 的注册表契约里 `Stream` 收 `JsonObject`、`StreamSimple` 收 `SimpleStreamOptions`，而 provider 边界的 `IReadOnlyDictionary<string, object?>` 只能无损转成后者（`Signal`、回调等进不了 JSON）。因此两条路径都调 `StreamSimple`——与既有的 `Compat.Stream` 完全一致。 |
| C53 | **`Provider.headers` 未移植** | TS 的组合 provider 会转发 `base.headers`；C# 的 `IProvider` 没有 `Headers` 成员（`CreateProviderOptions` 亦然），故丢弃。 |
| C54 | **能力接口恒实现，缺失时返回同一 error 结果** | TS 按能力**条件性安装** `fetchDeferred` / `cancelDeferred` / `generateImages` / `classify`；C# 的能力接口（`IImagesProvider` / `IClassifierProvider`）是静态实现的，故组合 provider 恒实现它们：延后能力经 `SupportsFetchDeferred` / `SupportsCancelDeferred` 表达（差异 C47），图片/分类在无实现时返回 TS `Models` 层会产出的同一 `imageErrorResult` / `classifierErrorResult`（含 `does not support image generation` 与 `has no image implementation for "…"` 两种文案的分界）。与 `ProviderFactory` 的既有做法一致。 |
| C55 | **`OAuthCredential.env` 的开放字段未建模** | TS `composeOAuthAuth.toAuth` 读 `credential.env`（`OAuthCredentials` 的开放索引签名）作为请求头模板的解析环境；C# 的 `Credential.OAuth` 只有具名的 provider 专属字段（`AccountId` / `GatewayConfig` 等），没有环境包，故恒传 `undefined`。 |
| C56 | **`composeModelProvider` 的 eager 校验先于认证检查** | TS 先跑一次 `getAllModels()` 再判「有没有认证方式」；C# 首版把认证检查放在构造组合 provider 之前，会让「无认证方式」抢先于结构性错误报出。已改为构造器内先做 eager 校验、构造后再检查认证，顺序与 TS 一致。 |
| C57 | **`ModelRuntime` 组合而非继承 `Models`** | TS 是 `class ModelRuntime implements Models`——`Models` 在那里是接口，`ModelRuntime` 内包一个 `MutableModels`。C# 的 `Models` 是 sealed class（同时扮演接口与实现），既不能继承也不能做泛型约束的基类型，故 `ModelRuntime` **组合**一个 `Models` 实例并重暴露同一组访问器（`GetProviders` / `GetModel` / `GetModelsOfType` / `GetAvailableAsync` / `CheckAuthAsync` / `GetAuthAsync` / `ListCredentialsAsync` 等）。调用方按同一组方法名工作，差别只是 `ModelRuntime` 不再是 `Models` 的子类型。 |
| C58 | **`snapshot.auth` 只保存真正解析成功的 provider** | TS 的 `snapshot.auth` 为**每个** provider 都留一条（未配置者值为 `undefined`）；但 `Map.get()` 对「键不存在」与「值为 undefined」返回相同结果，两种形状不可区分。C# 因此只存已解析的 provider，查询结果与 TS 一致。 |
| C59 | **`modelsPath` 的「未设置 / 显式 null」两态由 `ModelsPathDisabled` 承载** | TS 的 `createModelRuntime({ modelsPath })` 区分「未传」（用默认 `~/.pi/models.json`）与「显式 `null`」（完全不读文件），而 C# 的 `string?` 只有一个空值。故 `CreateModelRuntimeOptions` 拆成 `ModelsPath` + `ModelsPathDisabled` 两个成员。 |
| C60 | **`refresh()` 不需要「旧版发布包返回 undefined」的兜底** | TS 对某些旧版 `Models` 实现的 `refresh()` 可能拿到 `undefined`，故有 `?? {…}` 兜底；C# 的 `Models.RefreshAsync` 恒返回 `ModelsRefreshResult`，该分支不存在。 |
| C61 | **图片/分类请求同样解析认证并应用 `transformHeaders`** | 与 TS 一致（`generateImages` / `classify` 走同一个 `prepareRequest`），但这与 C# 既有 `Models.GenerateImagesAsync` / `Models.ClassifyAsync` 的「不解析认证」行为不同——`ModelRuntime` 才是图片/分类的认证边界，`Models` 那一层保持原样。 |
| C62 | **`ModelTypeMap[TType]` / `Api` 泛型参数折叠为 `ModelSpec` / `ModelType`** | TS 的 `findOfType<TType>` / `getModelsOfType<TType>` / `getModelOfType<TType>` 靠 `ModelTypeMap[TType]` 把类别映射到 `Model`/`ImageModel`/`ClassifierModel`，`stream<TApi>` / `complete<TApi>` 靠 `Api` 约束选项类型。C# 的目录是单一具名记录 `ModelSpec`（`Type` 字段承载类别），故泛型参数消失、选项统一为 `IReadOnlyDictionary<string, object?>`。 |
| C63 | **`registerProvider(name)` 的运行时守卫变为编译期约束** | TS 声明 `registerProvider(provider)` / `registerProvider(name, config)` 两个重载，实现体收 `config?` 并在缺省时抛 `Provider config is required when registering by name`。C# 的两个重载直接让 `config` 非空，该错误不可能发生，守卫随之删除。 |
| C64 | **`ResolvedRequestAuth` 由两条成员联合改为两个子记录** | 本仓判别联合的既有惯例（见 `ProcessImageResult`）。另有一处 JS 真值语义：TS 用 `...(baseUrl ? { baseUrl } : {})` 展开，故**空串也算「无 baseUrl」**；C# 用 `string.IsNullOrEmpty` 判定后存 `null`。 |
| C65 | **`AuthOperationOptions` 展开为末尾的 `CancellationToken`** | `getAvailableOfType` 的 `{ signal }` 变成末位可选 token；`stream` / `complete` 额外多出可选 token（TS 把 signal 放在选项里）。另：`getApiKeyForProvider` 的裸 `catch {}` 按原样保留——**取消也被吞成「无 key」**，与 TS 一致而非 .NET 惯例。 |
| C66 | **`GlobOptions.MagicalBraces` 是 `bool?`** | `magicalBraces` 在三个消费者里的默认值互不相同：`escape` 解构成 `false`，`unescape` 解构成 `true`，`Minimatch.hasMagic()` 按普通属性读（缺席即 falsy）。C# 的 `bool` 无法表达「缺席」，故用 `bool?`，各处再各自取默认——这是 `escape` 与 `unescape` 对 `{a}` 行为相反的原因。 |
| C67 | **`escape` / `unescape` 不读 `allowWindowsEscape`** | 上游只有 `Minimatch` 的构造器读那个已废弃的键（`awe` 拼接），`escape` / `unescape` 只解构 `windowsPathsNoEscape`。首版把两者一起读，语料立刻报出 23 处不符。 |
| C68 | **`defaults(def)` 未移植** | 它返回一个把默认项用 `Object.assign({}, def, options)` 垫在每次调用之下的匹配器，而该合并依赖 JS 的「自有键缺席」与「自有键为默认值」之分：`minimatch(p, pat, { nocase: true })` 不会覆盖默认项的 `dot`，而 C# 的 `GlobOptions` 字面量对**每个**成员都带值，会把 `dot` 覆盖成 `false`。忠实复刻需要把每个选项都改成可空，而移植后的调用点无人使用 `defaults`，C# 的习惯本就是直接传想要的那份选项。 |
| C69 | **`MatchPart` 判别联合取代 `string \| RegExp \| GLOBSTAR`** | `MatchLiteralPart` / `MatchRegexPart` / `MatchGlobstarPart`（单例，代替 `Symbol`）。三者公开，因为它们是 `Minimatch.Set` 的元素类型且 `GLOBSTAR` 是文档化导出；产生它们的 `GlobAst` 则保持 `internal`，即 `minimatch.AST` 不再对外。`MatchRegexPart` 额外带上 `Glob`（上游的 `_glob`，`toMMPattern` 里那次 `toString()`），供差分语料比对 `flatten` / `fillNegs` 的重写结果。 |
| C70 | **`assertValidPattern` 的 `typeof` 分支由类型系统承担** | 只保留 64 KiB 长度上限。上游抛 `TypeError`，C# 用 `ArgumentException` 并保留原文案（`invalid pattern` / `pattern is too long`），与本仓既有惯例一致。 |
| C71 | **`debug()` 轨迹与 `u` 正则标志省略** | `debug(...)` 与 `Symbol.for('nodejs.util.inspect.custom')` 只用于诊断，不参与判定。`u` 标志只在 JS 里为了让 `\p{…}` 生效；.NET 的 `Regex` 原生支持 `\p{…}`，因此 `MatchRegexPart` 只保留 `IgnoreCase`。 |
| C72 | **`parse()` 已不可能返回 `false`，相关死代码删除** | 上游 `make()` 的 `set.filter(s => s.indexOf(false) === -1)` 与 `#matchOne` 的 `p === false` 分支都是 9.x 之前的遗留：`toMMPattern()` 只返回字符串或 `RegExp`。C# 里保留它们需要为一个不可能的值发明表示，故删除并留注释。另：`makeRe()` 的裸 `catch` 收敛为 `catch (ArgumentException)`——那里唯一可能失败的就是 `Regex` 构造。 |
| C73 | **`firstPhasePreProcess` 的外层循环改用下标遍历** | 上游用 `for...of` 遍历数组，**同时在循环体里向同一数组 push 新备选**，于是循环会继续访问刚创建的数组——这就是它的工作列表语义。C# 的 `foreach` 遇到同样的改动会抛异常，故改为 `for (k = 0; k < globParts.Count; k++)`。 |
| C74 | **`defaultModelPerProvider` 用有序 `KeyValuePair` 列表承载** | TS 是对象字面量，`Object.keys` 的顺序（插入序）是 load-bearing——`findInitialModel` 的候选遍历与 `buildFallbackModel` 都依赖它。C# 的 `Dictionary` 不保证枚举顺序，故保留 `IReadOnlyList<KeyValuePair<string, string>>`（41 项）作为唯一真源，查找用的 `DefaultModelIds` 只是它的投影（`ToDictionary`）。 |
| C75 | **`Model` / `ModelTypeMap` 折叠为 `ModelSpec`，`modelsAreEqual` 落到 Pi.Ai** | TS 的 `availableModels: Model[]` 靠 `model.type` 判别类别；C# 的目录是单一具名记录 `ModelSpec`（`Type` 字段承载类别，同 C62）。`modelsAreEqual`（去重与 `rawExactMatches` 过滤都要用）在 Pi.Ai 侧此前无等价物，本批补上 `ModelOperations.ModelsAreEqual`：类别 + id + provider 三项全等，id / provider 是 JS 的 `===` 字符串比较，对应序号比较。 |
| C76 | **`AbortSignal` → `CancellationToken`** | 与 C2 / C44 一致：取消归一为 `OperationCanceledException`。`resolveModelScope` 把 signal 透传给 `runtime.getAvailable()`。 |
| C77 | **`isValidThinkingLevel` 就地复用 `ThinkingLevels.Parse`** | TS 从 `../cli/args.ts` 引 `isValidThinkingLevel`，而 `cli/args.ts` 属 4g（尚未移植）。它的取值集合与 `Pi.CodingAgent.Core.ThinkingLevels` 的解析集**逐项一致**（含 `off`），故直接复用而不为此提前搬运 4g 的文件。`DEFAULT_THINKING_LEVEL` 同理走既有的 `Defaults.DefaultThinkingLevel`。 |
| C78 | **`findInitialModel` 的退出改写成被拒绝的 `Task`** | TS 的 `findInitialModel` 是 `async` 函数，`process.exit(1)` 之前的分支会变成**被拒绝的 promise** 而非同步抛出。C# 里若同步 `throw`，只观察返回任务、不 try/catch 调用点的调用方就看不到异常。故用 `Task.FromException<InitialModelResult>(new ModelResolutionException(error))` 保持同一形状。 |
| C79 | **`localeCompare` → `CompareInfo.Compare`，`toLowerCase` → `JsString.ToLowerCase`** | 两处 JS 语义都要显式对应：①模型排序用 `CultureInfo.CurrentCulture.CompareInfo.Compare(a, b, CompareOptions.None)`（Node 与 .NET 都走 ICU，等价——沿用 `Pi.Tui.Autocomplete` 的既有结论）；②JS 的 `toLowerCase()` 是**全量**映射，与 .NET 的 `ToLowerInvariant`（简单映射）在 U+0130 等码点上不同，故一律用全仓既有的 `JsString.ToLowerCase`（语料里的 `caseFolding` 模型集就是为钉住这一条）。 |
| C80 | **TS 的字符串字面量联合 → `string` + 常量类** | `ModelScopeDiagnostic` 的 `type` / `code` 在 TS 是字面量联合，C# 收敛为 `string`，全部取值由 `ModelScopeDiagnostic.Types` / `ModelScopeDiagnostic.Codes` 两个常量类给出（同 `VirtualModels.RouteReasons` 的既有做法）。 |
| C81 | **`IModelResolverRuntime` 是新增的运行时切片** | `model-resolver` 只用到 `ModelRuntime` 的五个成员（`getModels` / `getModel` / `hasConfiguredAuth` / `getAvailableSnapshot` / `getAvailable`），而 `ModelRuntime` 恒带 42 个内置 provider——语料无法表达「只有 mock 模型」的 runtime。故把这五个成员抽成 `IModelResolverRuntime`，`ModelRuntime` 实现它；`GetModels()` 的接口无参签名与类的可选 provider 过滤参数不匹配，用**显式接口实现**桥接（`IReadOnlyList<ModelSpec> IModelResolverRuntime.GetModels() => GetModels();`）。 |

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
> ③`model-resolver-corpus` 的生成器额外依赖 **Node 的 TS 类型剥离**（Node ≥ 22.18 / 23.6 可直接 `node file.ts`），
> 因此不需要 tsc / tsx——它把**真实的** `model-resolver.ts` 原样 stage 出来、只重写 import 头（见下）。

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

`tests/Pi.CodingAgent.Tests/minimatch-corpus.json`（由 `tools/gen-coding-agent-minimatch-corpus.mjs` 生成，共 5,673 条用例 × 46 个被匹配路径）：

| 区段 | 内容 |
|---|---|
| `cases` | 246 个 pattern × 23 组选项。逐条记录 `globSet`（花括号展开 + 去重）、`globParts`（预处理重写结果）、`set`（编译后的路径段：字面量 / `**` 哨兵 / 正则源 + `_glob` 重建文本）、`hasMagic`、`makeRe` 的可用性与逐路径答案、`match`（`minimatch()` 函数）与 `matchDirect`（`Minimatch.match()`）的逐路径答案，以及 `pattern` / `negate` / `comment` / `empty` / `nocase` / `isWindows` / `windowsNoMagicRoot` / `windowsPathsNoEscape` / `maxGlobstarRecursion`。 |
| `escape` | 25 个输入 × 5 组选项的 `escape` / `unescape` / 往返。 |
| `matchList` | 5 个列表 × 6 个 pattern × 4 组选项的 `match(list, pattern, options)`。 |

> **选项集**：default、nocase、dot、nocase+dot、nocaseMagicOnly、noglobstar、noext、nobrace、nonegate、nocomment、matchBase、partial、flipNegate、magicalBraces、preserveMultipleSlashes、optimizationLevel0/2、**posix / win32 / win32+nocase / win32+dot / win32+preserveMultipleSlashes**（显式钉住平台，使 UNC 与盘符分支在任何宿主上都可测）、windowsPathsNoEscape、windowsPathsNoEscape+nocase、allowWindowsEscape:false、maxGlobstarRecursion0/1、maxExtglobRecursion0、braceExpandMax3、nonull、nocase+matchBase。
> **pattern 覆盖**：字面量与空串、注释、`*` / `?` / `**` 的各种组合、字符类（含 `[[:alpha:]]`、`[!a-c]`、`[]]`、`[a-`、`[[`、`[a-[:alpha:]]`）、五种 extglob（含 `!()`、`+(*)`、`*(?)`、`@(`）、花括号（序列 / 步长 / 补零 / 嵌套 / `{a},b}` 怪癖 / `{2..}` 与 `{}` 这类不展开形态）、否定（`!` / `!!` / `!` 单独）、转义（`\*` / `\[` / 结尾 `\`）、Windows 根（`C:/` / `C:\` / `//server/share` / `//?/C:`）、路径形态（尾随 `/`、`./`、`../`、`a/../b`、`a//b`）、以及 `**` 在头 / 中 / 尾 / 多段（`a/**/b/**/c/**/d`）的全部位置。
> **被匹配路径**覆盖了同样的形态，另加 `''`、`'/'`、`.hidden`、`.git/config`、`.`、`..`、UTF-8 名、大小写变体与反斜杠路径。
> **两条踩过的坑**：①语料里**状态快照必须早于任何 `match()` 调用**——`matchOne` 会把 pattern 的盘符段就地改写成被测路径的大小写（`pattern[pdi] = fd`），先跑匹配再拍快照会拿到被污染的结果。②逐路径答案存成 `'0'/'1'` 位串而非 JSON 布尔数组，否则 5,673 × 46 的数组会把语料从 3.9 MB 撑到 7.7 MB。

> **语料抓到的两处移植缺陷**（都不是 glob 算法本身，而是选项语义）：①`Glob.Escape` / `Glob.Unescape` 首版把 `allowWindowsEscape === false` 也算进 `windowsPathsNoEscape`，但上游只有 `Minimatch` 构造器读那个键——`escape` / `unescape` 只解构 `windowsPathsNoEscape`，于是 23 条 `allowWindowsEscape:false` 向量全部不符（差异 C67）。②`magicalBraces` 在 `escape` 里默认 `false`、在 `unescape` 里默认 `true`，首版用统一的 `bool` 默认值导致 `{a}` 的解转义全错——改为 `bool?` 后由各处自取默认（差异 C66）。两处都是**只看代码读不出来**的：`escape.js` 与 `unescape.js` 的解构默认值写在同一行的花括号里，很容易当作同一个值。

`tests/Pi.CodingAgent.Tests/model-resolver-corpus.json`（由 `tools/gen-coding-agent-model-resolver-corpus.mjs` 生成，约 558 KB）——4b 的最后一文件：

| 区段 | 条数 | 内容 |
|---|---:|---|
| `defaultModelPerProvider` / `providers` | 41 | 41 个内置 provider 的**顺序**与逐项 `getDefaultModelId` 查找 |
| `parsePatterns` | 1,206 | `parseModelPattern` 的模型 / 思考级别 / 告警三元组，覆盖严格与宽松两模式 |
| `exactMatches` | 270 | `findExactModelReferenceMatch` 的命中索引（`-1` = 未命中） |
| `scopes` | 279 | `resolveModelScopeFromModels` 的 scoped 列表与告警 |
| `cliModels` | 1,440 | `resolveCliModel` 的模型 / 来源 / 错误 |
| `initialModels` | 24 | `findInitialModel` 的结果 |
| `restoredModels` | 12 | `restoreModelFromSession` 的结果 |
| `scopeWarnings` | 12 | `resolveModelScope` 写到 stderr 的逐行诊断 |

> **本语料的三条要点**：
> ①**生成器 stage 的是真实源码**——把 `packages/coding-agent/src/core/model-resolver.ts` 原样拷到临时目录，
> **只重写 import 头**（正则替换 5 条 import），用 4 个 shim 顶掉不可达依赖（`@earendil-works/pi-ai` 的
> `modelsAreEqual`、`chalk`、`../cli/args.ts` 的 `isValidThinkingLevel`、`./defaults.ts` 的 `DEFAULT_THINKING_LEVEL`）。
> 每个 shim 都是从原文件**逐行抄来**并注明出处，因此语料反映的是上游**同一份代码**，包括文档注释里没写明的行为。
> ②**Node 24 的原生 TS 类型剥离**让生成器可以直接 `node model-resolver.ts`（无需 tsc / tsx），代价是 stage 出来的
> 源码里**不能残留任何未重写的 import**——生成器最后用 `^import\b[^\n]*from "([^"]*)"` 只扫**真的 import 语句**
> （首版用宽松正则把文档注释里的 `from "<pattern>:<thinking>"` 也当成 import，误报泄漏）。
> ③**跨 provider 的顺序不可复现**：`ModelRuntime.ProviderIds()` 返回 `HashSet<string>`，而 .NET 的字符串哈希
> **逐进程随机化**，所以「按 provider 分组后的跨组顺序」在 .NET 侧不稳定。凡依赖它的向量（`findInitialModel` 的
> 「第一可用模型」回退、`buildFallbackModel` 的部分分支）**已从语料中剔除**，改在 `ModelResolverTests` 里用
> **单一 provider 的 runtime** 覆盖——这是 4b 唯一一处「语料表达不了、只能手写」的行为。组**内**顺序是稳定的
> （`ModelRuntime` 是按 provider 过滤后的扁平列表），所以 `resolveModelScopeFromModels` 那类不经过 runtime 的
> 向量仍可全量回放。

## 测试覆盖

`tests/Pi.CodingAgent.Tests` 的 utils 层部分（下表，共 121 项；4b 的 6 个测试类见「4b 进度 → 测试覆盖」）：

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
| `MinimatchCorpusTests` | 10 | `minimatch-corpus.json` 的 5,673 条：`minimatch()` / `Minimatch.match()` / `makeRe()` 的逐路径答案、`globSet` / `globParts` / `set`（含正则源与 `_glob` 重建文本）/ `hasMagic` / `braceExpand` 的结构比对、`escape` / `unescape`、`matchList`，外加宿主平台与包版本守卫 |
| `MinimatchTests` | 44 | 语料覆盖不到的部分：`GlobStar` 单例与 `Sep`、`Filter`、64 KiB 上限、`makeRe()` 为空集返回 null、`hasMagic` 的 `magicalBraces` 门槛、**盘符改写只影响本实例**、`BraceExpansion` 的四个 DoS 上限与 Bash 怪癖、`escape` / `unescape` 的相反默认与「不读 `allowWindowsEscape`」、以及 `model-resolver` 那句 `minimatch(fullId, glob, { nocase: true }) \|\| minimatch(m.id, glob, { nocase: true })` 的端到端行为 |
| `ExtensionContractTests` | 15 | 4d-1 契约层差分：35 个事件线名集合、56 个事件变体名单、12 组字面量联合取值、`ISessionEvent` / `IToolCallEvent` / `IToolResultEvent` 三组标记接口覆盖、`isToolCallEventType` 守卫、`ExtensionAPI` / 四组上下文的公开方法名集合（与 TS 逐条对齐）、`ProviderHeaders` 的 null 即删除语义 |
| `EventBusTests` | 19 | 4d-2a 事件总线差分：注册顺序分发、快照语义（分发中退订/新订不影响当次 emit）、同 handler 重复注册的独立退订、退订幂等、sync/async handler 异常的捕获与 `Event handler error (<channel>):` 日志（不传播、不阻断其他 handler）、`clear()` 全清与可再注册、null 参数拒绝、工厂返回的控制器可经 `EventBus` 接口使用 |
| `ExtensionLoaderTests` | 45 | 4d-2b 加载器差分（向量取自 TS `8423-extension-factory-failure` / `extension-factory-cache` / `extensions-discovery`）：内联工厂注册工具/命令/flag/快捷键与 source-info 标记、错误聚合（工厂抛错、无工厂、无模块加载器、相对路径解析）、注册 API 校验（工具 schema 缺失、命令名空、flag 默认值类型）、flag 默认值提交与不覆盖既有值、provider 注册入队与失败回滚、MCP 注册（配置校验、属主冲突、`-`/`_` 命名空间冲突、退订属主语义）、`pi.events` 门面（注册/退订/跨扩展收发）、运行时桩与失效语义（action 抛错、`setModel` faulted task、失效后 API 拒绝服务、失效退订事件）、模块缓存（导入一次工厂每次重跑、`clearExtensionCache` 强制重导、cwd 变更失效）、目录发现（直接 `*.ts`/`*.js`、`index.ts`/`index.js`、`package.json` 清单优先与缺失回退、目录不存在）、加载顺序与去重（project → global → configured） |

## 4d-1 进度（2026-10-10）：扩展系统契约层

`core/extensions/types.ts`（2,272 行，纯类型模块）已移植，构建 0 警告 0 错误，
`Pi.CodingAgent.Tests` 447/447 通过（含新增 15 项契约差分测试）。

新增文件：

| 文件 | 内容 |
|---|---|
| `src/Pi.CodingAgent/Core/Extensions/ExtensionContexts.cs` | `ExtensionMode` / `WidgetPlacement` / `NotifyType` / `ForkPosition` / `DeliverAs` 字面量联合；`ExtensionUIContext`（29 个成员）、`ExtensionContext`（18）、`ExtensionToolContext`（2）、`ExtensionCommandContext`（7）、`ReplacedSessionContext`（2）四组上下文；`ContextUsage` / `CompactOptions` / `ExecuteToolOptions` / `NewSessionOptions` / `ForkOptions` / `NavigateTreeOptions` / `SwitchSessionOptions` / `SendMessageOptions` / `SendUserMessageOptions` / `CustomMessageDraft` |
| `src/Pi.CodingAgent/Core/Extensions/ExtensionEvents.cs` | `ExtensionEvent` 联合的 56 个变体（含 10 个 session 事件、8 个 tool_call 事件、9 个 tool_result 事件）、`BoundaryState` / `BoundaryResult` / 4 种 `SessionBoundaryDraft`、`TreePreparation`、12 个事件结果类型、`MessageRenderer` / `EntryRenderer` / `MarkdownTransformer` / `ToolRendererResolver` 委托、`ExtensionEventGuards` 类型守卫 |
| `src/Pi.CodingAgent/Core/Extensions/ExtensionApi.cs` | `ExtensionAPI`（34 个成员）、`RegisteredCommand` / `ResolvedCommand` / `RegisteredTool` / `ExtensionFlag` / `ExtensionShortcut` / `ToolInfo`、12 个 handler 委托、`ExtensionFactory` / `InlineExtension` / `ExtensionVirtualModel` |
| `src/Pi.CodingAgent/Core/Extensions/Types/Placeholders.cs` | 4e/4f 占位类型（`SourceInfo` / `ExecOptions` / `ExecResult` / `BashResult` / `BashOperations` / `ReadonlyFooterDataProvider` / `CompactionPreparation` / `CompactionResult` / `CacheWarmingDecisionEvent` / `CustomMessage<T>` / `CustomEntry<T>` / 5 个 session 条目类型 / `ReadonlySessionManager` / `SessionManager` / `SlashCommandInfo` / `BuildSystemPromptOptions` / `AppKeybinding` / `OverlayHandle` / `ProviderHeaders` / `Provider` / `Theme`），每处均有 `// 4e/4f 接入后替换` 标记（`EventBus` 占位已在 4d-2a 移除） |

与 TS 的差异（均为 C# 表达力限制下的等价选择，已在代码注释标注）：

| # | 差异 | 理由 |
|---|---|---|
| C86 | `on()` 用泛型 `On<E>` / `On<E,R>` + 事件名字符串，而非 35 个字面量重载 | C# 无字符串字面量类型；重载会与 lambda 推断冲突 |
| C87 | `registerProvider(name, config)` 复用 4b 已落地的 `ProviderConfigInput` | 避免重复声明同一形状 |
| C88 | `ExtensionVirtualModel` 不带泛型 `TState` | 路由状态在 C# 侧以 `JsonNode` 承载（与 `VirtualModelDefinition` 一致） |
| C89 | `SendUserMessage` 的 handler 用 `UserMessageContent`（Text / Blocks 联合）包装 string 与 content-block 数组 | 委托参数需要单一类型 |
| C90 | 事件变体命名沿用仓库既有约定（`AgentEvent` 无 `Event` 后缀），但 `InputEvent` / `ContextEvent` / `ContextWithSystemEvent` 保留后缀 | 前者与 `Pi.Agent.Types.AgentEvent` 一致；后者因 `Input` / `Context` 与事件负载字段名冲突，且本就是 TS 接口名 |
| C91 | `BoundaryState` 默认 `continue: true`、`outcome: "completed"`、`entries: []` | 对齐 TS 运行时构造该状态时传入的值 |
| C92 | `cache_warming_decision` 暂无事件变体（线名已登记） | `cache-warmer.ts` 属 4e，负载未知 |

## 4d-2a 进度（2026-10-10）：事件总线

`core/event-bus.ts`（34 行）已移植为 `src/Pi.CodingAgent/Core/EventBus.cs`，构建 0 警告 0 错误，
`Pi.CodingAgent.Tests` 466/466 通过（447 + 新增 19 项事件总线差分测试）。

| TS 符号 | C# 落点 |
|---|---|
| `EventBus`（接口：`emit` / `on`） | `Pi.CodingAgent.Core.EventBus` 接口（`Emit` / `On`） |
| `EventBusController extends EventBus`（+ `clear()`） | `Pi.CodingAgent.Core.EventBusController` 密封类（+ `Clear()`） |
| `createEventBus()` | `EventBusController.CreateEventBus()` |

行为契约（差分测试逐条锁定）：

- `on` 每次注册独立（即使同一 handler 传两次也是两个订阅，各自退订互不影响）；返回的退订函数幂等
  （对应 Node `off` 对已移除监听是 no-op）；
- `emit` 按注册顺序同步分发；handler 可以是同步或异步（TS `safeHandler` 对返回值 `await`，
  C# 对应 `Func<object?, Task>``）；
- handler 异常被 `safeHandler` 捕获并写 `Event handler error (<channel>): …`，不传播给 `emit`
  调用方，也不影响同频道其他 handler；同步 throw 在 `emit` 期间即落日志（与 TS 异步包装器的
  同步前缀一致），异步 reject 在 await 恢复后落日志；
- `emit` 采用「加锁快照 + 锁外调用」：分发过程中退订/新订不影响当次 emit（Node 在监听数 > 1 时
  克隆监听数组，语义相同），且 handler 内再次 `on`/退订不会死锁；
- `clear()` 移除全部频道的全部监听，可重复调用，清空后可重新注册。

与 TS 的差异：

| # | 差异 | 理由 |
|---|---|---|
| C93 | TS 的 `EventBusController` 是接口，`createEventBus()` 返回对象字面量；C# 落为 `EventBus` 接口 + `EventBusController` 密封类 | 消费方（loader）只依赖 `EventBus` 接口面，行为一致；密封类省一次间接 |
| C94 | 分发用「加锁快照 + 锁外调用」，而非 Node 的同步数组克隆 | 语义等价（见上），且避免 handler 内注册/退订时的重入死锁 |
| C95 | 错误日志为单行 `Event handler error (<channel>): <Exception.ToString()>`（堆栈在后续行） | TS `console.error(msg, err)` 两参数在项目既有约定（`Deprecation` / `ModelResolver`）里均为单行 `WriteLine` |

4d-2b（loader / runner）起依赖 4e/4f 的真实类型，届时按占位标记逐项收敛。

## 4d-2b 进度（2026-10-10）：扩展加载器

`core/extensions/loader.ts`（1,395 行）已移植，并随行落地 loader 直接依赖的三个小模块。
构建 0 警告 0 错误；`Pi.CodingAgent.Tests` 511/511 通过（466 + 新增 45 项加载器差分测试）。

新增文件：

| 文件 | 内容 |
|---|---|
| `src/Pi.CodingAgent/Core/Extensions/ExtensionLoader.cs` | `loadExtensions` / `loadExtensionsCached` / `discoverAndLoadExtensions` / `loadExtensionFromFactory` / `createExtensionRuntime` / `createExtensionAPI`、模块缓存（按 cwd 失效）、目录发现（直接文件 / index.ts / index.js / package.json 清单优先）、`IExtensionModuleLoader` 接缝 |
| `src/Pi.CodingAgent/Core/Extensions/ExtensionLoaderImpl.cs` | `ExtensionRuntimeImpl`（共享状态 + 抛错 action 桩 + 失效/退订跟踪）与 `ExtensionApiImpl`（注册、loading 期入队、commit/discard、`pi.events` 门面） |
| `src/Pi.CodingAgent/Core/Extensions/ExtensionRuntime.cs` | `Extension` / `ExtensionLoadError` / `ExtensionLoadWarning` / `LoadExtensionsResult` / `IExtensionRuntime` / `ExtensionEventHandler` |
| `src/Pi.CodingAgent/Core/SourceInfo.cs` | `SourceInfo` / `SourceScope` / `SourceOrigin` / `getSyntheticPathSource` / `isSyntheticPath` / `createSyntheticSourceInfo` |
| `src/Pi.CodingAgent/Core/Timings.cs` | `time` / `resetTimings` / `printTimings`，`PI_TIMING=1` 门控 |
| `src/Pi.CodingAgent/Core/Exec.cs` | `execCommand`（shell:false、输出累积、超时/中止 kill、等待失败 code=1） |
| `tests/Pi.CodingAgent.Tests/ExtensionLoaderTests.cs` | 差分测试，向量取自 TS `8423-extension-factory-failure.test.ts` / `extension-factory-cache.test.ts` / `extensions-discovery.test.ts` |

与 TS 的差异：

| # | 差异 | 理由 |
|---|---|---|
| C96 | `Extension` 的各集合在 C# 侧 eager 初始化为空集合（TS 懒建、首次使用前为 undefined）；`MessageRenderers` / `EntryRenderers` 以 `Delegate` 承载闭合泛型委托 | 消费方无需 null 检查；泛型擦除方式与 TS 把 `MessageRenderer<T>` 擦除到默认实例化同构，runner 分发时转型 |
| C97 | TS 用 jiti 运行时编译 TS 模块；C# 侧按决策 D2 走 `AssemblyLoadContext`，本批以 `IExtensionModuleLoader` 接缝占位，未提供加载器时聚合清晰错误而不中断其余扩展 | 4d-3 落地真实加载器；接缝让其余行为（缓存/发现/错误聚合/注册 API）先行可测 |
| C98 | `core/extensions/runner.ts` 未随本批移植 | 其依赖 Theme / SessionManager / ModelRegistry / system-prompt 均属 4e/4f，随 4e 一起落地 |
| C99 | `setModel` 桩返回 faulted `Task<bool>`（TS 为 rejected promise）；其余 action 桩同步 throw（与 TS 一致） | C# 中同步 throw 与 rejected promise 的观察时机不同，用 faulted task 保持 await 时可观测 |
| C100 | `core/source-info.ts` 只移植 loader 子集；`createSourceInfo` 未移植 | 其入参 `PathMetadata` 来自 `core/package-manager.ts`（4e） |
| C101 | `getCreateJiti` / `jiti-loader` / `jiti-static-loader` / `virtual-modules` / `getAliases` 未移植 | jiti 与 ESM specifier 别名是 TS 运行时机制，.NET 无对应物（决策 D2） |

4d-2b 之后剩余：4d-3 扩展加载器真实实现（AssemblyLoadContext + 内置扩展注册）、
runner（随 4e）、codemode/llama/mcp/tool-search 四个内置扩展包。
