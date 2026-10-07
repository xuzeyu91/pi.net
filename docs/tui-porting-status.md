# TUI 移植状态（packages/tui → src/Pi.Tui）

本文记录 `packages/tui`（19.3k 行 TS，不含测试）到 `src/Pi.Tui` 的逐文件移植进度与设计差异。
包级进度总览见 [porting-status.md](porting-status.md)，项目介绍见 [readme](../readme.md)。

> 参照源码：`D:\AI\参考项目\pi\packages\tui\src`

## 进度总览

| 指标 | 数值 |
|---|---|
| TS 源码（不含 `*.test.ts`） | 45 个文件 / 19,293 行 |
| 已移植 | 33 个文件 / 12,073 行（62.6%） |
| 待移植 | 12 个文件 / 7,220 行 |
| .NET 产出 | 37 个 `.cs` / 13,868 行 |
| 测试 | `Pi.Tui.Tests` 8,304 项全部通过 |
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
| `latex.ts` | 1506 | `Latex.cs` + `LatexData.cs` | LaTeX → Unicode/ANSI 渲染：符号/运算符/关系/重音/花体等 16 张查表（拆到 `LatexData.cs`）、命令与分组解析、上下标（Unicode 上下标优先，否则线性 `^`/`_`）、分数/根式/`\not`/`\operatorname`/`\overset` 等、`matrix`/`cases`/`aligned`/`array` 等环境、display 模式下的垂直堆叠布局（分数线与运算符上下限）、输出归一化。**行为由 2,822 条差分用例对照 TS 参考实现锁定**（见下） |
| `terminal-image.ts` | 757 | `TerminalImage.cs` | 协议探测与能力缓存、Kitty/iTerm2 编码器、图像元数据注册与查询（1000 条 FIFO 淘汰）、分块传输下的放置重建、`cropKittyImageLine`、`calculateImageCellSize/Rows`、PNG/JPEG/GIF/WebP 尺寸解析、`renderImage`、`imageFallback`、OSC 8 超链接 |
| `autocomplete.ts` | 861 | `Autocomplete.cs` + `AutocompleteData.cs` + `NodePath.cs` + `JsString.cs` | 斜杠命令补全（含 `skill:` 裸名模糊匹配的两段过滤）、`@` 模糊文件搜索（`fd` 子进程 + 打分/去重/排序）、readdir 路径补全（`~` 展开、`./` 保留、包裹符剥离、引号路径）、CJK 标点/空白分隔符判定、`applyCompletion` 光标换算。**行为由 5,244 条差分用例对照 TS 参考实现锁定**（见下），其中 769 条同时钉住了 `node:path` win32 语义 |

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
| `alt-screen-search.ts` | 327 | — | 备用屏搜索 |
| `tui-alt-screen.ts` | 1784 | 上述多数 | 备用屏渲染器（全屏模式） |
| `index.ts` | 192 | — | 桶文件（C# 无对应概念，公开面由类型可见性决定） |

## 建议的推进顺序

依赖关系决定了下面的顺序；每批都以「构建 0 警告 0 错误 + 测试全绿」收口。

