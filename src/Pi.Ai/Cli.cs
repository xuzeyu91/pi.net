using System.Text.Json;
using Pi.Ai.Auth;
using Pi.Ai.Models;
using Pi.Ai.Providers;

namespace Pi.Ai;

/// <summary>
/// OAuth 登录开发用 CLI。对应 TS <c>cli.ts</c>：<c>login [provider]</c> / <c>list</c> / <c>help</c>，
/// 凭据写入当前目录的 <c>auth.json</c>。为可测试性把输入输出注入为
/// <see cref="TextReader"/> / <see cref="TextWriter"/>（TS 用 readline）。
/// </summary>
public static class Cli
{
    /// <summary>凭据文件名。对应 TS <c>AUTH_FILE</c>。</summary>
    public const string AuthFile = "auth.json";

    /// <summary>支持 OAuth 的内建 provider（顺序与 <c>builtinProviders()</c> 一致）。</summary>
    public static IReadOnlyList<IProvider> OAuthProviders()
        => All.BuiltinProviders().Where(provider => provider.Auth?.OAuth is not null).ToList();

    /// <summary>读取 <c>auth.json</c>（缺失或损坏视为空表）。对应 TS <c>loadAuth</c>。</summary>
    public static Dictionary<string, Credential> LoadAuth(string? directory = null)
    {
        var path = Path.Combine(directory ?? Directory.GetCurrentDirectory(), AuthFile);
        if (!File.Exists(path)) return new Dictionary<string, Credential>(StringComparer.Ordinal);
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<Dictionary<string, Credential>>(json, AuthJson.Options)
                ?? new Dictionary<string, Credential>(StringComparer.Ordinal);
        }
        catch
        {
            return new Dictionary<string, Credential>(StringComparer.Ordinal);
        }
    }

    /// <summary>写回 <c>auth.json</c>。对应 TS <c>saveAuth</c>。</summary>
    public static void SaveAuth(IReadOnlyDictionary<string, Credential> auth, string? directory = null)
    {
        var path = Path.Combine(directory ?? Directory.GetCurrentDirectory(), AuthFile);
        File.WriteAllText(path, JsonSerializer.Serialize(auth, AuthJson.Options));
    }

    /// <summary>
    /// CLI 主入口。对应 TS <c>main()</c>：返回进程退出码（0 成功、1 失败）。
    /// </summary>
    public static async Task<int> RunAsync(string[] args, TextReader input, TextWriter output, TextWriter error,
        string? directory = null, CancellationToken cancellationToken = default)
    {
        var command = args.Length > 0 ? args[0] : null;
        var providers = OAuthProviders();

        if (command is null or "help" or "--help" or "-h")
        {
            var providerList = string.Join("\n", providers.Select(provider => $"  {provider.Id,-20} {provider.Name}"));
            await output.WriteLineAsync(
                "Usage: pi-ai <command> [provider]\n\nCommands:\n  login [provider]  Login to an OAuth provider\n" +
                $"  list              List available providers\n\nProviders:\n{providerList}").ConfigureAwait(false);
            return 0;
        }

        if (command == "list")
        {
            foreach (var provider in providers)
            {
                await output.WriteLineAsync($"{provider.Id,-20} {provider.Name}").ConfigureAwait(false);
            }
            return 0;
        }

        if (command == "login")
        {
            try
            {
                var providerId = args.Length > 1 ? args[1] : null;
                if (providerId is null)
                {
                    for (var option = 0; option < providers.Count; option++)
                    {
                        await output.WriteLineAsync($"  {option + 1}. {providers[option].Name}").ConfigureAwait(false);
                    }
                    var raw = await PromptAsync(input, output, $"Enter number (1-{providers.Count}): ")
                        .ConfigureAwait(false);
                    var selected = int.TryParse(raw, out var parsed) ? parsed - 1 : -1;
                    providerId = selected >= 0 && selected < providers.Count ? providers[selected].Id : null;
                }

                if (providerId is null || !providers.Any(provider => provider.Id == providerId))
                {
                    throw new InvalidOperationException($"Unknown provider: {providerId ?? ""}");
                }

                await LoginAsync(providerId, input, output, directory, cancellationToken).ConfigureAwait(false);
                return 0;
            }
            catch (Exception exception)
            {
                await error.WriteLineAsync($"Error: {exception.Message}").ConfigureAwait(false);
                return 1;
            }
        }

        await error.WriteLineAsync($"Error: Unknown command: {command}").ConfigureAwait(false);
        return 1;
    }

    /// <summary>
    /// 跑一家 provider 的 OAuth 登录并把凭据写入 <c>auth.json</c>。对应 TS <c>login()</c>。
    /// </summary>
    public static async Task LoginAsync(string providerId, TextReader input, TextWriter output,
        string? directory = null, CancellationToken cancellationToken = default)
    {
        var provider = OAuthProviders().FirstOrDefault(candidate => candidate.Id == providerId)
            ?? throw new InvalidOperationException($"Unknown provider: {providerId}");
        var oauth = provider.Auth!.OAuth!;

        var interaction = new ProviderAuthInteraction(
            new ConsoleAuthInteraction(input, output), cancellationToken);
        // 本开发 CLI 不持久化安装 id；应用应跨登录复用同一个。
        var credential = await oauth.LoginAsync(interaction,
            new LoginOptions { GetDeviceId = () => Guid.NewGuid().ToString() }, cancellationToken)
            .ConfigureAwait(false);

        var auth = LoadAuth(directory);
        auth[providerId] = credential;
        SaveAuth(auth, directory);
        await output.WriteLineAsync($"\nCredentials saved to {AuthFile}").ConfigureAwait(false);
    }

    private static async Task<string> PromptAsync(TextReader input, TextWriter output, string question)
    {
        await output.WriteAsync(question).ConfigureAwait(false);
        return await input.ReadLineAsync().ConfigureAwait(false) ?? "";
    }

    /// <summary>把交互映射到 <see cref="TextReader"/> / <see cref="TextWriter"/> 的 <see cref="IAuthInteraction"/>。</summary>
    private sealed class ConsoleAuthInteraction(TextReader input, TextWriter output) : IAuthInteraction
    {
        public async Task<string> PromptAsync(AuthPrompt prompt, CancellationToken cancellationToken = default)
        {
            if (prompt is AuthPrompt.Select select)
            {
                await output.WriteLineAsync($"\n{select.Message}").ConfigureAwait(false);
                for (var index = 0; index < select.Options.Count; index++)
                {
                    await output.WriteLineAsync($"  {index + 1}. {select.Options[index].Label}").ConfigureAwait(false);
                }
                var raw = await Cli.PromptAsync(input, output, $"Enter number (1-{select.Options.Count}): ")
                    .ConfigureAwait(false);
                var selected = (int.TryParse(raw, out var parsed) ? parsed : 0) - 1;
                if (selected < 0 || selected >= select.Options.Count)
                {
                    throw new InvalidOperationException("Invalid selection");
                }
                return select.Options[selected].Id;
            }

            var suffix = prompt switch
            {
                AuthPrompt.Text text when text.Placeholder is { Length: > 0 } => $" ({text.Placeholder})",
                AuthPrompt.Secret secret when secret.Placeholder is { Length: > 0 } => $" ({secret.Placeholder})",
                AuthPrompt.ManualCode manual when manual.Placeholder is { Length: > 0 } => $" ({manual.Placeholder})",
                _ => "",
            };
            return await Cli.PromptAsync(input, output, $"{MessageOf(prompt)}{suffix}: ").ConfigureAwait(false);
        }

        public void Notify(AuthEvent @event)
        {
            switch (@event)
            {
                case AuthEvent.AuthUrl authUrl:
                    output.WriteLine($"\nOpen this URL in your browser:\n{authUrl.Url}");
                    if (authUrl.Instructions is { Length: > 0 }) output.WriteLine(authUrl.Instructions);
                    break;
                case AuthEvent.DeviceCode deviceCode:
                    output.WriteLine($"\nOpen this URL in your browser:\n{deviceCode.VerificationUri}");
                    output.WriteLine($"Enter code: {deviceCode.UserCode}");
                    break;
                case AuthEvent.Info info:
                    output.WriteLine(info.Message);
                    break;
                case AuthEvent.Progress progress:
                    output.WriteLine(progress.Message);
                    break;
            }
        }

        private static string MessageOf(AuthPrompt prompt) => prompt switch
        {
            AuthPrompt.Text text => text.Message,
            AuthPrompt.Secret secret => secret.Message,
            AuthPrompt.Select select => select.Message,
            AuthPrompt.ManualCode manual => manual.Message,
            _ => "",
        };
    }
}

/// <summary>CLI 的 auth.json JSON 选项（wire 形状与 TS 一致：camelCase + 类型判别）。</summary>
internal static class AuthJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}
