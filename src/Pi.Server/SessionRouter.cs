using Pi.Chord.Context;
using Pi.Chord.Services;
using Pi.Protocol;

namespace Pi.Server;

/// <summary>排空期间关闭路由会话失败时携带的聚合错误。对应 TS <c>SessionCleanupError</c>。</summary>
internal sealed class SessionCleanupException(IEnumerable<Exception> errors, string message)
    : AggregateException(message, errors);

/// <summary>会话路由选项。对应 TS <c>SessionRouterOptions&lt;TMetadata&gt;</c>。</summary>
public sealed record SessionRouterOptions<TMetadata> where TMetadata : ISessionMetadata
{
    public required IServerHost<TMetadata> Host { get; init; }

    public required string ServerId { get; init; }

    public required Func<bool> IsClosing { get; init; }

    /// <summary>把一条连接的附加路由（null = 已脱离）推给客户端。</summary>
    public required Func<object, RpcTarget?, Context, Task> PublishAttachment { get; init; }

    public required Action<Exception> ReportError { get; init; }
}

/// <summary>
/// 会话路由器：把展示连接的服务调用路由到持久会话句柄，管理附加/脱离/删除/排空。
/// 对应 TS <c>SessionRouter&lt;TMetadata&gt;</c>（session-router.ts）。
/// </summary>
public sealed class SessionRouter<TMetadata> where TMetadata : ISessionMetadata
{
    private sealed class ClientAttachment
    {
        public required string Id { get; init; }

        public required object Client { get; init; }

        public required HostedSession Session { get; init; }

        public HashSet<Task> Operations { get; } = [];

        public Task<IRoutedSessionAttachment>? Acquiring { get; set; }

        public IRoutedSessionAttachment? Lease { get; set; }

        public Task? Releasing { get; set; }
    }

    private sealed class HostedSession
    {
        public required string Id { get; init; }

        public required IRoutedSessionHandle Handle { get; init; }

        public HashSet<ClientAttachment> Attachments { get; } = [];
    }

    private readonly SessionRouterOptions<TMetadata> _options;
    private readonly object _gate = new();
    private readonly Dictionary<string, HostedSession> _hostedSessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<HostedSession>> _openingSessions = new(StringComparer.Ordinal);
    private readonly Dictionary<object, ClientAttachment> _attachmentsByClient =
        new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<object> _disconnectedClients = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<object, Task> _clientOperations = new(ReferenceEqualityComparer.Instance);
    private Task? _closePromise;

    public SessionRouter(SessionRouterOptions<TMetadata> options) => _options = options;

    /// <summary>把一个服务调用路由到该客户端已附加的会话。对应 TS <c>executeServiceCall</c>。</summary>
    public async Task<object?> ExecuteServiceCallAsync(ServiceCall call, RpcTarget target, object client,
        ServiceUpdatePublisher publish, Context context)
    {
        var result = await RunForClientAsync(client, () =>
            StartServiceCallAsync(client, target, call, publish, context)).ConfigureAwait(false);
        return await result.ConfigureAwait(false);
    }

    /// <summary>把客户端附加到某个持久会话。对应 TS <c>attachClient</c>。</summary>
    public Task AttachClientAsync(object client, string sessionId, Context context)
    {
        if (_options.IsClosing()) return Task.FromException(new ServerDrainingError());
        return RunForClientAsync(client, () => AttachClientNowAsync(client, sessionId, context));
    }

    /// <summary>脱离该客户端当前附加的会话。对应 TS <c>detachClient</c>。</summary>
    public Task DetachClientAsync(object client, Context context)
        => RunForClientAsync(client, async () =>
        {
            var attachment = GetAttachment(client);
            if (attachment is not null) await ReleaseAttachmentAsync(attachment, context).ConfigureAwait(false);
        });

    /// <summary>关闭并移除一个被托管的会话（释放全部附加）。对应 TS <c>removeSession</c>。</summary>
    public async Task RemoveSessionAsync(string sessionId, Context context)
    {
        if (_options.IsClosing()) throw new ServerDrainingError();
        var hosted = GetHostedSession(sessionId);
        if (hosted is null) return;

        var errors = new List<Exception>();
        List<ClientAttachment> attachments;
        lock (_gate) attachments = hosted.Attachments.ToList();
        var releases = await AllSettledAsync(
            attachments.Select(attachment => ReleaseAttachmentAsync(attachment, context))).ConfigureAwait(false);
        errors.AddRange(releases);
        try
        {
            await hosted.Handle.CloseAsync(context).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            errors.Add(error);
        }
        lock (_gate)
        {
            if (ReferenceEquals(GetHostedSession(sessionId), hosted)) _hostedSessions.Remove(sessionId);
        }
        if (errors.Count == 1) throw errors[0];
        if (errors.Count > 1)
        {
            throw new AggregateException($"Failed to close Session {sessionId}", errors);
        }
    }