1. **`components/input.ts`** —— 依赖 keys / keybindings / stdin-buffer（均已完成），是 `editor.ts` 的前置。
2. **`components/editor.ts` + `editor-component.ts`** —— 依赖 input / kill-ring / undo-stack / word-navigation / autocomplete / latex。
3. **`components/markdown.ts` / `select-list.ts` / `settings-list.ts` / `image.ts` / `mouse-region.ts` / `alt-screen-flash.ts`** —— 其余叶子组件。
4. **`alt-screen-search.ts` → `tui-alt-screen.ts`** —— 最后收口，依赖上面几乎所有内容（含 `layout.ts` 的布局帧与命中测试）。
5. **`index.ts`** —— 公开面清单，随各文件落地自然完成。

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
| T15 | **JS 的 `\s` / `trim` 与 .NET 不是同一字符集** | JS `\s` 包含 U+FEFF、不含 U+0085；.NET `\s` 与 `char.IsWhiteSpace` 恰好相反。`latex.ts` 大量依赖 `\s` 判断（命令后的空白、`trimEnd`、`\operatorname*` 的修饰符跳过），故 `Latex` 显式实现 `IsJsWhitespace` / `JsTrim` / `JsTrimStart` / `JsTrimEnd`，并用显式字符类替换正则里的 `\s`。 |
| T16 | **星平面私有区哨兵按代理对处理** | TS 用 U+F0000–U+F0005 作内部哨兵（标记、保护空格、具名运算符边界）。这些码点在 UTF-16 里是代理对，.NET 的 `\uXXXX` 只吃 4 位十六进制，正则字符类也按码元工作。C# 侧写 `\uDB80\uDC0x`，并把带后行断言的字符类改写成「单码元字符类 \| 显式代理对」的择一形式，以免误匹配低代理相同的其他星平面字符。 |
| T14 | **`ScrollView` 的瞬态滚动条计时器** | 同 T13：`setTimeout` → `System.Threading.Timer`。Node 的 `unref()` 无 .NET 对应物，但 `System.Threading.Timer` 同样不会阻止进程退出。 |
| T17 | **`AbortSignal` → `CancellationToken`；`fd` 子进程改为可注入** | `autocomplete.ts` 的 `getSuggestions(lines, line, col, { signal, force })` 在 C# 侧是 `GetSuggestionsAsync(lines, line, col, AutocompleteRequest(Signal, Force))`，`Promise` → `Task`。`walkDirectoryWithFd` 仍用 `Process` 启动 `fd`（`ProcessStartInfo.ArgumentList`、UTF-8 stdout、`CancellationToken.Register` 时 `Kill(entireProcessTree: true)`），但多了一个 `internal static FdProcessOverride` 测试缝，用于在未安装 `fd` 的机器上驱动 fuzzy 分支（与 TS 侧替换 `child_process` 的沙盒 shim 等价）。`os.homedir()` → `Environment.SpecialFolder.UserProfile`。 |
| T18 | **`autocompleteSeparatorRegex` / `autocompleteBoundaryRegex` 改为码点表 + 谓词** | TS 用 `(?:\s|CJK 标点)` 正则做字符分类，其中 `\p{Script_Extensions=Han}` 等在 .NET 里没有对应写法，且集合含星平面码点（U+16FE2），而 .NET 字符类按 UTF-16 码元工作。因此 `AutocompleteData.SeparatorRanges` / `CjkPunctuationRanges` 由脚本从 Node 实跑枚举生成，分类改为 `IsSeparator(int codePoint)` 谓词；`(?:^\|sep)$` 这种「以分隔符结尾或为空」的用法写成 `IsTokenBoundary(string)`。顺带修掉了 `Fuzzy.Filter` 里 `.NET \s`/`trim` 与 JS 的字符集差异（T15 的同类问题）。 |
| T19 | **`node:path` win32 语义按 1:1 转写** | `autocomplete.ts` 用 `join` / `dirname` / `basename` 生成**用户可见**的补全串，而 `System.IO.Path` 归一化规则不同（`Path.Join(".", "x")` 是 `".\x"`，Node 返回 `"x"`）。`NodePath.cs` 因此逐行转写 Node v22.22.2 的 `path.win32`（含 UNC / 设备根 / 保留设备名 / CVE-2024-36139 的冒号守卫），并用 **769 条** 从 `node:path` 实跑捕获的向量钉住。 |
| T20 | **排序必须稳定，`localeCompare` 需与 Node 对齐** | JS `Array.prototype.sort` 稳定，`List<T>.Sort` 不稳定，而 `autocomplete.ts` 的比较器（先目录后文件、分数/深度/长度多级并列）会打平，故 C# 侧统一走 `StableSort`（`OrderBy` + `Comparer<T>.Create`）。`localeCompare` 用 `CultureInfo.CurrentCulture.CompareInfo`（Node 与 .NET 5+ 都走 ICU），并用 **1,225 条** `collate` 向量验证符号一致。 |

## 差分验证（latex.ts）

`latex.ts` 是纯函数，适合做逐用例差分。`tests/Pi.Tui.Tests/latex-corpus.json` 保存 **2,822 条**
`(source, display, expected)` 向量，其中 `expected` 由**原始 TypeScript 实现**实跑得到，`LatexTests`
逐条比对；`expected` 为 `null` 表示 TS 返回 `undefined`（语法不支持）。语料由两部分合成：

1. 上游 `packages/tui/test/latex.test.ts` 的全部用例（149 条去重后）；
2. 系统性扫描：每张查表的每个条目（全部符号 / 具名运算符 / 上下限运算符 / 关系 / 取反 / 花体字母 /
   重音 / 间距 / 字号 / 包裹命令）、全部环境、各种上下标与根式/分数形态、以及畸形输入（2,686 条）。

覆盖分布：display 模式 1,361 条、多行布局输出 255 条、返回 `undefined` 58 条。

重新生成（需要 Node 22.6+ 的类型擦除与 `get-east-asian-width`）：

