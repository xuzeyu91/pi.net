# Pi → .NET 迁移：现状分析、迁移清单与分阶段计划

> 参照源码：`D:\AI\参考项目\pi`（TypeScript monorepo，14 个 package）
> 目标仓库：`D:\AI\参考项目\pi.net`（.NET 10，11 个运行时项目 + 10 个测试项目）
> 本文档在迁移期间持续更新；逐文件细节见 [porting-status.md](porting-status.md) 与 [tui-porting-status.md](tui-porting-status.md)。

---

## 一、项目现状分析

### 1.1 源项目规模（`packages/*/src`，不含测试）

| 源包 | 源码行数 | 测试行数 | .NET 项目 |
|---|---:|---:|---|
| telemetry | 935 | 243 | Pi.Telemetry |
| protocol | 869 | 567 | Pi.Protocol |
| agent | 2,519 | 3,969 | Pi.Agent |
| mcp | 3,190 | 1,420 | Pi.Mcp |
| server | 1,958 | 1,054 | Pi.Server |
| client | 1,135 | 715 | Pi.Client |
| ai | 26,515 | 41,972 | Pi.Ai |
| chord | 8,817 | 6,794 | Pi.Chord |
| codemode | 1,837 | 1,107 | Pi.Codemode |
| env | 2,073 | 884 | Pi.Durable（`Env/` 子目录） |
| durable | 20,404 | 25,671 | Pi.Durable |
| tui | 19,293 | 18,731 | Pi.Tui |
| coding-agent | 19,427 | 13,239 | **未开始** |
| evals | 1,446 | 815 | **未开始** |
| **合计** | **110,418** | **121,181** | |

### 1.2 已完成的迁移（可构建、测试全绿）

| 源包 | 状态 | 说明 |
|---|---|---|
| telemetry | ✅ 完整 | 契约 + 参考适配器 + conformance 测试套件 |
| protocol | ✅ 完整 | CBOR framing/codec，definite-length 严格子集，与 TS **线上兼容** |
| agent | ✅ 完整 | 状态机 / 双队列 / agent-loop / 工具执行，含 `proxy.ts` |
| mcp | ✅ 完整 | JSON-RPC + in-memory/stdio/streamable-http 三传输 + 完整 OAuth 层 |
| server / client | ✅ 完整 | RPC 握手 / 请求 / 取消 / 推送、会话路由、Unix 域套接字与本地发现 |
| ai | ✅ 完整 | 10 个内建 API + 42 家 provider 全家桶 + compat + 全部 utils + CLI |
| chord | ✅ 完整 | delta / services / facets / node 层 / api |
| codemode | ✅ 完整 | identifier/types/source/declarations/runtime protocol + host 编排 + prelude；VM 执行经 `ICodemodeJsEngine` 注入点外置 |
| env | ✅ 完整 | 落在 `Pi.Durable/Env/`（Result / FileStat / LocalExecutionEnv / SnapshotFileWatcher / StreamDecoder / LineScanner） |
| durable | 🚧 源码完整 | 基础层 / storage / session / env / harness / tools / truncate / testing 全部落地；**部分 `harness-*.test.ts` 覆盖待补** |
| tui | 🚧 44/45 文件 | 核心层 + 输入/颜色/键位 + 组件（text/box/stack/loader/scroll-view/input/editor/select-list/markdown/settings-list/image/mouse-region/alt-screen-flash）+ latex/autocomplete/layout/terminal-image/alt-screen-search + 备用屏渲染器；**仅剩 `index.ts` 桶文件** |

### 1.3 待迁移清单（按依赖顺序）

