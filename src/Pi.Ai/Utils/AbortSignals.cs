using Pi.Ai.Types;

namespace Pi.Ai.Utils;

/// <summary>
/// 合并取消信号：多信号任一取消即触发。对应 TS <c>combineAbortSignals</c>
/// （C# 用 linked CancellationTokenSource 表达）。
/// </summary>
public static class AbortSignals
{
    public sealed class Combined : IDisposable
    {
        public CancellationToken Token { get; }

        private readonly CancellationTokenSource? _source;

        internal Combined(CancellationToken token, CancellationTokenSource? source)
        {
            Token = token;
            _source = source;
        }

        /// <summary>单信号直通时无需清理；多信号时释放 linked CTS。</summary>
        public void Dispose() => _source?.Dispose();
    }

    /// <summary>合并多个取消令牌。单个时直通（无额外清理），多个时建 linked 源。</summary>
    public static Combined Combine(params ReadOnlySpan<CancellationToken> signals)
    {
        var active = new List<CancellationToken>();
        foreach (var signal in signals)
        {
            if (signal.CanBeCanceled) active.Add(signal);
        }
        switch (active.Count)
        {
            case 0:
                return new Combined(CancellationToken.None, null);
            case 1:
                return new Combined(active[0], null);
            default:
            {
                var source = CancellationTokenSource.CreateLinkedTokenSource([.. active]);
                return new Combined(source.Token, source);
            }
        }
    }
}
