namespace Pi.Codemode;

/// <summary>
/// QuickJS wasm 模块。对应 TS <c>CodemodeWasmModule</c>（wasm.ts）：TS 是
/// <c>WebAssembly.compile</c> 的产物，经 structured clone 分享给 worker；C# 无宿主 wasm 编译 API，
/// 故只承载字节与来源路径，编译/实例化由注入的 <see cref="Runtime.ICodemodeJsEngine"/> 负责。
/// </summary>
public sealed record CodemodeWasmModule(byte[] Bytes, string Path);

/// <summary>对应 TS <c>loadQuickJSWasm</c>（wasm.ts）。</summary>
public static class CodemodeWasm
{
    private static readonly Dictionary<string, CodemodeWasmModule> Modules = new(StringComparer.Ordinal);

    private static readonly object Gate = new();

    /// <summary>
    /// 读取 QuickJS wasm，每路径缓存一次；加载失败不缓存，下次调用重试（对应 TS 在 catch 里从
    /// map 删除）。<para/>
    /// <paramref name="path"/> 缺省为 <see cref="AppContext.BaseDirectory"/> 下的
    /// <c>quickjs-wasi/quickjs.wasm</c>：TS 用 <c>require.resolve</c> 在 node_modules 里定位，
    /// C# 无对应物，故采用约定式默认；宿主可传显式路径（例如嵌入资源落盘后的位置）。
    /// </summary>
    public static CodemodeWasmModule LoadQuickJSWasm(string? path = null)
    {
        var resolved = path ?? Path.Combine(AppContext.BaseDirectory, "quickjs-wasi", "quickjs.wasm");
        lock (Gate)
        {
            if (Modules.TryGetValue(resolved, out var cached)) return cached;
            var module = new CodemodeWasmModule(File.ReadAllBytes(resolved), resolved);
            Modules[resolved] = module;
            return module;
        }
    }
}
