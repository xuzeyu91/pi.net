# 4d 扩展系统 — 执行方案

> 目标：把 TS `packages/coding-agent/src/core/extensions/*`（4,493 行）与
> `packages/coding-agent/src/extensions/*`（6,194 行）移植到
> `src/Pi.CodingAgent/Core/Extensions/*`。合计约 10,700 行 TS。
>
> 本文档记录**已确认的边界、已完成的调研、分批方案与验收标准**，供后续会话直接接续。

## 0. 已确认的决策

| # | 决策 | 依据 |
|---|---|---|
| D1 | **llama 拆分**：4d 只做 `client.ts` / `provider.ts` / `huggingface.ts` / `index.ts`（963 行）；`ui.ts`（503 行）随 4f | `ui.ts` 依赖 4f 的 `Theme`、`DynamicBorder`、`KeybindingsManager`，4f 未开始 |
| D2 | **扩展加载走 C# 原生插件路线** | 与 `migration-plan.md` 一致；TS 用 jiti 运行时编译 `.ts`，.NET 侧改为 `AssemblyLoadContext` 加载程序集。功能等价，分发形式从 `.ts` 文件变为 `.dll` |
| D3 | renderers 的 `renderCall` / `renderResult` 随 4f | 依赖 Theme（差异 C85），4c 已只落 `renderShell` |

## 1. 依赖现状（已核实）

| 依赖 | .NET 侧状态 |
|---|---|
| MCP 客户端 | ✅ `src/Pi.Mcp` 已完整移植；`extensions/mcp/index.ts`（1,226 行）是薄适配层 |
| QuickJS WASM 沙箱 | ✅ `src/Pi.Codemode` 已完整移植；`extensions/codemode/*`（1,227 行）是薄适配层 |
| `types.ts` 对 4e/4f 的引用 | 全部是 `import type`（类型声明），**不需要** 4e/4f 的实现即可编译 |
| llama HTTP 客户端 | 独立（调 llama-server），无 4e/4f 依赖 |

## 2. 文件清单与分批

### 批次 4d-1：契约层（`core/extensions/types.ts`，2,272 行）

| TS 符号 | 说明 |
|---|---|
| `ExtensionAPI` | 扩展入口契约（`registerTool` / `registerCommand` / `registerProvider` / `on` / `exec` …） |
| `ExtensionContext` | 扩展运行时上下文 |
| `Extension` / `ExtensionModule` / `ExtensionFactory` | 扩展描述与工厂 |
| `ExtensionManifest` | 清单（名称、版本、入口、权限） |
| `ToolDefinition`（扩展视角） | 4c 已落 `Core/Extensions/ToolDefinition.cs`，本批次补齐扩展侧字段 |
| `CommandDefinition` / `ProviderDefinition` / `HookDefinition` | 各类注册项 |
| `ExtensionEventMap` | 事件表 |

**注意**：`types.ts` 里引用的 `SessionManager`、`CompactionResult`、`Theme`、
`DynamicBorder`、`KeybindingsManager` 等类型来自 4e/4f。C# 侧做法：
- 能用 `object` 或泛型占位且不影响本批次编译的，先占位并标注 `// 4e/4f 接入后替换`；
- 必须强类型的（如事件负载），在 `Core/Extensions/Types/` 下定义**最小占位记录**，
  与 4e/4f 的真实类型对齐时再收敛。

### 批次 4d-2：加载器（`loader.ts` 1,033 行中的 loader/runner/registry）

| 文件 | 行数 | 要点 |
|---|---:|---|
| `core/extensions/loader.ts` | ~450 | 按 D2 改为 `AssemblyLoadContext`；保留 TS 的清单解析、依赖校验、错误聚合语义 |
| `core/extensions/runner.ts` | ~350 | 扩展生命周期（load → activate → deactivate → unload），异常隔离 |
| `core/extensions/registry.ts` | ~233 | 注册表：tool / command / provider / hook 的分发与冲突检测 |

