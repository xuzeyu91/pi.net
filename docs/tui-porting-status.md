# TUI 移植状态（packages/tui → src/Pi.Tui）

本文记录 `packages/tui`（19.3k 行 TS，不含测试）到 `src/Pi.Tui` 的逐文件移植进度与设计差异。
包级进度总览见 [porting-status.md](porting-status.md)，项目介绍见 [readme](../readme.md)。

> 参照源码：`D:\AI\参考项目\pi\packages\tui\src`

## 进度总览

| 指标 | 数值 |
|---|---|
| TS 源码（不含 `*.test.ts`） | 45 个文件 / 19,293 行 |
| 已移植 | 31 个文件 / 9,706 行（50.3%） |
| 待移植 | 14 个文件 / 9,587 行 |
| .NET 产出 | 31 个 `.cs` / 10,276 行 |
| 测试 | `Pi.Tui.Tests` 221 项全部通过 |
| 构建 | 0 警告 0 错误（`dotnet build Pi.slnx -m:1`） |

## 逐文件清单

### ✅ 已完成

| TS 文件 | 行数 | .NET | 说明 |
|---|---|---|---|
| `utils.ts` | 1397 | `Ansi.cs` / `UnicodeWidth.cs` / `TextLayout.cs` | 拆成三个文件：ANSI/OSC 提取与 SGR 跟踪、字素与可见宽度、换行/截断/切片/段落提取 |
| `keys.ts` | 1401 | `Keys.cs` | `matchesKey` / `parseKey` / Kitty CSI-u 解码 / 修饰键位掩码 / `Key` 构造助手 |
| `tui.ts` | 1493 | `Tui.cs` | `Component`/`Container`、焦点与 overlay 栈、渲染调度、overlay 布局解算、`compositeTuiLine` |
| `terminal.ts` | 554 | `Terminal.cs` | `ITerminal` + `ProcessTerminal` + `StringTerminal`（测试假件，TS 侧由 vitest 注入） |
| `tui-main-screen.ts` | 655 | `TuiMainScreen.cs` | 差分渲染全流程、同步输出（CSI 2026）、kitty 图像行预留、超宽行守卫 |
| `terminal-colors.ts` | 91 | `TerminalColors.cs` | OSC 11 背景色、DSR 配色方案上报解析 |
| `layout-node.ts` | 51 | `LayoutNode.cs` | 栈式布局节点与视口 |
| `fuzzy.ts` | 138 | `Fuzzy.cs` | 模糊匹配与打分排序 |
| `kill-ring.ts` | 46 | `KillRing.cs` | Emacs 风格 kill/yank 环 |
| `undo-stack.ts` | 28 | `UndoStack.cs` | 撤销栈（克隆函数由调用方提供） |
| `word-navigation.ts` | 117 | `WordNavigation.cs` | 词级光标移动 |
| `colors.ts` | 367 | `Colors.cs` | 颜色值/解析/转换/混合 + ANSI 样式序列 |
| `oklab.ts` | 233 | `Oklab.cs` | Oklab / OKHSL ↔ sRGB（Björn Ottosson 参考实现的移植） |
| `wheel-scroll.ts` | 82 | `WheelScroll.cs` | 滚轮事件 → 行数，含速度加速 |
| `keybindings.ts` | 320 | `Keybindings.cs` | 键位注册表、用户覆盖、冲突检测、全局单例 |
| `stdin-buffer.ts` | 444 | `StdinBuffer.cs` | 跨块转义序列缓冲、括号粘贴、序列超时冲刷 |
| `native-platform.ts` + `native-modifiers.ts` + `native-module-path.ts` | 109 | `NativePlatform.cs` | 三文件合一（见差异 T1） |
| `components/text.ts` | 113 | `Components/Text.cs` | 文本组件 |
| `components/spacer.ts` | 28 | `Components/Spacer.cs` | 空白行 |
| `components/box.ts` | 172 | `Components/Box.cs` | 内边距盒子 |
| `components/stack.ts` | 154 | `Components/Stack.cs` | 栈基类（可见性谓词、条目选项） |
| `components/v-stack.ts` | 33 | `Components/VStack.cs` | 垂直栈 |
| `components/h-stack.ts` | 44 | `Components/HStack.cs` | 水平栈 |
| `components/truncated-text.ts` | 65 | `Components/TruncatedText.cs` | 单行截断文本 |
| `components/loader.ts` | 101 | `Components/Loader.cs` | 动画加载指示器（帧序列、可配置间隔、spinner/message 着色函数、`setIndicator` 重启动画） |
| `components/cancellable-loader.ts` | 40 | `Components/CancellableLoader.cs` | Esc 取消的加载指示器，暴露 `CancellationToken` 与 `OnAbort` |
| `components/scroll-view.ts` | 224 | `Components/ScrollView.cs` | 单子项滚动视口：follow-end、overscroll、scrollbar（hidden/auto/always）、瞬态滚动条延迟隐藏、`IScrollLayoutState` |
| `layout.ts` | 449 | `Layout.cs` | 布局引擎：栈/滚动解算、盒子裁剪、kitty 图像行裁剪、滚动条几何与绘制、命中测试（`getLayoutBoxesAt` / `getScrollViewBox` / `getScrollViewsAt`） |
| `terminal-image.ts` | 757 | `TerminalImage.cs` | 协议探测与能力缓存、Kitty/iTerm2 编码器、图像元数据注册与查询（1000 条 FIFO 淘汰）、分块传输下的放置重建、`cropKittyImageLine`、`calculateImageCellSize/Rows`、PNG/JPEG/GIF/WebP 尺寸解析、`renderImage`、`imageFallback`、OSC 8 超链接 |

