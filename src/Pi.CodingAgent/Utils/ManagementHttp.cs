namespace Pi.CodingAgent.Utils;

/// <summary>Retry tuning for <see cref="ManagementHttp.FetchWithRetryAsync"/>.</summary>
public sealed record FetchRetryOptions
{
    /// <summary>Additional attempts after the first. Defaults to two.</summary>
    public int? MaxRetries { get; init; }

    /// <summary>Retry transient HTTP responses as well as transport failures. Defaults to true.</summary>
    public bool? RetryOnStatus { get; init; }

    /// <summary>Overall time budget shared by all attempts.</summary>
    public int? TimeoutMs { get; init; }

    /// <summary>Per-attempt timeout; a new one is created for every attempt.</summary>
    public int? AttemptTimeoutMs { get; init; }
}

/// <summary>Port of <c>utils/management-http.ts</c>.</summary>
/// <remarks>
/// <para>
/// This is intentionally a transport-level helper for idempotent management requests (version checks,
/// catalogs, downloads). It must not be used for agent or model operations: those can fail after the
/// HTTP request has started and are retried by their semantic caller instead.
/// </para>
/// <para>
/// Caller cancellation and <see cref="FetchRetryOptions.TimeoutMs"/> are terminal;
/// <see cref="FetchRetryOptions.AttemptTimeoutMs"/> aborts only the current attempt, so a hung connection
/// can be retried.
/// </para>
/// <para>
/// Deviation from TS: <c>fetch</c> accepts a URL plus an init object, and the same init can be reused
/// across attempts. An <see cref="HttpRequestMessage"/> may only be sent once, so the port takes a URL
/// and a per-attempt configuration callback and builds a fresh request for every attempt.
/// </para>
/// </remarks>
public static class ManagementHttp
{
    /// <summary>Statuses worth retrying: request timeout, too early, rate limited, and server errors.</summary>
    public static readonly IReadOnlySet<int> RetryableStatusCodes = new HashSet<int> { 408, 425, 429, 500, 502, 503, 504 };

    private static HttpClient? _client;
    private static HttpMessageHandler? _handlerOverride;

    /// <summary>Replace the transport, so the retry matrix can be exercised without a server.</summary>
    internal static HttpMessageHandler? HandlerOverride
    {
        get => _handlerOverride;
        set
        {
            _handlerOverride = value;
            _client?.Dispose();
            _client = null;
        }
    }

    /// <summary>The shared client. Its own timeout is disabled because the timeouts are per attempt.</summary>
    private static HttpClient Client => _client ??= new HttpClient(_handlerOverride ?? new SocketsHttpHandler())
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    /// <summary>
    /// Fetch a management HTTP resource with a bounded immediate retry.
    /// </summary>
    /// <param name="url">The request URL.</param>
    /// <param name="configure">Applied to each attempt's fresh request, e.g. to add headers.</param>
    /// <param name="options">Retry tuning.</param>
    /// <param name="cancellationToken">Caller cancellation, which is terminal.</param>
    public static async Task<HttpResponseMessage> FetchWithRetryAsync(
        string url,
        Action<HttpRequestMessage>? configure = null,
        FetchRetryOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new FetchRetryOptions();
        var maxRetries = options.MaxRetries is null or < 0 ? 2 : options.MaxRetries.Value;
        var retryOnStatus = options.RetryOnStatus ?? true;

        using var overallTimeout = options.TimeoutMs is int overall && overall > 0
            ? new CancellationTokenSource(overall)
            : null;
        var attemptTimeoutMs = options.AttemptTimeoutMs is int configuredAttempt && configuredAttempt > 0
            ? configuredAttempt
            : (int?)null;

        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            overallTimeout?.Token.ThrowIfCancellationRequested();

            using var attemptTimeout = attemptTimeoutMs is int attemptBudget
                ? new CancellationTokenSource(attemptBudget)
                : null;

            var tokens = new List<CancellationToken> { cancellationToken };
            if (overallTimeout is not null)
            {
                tokens.Add(overallTimeout.Token);
            }

            if (attemptTimeout is not null)
            {
                tokens.Add(attemptTimeout.Token);
            }

            using var linked = tokens.Count > 1
                ? CancellationTokenSource.CreateLinkedTokenSource([.. tokens])
                : null;
            var effectiveToken = linked?.Token ?? tokens[0];

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                configure?.Invoke(request);
                var response = await Client.SendAsync(request, effectiveToken).ConfigureAwait(false);

                var shouldRetry = retryOnStatus &&
                                  RetryableStatusCodes.Contains((int)response.StatusCode) &&
                                  attempt < maxRetries;
                if (!shouldRetry)
                {
                    return response;
                }

                // The response is being discarded before a retry; there is nothing useful to do if
                // disposing it also fails.
                response.Dispose();
            }
            catch (Exception error) when (error is OperationCanceledException or HttpRequestException or IOException)
            {
                var attemptTimedOut = attemptTimeout?.IsCancellationRequested == true &&
                                      !cancellationToken.IsCancellationRequested &&
                                      overallTimeout?.IsCancellationRequested != true;

                if (cancellationToken.IsCancellationRequested ||
                    overallTimeout?.IsCancellationRequested == true ||
                    (error is OperationCanceledException && !attemptTimedOut && overallTimeout is null) ||
                    attempt >= maxRetries)
                {
                    throw;
                }
            }
        }
    }
}