| 优先级 | 项目 | 待办 | 源规模 | 依赖 |
|---|---|---|---:|---|
| P0 | ~~tui~~ | ~~`components/markdown.ts`、`settings-list.ts`、`image.ts`、`mouse-region.ts`、`alt-screen-flash.ts`、`alt-screen-search.ts`~~ ✅ P63 完成 | ~1,930 行 | latex / colors / select-list / terminal-image |
| P0 | ~~tui~~ | ~~`tui-alt-screen.ts`（备用屏渲染器，含 `tui.ts` 屏幕级鼠标路由 + SGR 解码）~~ ✅ P64 完成 | 1,784 行 | 上述全部 + layout |
| P1 | tui | `index.ts`（桶文件，C# 无对应概念，公开面由类型可见性决定）—— 公开面已逐条核对完毕，无需额外代码 | 192 行 | — |
| P1 | coding-agent | 全包移植（会话 / 工具系统 / 技能 / 主题 / RPC 模式 / TUI 交互）。**实测 85,536 行 / 299 文件**（早期写的 19,427 行有误），拆成七个子阶段，见 [coding-agent-porting-status.md](coding-agent-porting-status.md) | 85,536 行 | ai / agent / mcp / durable / tui |
| P2 | durable | 补齐各 `harness-*.test.ts` 的差分测试覆盖（源码已 100% 移植；**用户决定顺延到 coding-agent 之后**） | ~8,900 行测试 | — |
| P3 | evals | 全包移植（评测 harness，依赖 coding-agent） | 1,446 行 | coding-agent |

> **2026-10-09 顺序调整**：原计划「Phase 3 durable 测试 → Phase 4 coding-agent」。经评估，durable **源码**已 100% 移植且已有 11,859 行 C# 测试，补测试只增加验证深度而不改变迁移完成度；coding-agent 则是 0% 移植的主产品且是 evals 的硬前置。用户确认后改为**直接推进 coding-agent**（Phase 4），durable 测试顺延。

### 1.4 当前工作区状态（本次会话开始时）

- 工作区存在**未提交**的进行中改动：`markdown.ts` + `alt-screen-search.ts` + 四个叶子组件（settings-list / image / mouse-region / alt-screen-flash）的移植，以及配套差分语料。
- 该批改动可构建（0 警告 0 错误），但测试有失败：`MarkedLexer` 的 CRLF 归一化错误（`\r\n` 被拆成两个 `\n`）、`MarkedRules` 每渲染重建导致性能退化（每次渲染 ~300–500ms）、以及若干测试夹具 / 语料缺陷。

---

## 二、分阶段计划

每个阶段以「构建 0 警告 0 错误 + 相关测试全绿 + 一致性核对」收口，然后提交并推送。

### 阶段 1 — tui 叶子组件收口（markdown / alt-screen-search / settings-list / image / mouse-region / alt-screen-flash）✅ 已完成（P63）

- **目标**：把工作区中未提交的 6 个 tui 文件移植补齐、修复缺陷、测试全绿并入库。
- **文件范围**：
  - `src/Pi.Tui/Marked/*.cs`（marked v18.0.5 的 tokenizer/lexer/rules 移植）
  - `src/Pi.Tui/Components/Markdown.cs`、`SettingsList.cs`、`Image.cs`、`MouseRegion.cs`、`AltScreenFlashContainer.cs`
  - `src/Pi.Tui/AltScreenSearch.cs`
  - `tests/Pi.Tui.Tests/MarkdownCorpusTests.cs`、`ExtraComponentsCorpusTests.cs`、`AltScreenSearchCorpusTests.cs` + 三份语料
- **验收标准**：`dotnet build Pi.slnx -m:1` 0 警告 0 错误；`Pi.Tui.Tests` 全绿（含 markdown 4,990 条、extra-components 1,692 条、alt-screen-search 语料）。
- **一致性检查点**：CRLF 归一化 = marked 的 `/\r\n|\r/g`；markdown 渲染逐条对照 TS 参考；settings/image/mouse-region/flash 行为逐条对照。

### 阶段 2 — tui 备用屏渲染器（`tui-alt-screen.ts`）✅ 已完成（P64）