### ⏳ 待移植

| TS 文件 | 行数 | 依赖 | 备注 |
|---|---|---|---|
| `components/input.ts` | 494 | keys / keybindings / stdin-buffer | 单行输入组件（编辑器基础） |
| `components/editor.ts` | 2472 | input / kill-ring / undo-stack / word-navigation / autocomplete / latex | 多行编辑器（最大单文件） |
| `editor-component.ts` | 74 | editor | 编辑器包装组件 |
| `components/markdown.ts` | 1025 | latex / colors | Markdown → ANSI 渲染 |
| `components/select-list.ts` | 273 | fuzzy | 选择列表 |
| `components/settings-list.ts` | 328 | select-list | 设置列表 |
| `components/image.ts` | 167 | terminal-image | 图像组件 |
| `components/mouse-region.ts` | 33 | — | 鼠标区域标记 |
| `components/alt-screen-flash.ts` | 51 | — | 备用屏闪烁提示 |
| `autocomplete.ts` | 861 | fuzzy | 自动补全引擎与 provider |
| `latex.ts` | 1506 | — | LaTeX → Unicode/ANSI 渲染 |
| `alt-screen-search.ts` | 327 | — | 备用屏搜索 |
| `tui-alt-screen.ts` | 1784 | 上述多数 | 备用屏渲染器（全屏模式） |
| `index.ts` | 192 | — | 桶文件（C# 无对应概念，公开面由类型可见性决定） |

## 建议的推进顺序

依赖关系决定了下面的顺序；每批都以「构建 0 警告 0 错误 + 测试全绿」收口。

1. **`latex.ts`** —— 纯函数式转换，无前置依赖；`markdown.ts` 依赖它。
2. **`autocomplete.ts`** —— 依赖已完成的 `fuzzy.ts`。
3. **`components/input.ts`** —— 依赖 keys / keybindings / stdin-buffer（均已完成），是 `editor.ts` 的前置。
4. **`components/editor.ts` + `editor-component.ts`** —— 依赖 input / kill-ring / undo-stack / word-navigation / autocomplete / latex。
5. **`components/markdown.ts` / `select-list.ts` / `settings-list.ts` / `image.ts` / `mouse-region.ts` / `alt-screen-flash.ts`** —— 其余叶子组件。
6. **`alt-screen-search.ts` → `tui-alt-screen.ts`** —— 最后收口，依赖上面几乎所有内容（含 `layout.ts` 的布局帧与命中测试）。
7. **`index.ts`** —— 公开面清单，随各文件落地自然完成。

## 关键设计差异（TS → C#）

