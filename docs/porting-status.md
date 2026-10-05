# 移植状态与设计说明

本文记录 Pi → .NET 10 的逐包移植进度、TS → C# 关键设计差异、CBOR 线上兼容要点与路线图。项目功能介绍见 [readme](../readme.md)。

> 参照源码：`D:\AI\参考项目\pi`

## 逐包移植进度

8 个运行时项目，构建 0 警告 0 错误；**254 项测试全部通过**（2026-10-05 实测）。

| TS 包（packages/） | .NET 项目（src/） | 源码规模 | 状态 | 测试 |
|---|---|---|---|---|
| telemetry | **Pi.Telemetry** | 0.9k 行 | ✅ 完整移植，含 conformance 套件 | 12 ✅ |
| protocol | **Pi.Protocol** | 0.9k 行 | ✅ 完整移植：CBOR / framing / codec | 14 ✅ |
| agent | Pi.Agent | 2.5k 行 | ✅ 完整移植（除 proxy.ts）：Agent 类（状态机 / 双队列 / 订阅 / abort / reset）+ agent-loop 主循环 + sequential / parallel 工具执行 | 13 ✅ |
| mcp | Pi.Mcp | 3.2k 行 | ✅ 完整移植：JSON-RPC 协议层 + 传输层全套（in-memory / stdio / streamable-http）+ McpClient 会话 + OAuth 全层（发现 / PKCE 授权码 / 动态注册 / 凭据失效重试 / MemoryStateStore / 本地回调服务器） | 19 ✅ |
| server / client | Pi.Server / Pi.Client | 3.1k 行 | ✅ 核心完整移植：RpcServer（TCP listener / 会话循环 / hello 握手校验 / 请求分发 / cancel / service_update 推送）+ RpcClient（握手 / 请求超时与取消 / service 订阅 / 关闭清理），端到端验证通过 | 2 ✅ |
| ai | Pi.Ai | 26.3k 行 | 🚧 主体完成，详见下节 | 151 ✅ |
| chord | Pi.Chord | 8.8k 行 | 🚧 delta 引擎全部 + services 核心 + facets（依赖图 / 拓扑激活 / reload / 服务槽接线 / 外部源绑定：目录发现 → offered 去重 → deferred 延迟源 → 按源分组 open → 就绪门 → 门面绑槽；Require / Use 两阶段）+ Context 值链；仅剩 node bundler | 43 ✅ |
| coding-agent | （未建） | 85k 行 | ⏳ 待 tui / codemode / durable 之后分阶段移植 | — |
| tui / codemode / durable / evals | （未建） | 41k 行 | ⏳ 后续阶段 | — |

### Pi.Ai 详情（26.3k 行，151 项测试）

已完成：

- **核心**：类型层 + EventStream + faux；ModelCatalog + Models 门面（GetModelsOfType / GenerateImagesAsync / ClassifyAsync）+ ProviderRegistry；GoogleThinking；RetryPolicy。
- **真实 API 样板**：openai-completions / anthropic-messages / google-generative-ai。
- **images / classify（P27）**：ImagesApiRegistry / ImagesApi 门面 + openrouter-images API；llama-cpp-classify（三端点 / 标签 token 缓存 / 深度升级 / softmax）；IImagesProvider / IClassifierProvider 能力接口。
- **TransformMessages（P27）**：图片降级 / 思维规范化 / 工具 ID 归一化 / 孤儿合成 / system 透传 / error 跳过。
- **请求基础设施**：Headers / SanitizeUnicode / ProviderError / ProviderRetry / ModelOperations。
- **Auth 层**：EnvApiKey / 凭据存储 / OAuthAuth 刷新编排 + AuthResolve 双检锁认证解析（stored credential 拥有 provider、15s 刷新超时、ModelsError 包装）。
- **OAuth 登录交互层（P26）**：AuthPrompt / AuthEvent 判别联合 + IAuthInteraction / ProviderAuthInteraction + PKCE + loopback 回调服务器（complete 先行 / claimed-settled / cancel / close / 超时）+ RFC 8628 设备码轮询（slow_down 增间隔 / 服务器下发优先）+ OAuthFlows 懒加载注册表。
- **9 家 provider OAuth 流**：anthropic（browser + copy_code）、openai-codex（browser + device + JWT accountId）、openai-chatgpt（动态 client + 专用 1455 回调）、openrouter（永久 key）、kimi-coding（设备码 + 刷新退避）、meta（设备码 + key 铸造）、radius（browser + device + discovery）、xai（设备码 + refresh 保留）、github-copilot（设备流 + 模型目录 + policy 启用 + proxy-ep baseUrl）。
- **Credential 扩展字段**：accountId / clientId / scopes / enterpriseUrl / availableModelIds / gatewayConfig / scope。

剩余：openai-responses / azure / bedrock / vertex / mistral-conversations / cloudflare / pi-messages 等 API；deferred；compat.ts / legacy-api-aliases 兼容层。

## 目录约定

```
pi.net/
├── Pi.slnx                    # 解决方案（新 .slnx 格式）
├── Directory.Build.props      # net10.0 / nullable / latest / 警告即错误
├── src/Pi.<Package>/          # 对应 packages/<pkg>
└── tests/Pi.<Package>.Tests/  # xunit.v3（MTP 运行器）
```

测试基于 xunit.v3 + Microsoft.Testing.Platform（MTP）。若 `dotnet test` 未触发执行（MTP 通道在部分环境下不生效），直接运行各测试项目的构建产物即可：`tests/Pi.<Pkg>.Tests/bin/Debug/net10.0/Pi.<Pkg>.Tests.exe`。

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

## 路线图

1. **ai 包收尾**：openai-responses / azure / bedrock / vertex / mistral-conversations / cloudflare / pi-messages 等 API 与 deferred → compat.ts / legacy-api-aliases 兼容层。
2. **chord 包**：仅剩 node bundler（esbuild 打包器的 C# 等价物，非运行时核心）。
3. **server/client**：chord services 集成（session-router、多服务路由）与 unix socket / named pipe 监听器。
4. **agent 包**：proxy.ts。
5. **coding-agent**：85k 行主产品（会话 / 工具系统 / 技能 / 主题 / RPC 模式），最后阶段按"核心命令最小闭环 → 逐步补全"推进。
6. **tui / codemode / durable / evals**：41k 行，最后阶段。
