using Pi.Ai.Auth;

namespace Pi.CodingAgent.Core;

/// <summary>
/// Credential-store overlay carrying non-persistent runtime API keys (for example
/// <c>--api-key</c> on the command line). Port of <c>core/runtime-credentials.ts</c>.
/// </summary>
/// <remarks>
/// The override map is an insertion-ordered list rather than a <see cref="Dictionary{TKey,TValue}"/>:
/// the TS code uses a JS <c>Map</c>, whose iteration order is insertion order and whose re-insertion
/// after a delete moves the key to the end. .NET's dictionary may reuse a freed slot instead, which
/// would change the order returned by <see cref="ListAsync"/>.
/// </remarks>
public sealed class RuntimeCredentials : ICredentialStore
{
    private readonly ICredentialStore _store;
    private readonly List<KeyValuePair<string, string>> _overrides = [];

    public RuntimeCredentials(ICredentialStore store) => _store = store;

    /// <summary>Register a runtime API key for a provider (not persisted).</summary>
    public void SetRuntimeApiKey(string providerId, string apiKey)
    {
        for (var i = 0; i < _overrides.Count; i++)
        {
            if (_overrides[i].Key != providerId) continue;
            _overrides[i] = new KeyValuePair<string, string>(providerId, apiKey);
            return;
        }
        _overrides.Add(new KeyValuePair<string, string>(providerId, apiKey));
    }

    /// <summary>Drop a runtime API key override (the stored credential is untouched).</summary>
    public void RemoveRuntimeApiKey(string providerId) => RemoveOverride(providerId);

    /// <summary>Whether a runtime API key override exists for this provider.</summary>
    public bool HasRuntimeApiKey(string providerId) => _overrides.Any(entry => entry.Key == providerId);

    public async Task<Credential?> ReadAsync(string providerId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var (id, apiKey) in _overrides)
        {
            if (id == providerId) return new Credential.ApiKey(apiKey);
        }
        return await _store.ReadAsync(providerId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CredentialInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        var stored = await _store.ListAsync(cancellationToken).ConfigureAwait(false);
        var entries = new List<CredentialInfo>(stored);
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < entries.Count; i++) index[entries[i].ProviderId] = i;

        cancellationToken.ThrowIfCancellationRequested();
        foreach (var (providerId, _) in _overrides)
        {
            var entry = new CredentialInfo(providerId, CredentialKind.ApiKey);
            if (index.TryGetValue(providerId, out var existing)) entries[existing] = entry;
            else
            {
                index[providerId] = entries.Count;
                entries.Add(entry);
            }
        }
        return entries;
    }

    /// <summary>Writes always go to the underlying store; overrides are never persisted.</summary>
    public Task<Credential?> ModifyAsync(string providerId, Func<Credential?, Task<Credential?>> fn,
        CancellationToken cancellationToken = default)
        => _store.ModifyAsync(providerId, fn, cancellationToken);

    public async Task DeleteAsync(string providerId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _store.DeleteAsync(providerId, cancellationToken).ConfigureAwait(false);
        RemoveOverride(providerId);
    }

    private void RemoveOverride(string providerId)
    {
        for (var i = 0; i < _overrides.Count; i++)
        {
            if (_overrides[i].Key != providerId) continue;
            _overrides.RemoveAt(i);
            return;
        }
    }
}
