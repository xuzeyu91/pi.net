using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Pi.Ai.Models;
using Pi.Ai.Types;

namespace Pi.Ai;

/// <summary>持久化的 provider 目录条目。对应 TS <c>ModelsStoreEntry</c>（models-store.ts）。</summary>
public sealed record ModelsStoreEntry
{
    /// <summary>持久化的各类型模型。</summary>
    public required IReadOnlyList<ModelSpec> Models { get; init; }

    /// <summary>远端目录 Last-Modified 头对应的 Unix 时间戳。</summary>
    [JsonPropertyName("lastModified")]
    public long? LastModified { get; init; }

    /// <summary>最近一次完成远端检查的 Unix 时间戳。</summary>
    [JsonPropertyName("checkedAt")]
    public long? CheckedAt { get; init; }

    /// <summary>远端目录 ETag（原样保存含引号，回传时作为 If-None-Match）。</summary>
    public string? Etag { get; init; }
}

/// <summary>ModelsStore 操作选项。对应 TS <c>ModelsStoreOperationOptions</c>。</summary>
public sealed record ModelsStoreOperationOptions
{
    public CancellationToken Signal { get; init; }
}

/// <summary>
/// 按 provider id 键控的持久模型目录。对应 TS <c>ModelsStore</c>（models-store.ts）。
/// </summary>
public interface IModelsStore
{
    Task<ModelsStoreEntry?> ReadAsync(string providerId,
        ModelsStoreOperationOptions? options = null, CancellationToken cancellationToken = default);

    Task WriteAsync(string providerId, ModelsStoreEntry entry,
        ModelsStoreOperationOptions? options = null, CancellationToken cancellationToken = default);

    Task DeleteAsync(string providerId,
        ModelsStoreOperationOptions? options = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// 内存实现（读取/写入均做深拷贝，对齐 TS <c>structuredClone</c>）。
/// 对应 TS <c>InMemoryModelsStore</c>。
/// </summary>
public sealed class InMemoryModelsStore : IModelsStore
{
    private readonly Dictionary<string, string> _entries = new(StringComparer.Ordinal);

    public Task<ModelsStoreEntry?> ReadAsync(string providerId,
        ModelsStoreOperationOptions? options = null, CancellationToken cancellationToken = default)
    {
        ThrowIfAborted(options, cancellationToken);
        return Task.FromResult(_entries.TryGetValue(providerId, out var json)
            ? Deserialize(json)
            : null);
    }

    public Task WriteAsync(string providerId, ModelsStoreEntry entry,
        ModelsStoreOperationOptions? options = null, CancellationToken cancellationToken = default)
    {
        ThrowIfAborted(options, cancellationToken);
        _entries[providerId] = System.Text.Json.JsonSerializer.Serialize(entry, ModelsStoreJson.Options);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string providerId,
        ModelsStoreOperationOptions? options = null, CancellationToken cancellationToken = default)
    {
        ThrowIfAborted(options, cancellationToken);
        _entries.Remove(providerId);
        return Task.CompletedTask;
    }

    private static ModelsStoreEntry? Deserialize(string json)
        => System.Text.Json.JsonSerializer.Deserialize<ModelsStoreEntry>(json, ModelsStoreJson.Options);

    private static void ThrowIfAborted(ModelsStoreOperationOptions? options, CancellationToken cancellationToken)
    {
        if (options?.Signal.CanBeCanceled == true) options.Signal.ThrowIfCancellationRequested();
        cancellationToken.ThrowIfCancellationRequested();
    }
}

/// <summary>ModelsStore 的 JSON 选项（wire 形状与 TS 一致）。</summary>
internal static class ModelsStoreJson
{
    public static System.Text.Json.JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
