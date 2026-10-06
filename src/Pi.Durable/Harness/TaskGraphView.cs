using Pi.Chord.Context;
using Pi.Chord.Delta;
using Pi.Chord.Services;
using Pi.Durable.Session;
using Pi.Durable.Storage;
using Pi.Durable.Types;

namespace Pi.Durable.Harness;

using Path = Pi.Chord.Delta.Path;
using TaskStatus = Pi.Durable.Types.TaskStatus;

/// <summary>存活任务的持久化状态（剥离 checkpoint 与 outcome 载荷）。对应 TS <c>TaskGraphState</c>。</summary>
public abstract record TaskGraphState
{
    /// <summary>pending / running：阶段名。</summary>
    public sealed record Active(string Status, string Phase) : TaskGraphState;

    /// <summary>waiting：等待的任务与连接策略。</summary>
    public sealed record WaitingOn(string Phase, IReadOnlyList<TaskId<object?>> On, JoinPolicy Policy)
        : TaskGraphState;

    /// <summary>结果已定，保持到普通自有工作排空。</summary>
    public sealed record Completing(string OutcomeStatus) : TaskGraphState;
}

/// <summary>任务图节点（类型化视图）。对应 TS <c>TaskGraphNode</c>。</summary>
public sealed record TaskGraphNode
{
    public required TaskId<object?> Id { get; init; }

    public required string Kind { get; init; }

    public required ConversationId ConversationId { get; init; }

    /// <summary>属主任务；对话自有任务缺省。</summary>
    public TaskId<object?>? Owner { get; init; }

    public required bool Background { get; init; }

    public required bool AbortRequested { get; init; }

    public required TaskGraphState State { get; init; }

    /// <summary>本任务拥有的对话，按 ID 序。</summary>
    public required IReadOnlyList<ConversationId> Conversations { get; init; }
}

/// <summary>图值的形状常量。挂载值是严格 JSON（DeltaApply 要求），节点形状对应 TS JSON 节点。</summary>
public static class TaskGraphShape
{
    public const string TasksKey = "tasks";
}

/// <summary>把 JSON 形状的图值转为类型化节点视图。</summary>
public static class TaskGraphReader
{
    /// <summary>读指定键的节点；不存在为 null。</summary>
    public static TaskGraphNode? Node(IReadOnlyDictionary<string, object?> graph, string key)
    {
        if (!graph.TryGetValue(TaskGraphShape.TasksKey, out var tasksValue)
            || tasksValue is not IReadOnlyDictionary<string, object?> tasks)
        {
            return null;
        }

        return tasks.TryGetValue(key, out var nodeValue) && nodeValue is IReadOnlyDictionary<string, object?> node
            ? FromJson(node)
            : null;
    }

    /// <summary>全部节点，按键（十进制 ID 字符串）排序。</summary>
    public static IReadOnlyList<TaskGraphNode> Nodes(IReadOnlyDictionary<string, object?> graph)
    {
        if (!graph.TryGetValue(TaskGraphShape.TasksKey, out var tasksValue)
            || tasksValue is not IReadOnlyDictionary<string, object?> tasks)
        {
            return [];
        }

        return [.. tasks.Keys
            .OrderBy(k => k, StringComparer.Ordinal)
            .Select(k => FromJson((IReadOnlyDictionary<string, object?>)tasks[k]!))];
    }

    /// <summary>存活节点数。</summary>
    public static int Count(IReadOnlyDictionary<string, object?> graph)
        => graph.TryGetValue(TaskGraphShape.TasksKey, out var tasksValue)
            && tasksValue is IReadOnlyDictionary<string, object?> tasks
            ? tasks.Count
            : 0;

    private static TaskGraphNode FromJson(IReadOnlyDictionary<string, object?> node) => new()
    {
        Id = TaskId<object?>.From(Convert.ToInt64(node["id"])),
        Kind = (string)node["kind"]!,
        ConversationId = ConversationId.From(Convert.ToInt64(node["conversationId"])),
        Owner = node.TryGetValue("owner", out var owner) && owner is not null
            ? TaskId<object?>.From(Convert.ToInt64(owner))
            : null,
        Background = Convert.ToBoolean(node["background"]),
        AbortRequested = Convert.ToBoolean(node["abortRequested"]),
        State = StateFrom((IReadOnlyDictionary<string, object?>)node["state"]!),
        Conversations = [.. ((IReadOnlyList<object?>)node["conversations"]!)
            .Select(v => ConversationId.From(Convert.ToInt64(v)))],
    };

