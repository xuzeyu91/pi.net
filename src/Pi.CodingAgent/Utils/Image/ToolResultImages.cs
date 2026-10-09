using Pi.Ai.Types;

namespace Pi.CodingAgent.Utils.Image;

/// <summary>Port of the TS <c>NormalizeToolResultImagesOptions</c>.</summary>
public sealed record NormalizeToolResultImagesOptions
{
    /// <summary>Whether oversized images are resized to inline provider limits. Default: true.</summary>
    public bool? AutoResizeImages { get; init; }

    /// <summary>
    /// Model-specific resize profile. Uses the conservative built-in defaults when omitted. (The TS
    /// passes a <c>ModelImageResizeOptions</c> straight into the structurally-identical
    /// <c>ImageResizeOptions</c>; C# bridges the two nominal types explicitly.)
    /// </summary>
    public ModelImageResizeOptions? ResizeOptions { get; init; }
}

/// <summary>
/// Port of <c>utils/tool-result-images.ts</c>: normalize image blocks returned by tool results.
/// </summary>
/// <remarks>
/// <para>
/// The <c>read</c> tool and <c>@file</c> CLI attachments run their images through
/// <see cref="ImageProcess"/>, but tools that produce images themselves (extensions, MCP bridges,
/// screenshot tools) hand back arbitrary base64 payloads that go straight into session history and
/// every subsequent provider request. Oversized images make the provider reject the whole
/// conversation, not just the offending turn, so normalize them once as they enter history.
/// </para>
/// <para>
/// Returns the original array instance when nothing changed so callers can skip rewriting the
/// result. Unlike <c>read</c>, an unprocessable image keeps its original block: the failure may
/// just be an unavailable image backend, and passing it through preserves the behavior tools have
/// instead of silently deleting their output.
/// </para>
/// </remarks>
public static class ToolResultImages
{
    /// <summary>The TS <c>normalizeToolResultImages</c>.</summary>
    public static async Task<IReadOnlyList<ContentBlock>> NormalizeToolResultImagesAsync(
        IReadOnlyList<ContentBlock> content, NormalizeToolResultImagesOptions? options = null)
    {
        bool hasImage = false;
        foreach (var block in content)
        {
            if (block is ImageContent)
            {
                hasImage = true;
                break;
            }
        }

        if (!hasImage)
        {
            return content;
        }

        bool autoResizeImages = options?.AutoResizeImages ?? true;
        var normalized = new List<ContentBlock>(content.Count);
        bool changed = false;

        foreach (var block in content)
        {
            if (block is not ImageContent image)
            {
                normalized.Add(block);
                continue;
            }

            var processed = await ImageProcess.ProcessImageAsync(
                LenientBase64.Decode(image.Data), image.MimeType ?? "", new ProcessImageOptions
                {
                    AutoResizeImages = autoResizeImages,
                    ResizeOptions = options?.ResizeOptions is { } profile
                        ? new ImageResizeOptions
                        {
                            MaxWidth = profile.MaxWidth,
                            MaxHeight = profile.MaxHeight,
                            MaxBytes = profile.MaxBytes,
                            JpegQuality = profile.JpegQuality,
                        }
                        : null,
                }).ConfigureAwait(false);

            if (processed is not ProcessImageResult.Ok ok)
            {
                normalized.Add(block);
                continue;
            }

            if (ok.Data == image.Data && ok.MimeType == image.MimeType && ok.Hints.Count == 0)
            {
                normalized.Add(block);
                continue;
            }

            normalized.Add(new ImageContent(ok.Data, ok.MimeType));
            if (ok.Hints.Count > 0)
            {
                normalized.Add(new TextContent(string.Join("\n", ok.Hints)));
            }

            changed = true;
        }

        return changed ? normalized : content;
    }
}
