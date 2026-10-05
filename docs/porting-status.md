# 移植状态与设计说明

本文记录 Pi → .NET 10 的逐包移植进度、TS → C# 关键设计差异、CBOR 线上兼容要点与路线图。项目功能介绍见 [readme](../readme.md)。

> 参照源码：`D:\AI\参考项目\pi`

## 逐包移植进度

8 个运行时项目，构建 0 警告 0 错误；**429 项测试全部通过**（2026-10-05 实测）。

| TS 包（packages/） | .NET 项目（src/） | 源码规模 | 状态 | 测试 |
|---|---|---|---|---|
| telemetry | **Pi.Telemetry** | 0.9k 行 | ✅ 完整移植，含 conformance 套件 | 12 ✅ |
| protocol | **Pi.Protocol** | 0.9k 行 | ✅ 完整移植：CBOR / framing / codec | 14 ✅ |
| agent | Pi.Agent | 2.5k 行 | ✅ 完整移植（除 proxy.ts）：Agent 类（状态机 / 双队列 / 订阅 / abort / reset）+ agent-loop 主循环 + sequential / parallel 工具执行 | 13 ✅ |
| mcp | Pi.Mcp | 3.2k 行 | ✅ 完整移植：JSON-RPC 协议层 + 传输层全套（in-memory / stdio / streamable-http）+ McpClient 会话 + OAuth 全层（发现 / PKCE 授权码 / 动态注册 / 凭据失效重试 / MemoryStateStore / 本地回调服务器） | 19 ✅ |
| server / client | Pi.Server / Pi.Client | 3.1k 行 | ✅ 核心完整移植：RpcServer（TCP listener / 会话循环 / hello 握手校验 / 请求分发 / cancel / service_update 推送）+ RpcClient（握手 / 请求超时与取消 / service 订阅 / 关闭清理），端到端验证通过 | 2 ✅ |
| ai | Pi.Ai | 26.3k 行 | ✅ 完整移植（10 内建 API + 42 provider + compat + models-store + 全部 utils + cli） | 283 ✅ |
| chord | Pi.Chord | 8.8k 行 | ✅ 完整移植（delta / services / facets / Context / node 层 / json / api.ts / handle.ts / **consumer.ts + loopback.ts**）；仅 `index.ts` 桶文件未做 | 86 ✅ |
| coding-agent | （未建） | 85k 行 | ⏳ 待 tui / codemode / durable 之后分阶段移植 | — |
| tui / codemode / durable / evals | （未建） | 41k 行 | ⏳ 后续阶段 | — |

### Pi.Ai 详情（26.3k 行，283 项测试）

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
- **真实 API 齐平（P28–P32）**：10 个内建 API 全部就位——openai-responses（含 codex / azure 变体）、google 家族（shared + generative-ai + vertex）、mistral-conversations、pi-messages、bedrock-converse-stream（SigV4 + AWS event-stream 二进制帧）。ApiRegistry 内建 10 个 API 注册与 TS `BUILTIN_APIS` 齐平。
- **deferred 全链路（P33）**：`DeferredHandle` + `AssistantMessage.Deferred` + `StopReason.Deferred`；`ProviderStreams.fetchDeferred/cancelDeferred`；`IProvider.StreamDeferred/CancelDeferredAsync`；`Models.StreamDeferred/FetchDeferredAsync/CancelDeferredAsync`；`Api/Lazy.cs`（`LazyStream` / `LazyApi`）。
- **createProvider 工厂（P33）**：`ProviderFactory.Create` —— 单 api 服务全部 chat 模型、api 映射按 `model.api` 分派（缺条目 → error 流）、`images`/`classifiers` 一次性操作分派（缺条目 → error 结果）、`filterModels` 凭据策略、至少一个实现校验。
- **provider 全家桶（P33）**：42 家内建 provider（`BuiltinProviders.Create`），含 api 映射家（fireworks / github-copilot / opencode / opencode-go / openrouter / cloudflare-ai-gateway）、纯分类家（typesafe / cloudflare-workers-ai）、动态家（radius）；非标准认证（bedrock AWS 凭据链、vertex ADC、cloudflare 按字段合并、anthropic auth-token/联合身份）；`All`（all.ts）：`getBuiltinModel(s)` / `getBuiltinImageModel(s)` / `getBuiltinClassifierModel(s)` / `getBuiltinProviders` / `getAllBuiltinModels` / `builtinProviders()` / `builtinModels()`。
- **compat 目录集成（P33）**：`Compat.Stream/CompleteAsync/StreamSimple/CompleteSimpleAsync`（环境密钥注入 + 内建 provider 归属判定 + cloudflare 未认证回退）、`GetModel/GetModels/GetProviders` 别名、`RegisterFauxProvider`；`EnvApiKeys`（env-api-keys.ts：`findEnvKeys`/`getEnvApiKey` + 全 env 映射 + Vertex ADC + Bedrock 多凭据源 + `<authenticated>` 标记）。

