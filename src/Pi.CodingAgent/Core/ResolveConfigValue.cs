using System.ComponentModel;
using System.Text.RegularExpressions;
using Pi.CodingAgent.Utils;

namespace Pi.CodingAgent.Core;

/// <summary>One part of a parsed config-value template. Port of the TS <c>TemplatePart</c> union.</summary>
public abstract record TemplatePart
{
    /// <summary>A literal chunk, kept verbatim.</summary>
    public sealed record Literal(string Value) : TemplatePart;

    /// <summary>An environment-variable reference to interpolate.</summary>
    public sealed record Env(string Name) : TemplatePart;
}

/// <summary>A parsed config-value reference: a shell command or a template. Port of <c>ConfigValueReference</c>.</summary>
public abstract record ConfigValueReference
{
    /// <summary>The value starts with <c>!</c>; the rest is a shell command.</summary>
    public sealed record Command(string Config) : ConfigValueReference;

    /// <summary>A literal template with optional environment interpolation.</summary>
    public sealed record Template(IReadOnlyList<TemplatePart> Parts) : ConfigValueReference;
}

/// <summary>
/// Port of <c>core/resolve-config-value.ts</c>: resolve configuration values that may be shell commands,
/// environment variables, or literals. Used by <c>auth-storage.ts</c> and <c>model-registry.ts</c>.
/// </summary>
public static partial class ConfigValueResolver
{
    // Cache for shell command results (persists for process lifetime).
    private static readonly Dictionary<string, string?> CommandResultCache = new(StringComparer.Ordinal);

    private static readonly Lock CacheLock = new();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex EnvVarNameRegex();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*")]
    private static partial Regex EnvVarNamePrefixRegex();

    /// <summary>The single environment variable a template names, when the template is exactly that one variable.</summary>
    public static string? GetConfigValueEnvVarName(string config)
    {
        var reference = ParseConfigValueReference(config);
        return reference is ConfigValueReference.Template { Parts: [TemplatePart.Env env] } ? env.Name : null;
    }

    /// <summary>Every environment variable a template references, in first-seen order and de-duplicated.</summary>
    public static IReadOnlyList<string> GetConfigValueEnvVarNames(string config)
    {
        var reference = ParseConfigValueReference(config);
        return reference is ConfigValueReference.Template template ? GetTemplateEnvVarNames(template.Parts) : [];
    }

    /// <summary>The referenced environment variables that are currently unset.</summary>
    public static IReadOnlyList<string> GetMissingConfigValueEnvVarNames(
        string config,
        IReadOnlyDictionary<string, string>? env = null)
    {
        var missing = new List<string>();
        foreach (var name in GetConfigValueEnvVarNames(config))
        {
            if (ResolveEnvConfigValue(name, env) is null) missing.Add(name);
        }
        return missing;
    }

    /// <summary>Whether the value is a shell command (starts with <c>!</c>).</summary>
    public static bool IsCommandConfigValue(string config) => ParseConfigValueReference(config) is ConfigValueReference.Command;

    /// <summary>Whether every environment variable the value references is set.</summary>
    public static bool IsConfigValueConfigured(string config, IReadOnlyDictionary<string, string>? env = null)
        => GetMissingConfigValueEnvVarNames(config, env).Count == 0;

    /// <summary>
    /// Resolve a config value (API key, header value, …) to an actual value.
    /// <list type="bullet">
    /// <item><description>Starting with <c>!</c>: the rest is run as a shell command and its trimmed stdout is used (cached).</description></item>
    /// <item><description><c>$NAME</c> / <c>${NAME}</c> interpolate the named environment variable.</description></item>
    /// <item><description>In non-command values <c>$$</c> escapes a literal <c>$</c> and <c>$!</c> a literal <c>!</c>.</description></item>
    /// <item><description>Anything else is a literal.</description></item>
    /// </list>
    /// </summary>
    public static string? ResolveConfigValue(string config, IReadOnlyDictionary<string, string>? env = null)
    {
        var reference = ParseConfigValueReference(config);
        return reference is ConfigValueReference.Command command
            ? ExecuteCommand(command.Config)
            : ResolveTemplate(((ConfigValueReference.Template)reference).Parts, env);
    }

    /// <summary>Like <see cref="ResolveConfigValue"/> but never consults or fills the command cache.</summary>
    public static string? ResolveConfigValueUncached(string config, IReadOnlyDictionary<string, string>? env = null)
    {
        var reference = ParseConfigValueReference(config);
        return reference is ConfigValueReference.Command command
            ? ExecuteCommandUncached(command.Config)
            : ResolveTemplate(((ConfigValueReference.Template)reference).Parts, env);
    }

