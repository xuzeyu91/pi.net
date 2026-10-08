# Pi.NET

Pi.NET 是 [Pi](https://github.com/earendil-works/pi) 的 .NET 10 移植版。Pi 是一个极简、可扩展的 Agent harness（TypeScript monorepo，约 17.1 万行），本项目把它的核心库逐一移植到 `net10.0`，让 .NET 生态用上同一套能力。

Pi 的理念是「让 Pi 适配你的工作流，而不是反过来」：库只做最小而完整的事，上层由你自己组合。本项目沿用这一理念——不是把 TS 代码翻成 C# 就完事，而是在语义等价的前提下用 C# 的方式重建（判别联合 → abstract record 模式匹配、JS Proxy → 显式动词 API）。

## 它能做什么

- **统一的多 Provider LLM API**——10 个内建 API 实现（openai-completions / openai-responses / openai-codex-responses / azure-openai-responses / anthropic-messages / google-generative-ai / google-vertex / mistral-conversations / bedrock-converse-stream / pi-messages）+ **42 家内建 provider 全家桶**；流式事件、工具调用、thinking 预算、延后响应（deferred）、图片生成（images）与文本分类（classify）；9 家 provider 的 OAuth 登录流（PKCE / RFC 8628 设备码 / loopback 回调 / 刷新编排）。
- **Agent 运行时**——Agent 状态机与双队列、agent-loop 主循环、顺序 / 并行工具执行、abort / reset。
- **MCP 客户端**——JSON-RPC 协议层 + in-memory / stdio / streamable-http 三种传输 + 完整 OAuth 层（元数据发现、PKCE 授权码、动态注册、凭据失效重试）。
- **应用编排运行时（Chord）**——服务与复制状态、delta 引擎、facets 依赖图与拓扑激活、外部服务源绑定。
- **TCP RPC 通道**——握手校验、请求分发、取消、推送，为远程控制 coding-agent 准备。
- **供应商中立的遥测**——契约 + 参考适配器 + conformance 测试。

## 包

| 包 | 说明 |
|---|---|
| **Pi.Telemetry** | 供应商中立的遥测契约、参考适配器与类型化 schema，带 conformance 测试套件 |
| **Pi.Protocol** | CBOR 线上协议：framing / codec，definite-length 严格子集 + JS safe-integer 边界，与 TS 实现**线上兼容** |
| **Pi.Ai** | 统一多 Provider LLM API：原生 API 样板 + 兼容商注册表；流式、工具调用、thinking、images / classify、OAuth 登录全流程 |
| **Pi.Agent** | Agent 运行时：状态机 / 双队列、agent-loop 主循环、工具执行 |
| **Pi.Mcp** | MCP 客户端：JSON-RPC + 三种传输 + OAuth 全层 |
| **Pi.Chord** | 独立的应用编排运行时：服务、复制状态、RPC 与插件 |
| **Pi.Server / Pi.Client** | RPC 传输：握手 / 请求 / 取消 / 推送、会话路由、Unix 域套接字与本地服务器发现 |
| **Pi.Codemode** | 沙箱化 JavaScript 执行：唯一能力是调用注入的工具；@options 源码解析、TypeScript 声明渲染与沙箱编排（VM 执行经 `ICodemodeJsEngine` 注入点外置） |
| **Pi.Tui** | 终端 UI 框架：差分渲染 + 同步输出、字素级宽度测量与换行、ANSI/OSC 解析与 SGR 跟踪、Kitty 键盘协议与按键解码、overlay 栈、焦点管理、鼠标事件派发、颜色（OKLCH/OKHSL）与键位注册表、stdin 转义序列缓冲、终端内联图像（Kitty / iTerm2 协议）、栈/滚动布局引擎、LaTeX 数学渲染、斜杠命令与文件路径自动补全（含 `fd` 模糊搜索）、单行输入组件（Emacs 风格 kill/yank、撤销、括号粘贴）、选择列表组件（过滤/滚动窗口/鼠标/滚轮） |

> tui 进行中（44/45 文件，见 [tui 移植状态](docs/tui-porting-status.md)）；coding-agent（交互式 CLI 主产品）与 evals 尚未移植。durable / codemode 已完整移植，见[移植状态](docs/porting-status.md)。

## 快速开始

需要 .NET 10 SDK。

```bash
dotnet build Pi.slnx    # 0 警告 0 错误（沙箱内需加 -m:1）
dotnet test  Pi.slnx    # 24657 项测试
```

测试基于 xunit.v3 + Microsoft.Testing.Platform（MTP）。若 `dotnet test` 未触发执行，直接运行测试产物：

```bash
tests/Pi.<Pkg>.Tests/bin/Debug/net10.0/Pi.<Pkg>.Tests.exe
```

## 移植状态

11 个运行时项目，构建 0 警告 0 错误，**24657 项测试**（2026-10-09 实测，P64 更新；durable 340 + tui 23812 + 其余 505；其中 tui 含 2822 条 latex、5244 条 autocomplete、5450 条 input、2598 条 select-list、7040 条 editor、4990 条 markdown、1692 条 extra-components 差分向量与 9109 条大小写映射向量，另有 37 条备用屏场景）。durable 满负荷时的间歇性挂起见 [docs/porting-status.md](docs/porting-status.md) 文末。

- ✅ 完整移植：telemetry / protocol / agent（含 proxy.ts）/ mcp
- ✅ 完整移植：server / client（RPC 主循环、会话路由、Unix 域套接字传输与本地服务器发现）
- ✅ 完整移植：ai（10 个内建 API + 42 家 provider 全家桶 + compat + 全部 utils + CLI）
- ✅ 完整移植：chord（delta / services / facets / node 层 / api）
- ✅ 完整移植：codemode（identifier / types / source / declarations / runtime protocol + host 沙箱编排 + prelude 源码 + Wasm 加载）
- 🚧 durable：基础层 + storage + session + env + harness（含 tools/events/harness.ts 装配）+ **testing 层（assertions / storage-conformance / env-conformance / runner / storage-benchmark）**已完成；各 `harness-*.test.ts` 对应测试补齐中
- 🚧 tui：核心层完成（差分渲染 + 同步输出、Unicode 宽度/换行/截断、ANSI/OSC/SGR 跟踪、按键与 Kitty 协议、overlay 栈与焦点、鼠标事件派发、颜色与 OKLCH/OKHSL、键位注册表、stdin 转义序列缓冲、终端图像（Kitty/iTerm2 编码与元数据）、布局引擎、LaTeX、自动补全、单行输入组件、选择列表组件、多行编辑器、markdown 渲染、设置列表、图像组件、备用屏搜索、**备用屏渲染器（全屏模式：搜索 / 选择与复制 / 滚动条 / Kitty 图像缓存 / overlay 路由）**），44/45 文件、23,812 项测试；仅剩 `index.ts` 桶文件
- ⏳ 未开始：coding-agent / evals

逐包进度、TS → C# 关键设计差异、CBOR 线上兼容要点与路线图，见 **[docs/porting-status.md](docs/porting-status.md)**。