- **目录结构 1:1 对齐 pi（P34）**：见下节「Pi.Ai 目录映射」。
- **根级模块补齐（P34）**：`ModelsStore.cs`（models-store.ts：`ModelsStoreEntry` / `IModelsStore` / `InMemoryModelsStore`）、`ImageModels.cs`（image-models.ts 兼容读取）、`Compat/ExtensionOAuthTypes.cs`（compat/extension-oauth-types.ts）、`OAuth.cs`（oauth.ts 类型入口）。
- **utils 与 cli 收尾（P35）**：
  - `Utils/Retry.cs`（retry.ts）：assistant-turn 重试策略——`RetryPolicy`（enabled/maxRetries/baseDelayMs/maxAgentDelayMs）、`RetryDelayMs`（指数退避 + safe-integer 处理 + 60s 默认上限）、`IsRetryableAssistantError`（可重试/不可重试两套错误文案正则，含订阅限额与网络/WS/流提前结束）、`AssistantCallAsync`（中止永不重试、退避期中止归一为 Aborted 消息、三个回调）。
  - `Utils/Overflow.cs`（overflow.ts）：25 条各 provider 超窗文案 + Cerebras 无 body 特例 + 三条非溢出排除（限流）；`IsContextOverflow` 三形态（错误文案 / 静默溢出 / length 截断溢出）、`IsRecoverableLength`。
  - `Utils/Validation.cs`（validation.ts）：typebox `Compile`/`Value.Convert` 在 C# 侧以手写 JSON Schema 校验器复刻（同 Pi.Protocol 路线）——可选 null 归一、按 schema 强制类型转换（allOf/anyOf/oneOf 联合、对象/数组递归、additionalProperties）、校验与 `路径: 文案` 错误格式化。
  - `Utils/AssistantMessageFrame.cs`（assistant-message-frame.ts）：紧凑可回放帧——11 种帧类型、`AssistantMessageFrameEncoder`（逐块偏移避免重放已覆盖增量、toolcall catch-up + JSON 前缀判定、终态不出帧）、`AssistantMessageFrameReducer`（回放为消息、gap/重复 start/块类型不符全部报错）。
  - `Utils/NodeHttpProxy.cs`（node-http-proxy.ts）：`<scheme>_proxy`/`all_proxy` 取值、`no_proxy` 规则（`*`、`*.domain`、`.domain`、`host:port`、裸 IPv6）、SOCKS/PAC 拒绝。
  - `Utils/TypeboxHelpers.cs`（typebox-helpers.ts）：`StringEnum` → JSON Schema 节点。
  - `Cli.cs`（cli.ts）：`login [provider]` / `list` / `help`，交互注入 TextReader/TextWriter 便于测试，凭据写入 `auth.json`。
  - `Types/Messages.cs` 补 `AssistantMessage.ResponseModel`（types.ts 的 <c>responseModel</c>）。

ai 包至此与 pi 1:1 齐平（除下表列出的三处刻意归并）。

### Pi.Chord 详情（8.8k 行，86 项测试）

已完成：