| # | 差异 | 说明 |
|---|---|---|
| T1 | **native 插件加载改为显式注入点** | TS 的 `native-platform.ts` 用 `createRequire()` 加载 `<platform>-platform[-x11].node` 预编译插件，并按路径缓存。.NET 没有 Node addon ABI，因此改为 `NativePlatform.SetNativePlatformHelper(INativePlatformHelper)` 由宿主显式提供（P/Invoke 包装或托管实现）。未安装 helper 时所有查询报告「不可用」——这正是 TS 在插件缺失时的行为。`native-module-path.ts` 的候选路径探测无 .NET 对应物，仅以 `GetNativeModuleCandidates` 保留搜索顺序作为文档。 |
| T2 | **`EventEmitter` → .NET event** | `StdinBuffer` 的 `data` / `paste` 事件改为 `event Action<string>?`；`setTimeout` 冲刷计时器改为 `Timer`，并用 `lock` 保护状态（TS 单线程，C# 计时器回调在线程池线程）。 |
| T3 | **原始模式（raw mode）是尽力而为** | Node 有 `process.stdin.setRawMode`；.NET 无跨平台等价 API。Unix 上 shell out `stty raw -echo`（停止时 `stty sane`），Windows 上用 P/Invoke 打开虚拟终端输入；输入经 `Console.ReadKey` 映射回框架期望的转义序列。 |
| T4 | **Unicode 数据源不同** | TS 用 npm `get-east-asian-width`、`\p{RGI_Emoji}`、`\p{Default_Ignorable_Code_Point}`；.NET 侧改用显式宽字符区间表 + emoji 启发式 + `CharUnicodeInfo`。字素簇用 `StringInfo`（UAX #29），词切分自行按 rune 分类。 |
| T5 | **JS 正则 `\d` → `[0-9]`** | .NET 的 `\d` 等价于 `\p{Nd}`，会匹配阿拉伯-印度数字等 Unicode 十进制数字，而 JS 的 `\d` 只匹配 `0-9`。所有移植的解析正则都显式写成 `[0-9]`。 |
| T6 | **JS `Math.round` → `JsMath.Round`** | .NET 的 `Math.Round` 默认银行家舍入（`Math.Round(2.5) == 2`），JS 是「.5 向上取整」（`Math.round(2.5) == 3`）。所有颜色/尺寸换算改用 `JsMath.Round`（`Math.Floor(v + 0.5)`）。 |
| T7 | **`RgbColor` 通道是 double 而非 byte** | TS 的 `RgbColor` 是 `{r,g,b: number}`，sRGB 混合会产生小数值。C# 的 `RgbColor` 因此用 `double` 通道，避免 `colorToRgb` 提前截断（`mixColors(srgb)` 的结果会随之失真）。 |
| T8 | **`structuredClone` → 调用方提供克隆函数** | `UndoStack<T>` 的 TS 实现用 `structuredClone` 深拷贝状态；C# 无等价物，改为构造函数接收 `Func<T,T>` 克隆函数。 |
| T9 | **声明合并 → 常量 + 字符串 id** | TS 的 `Keybindings` 接口靠 declaration merging 让下游包追加绑定 id；C# 无此机制，故 `TuiKeybindingIds` 提供常量，注册表按 `string` id 工作，下游包传入自己的定义表（对应 coding-agent 的 `KeybindingsManager` 子类）。 |
| T10 | **`KeyId \| KeyId[]` 保留为联合结构** | `getResolvedBindings()` 在 TS 里对单键返回裸字符串、多键返回数组。C# 用 `KeybindingKeys` 结构保留该区别（否则 coding-agent 回写配置文件时形状会变）。 |
| T11 | **文件型调试日志未移植** | `tui-main-screen.ts` 的 `PI_TUI_DEBUG` / `PI_TUI_DEBUG_REDRAW` 会把整屏内容与崩溃信息写到 `/tmp/tui/*.log` 和 `pi-tui-crash.log`。C# 侧未移植文件日志，但**超宽行守卫仍然抛异常**（并先停止终端以复原状态）。 |
| T12 | **`ProcessTerminal` 无 `drainInput` 的 stdin 排空** | TS 的 `drainInput` 从 stdin 真读并丢弃残余字节，避免退出后按键泄漏到父 shell。.NET 侧因输入读取在 `Console.ReadKey` 上，改为「解除回调 + 等待 idle 窗口」的近似实现。 |
| T13 | **`setInterval` 动画计时器 → `System.Threading.Timer`** | `Loader` 的帧推进在 TS 里由单线程 `setInterval` 驱动；C# 用 `System.Threading.Timer`，回调可能在线程池线程上运行，与渲染线程并发访问组件状态。tick 体内用 `lock` 保护，且 `ITui.RequestRender` 本身已是线程安全的。`ScrollView` 的瞬态滚动条延迟隐藏同理（T14）。 |
| T14 | **`ScrollView` 的瞬态滚动条计时器** | 同 T13：`setTimeout` → `System.Threading.Timer`。Node 的 `unref()` 无 .NET 对应物，但 `System.Threading.Timer` 同样不会阻止进程退出。 |

