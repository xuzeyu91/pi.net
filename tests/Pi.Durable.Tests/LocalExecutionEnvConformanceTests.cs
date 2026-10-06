using Pi.Durable.Env;
using Pi.Durable.Testing;
using Xunit;

namespace Pi.Durable.Tests;

/// <summary>
/// 环境契约测试。对应 TS <c>env/node.test.ts</c> 经 <c>registerEnvConformance</c> 注册的
/// 全部 <c>testing/env-conformance.ts</c> 用例（文件系统 / 目录读取器 / watch / shell 执行）。
/// <para>与 <see cref="EnvTests"/> 的区别：后者是本仓既有的手写单元测试；本类直接消费移植后的
/// conformance 套件，逐条对齐 TS 参考的用例名与判据。</para>
/// <para>宿主差异：TS 用例把 shell 写成 <c>["sh", "-c"]</c>（POSIX 假设）。Windows 上若无
/// <c>sh</c> 可解析，则整个 shell/watch 依赖套件跳过——这与 <see cref="EnvTests"/> 既有的
/// <c>HasBash()</c> 跳过策略一致。</para>
/// </summary>
public class LocalExecutionEnvConformanceTests
{
    private static IReadOnlyList<EnvConformanceCase> Cases() =>
        EnvConformance.CreateEnvConformance(new EnvConformanceOptions
        {
            Assertions = ConformanceAssertions.CreateEnv(),
            WithEnv = async use =>
            {
                var dir = Path.Join(Path.GetTempPath(), "pi-net-conformance-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                var env = new LocalExecutionEnv(new LocalExecutionEnv.LocalEnvOptions { Cwd = dir });
                try
                {
                    await use(env);
                }
                finally
                {
                    await env.CleanupAsync(Pi.Chord.Context.Context.Background);
                    try
                    {
                        Directory.Delete(dir, recursive: true);
                    }
                    catch (IOException)
                    {
                        // 清理失败不影响用例结论。
                    }
                }
            },
            Shell = ShellCommandParts(),
            Symlinks = HasPosixShell(),
        });

    /// <summary>POSIX shell 的可执行文件 + <c>-c</c>；不可用时退回 <c>["sh", "-c"]</c>（用例会因 spawn_error 失败，故调用方先跳过）。</summary>
    private static IReadOnlyList<string> ShellCommandParts()
    {
        var bash = FindBash();
        return bash is not null ? [bash, "-c"] : ["sh", "-c"];
    }

    private static bool HasPosixShell() => FindBash() is not null;

    private static string? FindBash()
    {
        var programFiles = Environment.GetEnvironmentVariable("ProgramFiles");
        if (programFiles is not null)
        {
            var candidate = Path.Join(programFiles, "Git", "bin", "bash.exe");
            if (File.Exists(candidate)) return candidate;
        }

        var programFilesX86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
        if (programFilesX86 is not null)
        {
            var candidate = Path.Join(programFilesX86, "Git", "bin", "bash.exe");
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    public static IEnumerable<object[]> CaseNames() =>
        Cases().Select(@case => new object[] { @case.Name });

    [Theory]
    [MemberData(nameof(CaseNames))]
    public async Task PassesEnvConformanceSuite(string caseKey)
    {
        var @case = Cases().Single(candidate => candidate.Name == caseKey);
        if (RequiresPosixShell(@case.Name) && !HasPosixShell())
        {
            return; // 环境无 POSIX shell 时跳过（对齐 EnvTests.HasBash 的跳过策略）。
        }

        await @case.Run();
    }

    /// <summary>shell 执行、watch 改名、符号链接相关用例依赖 POSIX shell 与 <c>ln -s</c>。</summary>
    private static bool RequiresPosixShell(string name) =>
        name.Contains("exec", StringComparison.Ordinal)
        || name.Contains("watch", StringComparison.Ordinal)
        || name.Contains("symlink", StringComparison.Ordinal);
}