- **delta 引擎**：wire 编解码、diff/apply、revision 校验、Tracker 显式动词 API。
- **services**：ReplicatedState（发布者/副本/附加源）、RemoteServiceProvider、Endpoint、StateCodec、ServiceWire、Instances。
- **facets**：FacetKernel（依赖图 / Kahn 拓扑 / reload / 原子激活与回滚）、FacetLifecycle、外部服务源绑定（Require / Use 两阶段）。
- **Context 值链**。
- **node 层（P36）**：`Node/Manifest.cs`（格式常量 + 条目/清单/artifact 记录）、`Node/Bundle.cs`（`bundleFacets`：逐 entry 内容寻址打包 → 清单落盘 → 「临时目录 + 原子替换」，含输出路径/多余产物/选项校验与 SHA-256 integrity）、`Node/BundleLoader.cs`（清单与 artifact 校验、完整性校验、相对文件名解析、包 specifier 校验、`createFacetBundleLoader` / `createFacetBundleArtifactLoader`）、`Node/Package.cs`（package.json 元数据 + 约定/配置 entry 解析 + 目录逃逸防护 + peerDependencies/chord.external 展开）、`Bundler.cs` / `Node.cs`（bundler.ts / node.ts 的入口表面）、`Json.cs`（json.ts：严格 JSON 契约 + 无别名深拷贝 + 环检测）。
- **类型补齐**：`IFacetLoader` / `LoadedFacets`（拆卸后 facets 清空）/ `FacetHost`。
- **api.ts（P37）**：`Api.CreateFacetHostAsync` / `CreateStaticFacetLoader` / `CombineFacetLoaders`（任一失败反序清理、错误聚合、拆卸幂等）/ `DefineFacet` / `DefineService`（空 id 与 `$chord.` 保留命名空间校验）/ `ReplicatedState`（可变 / 附加到授权源两个重载）。
- **services/handle.ts（P37）**：`ServiceSlot`（`Bind`/`Unbind`/`View<T>`/`Resolve` 成员解析：成员字典 → 公有属性 → 公有字段）+ `ResolvedServiceMember` + 成员调用（`Func<object?[], object?>` / `Delegate.DynamicInvoke`）。
- **facets/loader.ts 对齐（P37）**：`FacetLoader.DisposeLoadedFacetsAsync` 改为 TS 忠实的 `LoadedFacets[]` 并行拆卸 + 按输入序聚合；原 FacetKernel 版本改名 `DisposeKernelsAsync`。
- **骨架清理（P37）**：删除 `ChordTypes.cs`——`Json` 已迁至 `Json.cs`、`ChordApi` 由 `Api.cs` 取代、`IDraft` 是 TS 的**类型级**递归 readonly（`delta/draft.ts` 仅 10 行 type alias），C# 无运行期对应物故不保留。

- **services/consumer.ts + loopback.ts（P38）**：`IRemoteServiceTransport` / `IRemoteServiceSubscription` / `RemoteServiceBindingOptions`、`RemoteServiceMember`（方法可调用 + 状态可读可订阅，含 `#expect` 种类状态机与 `setDescription`）、`RemoteServiceFacade`（按成员名取句柄 + `IReadOnlyDictionary<string, object?>` 视图）、`KeyedBinding<T>`（`InstanceDirectory` 驱动的 spawn/rebind/reset 处理）、`RemoteServiceBinding`（allowlist / 模式锁 / 就绪门 / rebind / dispose 错误聚合 / `UseRaw`）、`LoopbackServiceTransport.Create(provider)`。
- **api.ts 补齐**：`Api.CreateRemoteServiceBinding(options)`。

剩余：`index.ts` 桶文件（C# 无桶文件概念，公开面由各类型可见性决定）、`types.ts` 中少数仅供 TS 类型推导的别名（如 `Draft<T, Depth>`）。

### Pi.Ai 目录映射（packages/ai/src → src/Pi.Ai，1:1）