    /// <summary>连接断开：释放附加但不推送（客户端已不可达）。对应 TS <c>disconnect</c>。</summary>
    public async Task DisconnectAsync(object client, Context context)
    {
        lock (_gate) _disconnectedClients.Add(client);
        try
        {
            await RunForClientAsync(client, async () =>
            {
                var attachment = GetAttachment(client);
                if (attachment is not null)
                {
                    await ReleaseAttachmentAsync(attachment, context, publish: false).ConfigureAwait(false);
                }
            }).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate) _disconnectedClients.Remove(client);
        }
    }

    /// <summary>排空并关闭全部路由会话（幂等）。对应 TS <c>close</c>。</summary>
    public Task CloseAsync(Context context)
    {
        lock (_gate) _closePromise ??= CloseInternalAsync(context);
        return _closePromise;
    }

    private async Task CloseInternalAsync(Context context)
    {
        List<Task> operationPromises;
        List<Task> openingPromises;
        lock (_gate)
        {
            operationPromises = _clientOperations.Values.ToList();
            openingPromises = _openingSessions.Values.Cast<Task>().ToList();
        }

        var closeErrors = new List<Exception>();
        var settledErrors = (await AllSettledAsync(operationPromises).ConfigureAwait(false))
            .Concat(await AllSettledAsync(openingPromises).ConfigureAwait(false));
        foreach (var error in settledErrors)
        {
            _options.ReportError(error);
            if (error is SessionCleanupException) closeErrors.Add(error);
        }

        List<HostedSession> hosted;
        List<ClientAttachment> attachments;
        lock (_gate)
        {
            hosted = _hostedSessions.Values.ToList();
            attachments = hosted.SelectMany(session => session.Attachments).ToList();
        }
        closeErrors.AddRange(await AllSettledAsync(
            attachments.Select(attachment => ReleaseAttachmentAsync(attachment, context))).ConfigureAwait(false));

        var closeResults = await AllSettledByIndexAsync(
            hosted.Select(session => session.Handle.CloseAsync(context)).ToList()).ConfigureAwait(false);
        for (var index = 0; index < closeResults.Count; index++)
        {
            var error = closeResults[index];
            if (error is null)
            {
                lock (_gate)
                {
                    if (ReferenceEquals(GetHostedSession(hosted[index].Id), hosted[index]))
                    {
                        _hostedSessions.Remove(hosted[index].Id);
                    }
                }
                continue;
            }
            _options.ReportError(error);
            closeErrors.Add(error);
        }

        lock (_gate)
        {
            _attachmentsByClient.Clear();
            _clientOperations.Clear();
        }
        if (closeErrors.Count > 0)
        {
            throw new AggregateException("Failed to close routed Sessions", closeErrors);
        }
    }

    /// <summary>
    /// 按客户端串行化操作（前一个完成才跑下一个；前一个的失败被吞掉，不影响后续）。
    /// 对应 TS <c>runForClient</c>。
    /// </summary>
    private Task RunForClientAsync(object client, Func<Task> operation)
        => RunForClientAsync<object?>(client, async () =>
        {
            await operation().ConfigureAwait(false);
            return null;
        });

    private async Task<T> RunForClientAsync<T>(object client, Func<Task<T>> operation)
    {
        Task previous;
        lock (_gate) previous = _clientOperations.GetValueOrDefault(client) ?? Task.CompletedTask;
        var result = RunAfterAsync(previous, operation);
        var tail = result.ContinueWith(_ => { }, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        lock (_gate) _clientOperations[client] = tail;
        _ = tail.ContinueWith(_ =>
        {
            lock (_gate)
            {
                if (_clientOperations.TryGetValue(client, out var current) && ReferenceEquals(current, tail))
                {
                    _clientOperations.Remove(client);
                }
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return await result.ConfigureAwait(false);
    }

    private static async Task<T> RunAfterAsync<T>(Task previous, Func<Task<T>> operation)
    {
        try
        {
            await previous.ConfigureAwait(false);
        }
        catch
        {
            // 对齐 TS 的 previous.catch(() => {})。
        }
        return await operation().ConfigureAwait(false);
    }

    private async Task AttachClientNowAsync(object client, string sessionId, Context context)
    {
        if (_options.IsClosing() || IsDisconnected(client)) throw new ServerDrainingError();
        var current = GetAttachment(client);
        if (current is not null && current.Session.Id == sessionId) return;

        var hosted = await AcquireAsync(sessionId, context).ConfigureAwait(false);
        if (_options.IsClosing() || IsDisconnected(client)) throw new ServerDrainingError();
        if (current is not null)
        {
            await ReleaseAttachmentAsync(current, context, publish: false).ConfigureAwait(false);
        }

        var attachment = new ClientAttachment
        {
            Id = Guid.NewGuid().ToString(),
            Client = client,
            Session = hosted,
        };
        lock (_gate) hosted.Attachments.Add(attachment);
        try
        {
            var acquiring = hosted.Handle.AttachClientAsync(context);
            attachment.Acquiring = acquiring;
            attachment.Lease = await acquiring.ConfigureAwait(false);
        }
        catch
        {
            lock (_gate) hosted.Attachments.Remove(attachment);
            throw;
        }

        if (!IsHostedCurrent(hosted) || !HasAttachment(hosted, attachment) || IsDisconnected(client)
            || _options.IsClosing())
        {
            await ReleaseAttachmentAsync(attachment, context).ConfigureAwait(false);
            throw new ServerDrainingError();
        }

        lock (_gate) _attachmentsByClient[client] = attachment;
        await _options.PublishAttachment(client,
            new RpcTarget.SessionTarget(_options.ServerId, sessionId, attachment.Id), context)
            .ConfigureAwait(false);
    }

    private async Task<Task<object?>> StartServiceCallAsync(object client, RpcTarget target, ServiceCall call,
        ServiceUpdatePublisher publish, Context context)
    {
        var attachment = RequireAttachment(client, target);
        var result = attachment.Lease!.InvokeServiceAsync(call, publish, context);
        TrackOperation(attachment, result);
        return result;
    }

    private void TrackOperation(ClientAttachment attachment, Task result)
    {
        lock (_gate) attachment.Operations.Add(result);
        _ = result.ContinueWith(task =>
        {
            lock (_gate) attachment.Operations.Remove(task);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private ClientAttachment RequireAttachment(object client, RpcTarget target)
    {
        if (_options.IsClosing() || IsDisconnected(client)) throw new ServerDrainingError();
        if (target is not RpcTarget.SessionTarget session) throw new SessionNotAttachedError();
        var attachment = GetAttachment(client);
        if (attachment is null || attachment.Session.Id != session.SessionId
            || attachment.Id != session.AttachmentId)
        {
            throw new SessionNotAttachedError();
        }
        return attachment;
    }

    private Task ReleaseAttachmentAsync(ClientAttachment attachment, Context context, bool publish = true)
    {
        lock (_gate)
        {
            attachment.Releasing ??= ReleaseAttachmentCoreAsync(attachment, context, publish);
            return attachment.Releasing;
        }
    }

    private async Task ReleaseAttachmentCoreAsync(ClientAttachment attachment, Context context, bool publish)
    {
        var errors = new List<Exception>();
        try
        {
            List<Task> operations;
            lock (_gate) operations = attachment.Operations.ToList();
            await AllSettledAsync(operations).ConfigureAwait(false);

            try
            {
                var lease = attachment.Lease;
                if (lease is null && attachment.Acquiring is not null)
                {
                    lease = await attachment.Acquiring.ConfigureAwait(false);
                }
                if (lease is not null) await lease.ReleaseAsync(context).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                errors.Add(error);
            }
            if (errors.Count == 1) throw errors[0];
            if (errors.Count > 1)
            {
                throw new AggregateException("Failed to release Session attachment", errors);
            }
        }
        finally
        {
            await ClearAttachmentAsync(attachment, context, publish).ConfigureAwait(false);
        }
    }

    private async Task ClearAttachmentAsync(ClientAttachment attachment, Context context, bool publish)
    {
        var publishDetach = false;
        lock (_gate)
        {
            attachment.Session.Attachments.Remove(attachment);
            if (_attachmentsByClient.TryGetValue(attachment.Client, out var current)
                && ReferenceEquals(current, attachment))
            {
                _attachmentsByClient.Remove(attachment.Client);
                publishDetach = publish;
            }
        }
        if (publishDetach)
        {
            await _options.PublishAttachment(attachment.Client, null, context).ConfigureAwait(false);
        }
    }

    private async Task<HostedSession> AcquireAsync(string sessionId, Context context)
    {
        Task<HostedSession>? opening;
        lock (_gate)
        {
            if (GetHostedSession(sessionId) is { } existing) return existing;
            opening = _openingSessions.GetValueOrDefault(sessionId);
        }
        if (opening is not null) return await opening.ConfigureAwait(false);

        var pending = OpenAsync(sessionId, context);
        lock (_gate) _openingSessions[sessionId] = pending;
        try
        {
            return await pending.ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                if (_openingSessions.TryGetValue(sessionId, out var current) && ReferenceEquals(current, pending))
                {
                    _openingSessions.Remove(sessionId);
                }
            }
        }
    }

    private async Task<HostedSession> OpenAsync(string sessionId, Context context)
    {
        var metadata = await _options.Host.ResolveSessionAsync(sessionId, context).ConfigureAwait(false);
        var handle = await _options.Host.OpenSessionAsync(metadata, context).ConfigureAwait(false);
        if (_options.IsClosing())
        {
            try
            {
                await handle.CloseAsync(context).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                _options.ReportError(error);
                throw new SessionCleanupException([new ServerDrainingError(), error],
                    "Failed to close routed Session acquired while draining");
            }
            throw new ServerDrainingError();
        }

        var hosted = new HostedSession { Id = metadata.Id, Handle = handle };
        lock (_gate) _hostedSessions[hosted.Id] = hosted;
        if (handle.Terminated is { } terminated)
        {
            _ = terminated.ContinueWith(task => Invalidate(hosted, task.IsFaulted
                    ? task.Exception!.GetBaseException()
                    : task.Result),
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        return hosted;
    }

    private void Invalidate(HostedSession hosted, Exception? error)
    {
        List<ClientAttachment> attachments;
        lock (_gate)
        {
            if (!ReferenceEquals(GetHostedSession(hosted.Id), hosted)) return;
            _hostedSessions.Remove(hosted.Id);
            attachments = hosted.Attachments.ToList();
        }
        foreach (var attachment in attachments)
        {
            _ = ReleaseAttachmentAsync(attachment, Context.Background)
                .ContinueWith(task => _options.ReportError(task.Exception!.GetBaseException()),
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
        if (error is not null) _options.ReportError(error);
    }

    private HostedSession? GetHostedSession(string sessionId)
        => _hostedSessions.GetValueOrDefault(sessionId);

    private ClientAttachment? GetAttachment(object client)
        => _attachmentsByClient.GetValueOrDefault(client);

    private bool IsDisconnected(object client)
    {
        lock (_gate) return _disconnectedClients.Contains(client);
    }

    private bool IsHostedCurrent(HostedSession hosted)
    {
        lock (_gate) return ReferenceEquals(GetHostedSession(hosted.Id), hosted);
    }

    private bool HasAttachment(HostedSession hosted, ClientAttachment attachment)
    {
        lock (_gate) return hosted.Attachments.Contains(attachment);
    }

    /// <summary>等待全部任务完成，返回失败原因（对齐 <c>Promise.allSettled</c>）。</summary>
    private static async Task<List<Exception>> AllSettledAsync(IEnumerable<Task> tasks)
    {
        var list = tasks.ToList();
        if (list.Count == 0) return [];
        await Task.WhenAll(list.Select(SettleAsync)).ConfigureAwait(false);
        return list.Where(task => task.IsFaulted).Select(Unwrap).ToList();
    }

    /// <summary>逐项等待，按输入顺序返回「失败原因或 null」（保留索引对应关系）。</summary>
    private static async Task<List<Exception?>> AllSettledByIndexAsync(List<Task> tasks)
    {
        var results = new List<Exception?>(tasks.Count);
        foreach (var task in tasks)
        {
            try
            {
                await task.ConfigureAwait(false);
                results.Add(null);
            }
            catch (Exception)
            {
                results.Add(Unwrap(task));
            }
        }
        return results;
    }

    private static async Task SettleAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
            // allSettled 语义：失败由调用方按 IsFaulted 收集。
        }
    }

    private static Exception Unwrap(Task task)
    {
        var exception = task.Exception!;
        return exception.InnerExceptions.Count == 1 ? exception.InnerExceptions[0] : exception;
    }
}