**TS → C# 关键差异**：
- TS 的 `jiti` 运行时编译 → C# 的 `AssemblyLoadContext`（可卸载上下文，支持热重载）。
- TS 的 `import()` 动态导入 → `Assembly.LoadFrom` + 反射查找 `ExtensionFactory` 入口。
- TS 的 ESM 顶层 await → C# 的异步 `ActivateAsync`。
- 保留 TS 的**错误聚合**语义：一个扩展加载失败不影响其他扩展，错误收集后统一上报。

### 批次 4d-3：内置扩展注册（`extensions/index.ts`，15 行）✅ 已完成（2026-10-10）

> 计划原写 `core/extensions/builtin.ts`（1,151 行）有误：该文件不存在。实际注册表是
> `extensions/index.ts`（15 行），4 项 InlineExtension 描述；`replaceable` / `builtin:` 前缀的
> 消费逻辑在 `core/resource-loader.ts`（4e）。本批已落地注册表 + `AssemblyLoadContext`
> 模块加载器（`IExtensionEntry` 入口约定），4 个 factory 为占位实现，随 4d-4~4d-7 替换。

把内置扩展（mcp / codemode / tool-search / llama）按 D1/D2 注册进加载器。

### 批次 4d-4：MCP 适配层（`extensions/mcp/index.ts`，1,226 行）

底层 `Pi.Mcp` 已就绪，本批次是薄适配：把 `Pi.Mcp` 的客户端能力包成
`ExtensionAPI.registerTool` / `registerProvider` 调用。重点核对：
- MCP server 配置来源（settings / `.mcp.json`）；
- 工具名冲突时的命名空间前缀；
- 连接失败的重试与降级。

### 批次 4d-5：codemode 适配层（`extensions/codemode/*`，1,227 行）

底层 `Pi.Codemode`（QuickJS WASM）已就绪。重点核对：
- `execute.ts` 的沙箱边界（哪些 API 暴露给脚本）；
- `tool.ts` 的工具描述生成（供模型选择）；
- 输出截断与 `structuredContent` 形状。

### 批次 4d-6：tool-search（`extensions/tool-search/tool.ts`，524 行）

BM25 排序 + 工具索引。独立，无外部依赖。

### 批次 4d-7：llama 非 UI 部分（963 行）

`client.ts`（HTTP 客户端）/ `provider.ts`（提供者注册）/ `huggingface.ts`（模型下载）/
`index.ts`（入口）。`ui.ts` 留到 4f。

## 3. 每批次验收标准

1. `dotnet build Pi.slnx` 0 警告 0 错误。
2. 新增/更新的差分测试通过；完整测试套件无回归。
3. 与 TS 逐条核对：**公开 API 表面**（方法名、参数、返回形状）、**错误信息文本**、
   **边界条件**（空输入、重复注册、加载失败、并发注册）。
4. 在 `docs/coding-agent-porting-status.md` 追加本批次记录（文件清单 + 差异编号）。
5. 提交信息说明本批次完成内容，推送前确认构建与测试通过。

## 4. 风险与注意事项

| 风险 | 缓解 |
|---|---|
| `types.ts` 占位类型与 4e/4f 真实类型漂移 | 每个占位处加 `// 4e/4f 接入后替换` 注释，4e/4f 落地时统一收敛 |
| `AssemblyLoadContext` 卸载不彻底导致热重载泄漏 | 参考 `Pi.Codemode` 已有的 WASM 加载/卸载实现，保持弱引用与 `Collect` 策略一致 |
| 扩展加载顺序影响工具注册结果 | 保留 TS 的确定性排序（按清单名称 + 依赖拓扑） |
| MCP/codemode 适配层的工具名冲突策略 | 严格按 TS 的前缀规则，差分测试覆盖 |

## 5. 待办 / 未决

- [x] 4d-1 契约层（含占位类型策略落地）
- [x] 4d-2 加载器（`AssemblyLoadContext` 方案细化）
- [x] 4d-3 内置扩展注册表 + `IExtensionModuleLoader` 的 ALC 实现（factory 占位待 4d-4~4d-7 替换）
- [ ] 4d-4 ~ 4d-7
- [ ] llama `ui.ts` → 4f
- [ ] renderers 的 `renderCall` / `renderResult` → 4f