| TS | .NET | 说明 |
|---|---|---|
| `types.ts` | `Types/`（`Messages.cs` / `AssistantMessageEvent.cs` / `Classifier.cs` / `Images.cs` / `ProviderStreams.cs` / `DeferredHandle.cs`） | 判别联合 → abstract record |
| `models.ts` / `model-catalog.ts` / `models-store.ts` | `Models/Models.cs` / `Models/ModelCatalog.cs` / `Models/CreateProvider.cs` / `ModelsStore.cs` | `createProvider` 单列一文件 |
| `api/<name>.ts` | `Api/<Name>.cs`（`AnthropicMessages` / `OpenAiCompletions` / `OpenAiResponses` / …） | 10 个内建 API + `SimpleOptions` / `ConstrainedSampling` / `TransformMessages` 等 |
| `api/lazy.ts` + `api/<name>.lazy.ts` | `Api/Lazy.cs` + `Api/LazyApis.cs` | C# 无动态 import，`LazyApis.<Name>Api()` 即各 `.lazy.ts` 的模块句柄 |
| `auth/*.ts` / `auth/oauth/*.ts` | `Auth/*.cs` / `Auth/OAuth/*.cs` | `auth/helpers.ts` → `Auth/Helpers.cs` |
| `providers/all.ts` | `Providers/All.cs` | `builtinProviders()` / `builtinModels()` / `getBuiltin*` |
| `providers/<name>.ts` | `Providers/<Name>.cs`（42 个） | 每家一个工厂文件，静态类 `<Name>.Provider()` 对应 `xxxProvider()` |
| `providers/cloudflare-auth.ts` / `cloudflare-stream.ts` / `opencode-headers.ts` / `radius-config.ts` / `radius.ts` / `faux.ts` | `Providers/CloudflareAuth.cs` / `CloudflareStream.cs` / `OpenCodeHeaders.cs` / `RadiusConfig.cs` / `Radius.cs` / `Faux.cs` | 同名同职责 |
| `providers/images/register-builtins.ts` | `Providers/Images/RegisterBuiltins.cs` | 同名 |
| `utils/<name>.ts` | `Utils/<Name>.cs` | 逐文件对应 |
| `env-api-keys.ts` / `legacy-api-aliases.ts` / `images.ts` / `images-api-registry.ts` / `image-models.ts` / `session-resources.ts` / `compat.ts` | `EnvApiKeys.cs` / `LegacyApiAliases.cs` / `Images.cs` / `ImagesApiRegistry.cs` / `ImageModels.cs` / `SessionResources.cs` / `Compat.cs` | 根级文件 |
| `compat/extension-oauth-types.ts` / `oauth.ts` | `Compat/ExtensionOAuthTypes.cs` / `OAuth.cs` | 扩展 OAuth 兼容类型 |

三处刻意的归并（其余保持 1:1）：

1. **`providers/<name>.models.ts` × 42 + `models.generated.ts`** → 嵌入资源 `ModelData/<provider>.json` + `Providers/BuiltinCatalog.cs`。TS 的 `.models.ts` 只是 `flatten*ModelCatalog` 的薄壳，且生成产物不入库；C# 以「按 provider 键控的读取器 + 嵌入 JSON」承载（与 P18 的既定处理一致）。
2. **`index.ts` 桶文件** → C# 无桶文件概念，公开面由各类型自身的可见性决定；`index.ts` 的「core only、无副作用」约定改为在本文档与各文件注释中说明。
3. **`bun-oauth.ts` / `bedrock-provider.ts`** → 前者是「把 OAuth 流程静态注册进 Bun 单文件二进制」，C# 无打包器，`OAuthFlows` 的静态注册表即其等价物；后者只是 `api/bedrock-converse-stream.ts` 的模块重导出，C# 由 `LazyApis.BedrockConverseStreamApi()` 承载。