    private static TaskGraphState StateFrom(IReadOnlyDictionary<string, object?> state)
    {
        var status = (string)state["status"]!;
        return status switch
        {
            "pending" or "running" => new TaskGraphState.Active(status, (string)state["phase"]!),
            "waiting" => new TaskGraphState.WaitingOn(
                (string)state["phase"]!,
                [.. ((IReadOnlyList<object?>)state["on"]!).Select(v => TaskId<object?>.From(Convert.ToInt64(v)))],
                Enum.TryParse<JoinPolicy>((string)state["policy"]!, true, out var policy)
                    ? policy
                    : JoinPolicy.FailFast),
            _ => new TaskGraphState.Completing((string)state["outcomeStatus"]!),
        };
    }
}

/// <summary>
/// Harness 的任务图挂载：由第一个观察者在 Session 线上构建，最后一个观察者离开时丢弃；
/// 从 Session 的提交发布（持久）推进。对应 TS harness <c>task-graph.ts</c> 的 <c>TaskGraphView</c>。
/// </summary>
public sealed class TaskGraphView
{
    private static readonly IReadOnlyList<TaskStatus> LiveStatuses =
        [TaskStatus.Pending, TaskStatus.Running, TaskStatus.Waiting, TaskStatus.Completing];

    private const int ScanPageSize = 256;

    private static readonly Path TasksPath = Path.Root.Append(Seg.Key(TaskGraphShape.TasksKey));

    private readonly DurableSession _session;
    private readonly IStorage _storage;
    private readonly object _gate = new();
    private Mount? _mount;
    private bool _closed;

    /// <summary>挂载：当前修订（严格 JSON）与观察者（值对象整体替换，故持可变字段而非 record）。</summary>
    private sealed class Mount
    {
        public IReadOnlyDictionary<string, object?> Value = new Dictionary<string, object?>
        {
            [TaskGraphShape.TasksKey] = new Dictionary<string, object?>(),
        };

        public readonly HashSet<object> Observers = [];
    }

    public TaskGraphView(DurableSession session, IStorage storage)
    {
        _session = session;
        _storage = storage;
        _session.SubscribeCommits((publication, context) =>
        {
            lock (_gate)
            {
                if (_mount is not null) Advance(_mount, publication, context);
            }
        });
        _session.SubscribeClose(() =>
        {
            object[] observers;
            lock (_gate)
            {
                _closed = true;
                observers = [.. _mount?.Observers ?? []];
                _mount = null;
            }

            foreach (var observer in observers) CloseObserver(observer);
        });
    }

    /// <summary>图的可释放只读 Chord 状态（JSON 形状）。对应 TS <c>state()</c>。</summary>
    public async Task<AttachedReplicatedState<IReadOnlyDictionary<string, object?>>> StateAsync(Context context)
    {
        var (observer, detach) = await AttachAsync(
            (value, release) => new CommittedStateSource<IReadOnlyDictionary<string, object?>>(value, release),
            context).ConfigureAwait(false);
        try
        {
            return ReplicatedStateAttachments.AttachReplicatedStateSource(observer);
        }
        catch (Exception)
        {
            detach();
            throw;
        }
    }

    /// <summary>图的序列化精确帧 watch（JSON 形状）；取消 context 使其停止。对应 TS <c>watch()</c>。</summary>
    public async Task<CommittedWatch<IReadOnlyDictionary<string, object?>>> WatchAsync(Context context)
    {
        var (observer, _) = await AttachAsync(
            (value, release) => new CommittedWatch<IReadOnlyDictionary<string, object?>>(value, release),
            context).ConfigureAwait(false);
        var signal = context.AbortSignal;
        if (signal?.IsCancellationRequested == true)
        {
            observer.Cancel();
            throw new OperationCanceledException(signal.GetValueOrDefault());
        }

        if (signal is { } watchSignal) observer.ObserveCancellation(watchSignal);
        return observer;
    }

    /// <summary>从当前修订创建观察者并注册，原子地在 Session 线上。对应 TS 私有 <c>#attach</c>。</summary>
    private async Task<(T Observer, Action Detach)> AttachAsync<T>(
        Func<IReadOnlyDictionary<string, object?>, Action, T> create, Context context)
        where T : notnull
    {
        return await _session.ReadOnLineAsync(async () =>
        {
            Mount? mount;
            lock (_gate) mount = _mount;
            if (mount is null)
            {
                var value = await BuildAsync(context).ConfigureAwait(false);
                lock (_gate)
                {
                    _mount ??= new Mount { Value = value };
                    mount = _mount;
                }
            }

            var (observer, detach) = AttachTo(mount!, create);
            // 挂载构建期间 close 或取消可能已开始；此后不再注册。
            lock (_gate)
            {
                if (_closed) throw HarnessUtil.ClosedError();
            }

            context.AbortSignal?.ThrowIfCancellationRequested();
            lock (_gate)
            {
                _mount = mount;
                mount.Observers.Add(observer);
            }

            return (observer, detach);
        }).ConfigureAwait(false);
    }