```bash
# 1. 取一份 latex.ts 与 utils.ts 到临时目录，装好唯一的 npm 依赖
mkdir /tmp/latexcheck && cd /tmp/latexcheck && npm init -y && npm install get-east-asian-width
cp <pi>/packages/tui/src/{latex.ts,utils.ts} .

# 2. 把上游测试的断言改接到记录器上，让整套用例「跑一遍但不 assert」
#    （替换 node:assert → ./assert-shim.mjs、node:test → ./test-shim.mjs、
#      ../src/index.ts → ./latex-proxy.ts；代理在每次 renderLatex 调用时记录入参）
# 3. 执行并导出语料
node --experimental-strip-types --no-warnings dump.mjs   # 上游用例
node --experimental-strip-types --no-warnings gen.mjs > synthetic.json   # 系统扫描
```

`assert-shim` 的关键点：`assert.strictEqual(renderLatex(x), y)` 的参数自左向右求值，因此断言被调用时
「最近一次 `renderLatex` 调用」就是被测输入——无需解析表达式即可把输入与期望配对。

## 差分验证（autocomplete.ts）

`autocomplete.ts` 依赖文件系统与 `fd` 子进程，因此除了纯函数，还把**可确定性驱动的整条链路**都纳入了差分。
`tests/Pi.Tui.Tests/autocomplete-corpus.json` 保存 **5,244 条**向量，全部由**原始 TypeScript 实现**实跑得到：

| 区段 | 条数 | 内容 |
|---|---|---|
| `api` | 2,051 | 7 个夹具目录树 ×（前缀扫描 / 分隔符上下文 / 包裹符 / 引号路径 / `@` 前缀 / 越界光标）的 `getSuggestions` 与 `applyCompletion` 结果；其中 504 条返回非空建议 |
| `slash` | 290 | 10 组命令表 × 查询串 / 前导空白 / 参数补全（同步、`null`、`[]`、异步）；基准目录为**空目录**，使「未命中命令而落到文件补全」的分支也确定 |
| `helpers` | 597 | 模块私有纯函数：`toDisplayPath` / `escapeRegex` / `buildFdPathQuery` / `findLastDelimiter` / `stripLeadingWrappers` / `findUnclosedQuoteStart` / `isTokenStart` / `extractQuotedPrefix` / `parsePathPrefix` / `buildCompletionValue` |
| `scores` | 234 | 私有 `scoreEntry` 的完整打分矩阵（精确 / 前缀 / 子串 / 全路径 / 目录加成 / 零分） |
| `paths` | 769 | **`node:path` win32 自身**的 `dirname` / `basename` / `join`（含 UNC、设备根、保留设备名、冒号守卫）——用于钉住 `NodePath.cs` |
| `fd` | 28 | `@` 模糊搜索：分块 stdout、去重、打分/深度/长度排序、作用域解析（`src/ma`、`../outside/a`、不存在目录）、`.git` 过滤、反斜杠输出、非零退出码、spawn 失败、已取消、截断到 20 条 |
| `trigger` | 45 | `shouldTriggerFileCompletion` |
| `collate` | 1,225 | `String.prototype.localeCompare` 在补全标签集合上的符号 |

`fd` 区段的驱动方式：沙盒里把 `autocomplete.ts` 的 `import { spawn } from "child_process"` 改指到一个
`child_process` shim（按队列吐预设 stdout），C# 侧对应 `FdProcessOverride`——**两边喂同一份语料里的
`responses`**，因此被测的是 `walkDirectoryWithFd` 的解析、去重、打分与排序，而不是 `fd` 本身。

沙盒一致性校验（必做）：用同一份精简 `utils.ts` 跑上游 `test/autocomplete.test.ts` 与
`test/autocomplete-skill-slash.test.ts`，**17/17 + 7/7 全部通过**（`fd` 相关套件因本机无 `fd` 自动跳过）。

重新生成（需要 Node 22.6+）：

```bash
mkdir /tmp/accheck && cd /tmp/accheck
cp <pi>/packages/tui/src/{autocomplete.ts,fuzzy.ts} .
# 手工写一份只含 autocompleteSeparatorRegex/autocompleteBoundaryRegex 的 utils.ts（去掉 get-east-asian-width）
cp autocomplete.ts autocomplete-probe.ts   # 追加 export { toDisplayPath, escapeRegex, ... }
sed 's|from "child_process"|from "./child_process-shim.mjs"|' autocomplete-probe.ts > autocomplete-fd.ts
node --experimental-strip-types --no-warnings gen.mjs   > autocomplete-corpus.json   # api/slash/helpers/scores/cjk
node --experimental-strip-types --no-warnings genpath.mjs > path-corpus.json        # node:path 扫描
node --experimental-strip-types --no-warnings genfd.mjs   > fd-corpus.json          # fd 区段
node --experimental-strip-types --no-warnings merge.mjs                             # 合并 + 生成 cjk 区间
```