- **目标**：移植全屏渲染器 `TuiAltScreen`，并把 `tui.ts` 中剩余的屏幕级鼠标路由（`dispatchMouseToOverlay` / `resolveMouseFocusTarget` / `renderedOverlayLayouts`）与 SGR 鼠标解码接入。
- **文件范围**：新增 `src/Pi.Tui/TuiAltScreen.cs`（2,450 行）；补充 `Tui.cs` / `Layout.cs` 的屏幕级 API。
- **验收标准**：构建 0 警告 0 错误；新增差分 / 行为测试通过；`index.ts` 公开面清单核对。
- **一致性检查点**：备用屏进入/退出序列、同步输出、差分渲染、overlay 鼠标路由、SGR 解码与 TS 1:1。
- **完成情况**：
  - `TuiAltScreen.cs`（`TuiBase` + `IViewportTui`）落地全部 ~90 个成员；`Tui.cs` 补 `OverlayBounds` / `IOverlayHandle.GetBounds` / `IViewportTui` / `renderedOverlayLayouts` / `resolveMouseFocusTarget` / `dispatchMouseToOverlay`。
  - 新增 `tests/Pi.Tui.Tests/alt-screen-corpus.json`（37 条场景，由 TS 参考实跑捕获）+ `AltScreenCorpusTests.cs`（37 条差分 + 2 项守卫）。
  - 修复 1 处**真实缺陷**（`ShowOverlay` / `HideOverlay` / `OverlayHandleImpl.Hide` 在 `stop()` 后仍写 `HideCursor`，应经 `hideTerminalCursor()` 守卫）与 2 处 TS 语义偏差（`ViewportHeight / 3`、稳定排序）。
  - 一致性校验：上游 `test/tui-alt-screen.test.ts` **64/64 通过**；`Pi.Tui.Tests` 全量 23,812 项全绿。

### 阶段 3 — durable 测试覆盖补齐 ⏸ 顺延（2026-10-09 决定）

- **目标**：把 `packages/durable/test/harness-*.test.ts` 中尚未移植的用例补齐为 C# 差分测试。
- **文件范围**：`tests/Pi.Durable.Tests/*`。
- **验收标准**：`Pi.Durable.Tests` 全绿（含已知的沙箱环境相关间歇性挂起已隔离）。
- **一致性检查点**：逐测试文件核对断言集合与 TS 对齐。
- **顺延原因**：durable **源码**已 100% 移植，且已有 11,859 行 C# 测试覆盖 events / generation / inbox / inspect / lifecycle / output(+skip) / prompt / registry / submissions / task-graph / tools / view / context / scheduler / types / session / env / storage-conformance。补测试只增加验证深度，不改变迁移完成度；coding-agent 是 0% 移植的主产品且是 evals 的硬前置。
- **剩余缺口**（约 8,900 行 TS）：`harness-compaction`（2,304）、`harness-structured`（1,712）、`harness-tasks`（1,404）、`harness-ownership`（965）、`harness-tasks-recovery`（886）、`harness-conversations`（476）、`harness-live-deltas`（454）、`harness-tools-recovery`（405）、`harness-generation-recovery`（289）。

### 阶段 4 — coding-agent（最大单项）🚧 进行中