    private static (T Observer, Action Detach) AttachTo<T>(
        Mount mount, Func<IReadOnlyDictionary<string, object?>, Action, T> create)
    {
        T? observer = default;
        Action detach = () =>
        {
            lock (mount.Observers)
            {
                mount.Observers.Remove(observer!);
            }
        };
        observer = create(mount.Value, detach);
        return (observer, detach);
    }

    private async Task<IReadOnlyDictionary<string, object?>> BuildAsync(Context context)
    {
        var tasks = new Dictionary<string, object?>();
        var records = new List<TaskRecord>();
        foreach (var status in LiveStatuses)
        {
            records.AddRange(await HarnessUtil.ScanAllAsync(
                cursor => _storage.ScanTasksAsync(
                    new TaskQuery { Status = status }, ScanPageSize, cursor)).ConfigureAwait(false));
        }

        records.Sort((a, b) => a.Id.Value.CompareTo(b.Id.Value));
        foreach (var record in records)
        {
            var owned = await HarnessUtil.ScanAllAsync(
                cursor => _storage.ScanConversationsAsync(
                    new ConversationQuery { OwnerTaskId = record.Id }, ScanPageSize, cursor))
                .ConfigureAwait(false);
            var conversations = owned
                .Select(conversation => (object?)conversation.Id.Value)
                .OrderBy(value => value)
                .ToList();
            tasks[IdKey(record.Id)] = NodeOf(record, conversations);
        }

        return new Dictionary<string, object?> { [TaskGraphShape.TasksKey] = tasks };
    }

    /// <summary>从一次发布派生挂载的操作，应用后交给每个观察者。对应 TS <c>advance</c>。</summary>
    private static void Advance(Mount mount, CommitPublication publication, Context context)
    {
        var ops = new List<DeltaOp>();
        // 本次发布设置或删除的节点，覆盖挂载值之上。
        var changed = new Dictionary<string, IReadOnlyDictionary<string, object?>?>();
        IReadOnlyDictionary<string, object?>? Node(string key)
        {
            if (changed.TryGetValue(key, out var value)) return value;
            if (mount.Value.TryGetValue(TaskGraphShape.TasksKey, out var tasksValue)
                && tasksValue is IReadOnlyDictionary<string, object?> tasks)
            {
                return tasks.TryGetValue(key, out var node)
                    ? (IReadOnlyDictionary<string, object?>?)node
                    : null;
            }

            return null;
        }

        foreach (var change in publication.Changes)
        {
            if (change is not CommitChange.TaskTable taskChange) continue;
            var record = taskChange.Value;
            var key = IdKey(record.Id);
            var previous = Node(key);
            if (record.State.Status == TaskStatus.Terminal)
            {
                if (previous is null) continue;
                ops.Add(new DeltaOp.Delete(TasksPath.Append(Seg.Key(key))));
                changed[key] = null;
                continue;
            }

            var previousConversations = previous is null
                ? []
                : (IReadOnlyList<object?>)previous["conversations"]!;
            var next = NodeOf(record, previousConversations);
            if (previous is not null && JsonEquals(next, previous)) continue;
            ops.Add(new DeltaOp.Set(TasksPath.Append(Seg.Key(key)), next));
            changed[key] = next;
        }

        // 在任务之后处理，使同一次提交内随属主任务创建的对话能找到属主节点。
        // 发布内变更顺序未指定，因此每个属主的列表再次排序。
        var created = new Dictionary<string, List<ConversationId>>();
        foreach (var change in publication.Changes)
        {
            if (change is not CommitChange.ConversationTable conversationChange) continue;
            if (conversationChange.Value.Owner is not { } owner) continue;
            var key = IdKey(owner.TaskId);
            if (Node(key) is null) continue;
            if (!created.TryGetValue(key, out var ids)) created[key] = ids = [];
            ids.Add(conversationChange.Value.Id);
        }

        foreach (var (key, ids) in created)
        {
            var conversations = new List<object?>((IReadOnlyList<object?>)Node(key)!["conversations"]!);
            conversations.AddRange(ids.Select(id => (object?)id.Value));
            conversations.Sort((a, b) => Convert.ToInt64(a).CompareTo(Convert.ToInt64(b)));
            ops.Add(new DeltaOp.Set(TasksPath.Append(Seg.Key(key)).Append(Seg.Key("conversations")),
                conversations));
        }

        if (ops.Count == 0) return;
        mount.Value = (IReadOnlyDictionary<string, object?>)DeltaApply.Apply(ops, mount.Value)!;        var frameContext = ContextSignals.WithoutAbortSignal(context);
        object[] observers;
        lock (mount.Observers) observers = [.. mount.Observers];
        foreach (var observer in observers)
        {
            switch (observer)
            {
                case CommittedWatch<IReadOnlyDictionary<string, object?>> watch:
                    watch.Advance(mount.Value, ops, frameContext);
                    break;
                case CommittedStateSource<IReadOnlyDictionary<string, object?>> source:
                    source.Advance(mount.Value, ops, frameContext);
                    break;
            }
        }
    }

