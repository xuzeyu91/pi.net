using System.Text.RegularExpressions;
using Pi.Durable.Types;

namespace Pi.Durable.Harness;

/// <summary>
/// 每个注册表都持有的内建任务定义；它们不是扩展，不能被移除或替换。
/// 对应 TS <c>harness/registry.ts</c> 的 <c>BUILTIN_TASKS</c>
/// （C# 暂只含 pi.tool——GenerationTask / CompactionTask 随其文件落地追加）。
/// </summary>
public static class Registry
{
    public static IReadOnlyList<AnyDurableTask> BuiltinTasks { get; } =
    [
        AnyDurableTask.From(ToolTask.Instance),
    ];

    private static readonly Regex SectionKey = new("^[a-z][a-z0-9_-]*$", RegexOptions.None, TimeSpan.FromSeconds(1));

    /// <summary>一次已发布注册表状态的不可变视图。对应 TS <c>RegistryState</c>。</summary>
    private sealed class RegistryState : IRegistrySnapshot
    {
        private readonly IReadOnlyList<IExtension> _extensions;
        private readonly IReadOnlyDictionary<string, IExtension> _byName;
        private readonly IReadOnlyDictionary<string, AnyDurableTask> _tasks;

        public RegistryState(IReadOnlyList<IExtension> extensions)
        {
            _extensions = extensions;
            _byName = extensions.ToDictionary(extension => extension.Name, StringComparer.Ordinal);
            var tasks = new Dictionary<string, AnyDurableTask>(StringComparer.Ordinal);
            foreach (var task in BuiltinTasks) tasks[task.Name] = task;
            foreach (var extension in extensions)
            {
                foreach (var task in extension.Tasks ?? [])
                {
                    if (!tasks.TryAdd(task.Name, task))
                    {
                        throw new InvalidOperationException(
                            $"Task {task.Name} of extension {extension.Name} is already installed");
                    }
                }
            }

            _tasks = tasks;
        }

        public IReadOnlyList<IExtension> Installed() => _extensions;

        public IExtension? Extension(string name) => _byName.GetValueOrDefault(name);

        public IReadOnlyList<(IExtension Extension, IToolRegistration Tool)> Tools()
            => [.. _extensions.SelectMany(extension => (extension.Tools ?? []).Select(tool => (extension, tool)))];

        public IReadOnlyList<(IExtension Extension, IPromptSection Section)> Sections()
            => [.. _extensions.SelectMany(extension => (extension.Sections ?? []).Select(section => (extension, section)))];

        public IReadOnlyList<AnyDurableTask> Tasks() => [.. _tasks.Values];

        public AnyDurableTask? Task(string name) => _tasks.GetValueOrDefault(name);
    }

    private sealed class RegistryImpl : IRegistry
    {
        private RegistryState _current = new([]);
        private readonly object _gate = new();
        private readonly HashSet<Action> _listeners = [];

        public IRegistrySnapshot Snapshot()
        {
            lock (_gate) return _current;
        }

        public IDisposable Subscribe(Action listener)
        {
            lock (_gate) _listeners.Add(listener);
            return new Unsubscribe(this, listener);
        }

        public void Install(IExtension extension)
        {
            ValidateExtension(extension);
            List<IExtension> next;
            lock (_gate)
            {
                var current = _current.Installed().ToList();
                var index = current.FindIndex(installed => installed.Name == extension.Name);
                next = index < 0 ? [.. current, extension] : current.Select((installed, at) => at == index ? extension : installed).ToList();
            }

            Publish(next);
        }

        public void Uninstall(IExtension extension)
        {
            List<IExtension> next;
            lock (_gate)
            {
                var current = _current.Installed();
                if (!current.Any(installed => installed.Name == extension.Name)) return;
                next = current.Where(installed => installed.Name != extension.Name).ToList();
            }

            Publish(next);
        }

        /// <summary>构建并校验下一状态（任务名冲突时抛出），然后同步发布。对应 TS <c>#publish</c>。</summary>
        private void Publish(List<IExtension> extensions)
        {
            var state = new RegistryState(extensions);
            Action[] listeners;
            lock (_gate)
            {
                _current = state;
                listeners = [.. _listeners];
            }

            foreach (var listener in listeners) listener();
        }

        private sealed class Unsubscribe(RegistryImpl registry, Action listener) : IDisposable
        {
            public void Dispose()
            {
                lock (registry._gate) registry._listeners.Remove(listener);
            }
        }
    }

    /// <summary>一个扩展内唯一的工具名与段键；段键有效且未保留。对应 TS <c>validateExtension</c>。</summary>
    private static void ValidateExtension(IExtension extension)
    {
        var tools = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tool in extension.Tools ?? [])
        {
            if (!tools.Add(tool.Name))
            {
                throw new InvalidOperationException(
                    $"Extension {extension.Name} has two tools named {tool.Name}");
            }
        }

        var sections = new HashSet<string>(StringComparer.Ordinal);
        foreach (var section in extension.Sections ?? [])
        {
            if (!SectionKey.IsMatch(section.Key))
            {
                throw new ArgumentException(
                    $"Section key \"{section.Key}\" must match {SectionKey}");
            }

            if (section.Key == AgentDocs.InstructionsKey)
            {
                throw new InvalidOperationException(
                    $"Section key {section.Key} is reserved for the agent's instructions");
            }

            if (!sections.Add(section.Key))
            {
                throw new InvalidOperationException(
                    $"Extension {extension.Name} has two sections with key {section.Key}");
            }
        }
    }

    /// <summary>创建一个只持有内建任务的应用拥有的注册表。对应 TS <c>createRegistry</c>。</summary>
    public static IRegistry CreateRegistry() => new RegistryImpl();
}
