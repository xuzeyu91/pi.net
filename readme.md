# Pi .NET 10 移植

将 [Pi](https://github.com/earendil-works/pi)（极简可扩展的 AI Coding Agent harness，TypeScript monorepo，约 17.1 万行）移植为基于 **.NET 10**（`net10.0`）的版本。源码参照：`D:\AI\参考项目\pi`。

## 项目映射与移植状态

| TS 包（packages/） | .NET 项目（src/） | 源码规模 | 本轮状态 |
|---|---|---|---|
| telemetry | **Pi.Telemetry** | 0.9k 行 | ✅ 完整移植 + conformance 测试 12/12 通过 |
| protocol | **Pi.Protocol** | 0.9k 行 | ✅ 完整移植（CBOR/framing/codec）+ 测试 14/14 通过 |
| ai | Pi.Ai | 26.3k 行 | 🚧 核心类型层 + EventStream + faux + 两个真实 provider（**openai-completions**：delta 模型 + **anthropic-messages**：content_block 事件模型/tool_result 块/system 独立字段）+ 测试 6/6 通过；兼容层与其余 provider 待移植 |
| agent | Pi.Agent | 2.5k 行 | ✅ 完整移植（除 proxy.ts）：Agent 类（状态机/双队列/订阅/abort/reset）+ agent-loop 主循环 + sequential/parallel 工具执行；测试 13/13 通过 |
| mcp | Pi.Mcp | 3.2k 行 | 🚧 JSON-RPC 协议层 + 传输层全套（in-memory / stdio / streamable-http）+ McpClient 完整会话 + **OAuth 层**（元数据/令牌/客户端信息结构化解析、WWW-Authenticate 挑战、受保护资源与授权服务器发现、RFC 9207 issuer 校验）；测试 15/15 通过；仅 OAuth flow（PKCE 授权码流程）与 callback/provider 待移植 |
| chord | Pi.Chord | 8.8k 行 | 🚧 JSON 契约 + **delta 引擎**（Op/路径安全/不可变 applier/diff/**wire 编解码（path 驻留+元数省略）**/revision-validator）+ **services 协议层**（控制调用/wire 快照与更新解析/状态编解码器注册表）+ 测试 15/15 通过；tracker（Proxy 可变视图）与 services/state+provider/facets 待移植 |
| server / client | Pi.Server / Pi.Client | 3.1k 行 | ✅ **核心完整移植**：RpcServer（TCP listener/会话循环/hello 握手校验/请求分发/cancel/service_update 推送）+ RpcClient（握手/请求超时与取消/service 订阅/关闭清理）；端到端测试 2/2 通过 |
| coding-agent | （未建） | 85k 行 | ⏳ 待 tui/codemode/durable 之后分阶段移植 |
| tui / codemode / durable / evals | （未建） | 41k 行 | ⏳ 后续会话 |

测试项目共 7 个：Pi.Telemetry(12) / Pi.Protocol(14) / Pi.Agent(13) / Pi.Mcp(15) / Pi.Chord(15) / Pi.Ai(6) / Pi.Server(2)，合计 **77 项全部通过**。
构建：`dotnet build Pi.slnx`（当前 0 警告 0 错误）。

## 目录约定

```
pi.net/
├── Pi.slnx                    # 解决方案（新 .slnx 格式）
├── Directory.Build.props      # net10.0 / nullable / latest / 警告即错误
├── src/Pi.<Package>/          # 对应 packages/<pkg>
└── tests/Pi.<Package>.Tests/  # xunit.v3（MTP 运行器）
```

## 关键设计差异（TS → C#）

1. **Task 不自动展开（最重要的语义差异）**。TS 的 `Promise.resolve(p)` 会自动展平嵌套 promise，因此原版 callback 可以返回 `T | Promise<T>`；C# 的 `Task<Task<T>>` 不展开。telemetry 曾因此出现"同步重载把 `T` 推断成 `Task<object>` 导致取消/异常被吞"的 bug。**结论：凡是 TS 里"可同步可异步"的回调，C# 统一为单一 `Func<..., Task<T>>` 签名，同步值用 `Task.FromResult` 包装**。
2. **TS 判别联合 → C# abstract record + 密封嵌套 record**，`switch` 模式匹配替代 `type` 字段判别。注意三个坑：嵌套 record 位置参数名不能与 record 类型名相同（`Error(Error?)` 会和拷贝构造器产生二义）；record positional 参数生成 `{ get; init; }`，不能直接 override 基类 `{ get; }` 抽象属性；`private` 主构造器子类不可 base 调用，用 `private protected`。
3. **typebox → 手写校验 + CBOR 值模型**。protocol 的 schema 校验（StrictObject / minLength / pattern / literal / union）用手工校验器复刻，值模型统一为 `object?`（null/bool/long/double/string/byte[]/List/Dictionary）。整数统一 `long` 并保留 JS safe-integer（±2^53-1）边界检查以维持线上兼容。
4. **deepStrictEqual → 手写深比较 / JSON 序列化比较**。C# 字典与 record 嵌套成员是引用相等，conformance 套件统一走 JSON 规范化比较。
5. **JS Proxy 不可读对象 → 抛异常的派生类**。conformance 的 passivity/原子性用例用"枚举即抛"的 `SpanAttributes` 派生类模拟，行为等价（payload 失败被吞、已有状态不变）。
6. **扩展系统改 C# 原生插件**（用户决策）：原版 jiti 运行时加载 TS 扩展不可原生等价，改为 AssemblyLoadContext 动态加载，牺牲 TS 扩展生态兼容。
7. **jstruct 侧的宿主差异**：`unix socket` → Windows 上 named pipe / `UnixDomainSocketEndPoint` 双轨；`cross-spawn` → `System.Diagnostics.Process`；`undici/fetch` → `HttpClient`；`diff` → DiffPlex 或自实现 Myers（待定）；`quickjs-wasi`（codemode）→ Jint/ClearScript（待定，用户已选 C# 插件优先）。

## CBOR 线上兼容要点（已测试锁定）

- definite-length 严格子集：拒 tag、拒 indefinite、拒非 finite、整数限 safe range；
- TS `undefined` 语义 → C# `null`：map 条目在**写 count 之前**过滤；
- depth 从 0 计数（`depth > maxDepth` 才拒绝），`MaxDepth: 0` 仍可编码标量。

## 后续会话路线图

1. **ai 包**：model catalog（JSON 资源替代 models.generated.ts）→ 其余 ~20 家 provider（lazy 注册模式，两大事件模型样板已就位）→ reasoning details/cache control/thinking budgets 兼容层。openai-completions 与 anthropic-messages 核心/types/EventStream/faux 已完成。
2. **agent 包**：agent.ts（Agent 类：状态机/队列/subscribe API）+ proxy.ts；agent-loop 主循环与工具执行已完成。
3. **chord 包**：delta 引擎 + services 协议层已完成；剩余 tracker.ts（JS Proxy 可变视图 → C# 需选型 DynamicObject 或显式 API）、services/state.ts+provider.ts（订阅状态机）、facets/host → node bundle。（json.ts 已完成）
4. **mcp 包剩余**：OAuth flow.ts（PKCE 授权码流程）+ callback.ts + provider.ts（types/errors/discovery 已完成）。
5. **server/client**：TCP 核心链路已完成（握手/请求/取消/推送）；chord services 集成（session-router、多服务路由）与 unix socket/named pipe 监听器待续。
6. **coding-agent**：85k 行主产品（会话/工具系统/技能/主题/RPC 模式），最后阶段按"核心命令最小闭环 → 逐步补全"推进。