- **目标**：移植交互式 CLI 主产品。**实测 85,536 行 / 299 文件**（早期写的 19,427 行有误）。
- **子阶段（按依赖顺序）**：
  1. **4a `src/utils/*` + 无依赖根级模块** —— ✅ 已完成：utils 36/36 文件 + `config.ts` + `migrations.ts`（2026-10-09）
  2. **4b 配置 / 信任 / 模型层** —— ✅ 已完成：settings-manager / trust-manager / project-trust / auth-storage / model-config / models-store / radius / virtual-models / mcp-servers / keybindings / runtime-credentials / remote-catalog-provider / provider-composer / model-runtime / model-registry / model-resolver 共 **16/16 文件（6,844 / 6,844 行）**完成（2026-10-09）。Pi.Ai 侧的 `Models` 凭据/刷新/可用性运行时层（`ModelsAuth.cs` / `ModelsRefresh.cs` / `ModelsRequestOptions.cs` / `ModelSpecJson.cs`）已补齐；`model-resolver` 另需的 `modelsAreEqual` 补进 `ModelOperations`。`minimatch` 依赖按 `minimatch@10.2.6` 逐行移植到 `Utils/Glob/`（6 文件），用 5,673 条差分语料锁定；`model-resolver` 本身用 1,206 条 pattern + 279 条 scope 的差分语料（staged 真实 TS 源码，Node 类型剥离直跑）加 28 项手写测试覆盖。
  3. 4c 工具系统（`core/tools/*` + `core/tools/renderers/*`）
  4. 4d 扩展系统（`core/extensions/*` + `extensions/*`：codemode / llama / mcp / tool-search）
  5. 4e 会话与资源（agent-session / session-manager / resource-loader / package-manager / compaction / export-html / system-prompt / telemetry / sdk）
  6. 4f 模式层（`modes/rpc/*`、`modes/interactive/*`，含 7,082 行的 `interactive-mode.ts`）
  7. 4g 入口与实验层（`cli/*`、`main.ts`、`cli.ts`、`rpc-entry.ts`、`bun/*`、`client/*`、`experimental/*`）
- **验收标准**：逐子阶段构建 0 警告 0 错误 + 测试通过。
- **一致性检查点**：会话生命周期、工具调用与渲染、扩展加载（C# 原生插件路线）、主题与键位。
- **详细进度与设计差异**见 [coding-agent-porting-status.md](coding-agent-porting-status.md)。

### 阶段 5 — evals

- **目标**：移植评测 harness（依赖 coding-agent）。
- **文件范围**：`src/cli.ts` / `docker.ts` / `harness.ts` / `plan.ts` / `report.ts`。
- **验收标准**：构建 0 警告 0 错误；能在本地跑通 smoke 评测。
- **一致性检查点**：报告 JSON 形状、计划生成、docker 编排。

---

## 三、可追溯性

- 阶段完成记录写入 `docs/porting-status.md` / `docs/tui-porting-status.md`，并在 `.workbuddy-ai/memory/` 追加工作日志。
- 每个阶段一次提交，提交信息沿用仓库既有风格：`type(scope): 中文标题` + 分节要点（改动 / 差异 / 验证 / 待续）。
- 潜在风险集中记录在本文件第四节。

## 四、潜在风险

1. **性能**：`RegexOptions.Compiled` 规则集若按渲染重建会造成数量级退化（阶段 1 已修复）。
2. **沙箱限制**：`dotnet build` 需 `-m:1`；监听端口 / 长路径 / loopback 相关测试在沙箱内失败，与移植代码无关。
3. **durable 间歇性挂起**：满负荷运行 `Pi.Durable.Tests` 偶发挂起（环境相关，见 porting-status 第 55 条）。
4. **coding-agent 规模**：实测 **85,536 行**（早期写的 19.4k 有误，少算一个数量级）+ 13.2k 行测试，是剩余工作量的主体，需分多批推进。
5. **coding-agent 既有测试失败（4a 遗留，20 项）**：`Pi.CodingAgent.Tests` 中 `ClipboardImageTests`(12) / `ExifOrientationTests`(2) / `ToolResultImagesTests`(1) / `ImageConvertAndResizeTests`(1) / `FrontmatterCorpusTests`(2) 失败。这些测试均不经过模型层（不引用 `Pi.Ai.Models`），与 4b 改动无因果关系；`FrontmatterCorpusTests` 的两项是 YAML 块标量（`|` / `>`）**尾随换行丢失**的真实缺陷。待 4a 收尾批次一并处理。