另：P33 之前 C# 自造的三个抽象（`ProviderRegistry` / `DedicatedProviderRegistry` / `OpenAiCompatibleProvider`）在 P34 删除——pi 没有对应物，其职责由 42 个 `providers/<name>.ts` 文件与 `providers/all.ts` 承担。

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
8. **compat 的 api-dispatch 回退走简单选项桥接**：TS `compat.stream` 把完整选项透传给注册表里的 API 实现；C# 的 `ApiProvider` 完整选项入口是 wire `JsonObject`（api-dispatch 用 JSON 反序列化 provider 专属选项），而 provider 边界统一为 `IReadOnlyDictionary<string, object?>`。compat 因此把字典选项经 `ProviderStreamOptions.FromDictionary` 桥接为 `SimpleStreamOptions` 再派发；apiKey / baseUrl / headers / env / reasoning 等公共字段语义一致。内建 provider 归属判定用引用比较快照（TS 的 `!==`），未注册的内建 api（anthropic-messages / openai-completions）快照为 null，因此「未被覆盖」与「被覆盖」都判定正确。
9. **Radius 动态目录的持久化**：TS 经 ModelsStore 的 `context.publish` 事务化持久化刷新结果；C# 侧持久化上下文（ModelsStore）尚未移植，`RadiusProvider.RefreshModelsAsync` 只做内存更新，持久化由调用方按需处理。
10. **`compat.ts` 的静态门面类名取 `CompatApi`**：C# 无法让类型 `Compat` 与命名空间 `Pi.Ai.Compat`（来自 `compat/` 目录）同名共存，故模块类名加 `Api` 后缀（`compat/extension-oauth-types.ts` 仍落在 `Pi.Ai.Compat`）。
11. **chord 打包/加载的两处注入点**：TS 用 esbuild 打包、用 `node:vm` 的 `compileFunction` 执行 bundle；C# 无等价物，故拆成 `IFacetEntryBundler`（单 entry 打包，请求里带全量 esbuild 配置：banner / format cjs / entryNames / outExtension / supported dynamic-import=false / target 缺省）与 `IFacetModuleHost`（CommonJS 模块执行）两个接口。默认模块宿主 `UnsupportedFacetModuleHost` 明确拒绝 JS bundle（扩展已改为 AssemblyLoadContext 插件路线），注入宿主即可运行 JavaScript bundle。
12. **chord `FacetHost.Services` 的前置条件**：TS 的 `assembleProviders()` 无条件创建 `RemoteServiceProvider`（目录含全部非 local 供给），因此 `kernel.provider` 恒可用；C# 因无法在编译期约束「实现必须是成员字典」，只在存在**可发布供给**（成员字典且成员为 `Func<object?[], object?>` 或 `IReplicatedStateInternals`）时才创建 provider，故 `FacetHost.Services` 在纯本地装配下会抛「not assembled」。另外 TS 在该阶段还会建内部 loopback 绑定（`createLoopbackServiceTransport`），属 `services/consumer.ts` 范畴，C# 待其落地后补齐。
13. **chord 远程服务门面改显式 API**：TS 的 `MemberSlot`/`ServiceFacade` 用 `Proxy` 让「一个成员」同时是函数与对象（apply + get），消费者写 `svc.echo(args, ctx)` / `svc.doc.value`；C# 无 Proxy，改为：`RemoteServiceFacade.Member(name)` 取 `RemoteServiceMember`，方法用 `CallAsync(args, ctx)` / `AsCallable()`（尾随 `Context` 约定），状态用 `Value` / `Subscribe`；门面本身实现 `IReadOnlyDictionary<string, object?>`（键 = 最近快照的成员名），因此 `UseAs<T>` 只在 `T` 为成员字典视图或 `object` 时可用。另外 TS 的 `use()` 直接返回 `Proxy`，C# 返回门面（`Use<T>`）+ 转型便捷法（`UseAs<T>`）。
14. **`node.ts` 的门面类名取 `NodeApi`**：C# 无法让类型 `Node` 与 `node/` 目录产生的命名空间 `Pi.Chord.Node` 同名共存（同 `CompatApi`）。
15. **`ModelAuth.Headers` 的 null 抑制**：TS 允许 `Authorization: null` 抑制同名默认头；C# 的 `ModelAuth.Headers` 值类型为 `string?` 以保留该语义（Cloudflare AI Gateway 用 `cf-aig-authorization` 并抑制默认 Authorization / x-api-key）。

## CBOR 线上兼容要点（已测试锁定）

- definite-length 严格子集：拒 tag、拒 indefinite、拒非 finite、整数限 safe range；
- TS `undefined` 语义 → C# `null`：map 条目在**写 count 之前**过滤；
- depth 从 0 计数（`depth > maxDepth` 才拒绝），`MaxDepth: 0` 仍可编码标量。

## 路线图

1. **ai 包**：✅ 已完成（目录结构 1:1 对齐 pi）。
2. **chord 包**：✅ 已完成（除 `index.ts` 桶文件）。
3. **server/client**：chord services 集成（session-router、多服务路由）与 unix socket / named pipe 监听器。
4. **agent 包**：proxy.ts。
5. **coding-agent**：85k 行主产品（会话 / 工具系统 / 技能 / 主题 / RPC 模式），最后阶段按"核心命令最小闭环 → 逐步补全"推进。
6. **tui / codemode / durable / evals**：41k 行，最后阶段。
