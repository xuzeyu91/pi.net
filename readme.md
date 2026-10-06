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

> coding-agent（交互式 CLI 主产品）与 tui / durable / evals 尚未移植；codemode 已完整移植（纯逻辑层 + 执行层沙箱编排），见[移植状态](docs/porting-status.md)。

## 快速开始

需要 .NET 10 SDK。

```bash
dotnet build Pi.slnx    # 0 警告 0 错误（沙箱内需加 -m:1）
dotnet test  Pi.slnx    # 664 项测试
```

测试基于 xunit.v3 + Microsoft.Testing.Platform（MTP）。若 `dotnet test` 未触发执行，直接运行测试产物：

```bash
tests/Pi.<Pkg>.Tests/bin/Debug/net10.0/Pi.<Pkg>.Tests.exe
```

## 移植状态

10 个运行时项目，构建 0 警告 0 错误，**664 项测试全部通过**（2026-10-06 实测，P54 复测更新）。

- ✅ 完整移植：telemetry / protocol / agent（含 proxy.ts）/ mcp
- ✅ 完整移植：server / client（RPC 主循环、会话路由、Unix 域套接字传输与本地服务器发现）
- ✅ 完整移植：ai（10 个内建 API + 42 家 provider 全家桶 + compat + 全部 utils + CLI）
- ✅ 完整移植：chord（delta / services / facets / node 层 / api）
- ✅ 完整移植：codemode（identifier / types / source / declarations / runtime protocol + host 沙箱编排 + prelude 源码 + Wasm 加载）
- 🚧 durable：基础层 + storage + session + env 层已完成（storage/session 内核/env 全表面），tools / harness / testing 推进中
- ⏳ 未开始：coding-agent / tui / evals

逐包进度、TS → C# 关键设计差异、CBOR 线上兼容要点与路线图，见 **[docs/porting-status.md](docs/porting-status.md)**。
