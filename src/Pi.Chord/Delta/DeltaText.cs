namespace Pi.Chord.Delta;

/// <summary>delta 层的文本自由函数。对应 TS <c>chord/delta/index.ts</c> 的 <c>overlap</c>。</summary>
public static class DeltaText
{
    /// <summary>
    /// a 的最长后缀同时是 b 的前缀的长度。先用长头探测（候选少、且能命中滚动窗口产生的大重叠），
    /// 再回退单字符（能找到任意重叠，代价是候选更多）。用 indexOf 探测并做精确子串验证，
    /// 热循环走原生路径。恒正确：返回的 n 满足 a[^n..] == b[..n]。找不到返回 0（调用方发整体 set，只大不错）。
    /// </summary>
    public static int Overlap(string a, string b, int scan, int probe = 64, int maxCandidates = 8)
    {
        if (a.Length == 0 || b.Length == 0 || scan == 0) return 0;
        var tail = a.Length > scan ? a[^scan..] : a;

        // 长度 h 的探针只能找到不短于 h 的重叠——头必须真出现在 a 里。重复性输出（构建日志、单字符长跑）
        // 会让长头命中成千上万个位置；放弃则返回 0，发出 set：更大，但绝不出错。
        foreach (var h in new[] { Math.Min(probe, b.Length), 1 })
        {
            var head = b[..h];
            var tried = 0;
            for (var k = tail.IndexOf(head, StringComparison.Ordinal); k != -1;
                 k = tail.IndexOf(head, k + 1, StringComparison.Ordinal))
            {
                if (++tried > maxCandidates) break;
                var n = tail.Length - k;
                if (n <= b.Length && string.CompareOrdinal(tail, k, b, 0, n) == 0) return n;
            }

            if (h == 1) break;
        }

        return 0;
    }
}