    /// <summary>Resolve a config value or throw a descriptive error naming the failed source.</summary>
    public static string ResolveConfigValueOrThrow(
        string config,
        string description,
        IReadOnlyDictionary<string, string>? env = null)
    {
        var resolvedValue = ResolveConfigValueUncached(config, env);
        if (resolvedValue is not null) return resolvedValue;

        var reference = ParseConfigValueReference(config);
        if (reference is ConfigValueReference.Command command)
        {
            throw new InvalidOperationException(
                $"Failed to resolve {description} from shell command: {command.Config[1..]}");
        }

        if (reference is ConfigValueReference.Template)
        {
            var missingEnvVars = GetMissingConfigValueEnvVarNames(config, env);
            if (missingEnvVars.Count == 1)
            {
                throw new InvalidOperationException(
                    $"Failed to resolve {description} from environment variable: {missingEnvVars[0]}");
            }
            if (missingEnvVars.Count > 1)
            {
                throw new InvalidOperationException(
                    $"Failed to resolve {description} from environment variables: {string.Join(", ", missingEnvVars)}");
            }
        }

        throw new InvalidOperationException($"Failed to resolve {description}");
    }

    /// <summary>Resolve every header value with the same logic as API keys, dropping entries that resolve to null.</summary>
    public static IReadOnlyDictionary<string, string>? ResolveHeaders(
        IReadOnlyDictionary<string, string>? headers,
        IReadOnlyDictionary<string, string>? env = null)
    {
        if (headers is null) return null;
        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in headers)
        {
            var resolvedValue = ResolveConfigValue(value, env);
            if (!string.IsNullOrEmpty(resolvedValue)) resolved[key] = resolvedValue;
        }
        return resolved.Count > 0 ? resolved : null;
    }

    /// <summary>Like <see cref="ResolveHeaders"/> but throws on the first header that cannot be resolved.</summary>
    public static IReadOnlyDictionary<string, string>? ResolveHeadersOrThrow(
        IReadOnlyDictionary<string, string>? headers,
        string description,
        IReadOnlyDictionary<string, string>? env = null)
    {
        if (headers is null) return null;
        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in headers)
        {
            resolved[key] = ResolveConfigValueOrThrow(value, $"{description} header \"{key}\"", env);
        }
        return resolved.Count > 0 ? resolved : null;
    }

    /// <summary>Clear the config-value command cache. Exported for testing.</summary>
    public static void ClearConfigValueCache()
    {
        lock (CacheLock)
        {
            CommandResultCache.Clear();
        }
    }

    /// <summary>Parse a config value into a reference. Port of <c>parseConfigValueReference</c>.</summary>
    internal static ConfigValueReference ParseConfigValueReference(string config)
        => config.StartsWith('!')
            ? new ConfigValueReference.Command(config)
            : new ConfigValueReference.Template(ParseConfigValueTemplate(config));

    /// <summary>Split a template into literal and environment parts. Port of <c>parseConfigValueTemplate</c>.</summary>
    internal static IReadOnlyList<TemplatePart> ParseConfigValueTemplate(string config)
    {
        var parts = new List<TemplatePart>();
        var index = 0;

        while (index < config.Length)
        {
            var dollarIndex = config.IndexOf('$', index);
            if (dollarIndex < 0)
            {
                AppendLiteral(parts, config[index..]);
                break;
            }

            AppendLiteral(parts, config[index..dollarIndex]);
            var nextChar = dollarIndex + 1 < config.Length ? config[dollarIndex + 1] : '\0';

            if (nextChar is '$' or '!')
            {
                AppendLiteral(parts, nextChar.ToString());
                index = dollarIndex + 2;
                continue;
            }

            if (nextChar == '{')
            {
                var endIndex = config.IndexOf('}', dollarIndex + 2);
                if (endIndex < 0)
                {
                    AppendLiteral(parts, "$");
                    index = dollarIndex + 1;
                    continue;
                }

                var name = config[(dollarIndex + 2)..endIndex];
                if (EnvVarNameRegex().IsMatch(name))
                {
                    parts.Add(new TemplatePart.Env(name));
                }
                else
                {
                    AppendLiteral(parts, config[dollarIndex..(endIndex + 1)]);
                }
                index = endIndex + 1;
                continue;
            }

            var match = EnvVarNamePrefixRegex().Match(config[(dollarIndex + 1)..]);
            if (match.Success)
            {
                parts.Add(new TemplatePart.Env(match.Value));
                index = dollarIndex + 1 + match.Value.Length;
                continue;
            }

            AppendLiteral(parts, "$");
            index = dollarIndex + 1;
        }

        return parts;
    }

    private static void AppendLiteral(List<TemplatePart> parts, string value)
    {
        if (value.Length == 0) return;
        if (parts.Count > 0 && parts[^1] is TemplatePart.Literal previous)
        {
            parts[^1] = new TemplatePart.Literal(previous.Value + value);
            return;
        }
        parts.Add(new TemplatePart.Literal(value));
    }

    private static string? ResolveEnvConfigValue(string name, IReadOnlyDictionary<string, string>? env)
    {
        // JS `env?.[name] || process.env[name] || undefined`: an empty string falls through.
        if (env is not null && env.TryGetValue(name, out var scoped) && scoped.Length > 0) return scoped;
        var processValue = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrEmpty(processValue) ? null : processValue;
    }

    private static IReadOnlyList<string> GetTemplateEnvVarNames(IReadOnlyList<TemplatePart> parts)
    {
        var names = new List<string>();
        foreach (var part in parts)
        {
            if (part is not TemplatePart.Env env || names.Contains(env.Name, StringComparer.Ordinal)) continue;
            names.Add(env.Name);
        }
        return names;
    }

    private static string? ResolveTemplate(IReadOnlyList<TemplatePart> parts, IReadOnlyDictionary<string, string>? env)
    {
        var resolved = new System.Text.StringBuilder();
        foreach (var part in parts)
        {
            if (part is TemplatePart.Literal literal)
            {
                resolved.Append(literal.Value);
                continue;
            }
            var envValue = ResolveEnvConfigValue(((TemplatePart.Env)part).Name, env);
            if (envValue is null) return null;
            resolved.Append(envValue);
        }
        return resolved.ToString();
    }

    private static (bool Executed, string? Value) ExecuteWithConfiguredShell(string command)
    {
        try
        {
            var shellConfig = Shell.GetShellConfig();
            var commandFromStdin = shellConfig.CommandTransport == "stdin";
            var args = commandFromStdin ? shellConfig.Args : [.. shellConfig.Args, command];
            var result = ChildProcess.SpawnSync(shellConfig.Shell, args, new SpawnSyncOptions
            {
                Encoding = "utf-8",
                Input = commandFromStdin ? command : null,
                TimeoutMs = 10000,
                Stdio = [commandFromStdin ? StdioMode.Pipe : StdioMode.Ignore, StdioMode.Pipe, StdioMode.Ignore],
                WindowsHide = true,
            });

            if (result.Error is not null)
            {
                return IsExecutableMissing(result.Error) ? (false, null) : (true, null);
            }

            if (result.Status != 0) return (true, null);

            var value = result.Stdout.Trim();
            return (true, value.Length == 0 ? null : value);
        }
        catch (Exception)
        {
            return (false, null);
        }
    }

    private static string? ExecuteWithDefaultShell(string command)
    {
        try
        {
            var (shell, args) = NodePath.IsWindows
                ? (Environment.GetEnvironmentVariable("comspec") ?? "cmd.exe", (IReadOnlyList<string>)["/d", "/s", "/c", command])
                : ("/bin/sh", (IReadOnlyList<string>)["-c", command]);
            var result = ChildProcess.SpawnSync(shell, args, new SpawnSyncOptions
            {
                Encoding = "utf-8",
                TimeoutMs = 10000,
                Stdio = [StdioMode.Ignore, StdioMode.Pipe, StdioMode.Ignore],
            });
            return result.Status == 0 && result.Error is null ? NullIfEmpty(result.Stdout.Trim()) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? ExecuteCommandUncached(string commandConfig)
    {
        var command = commandConfig[1..];
        if (!NodePath.IsWindows) return ExecuteWithDefaultShell(command);
        var configuredResult = ExecuteWithConfiguredShell(command);
        return configuredResult.Executed ? configuredResult.Value : ExecuteWithDefaultShell(command);
    }

    private static string? ExecuteCommand(string commandConfig)
    {
        lock (CacheLock)
        {
            if (CommandResultCache.TryGetValue(commandConfig, out var cached)) return cached;
        }

        var result = ExecuteCommandUncached(commandConfig);
        lock (CacheLock)
        {
            CommandResultCache[commandConfig] = result;
        }
        return result;
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    // Node reports a missing executable as `err.code === "ENOENT"`; .NET surfaces ERROR_FILE_NOT_FOUND.
    private static bool IsExecutableMissing(Exception error) =>
        error is Win32Exception { NativeErrorCode: 2 } or FileNotFoundException;
}
