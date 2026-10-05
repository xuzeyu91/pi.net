using Pi.Ai.Auth.OAuth;
using Pi.Ai.Tests.Auth;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Pi.Ai.Api;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.Ai.Utils;
using Xunit;

namespace Pi.Ai.Tests.Api;

/// <summary>provider 请求重试测试。对应 TS provider-retry 的 SDK 镜像语义。</summary>
public class ProviderRetryTests
{
    private static ProviderHttpException HttpError(int status, IReadOnlyDictionary<string, string>? headers = null)
        => new(status, "body", headers: headers);

    [Fact]
    public async Task RetriesOn429ThenSucceeds()
    {
        var attempts = 0;
        var result = await ProviderRetry.RetryAsync(() =>
        {
            attempts++;
            return attempts < 3
                ? throw HttpError(429)
                : Task.FromResult("ok");
        }, new ProviderRetryOptions { MaxRetries = 2 });

        Assert.Equal("ok", result);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task RespectsXShouldRetryFalse()
    {
        var attempts = 0;
        var error = await Assert.ThrowsAsync<ProviderHttpException>(() =>
            ProviderRetry.RetryAsync<string>(() =>
            {
                attempts++;
                throw HttpError(429, new Dictionary<string, string> { ["x-should-retry"] = "false" });
            }, new ProviderRetryOptions { MaxRetries = 2 }));
        Assert.Equal(429, error.Status);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task DoesNotRetryNonRetryableStatus()
    {
        var attempts = 0;
        await Assert.ThrowsAsync<ProviderHttpException>(() =>
            ProviderRetry.RetryAsync<string>(() =>
            {
                attempts++;
                throw HttpError(400);
            }, new ProviderRetryOptions { MaxRetries = 3 }));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task FailsImmediatelyWhenServerDelayExceedsCap()
    {
        var attempts = 0;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ProviderRetry.RetryAsync<string>(() =>
            {
                attempts++;
                throw HttpError(429, new Dictionary<string, string> { ["retry-after"] = "120" });
            }, new ProviderRetryOptions { MaxRetries = 2 }));
        Assert.Contains("retry delay", error.Message);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task ThrowsLastWhenRetriesExhausted()
    {
        var attempts = 0;
        var error = await Assert.ThrowsAsync<ProviderHttpException>(() =>
            ProviderRetry.RetryAsync<string>(() =>
            {
                attempts++;
                throw HttpError(500);
            }, new ProviderRetryOptions { MaxRetries = 1 }));
        Assert.Equal(500, error.Status);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task NonHttpExceptionsAreNotRetried()
    {
        var attempts = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ProviderRetry.RetryAsync<string>(() =>
            {
                attempts++;
                throw new InvalidOperationException("logic bug");
            }, new ProviderRetryOptions { MaxRetries = 2 }));
        Assert.Equal(1, attempts);
    }
}

/// <summary>错误规范化测试。对应 TS error-body 测试语义。</summary>
public class ProviderErrorTests
{
    [Fact]
    public void NormalizesProviderHttpException()
    {
        var error = new ProviderHttpException(503, "  upstream exploded  ");
        var norm = ProviderError.Normalize(error);
        Assert.Equal(503, norm.Status);
        Assert.Equal("upstream exploded", norm.Body);
        Assert.False(norm.MessageCarriesBody);
        Assert.Equal("503: upstream exploded", ProviderError.Format(norm));
    }

    [Fact]
    public void FormatWithPrefix()
    {
        var norm = ProviderError.Normalize(new ProviderHttpException(429, "rate limited"));
        Assert.Equal("llama.cpp error (429): rate limited", ProviderError.Format(norm, "llama.cpp error"));
    }

    [Fact]
    public void KeepsMessageWhenBodyAlreadyCarried()
    {
        var error = new ProviderHttpException(400, "Input is too long", message: "400: Input is too long");
        var norm = ProviderError.Normalize(error);
        Assert.True(norm.MessageCarriesBody);
        Assert.Equal("400: Input is too long", ProviderError.Format(norm));
    }

    [Fact]
    public void TruncatesLongBodies()
    {
        var longBody = new string('x', 5000);
        var truncated = ProviderError.TruncateErrorText(longBody, ProviderError.MaxProviderErrorBodyChars);
        Assert.Contains("[truncated 1000 chars]", truncated);
    }
}

/// <summary>未配对代理项清理测试。</summary>
public class SanitizeUnicodeTests
{
    [Fact]
    public void PreservesPairedSurrogates()
    {
        // 🙈 = U+1F648（正确成对的代理项对）
        Assert.Equal("Hello \U0001F648 World", SanitizeUnicode.SanitizeSurrogates("Hello \U0001F648 World"));
    }

    [Fact]
    public void RemovesUnpairedSurrogates()
    {
        var unpairedHigh = "\uD83D";
        var unpairedLow = "\uDE00";
        Assert.Equal("Text  here", SanitizeUnicode.SanitizeSurrogates($"Text {unpairedHigh} here"));
        Assert.Equal(" here", SanitizeUnicode.SanitizeSurrogates($"{unpairedLow} here"));
    }
}

/// <summary>OpenRouter 图片生成测试。</summary>
public class OpenRouterImagesTests
{
    private static ModelSpec ImageModel() => new()
    {
        Id = "google/gemini-2.5-flash-image-preview", Name = "Gemini Image", Api = "openrouter-images",
        Provider = "openrouter", BaseUrl = "https://openrouter.ai/api/v1",
        Input = ["text", "image"], Cost = new ModelCostRates(0.30, 2.50, CacheRead: 0.03, CacheWrite: 0.125),
    };

    private static HttpResponseMessage OkResponse(JsonObject body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };

    [Fact]
    public async Task GeneratesTextAndImageOutput()
    {
        var handler = new FakeHttpHandler(_ => OkResponse(new JsonObject
        {
            ["id"] = "gen-1",
            ["choices"] = new JsonArray(new JsonObject
            {
                ["message"] = new JsonObject
                {
                    ["content"] = "here is your image",
                    ["images"] = new JsonArray(new JsonObject
                    {
                        ["image_url"] = new JsonObject { ["url"] = "data:image/png;base64,QUJD" },
                    }),
                },
            }),
            ["usage"] = new JsonObject
            {
                ["prompt_tokens"] = 100,
                ["completion_tokens"] = 20,
                ["prompt_tokens_details"] = new JsonObject
                {
                    ["cached_tokens"] = 40,
                    ["cache_write_tokens"] = 10,
                },
            },
        }));
        var result = await OpenRouterImages.GenerateImages(
            ImageModel(),
            new ImagesContext { Input = [new TextContent("draw a cat")] },
            new ImagesOptions { ApiKey = "key" },
            new HttpClient(handler));

        Assert.Equal(ImagesStopReason.Stop, result.StopReason);
        Assert.Equal("gen-1", result.ResponseId);
        Assert.Equal("here is your image", result.Output.OfType<TextContent>().Single().Text);
        var image = result.Output.OfType<ImageContent>().Single();
        Assert.Equal("image/png", image.MimeType);
        Assert.Equal("QUJD", image.Data);

        // usage：cacheRead = 40-10=30、input = 100-30-10=60
        Assert.Equal(60, result.Usage!.Input);
        Assert.Equal(20, result.Usage.Output);
        Assert.Equal(30, result.Usage.CacheRead);
        Assert.Equal(10, result.Usage.CacheWrite);
        // 成本 = 0.30/1M*60 + 2.50/1M*20 + 0.03/1M*30 + 0.125/1M*10
        Assert.Equal(0.30 / 1e6 * 60 + 2.50 / 1e6 * 20 + 0.03 / 1e6 * 30 + 0.125 / 1e6 * 10,
            result.Usage.Cost!.Value, 12);

        var request = handler.Requests.Single();
        Assert.Equal("https://openrouter.ai/api/v1/chat/completions", request.Url);
        var body = JsonNode.Parse(request.Body!)!.AsObject();
        Assert.Equal("google/gemini-2.5-flash-image-preview", body.Str("model"));
        Assert.Equal(false, body.Bool("stream"));
        Assert.Equal(["image", "text"], body["modalities"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray());
        Assert.Equal("Bearer key", request.Headers["authorization"]);
    }

    [Fact]
    public async Task MissingApiKeyReturnsErrorResult()
    {
        var result = await OpenRouterImages.GenerateImages(
            ImageModel(), new ImagesContext { Input = [new TextContent("hi")] }, new ImagesOptions());
        Assert.Equal(ImagesStopReason.Error, result.StopReason);
        Assert.Contains("No API key for provider: openrouter", result.ErrorMessage);
        Assert.Empty(result.Output);
    }

    [Fact]
    public async Task HttpFailureReturnsErrorResultWithBody()
    {
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("upstream exploded"),
        });
        var result = await OpenRouterImages.GenerateImages(
            ImageModel(), new ImagesContext { Input = [new TextContent("hi")] },
            new ImagesOptions { ApiKey = "key", MaxRetries = 0 },
            new HttpClient(handler));
        Assert.Equal(ImagesStopReason.Error, result.StopReason);
        Assert.Contains("503", result.ErrorMessage);
        Assert.Contains("upstream exploded", result.ErrorMessage);
    }

    [Fact]
    public async Task SignalAbortedMarksResultAborted()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var result = await OpenRouterImages.GenerateImages(
            ImageModel(), new ImagesContext { Input = [new TextContent("hi")] },
            new ImagesOptions { ApiKey = "key", Signal = cts.Token });
        Assert.Equal(ImagesStopReason.Aborted, result.StopReason);
    }
}
