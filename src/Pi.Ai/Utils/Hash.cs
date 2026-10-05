namespace Pi.Ai.Utils;

/// <summary>确定性短哈希。对应 TS <c>shortHash</c>（utils/hash.ts）——JS Math.imul 32 位语义。</summary>
public static class Hash
{
    private static uint Imul(uint a, uint b) => a * b; // uint 运算天然按 32 位截断

    private static uint LogicShiftRight(uint value, int bits) => value >> bits;

    public static string ShortHash(string str)
    {
        uint h1 = 0xdeadbeef;
        uint h2 = 0x41c6ce57;
        foreach (var ch in str)
        {
            var c = (uint)ch;
            h1 = Imul(h1 ^ c, 2654435761);
            h2 = Imul(h2 ^ c, 1597334677);
        }
        h1 = Imul(h1 ^ LogicShiftRight(h1, 16), 2246822507) ^ Imul(h2 ^ LogicShiftRight(h2, 13), 3266489909);
        h2 = Imul(h2 ^ LogicShiftRight(h2, 16), 2246822507) ^ Imul(h1 ^ LogicShiftRight(h1, 13), 3266489909);
        return ToBase36(h2) + ToBase36(h1);
    }

    /// <summary>无符号 32 位整数的 36 进制小写表示（对应 JS Number.prototype.toString(36)）。</summary>
    private static string ToBase36(uint value)
    {
        const string digits = "0123456789abcdefghijklmnopqrstuvwxyz";
        if (value == 0) return "0";
        var chars = new List<char>();
        while (value > 0)
        {
            chars.Add(digits[(int)(value % 36)]);
            value /= 36;
        }
        chars.Reverse();
        return new string(chars.ToArray());
    }
}