## 测试覆盖

`tests/Pi.Tui.Tests`（152 项，已禁用并行化——多个用例共享进程级全局状态：能力缓存、Kitty 元数据注册表、环境变量探测、全局键位注册表、native helper）：

| 测试文件 | 项数 | 覆盖 |
|---|---|---|
| `TextEngineTests.cs` | 24 | 可见宽度、ANSI/OSC/APC 提取、tab 展开、截断/切片/换行、SGR 跨行携带、overlay 合成 |
| `KeysAndUtilTests.cs` | 13 | 传统与 Kitty 按键匹配、`parseKey`、释放/重复判定、可打印解码、模糊匹配、kill ring、undo 栈、词导航 |
| `ComponentTests.cs` | 17 | Text/Spacer/Box/TruncatedText/VStack/HStack 渲染、栈可见性、`TuiMainScreen` 差分渲染与同步输出、宽度变更全量重绘、超宽行守卫、overlay 合成 |
| `ColorAndUtilTests.cs` | 25 | 颜色解析（hex/oklch/okhsl）、调色板索引、JS 舍入、OKLCH/OKHSL 转换与往返、混色、ANSI 序列、样式嵌套顺序、滚轮加速、native helper 缺失路径 |
| `InputTests.cs` | 27 | 键位注册表（默认/覆盖/冲突/形状/全局单例）、stdin 缓冲（分块 CSI、SGR 鼠标、WezTerm ESC 对、括号粘贴、kitty 可打印去重、超时冲刷、字节与 UTF-8 入口） |
| `LoaderTests.cs` | 21 | Loader 默认帧与着色函数、`setIndicator` 的 verbatim/空帧/自定义帧、`setMessage`、`invalidate` 刷新、单帧不动画、多帧动画与 `stop` 冻结、重复 `start` 不叠加计时器、render 前置空行与宽度填充；CancellableLoader 的 Esc/Ctrl-C 取消、`OnAbort` 回调、其他按键不取消、`dispose` 停表且 token 仍可用 |
| `LayoutTests.cs` | 48 | 叶子盒的固有高度与裁剪、仅绘制有源行、光标标记行滚动入视、vstack 纵向堆叠/间距/`grow` 填充/隐藏条目不留空隙、hstack 并排/center/end/stretch 对齐/零宽子项、布局节点暴露；ScrollView 的视口裁剪、滚动平移、`scrollBy` 边界与未消费余量、follow-end、`disableFollow`、`scrollToStart/End`、内容收缩时的钳制、子项变异拒绝、非 vertical 轴拒绝、always/auto/hidden 滚动条与延迟隐藏；滚动条几何与绘制、`replaceScrollbarCell`；命中测试的深度排序与嵌套滚动视图；OSC 133 区域前缀剥离 |
| `TerminalImageTests.cs` | 46 | Kitty/iTerm2 编码与 base64 长度、单元格尺寸与宽高比优化、行数换算、PNG/JPEG/GIF/WebP 尺寸解析、Kitty 元数据注册/查询/FIFO 淘汰、跨块放置重建、`cropKittyImageLine`、`renderImage` 的 Kitty/iTerm2/降级分支、`imageFallback` 与 OSC 8 超链接、能力探测（Kitty/Ghostty/WezTerm/iTerm2/tmux 转发） |
