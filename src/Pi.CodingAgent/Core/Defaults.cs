using Pi.Ai.Types;

namespace Pi.CodingAgent.Core;

/// <summary>Port of <c>core/defaults.ts</c>.</summary>
public static class Defaults
{
    public const ThinkingLevel DefaultThinkingLevel = ThinkingLevel.Medium;

    public static readonly IReadOnlyList<ThinkingLevel> ThinkingLevelOptions =
    [
        ThinkingLevel.Off,
        ThinkingLevel.Minimal,
        ThinkingLevel.Low,
        ThinkingLevel.Medium,
        ThinkingLevel.High,
        ThinkingLevel.XHigh,
        ThinkingLevel.Max,
    ];
}