## 测试覆盖

`tests/Pi.Tui.Tests`（8,304 项，已禁用并行化——多个用例共享进程级全局状态：能力缓存、Kitty 元数据注册表、环境变量探测、全局键位注册表、native helper）：

| 测试文件 | 项数 | 覆盖 |
|---|---|---|
| `TextEngineTests.cs` | 24 | 可见宽度、ANSI/OSC/APC 提取、tab 展开、截断/切片/换行、SGR 跨行携带、overlay 合成 |
| `KeysAndUtilTests.cs` | 13 | 传统与 Kitty 按键匹配、`parseKey`、释放/重复判定、可打印解码、模糊匹配、kill ring、undo 栈、词导航 |
| `ComponentTests.cs` | 17 | Text/Spacer/Box/TruncatedText/VStack/HStack 渲染、栈可见性、`TuiMainScreen` 差分渲染与同步输出、宽度变更全量重绘、超宽行守卫、overlay 合成 |
| `ColorAndUtilTests.cs` | 25 | 颜色解析（hex/oklch/okhsl）、调色板索引、JS 舍入、OKLCH/OKHSL 转换与往返、混色、ANSI 序列、样式嵌套顺序、滚轮加速、native helper 缺失路径 |
| `InputTests.cs` | 27 | 键位注册表（默认/覆盖/冲突/形状/全局单例）、stdin 缓冲（分块 CSI、SGR 鼠标、WezTerm ESC 对、括号粘贴、kitty 可打印去重、超时冲刷、字节与 UTF-8 入口） |
| `LatexTests.cs` | 2,824 | 2,822 条差分向量逐条对照 TS 参考实现（含 display 模式的分数堆叠、运算符上下限、矩阵/分段函数布局），另 2 项语料完整性守卫（条数下界、同时覆盖两种 display 模式与 `undefined` 结果） |
| `LoaderTests.cs` | 21 | Loader 默认帧与着色函数、`setIndicator` 的 verbatim/空帧/自定义帧、`setMessage`、`invalidate` 刷新、单帧不动画、多帧动画与 `stop` 冻结、重复 `start` 不叠加计时器、render 前置空行与宽度填充；CancellableLoader 的 Esc/Ctrl-C 取消、`OnAbort` 回调、其他按键不取消、`dispose` 停表且 token 仍可用 |
| `LayoutTests.cs` | 48 | 叶子盒的固有高度与裁剪、仅绘制有源行、光标标记行滚动入视、vstack 纵向堆叠/间距/`grow` 填充/隐藏条目不留空隙、hstack 并排/center/end/stretch 对齐/零宽子项、布局节点暴露；ScrollView 的视口裁剪、滚动平移、`scrollBy` 边界与未消费余量、follow-end、`disableFollow`、`scrollToStart/End`、内容收缩时的钳制、子项变异拒绝、非 vertical 轴拒绝、always/auto/hidden 滚动条与延迟隐藏；滚动条几何与绘制、`replaceScrollbarCell`；命中测试的深度排序与嵌套滚动视图；OSC 133 区域前缀剥离 |
| `TerminalImageTests.cs` | 46 | Kitty/iTerm2 编码与 base64 长度、单元格尺寸与宽高比优化、行数换算、PNG/JPEG/GIF/WebP 尺寸解析、Kitty 元数据注册/查询/FIFO 淘汰、跨块放置重建、`cropKittyImageLine`、`renderImage` 的 Kitty/iTerm2/降级分支、`imageFallback` 与 OSC 8 超链接、能力探测（Kitty/Ghostty/WezTerm/iTerm2/tmux 转发） |
| `AutocompleteTests.cs` | 5,259 | 5,244 条差分向量逐条对照 TS 参考实现（2,051 条路径补全/`applyCompletion`、290 条斜杠命令、597 条私有纯函数、234 条打分、769 条 `node:path`、28 条 `fd` 模糊搜索、45 条 Tab 触发、1,225 条 `localeCompare`），另 15 项守卫（语料条数与覆盖分布、分隔符区间表逐条边界、CJK 字母不参与分隔、`JsString` 的 trim/slice 语义） |
