using System.Text.Json.Nodes;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Extensions.Mcp;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// Differential tests for the MCP server configuration and log (port of
/// <c>extensions/mcp/config.ts</c> / <c>log.ts</c>, batch 4d-4a). Vectors are transcribed from the
/// TS behaviour: global/project merge, override semantics, namespace clashes, error messages, the
/// <c>/mcp</c> write-back path (indentation and foreign keys preserved), and the server log format.
/// </summary>
public class McpConfigTests : IDisposable
{
    private readonly List<string> tempDirs = [];

    private string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pi-mcp-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(dir);
        tempDirs.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        foreach (var dir in tempDirs)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // Best effort cleanup.
            }
        }
    }

    private static void WriteFile(string path, string content)
    {
        var parent = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(parent);
        File.WriteAllText(path, content);
    }

    private static McpServerConfig StdioConfig() => new(new JsonObject { ["command"] = "echo" });

    // ------------------------------------------------------------------ loadMcpConfig

    [Fact]
    public void Load_GlobalOnly_ReadsServersInOrder()
    {
        var dir = NewTempDir();
        var path = Path.Combine(dir, "mcp.json");
        WriteFile(path, """
        {
          "mcpServers": {
            "filesystem": { "command": "npx", "args": ["-y", "server-fs"] },
            "docs": { "url": "https://example.com/mcp" }
          }
        }
        """);

        var config = McpConfig.Load(dir, dir, projectTrusted: false);

        Assert.Empty(config.Errors);
        Assert.Equal(2, config.Servers.Count);
        Assert.Equal("filesystem", config.Servers[0].Name);
        Assert.Equal("npx", config.Servers[0].Config.Command);
        Assert.Equal(["-y", "server-fs"], config.Servers[0].Config.Args);
        Assert.Equal(path, config.Servers[0].Source);
        Assert.Equal("global", config.Servers[0].Scope);
        Assert.Equal("docs", config.Servers[1].Name);
        Assert.Equal("https://example.com/mcp", config.Servers[1].Config.Url);
        Assert.Null(config.AutoEnableCodemode);
        Assert.Null(config.ProjectConfig);
    }

    [Fact]
    public void Load_ProjectOverride_KeepsGlobalFieldsAndMarksOverride()
    {
        var dir = NewTempDir();
        var agentDir = Path.Combine(dir, "agent");
        var cwd = Path.Combine(dir, "project");
        WriteFile(Path.Combine(agentDir, "mcp.json"), """
        {
          "mcpServers": {
            "internal-tools": { "command": "npx", "args": ["internal"], "env": { "TOKEN": "secret" } }
          }
        }
        """);
        var projectPath = Path.Combine(cwd, Config.ConfigDirName, "mcp.json");
        WriteFile(projectPath, """
        {
          "mcpServers": {
            "internal-tools": { "enabled": false }
          }
        }
        """);

        var config = McpConfig.Load(agentDir, cwd, projectTrusted: true);

        Assert.Empty(config.Errors);
        var server = Assert.Single(config.Servers);
        Assert.Equal("internal-tools", server.Name);
        Assert.False(server.Config.Enabled);
        Assert.Equal("npx", server.Config.Command);
        Assert.Equal(["internal"], server.Config.Args);
        Assert.Equal("secret", server.Config.Env!["TOKEN"]);
        Assert.Equal(projectPath, server.Override);
        Assert.Equal(projectPath, config.ProjectConfig);
    }

    [Fact]
    public void Load_ProjectOverrideWithoutBase_Errors()
    {
        var dir = NewTempDir();
        var projectPath = Path.Combine(dir, Config.ConfigDirName, "mcp.json");
        WriteFile(projectPath, """
        {
          "mcpServers": {
            "orphan": { "enabled": false }
          }
        }
        """);

        var config = McpConfig.Load(Path.Combine(dir, "agent"), dir, projectTrusted: true);

        var error = Assert.Single(config.Errors);
        Assert.Equal($"{projectPath}: server \"orphan\" needs \"command\" or \"url\", or a global server to override", error);
        Assert.Empty(config.Servers);
    }

    [Fact]
    public void Load_ProjectOverrideWithExtraKeys_Errors()
    {
        var dir = NewTempDir();
        WriteFile(Path.Combine(dir, "agent", "mcp.json"), """
        { "mcpServers": { "srv": { "command": "echo" } } }
        """);
        var projectPath = Path.Combine(dir, Config.ConfigDirName, "mcp.json");
        WriteFile(projectPath, """
        { "mcpServers": { "srv": { "enabled": false, "args": ["x"] } } }
        """);

        var config = McpConfig.Load(Path.Combine(dir, "agent"), dir, projectTrusted: true);

        var error = Assert.Single(config.Errors);
        Assert.Equal($"{projectPath}: server \"srv\": an override can only set enabled, exposure, toolExposure", error);
    }

    [Fact]
    public void Load_NamespaceClash_Errors()
    {
        var dir = NewTempDir();
        var path = Path.Combine(dir, "mcp.json");
        WriteFile(path, """
        { "mcpServers": { "a-b": { "command": "echo" }, "a_b": { "command": "echo" } } }
        """);

        var config = McpConfig.Load(dir, dir, projectTrusted: false);

        var error = Assert.Single(config.Errors);
        Assert.Equal($"{path}: server \"a_b\" conflicts with \"a-b\"", error);
        var server = Assert.Single(config.Servers);
        Assert.Equal("a-b", server.Name);
    }

    [Fact]
    public void Load_ProjectUrlWithAuth_Errors()
    {
        var dir = NewTempDir();
        var projectPath = Path.Combine(dir, Config.ConfigDirName, "mcp.json");
        WriteFile(projectPath, """
        { "mcpServers": { "docs": { "url": "https://example.com/mcp", "auth": { "provider": "openai" } } } }
        """);

        var config = McpConfig.Load(Path.Combine(dir, "agent"), dir, projectTrusted: true);

        var error = Assert.Single(config.Errors);
        Assert.Equal($"{projectPath}: server \"docs\": auth is only allowed in the global mcp.json", error);
    }

    [Fact]
    public void Load_AutoEnableCodemode_ReadsBooleanAndRejectsOther()
    {
        var dir = NewTempDir();
        WriteFile(Path.Combine(dir, "mcp.json"), """
        { "autoEnableCodemode": false, "mcpServers": {} }
        """);

        var config = McpConfig.Load(dir, dir, projectTrusted: false);

        Assert.Empty(config.Errors);
        Assert.False(config.AutoEnableCodemode);

        var other = NewTempDir();
        var path = Path.Combine(other, "mcp.json");
        WriteFile(path, """
        { "autoEnableCodemode": "yes", "mcpServers": {} }
        """);

        var invalid = McpConfig.Load(other, other, projectTrusted: false);

        var error = Assert.Single(invalid.Errors);
        Assert.Equal($"{path}: autoEnableCodemode must be a boolean", error);
        Assert.Null(invalid.AutoEnableCodemode);
    }

    [Fact]
    public void Load_InvalidJson_ErrorsWithPathPrefix()
    {
        var dir = NewTempDir();
        var path = Path.Combine(dir, "mcp.json");
        WriteFile(path, "{ not json");

        var config = McpConfig.Load(dir, dir, projectTrusted: false);

        var error = Assert.Single(config.Errors);
        // The parser message differs between Node and .NET (difference C106); the path prefix matches.
        Assert.StartsWith($"{path}: ", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_NotAnObject_Errors()
    {
        var dir = NewTempDir();
        var path = Path.Combine(dir, "mcp.json");
        WriteFile(path, "[]");

        var config = McpConfig.Load(dir, dir, projectTrusted: false);

        var error = Assert.Single(config.Errors);
        Assert.Equal($"{path}: expected an object with an \"mcpServers\" object", error);
    }

    [Fact]
    public void Load_McpServersNotAnObject_Errors()
    {
        var dir = NewTempDir();
        var path = Path.Combine(dir, "mcp.json");
        WriteFile(path, "{ \"mcpServers\": [] }");

        var config = McpConfig.Load(dir, dir, projectTrusted: false);

        var error = Assert.Single(config.Errors);
        Assert.Equal($"{path}: expected an object with an \"mcpServers\" object", error);
    }

    [Fact]
    public void Load_NotTrusted_IgnoresProjectFile()
    {
        var dir = NewTempDir();
        WriteFile(Path.Combine(dir, "agent", "mcp.json"), """
        { "mcpServers": { "srv": { "command": "echo" } } }
        """);
        WriteFile(Path.Combine(dir, Config.ConfigDirName, "mcp.json"), """
        { "mcpServers": { "other": { "command": "echo" } } }
        """);

        var config = McpConfig.Load(Path.Combine(dir, "agent"), dir, projectTrusted: false);

        Assert.Empty(config.Errors);
        var server = Assert.Single(config.Servers);
        Assert.Equal("srv", server.Name);
        Assert.Null(config.ProjectConfig);
    }

    [Fact]
    public void Load_MissingFiles_EmptyConfig()
    {
        var dir = NewTempDir();

        var config = McpConfig.Load(Path.Combine(dir, "agent"), dir, projectTrusted: true);

        Assert.Empty(config.Errors);
        Assert.Empty(config.Servers);
    }

    [Fact]
    public void Load_InvalidServerConfig_ErrorsFromValidator()
    {
        var dir = NewTempDir();
        var path = Path.Combine(dir, "mcp.json");
        WriteFile(path, """
        { "mcpServers": { "broken": { "args": ["x"] } } }
        """);

        var config = McpConfig.Load(dir, dir, projectTrusted: false);

        var error = Assert.Single(config.Errors);
        Assert.StartsWith($"{path}: ", error, StringComparison.Ordinal);
        Assert.Empty(config.Servers);
    }

    // ------------------------------------------------------------------ updateMcpServerConfig

    [Fact]
    public void Update_EnabledTrue_RemovesDefaultKey()
    {
        var dir = NewTempDir();
        var path = Path.Combine(dir, "mcp.json");
        WriteFile(path, """
        {
            "mcpServers": {
                "filesystem": {
                    "command": "npx",
                    "enabled": false
                }
            }
        }
        """);

        McpConfig.Update(path, "filesystem", new McpServerConfigPatch { Enabled = true });

        var text = File.ReadAllText(path);
        Assert.DoesNotContain("enabled", text, StringComparison.Ordinal);
        Assert.Contains("\"command\": \"npx\"", text, StringComparison.Ordinal);
        Assert.EndsWith("\n", text, StringComparison.Ordinal);
        // The file's four-space indentation is kept.
        Assert.Contains("\n    \"mcpServers\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Update_OverrideEntry_KeepsExplicitValues()
    {
        var dir = NewTempDir();
        var path = Path.Combine(dir, "mcp.json");
        WriteFile(path, """
        {
            "mcpServers": {
                "filesystem": {
                    "enabled": false
                }
            }
        }
        """);

        McpConfig.Update(path, "filesystem", new McpServerConfigPatch { Enabled = true });

        var text = File.ReadAllText(path);
        Assert.Contains("\"enabled\": true", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Update_ExposureCodemode_RemovesKey_AndDeferred_SetsKey()
    {
        var dir = NewTempDir();
        var path = Path.Combine(dir, "mcp.json");
        WriteFile(path, """
        {
            "mcpServers": {
                "a": { "command": "echo", "exposure": "direct" },
                "b": { "command": "echo", "exposure": "direct" }
            }
        }
        """);

        McpConfig.Update(path, "a", new McpServerConfigPatch { Exposure = "codemode" });
        McpConfig.Update(path, "b", new McpServerConfigPatch { Exposure = "deferred" });

        var servers = (JsonObject)JsonNode.Parse(File.ReadAllText(path))!["mcpServers"]!;
        Assert.False(servers["a"]!.AsObject().ContainsKey("exposure"));
        Assert.Equal("deferred", servers["b"]!["exposure"]!.GetValue<string>());
    }

    [Fact]
    public void Update_MissingServer_Throws()
    {
        var dir = NewTempDir();
        var path = Path.Combine(dir, "mcp.json");
        WriteFile(path, """
        { "mcpServers": { "srv": { "command": "echo" } } }
        """);

        var error = Assert.Throws<InvalidOperationException>(
            () => McpConfig.Update(path, "nope", new McpServerConfigPatch { Enabled = false }));
        Assert.Equal($"{path} does not define MCP server \"nope\"", error.Message);
    }

    [Fact]
    public void Update_WithOverride_CreatesMissingFileAndEntry()
    {
        var dir = NewTempDir();
        var path = Path.Combine(dir, "nested", "mcp.json");

        McpConfig.Update(path, "srv", new McpServerConfigPatch { Enabled = false }, @override: true);

        var servers = (JsonObject)JsonNode.Parse(File.ReadAllText(path))!["mcpServers"]!;
        Assert.False(servers["srv"]!["enabled"]!.GetValue<bool>());
    }

    [Fact]
    public void Update_PreservesForeignKeys()
    {
        var dir = NewTempDir();
        var path = Path.Combine(dir, "mcp.json");
        WriteFile(path, """
        {
            "autoEnableCodemode": true,
            "mcpServers": {
                "srv": { "command": "echo" }
            },
            "futureKey": { "nested": [1, 2] }
        }
        """);

        McpConfig.Update(path, "srv", new McpServerConfigPatch { Enabled = false });

        var parsed = (JsonObject)JsonNode.Parse(File.ReadAllText(path))!;
        Assert.True(parsed["autoEnableCodemode"]!.GetValue<bool>());
        Assert.NotNull(parsed["futureKey"]);
        Assert.False(parsed["mcpServers"]!["srv"]!["enabled"]!.GetValue<bool>());
    }

    // ------------------------------------------------------------------ add / remove

    [Fact]
    public void Add_NewAndReplaced()
    {
        var dir = NewTempDir();
        var path = Path.Combine(dir, "mcp.json");

        Assert.False(McpConfig.Add(path, "srv", StdioConfig()));
        Assert.True(McpConfig.Add(path, "srv", StdioConfig()));

        var servers = (JsonObject)JsonNode.Parse(File.ReadAllText(path))!["mcpServers"]!;
        Assert.Equal("echo", servers["srv"]!["command"]!.GetValue<string>());
    }

    [Fact]
    public void Remove_ExistingMissingAndNoFile()
    {
        var dir = NewTempDir();
        var path = Path.Combine(dir, "mcp.json");
        WriteFile(path, """
        { "mcpServers": { "srv": { "command": "echo" } } }
        """);

        Assert.True(McpConfig.Remove(path, "srv"));
        Assert.False(McpConfig.Remove(path, "srv"));
        Assert.False(McpConfig.Remove(Path.Combine(dir, "missing.json"), "srv"));
        Assert.DoesNotContain("srv", File.ReadAllText(path), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ log

    [Fact]
    public void FormatMessage_StringDataWithLoggerAndLevel()
    {
        var now = new DateTimeOffset(2026, 10, 10, 12, 34, 56, 789, TimeSpan.Zero);
        var parameters = JsonNode.Parse("""{ "level": "warning", "logger": "fetch", "data": "hello" }""");

        var line = McpServerLogFormat.FormatMessage("srv", parameters, now);

        Assert.Equal("2026-10-10T12:34:56.789Z [srv] warning fetch: hello\n", line);
    }

    [Fact]
    public void FormatMessage_DefaultsLevelAndIndentsContinuationLines()
    {
        var now = new DateTimeOffset(2026, 1, 2, 3, 4, 5, 0, TimeSpan.Zero);
        var parameters = JsonNode.Parse("""{ "data": "first\nsecond" }""");

        var line = McpServerLogFormat.FormatMessage("srv", parameters, now);

        Assert.Equal("2026-01-02T03:04:05.000Z [srv] info first\n    second\n", line);
    }

    [Fact]
    public void FormatMessage_NonRecordParams_WrapAsData()
    {
        var now = new DateTimeOffset(2026, 1, 2, 3, 4, 5, 0, TimeSpan.Zero);

        Assert.Equal("2026-01-02T03:04:05.000Z [srv] info hi\n", McpServerLogFormat.FormatMessage("srv", JsonValue.Create("hi"), now));
        Assert.Equal("2026-01-02T03:04:05.000Z [srv] info 42\n", McpServerLogFormat.FormatMessage("srv", JsonValue.Create(42), now));
    }

    [Fact]
    public void FormatMessage_EmptyLogger_IsOmitted()
    {
        var now = new DateTimeOffset(2026, 1, 2, 3, 4, 5, 0, TimeSpan.Zero);
        var parameters = JsonNode.Parse("""{ "logger": "", "data": "x" }""");

        var line = McpServerLogFormat.FormatMessage("srv", parameters, now);

        Assert.Equal("2026-01-02T03:04:05.000Z [srv] info x\n", line);
    }

    [Fact]
    public void ServerLog_Write_CreatesFileAndAppends()
    {
        var dir = NewTempDir();
        var path = Path.Combine(dir, "logs", "mcp.log");
        var log = new McpServerLog(path);

        log.Write("srv", JsonNode.Parse("""{ "data": "one" }"""));
        log.Write("srv", JsonNode.Parse("""{ "data": "two" }"""));

        var lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Length);
        Assert.EndsWith(" [srv] info one", lines[0], StringComparison.Ordinal);
        Assert.EndsWith(" [srv] info two", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void ServerLog_Write_RotatesPastMaxBytes()
    {
        var dir = NewTempDir();
        var path = Path.Combine(dir, "mcp.log");
        File.WriteAllText(path, new string('x', 5 * 1024 * 1024 + 1));
        var log = new McpServerLog(path);

        log.Write("srv", JsonNode.Parse("""{ "data": "after rotation" }"""));

        Assert.True(File.Exists($"{path}.1"));
        Assert.Equal(new string('x', 5 * 1024 * 1024 + 1), File.ReadAllText($"{path}.1"));
        Assert.EndsWith(" [srv] info after rotation\n", File.ReadAllText(path), StringComparison.Ordinal);
    }
}