    private static string IdKey(TaskId<object?> id)
        => id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>节点 → 严格 JSON 形状（id/conversationId/owner/conversations 为十进制数值）。</summary>
    internal static IReadOnlyDictionary<string, object?> NodeOf(
        TaskRecord record, IReadOnlyList<object?> conversations)
    {
        var json = new Dictionary<string, object?>
        {
            ["id"] = record.Id.Value,
            ["kind"] = record.Kind,
            ["conversationId"] = record.ConversationId.Value,
            ["background"] = record.Background,
            ["abortRequested"] = record.AbortRequested,
            ["state"] = StateOf(record),
            ["conversations"] = conversations.ToList(),
        };
        if (record.Owner is { } owner) json["owner"] = owner.Value;
        return json;
    }

    private static IReadOnlyDictionary<string, object?> StateOf(TaskRecord record)
    {
        var state = record.State;
        switch (state.Status)
        {
            case TaskStatus.Pending:
            case TaskStatus.Running:
                return new Dictionary<string, object?>
                {
                    ["status"] = state.Status == TaskStatus.Pending ? "pending" : "running",
                    ["phase"] = PhaseOf(state.Checkpoint),
                };
            case TaskStatus.Waiting:
                return new Dictionary<string, object?>
                {
                    ["status"] = "waiting",
                    ["phase"] = PhaseOf(state.Checkpoint),
                    ["on"] = (state.On ?? []).Select(id => (object?)id.Value).ToList(),
                    ["policy"] = (state.Policy ?? JoinPolicy.FailFast).ToString(),
                };
            // 终态记录不会到达这里：它们离开图。
            case TaskStatus.Completing:
            case TaskStatus.Terminal:
                return new Dictionary<string, object?>
                {
                    ["status"] = "completing",
                    ["outcomeStatus"] = (state.Outcome?.Status ?? TaskOutcomeStatus.Completed)
                        .ToString()
                        .ToLowerInvariant(),
                };
            default:
                throw new ArgumentOutOfRangeException(nameof(record), state.Status, null);
        }
    }

    /// <summary>checkpoint 的阶段名（擦除对象：先反射 Phase 属性，回退 JSON 键 phase）。</summary>
    private static string PhaseOf(object? checkpoint)
    {
        if (checkpoint is null) return "";
        var phase = checkpoint.GetType().GetProperty("Phase");
        if (phase?.GetValue(checkpoint) is string value) return value;
        try
        {
            var element = System.Text.Json.JsonSerializer.SerializeToElement(checkpoint);
            if (element.ValueKind == System.Text.Json.JsonValueKind.Object
                && element.TryGetProperty("phase", out var found)
                && found.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                return found.GetString() ?? "";
            }
        }
        catch
        {
            // 非 JSON 形状的 checkpoint：阶段名未知。
        }

        return "";
    }

    private static bool JsonEquals(object? a, object? b)
        => System.Text.Json.JsonSerializer.SerializeToElement(a).ToString()
            == System.Text.Json.JsonSerializer.SerializeToElement(b).ToString();

    private static void CloseObserver(object observer)
    {
        switch (observer)
        {
            case CommittedWatch<IReadOnlyDictionary<string, object?>> watch:
                watch.CloseSession();
                break;
            case CommittedStateSource<IReadOnlyDictionary<string, object?>> source:
                source.CloseSession();
                break;
        }
    }
}
