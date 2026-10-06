using System.Text;
using Pi.Chord.Context;
using Pi.Durable.Env;
using ExecutionError = Pi.Durable.Env.ExecutionError;
using FileError = Pi.Durable.Env.FileError;
using FileInfo = Pi.Durable.Env.FileInfo;

namespace Pi.Durable.Testing;

/// <summary>
/// 与运行器无关的 <see cref="IExecutionEnv"/> 契约用例。对应 TS <c>testing/env-conformance.ts</c>
/// 的 <c>createEnvConformance</c>：<c>withEnv</c> 必须为每个用例恰好调用并等待一次回调，
/// 每次提供一个 <c>cwd</c> 为全新、空、可写目录的环境。
/// <para>移植说明：TS 的 <c>Result</c> 是 <c>{ ok, value } | { ok, error }</c> 判别联合，用例用
/// <c>getOrThrow</c> 取成功值、用 <c>errorCode</c> 取失败码；C# 的 <see cref="Result{T,TError}"/>
/// 通过 <c>IsOk</c> / <c>Value</c> / <c>Error</c> 承载同样语义，此处提供 <c>GetOrThrow</c> 与
/// <c>ErrorCode</c> 两个等价辅助。</para>
/// </summary>
public static class EnvConformance
{
    private static Pi.Chord.Context.Context Context => Pi.Chord.Context.Context.Background;

    /// <summary>创建与运行器无关的环境 conformance 用例。</summary>
    public static IReadOnlyList<EnvConformanceCase> CreateEnvConformance(EnvConformanceOptions options)
    {
        var assert = options.Assertions;
        var shell = options.Shell ?? ["sh", "-c"];
        var symlinks = options.Symlinks ?? true;

        var cases = new List<EnvConformanceCase>();

        EnvConformanceCase CreateCase(string name, Func<IExecutionEnv, Task> test, int? timeoutMs = null) =>
            new()
            {
                Name = name,
                TimeoutMs = timeoutMs,
                Run = () => options.WithEnv(test),
            };

        // watch 用例每步最多等待三秒，超过测试运行器默认允许值。
        EnvConformanceCase WatchCase(string name, Func<IExecutionEnv, Task> test) => CreateCase(name, test, 30_000);

        void AddCase(string name, Func<IExecutionEnv, Task> test, int? timeoutMs = null) =>
            cases.Add(CreateCase(name, test, timeoutMs));

        void AddWatchCase(string name, Func<IExecutionEnv, Task> test) => cases.Add(WatchCase(name, test));

        // ─── binary reader ───

        AddCase("binary reader reads byte ranges of the opened file", async env =>
        {
            GetOrThrow(await env.WriteFileAsync("data.txt", "hello world", Context));
            var reader = GetOrThrow(await env.OpenBinaryReaderAsync("data.txt", null, Context));
            var info = GetOrThrow(await reader.InfoAsync(Context));
            Assert.PartialDeepEqual(assert, new { info.Name, info.Size }, new { Name = "data.txt", Size = 11L });
            Assert.StrictEqual(assert, Decode(GetOrThrow(await reader.ReadAsync(0, 5, Context))), "hello");
            Assert.StrictEqual(assert, Decode(GetOrThrow(await reader.ReadAsync(6, 100, Context))), "world");
            Assert.StrictEqual(assert, GetOrThrow(await reader.ReadAsync(11, 4, Context)).Length, 0L);
            Assert.StrictEqual(assert, GetOrThrow(await reader.ReadAsync(50, 1, Context)).Length, 0L);
            Assert.StrictEqual(assert, GetOrThrow(await reader.ReadAsync(3, 0, Context)).Length, 0L);
            Assert.StrictEqual(assert, ErrorCode(await reader.ReadAsync(-1, 1, Context)), "invalid");
            // 不可移植差异：源 TS 断言 read(0, 1.5) 因“长度非整数”而 invalid；C# 的 ReadAsync(long offset, long length)
            // 在类型系统层面无法表达非整数长度，该子断言无语义等价物，故不移植。
            Assert.StrictEqual(assert, ErrorCode(await reader.ReadAsync(0, 1, AbortedContext())), "aborted");
            await reader.CloseAsync(Context);
            await reader.CloseAsync(Context);
            Assert.StrictEqual(assert, ErrorCode(await reader.ReadAsync(0, 1, Context)), "invalid");
            Assert.StrictEqual(assert, ErrorCode(await reader.InfoAsync(Context)), "invalid");
        });

        AddCase("binary reader scans lines like decoding the whole file", async env =>
        {
            // 字节序标记、换行前的非法序列、空行、稍后的 U+FEFF、结尾无换行。
            var bytes = new byte[]
            {
                0xef, 0xbb, 0xbf, 0x61, 0x0a, 0xe2, 0x82, 0x0a, 0x0a, 0xef, 0xbb, 0xbf, 0x62, 0x0a, 0xc3, 0xa9,
            };
            GetOrThrow(await env.WriteFileAsync("lines.txt", bytes, Context));
            var lines = DecodeWithBom(bytes, ignoreBom: false).Split("\n");
            var reader = GetOrThrow(await env.OpenBinaryReaderAsync("lines.txt", null, Context));
            try
            {
                var ranges = new (long StartLine, long? EndLine)[]
                {
                    (0, null),
                    (0, 1),
                    (1, 3),
                    (2, 3),
                    (3, null),
                    (4, 9),
                };
                foreach (var (startLine, endLine) in ranges)
                {
                    var scan = GetOrThrow(await reader.ScanLinesAsync(new LineScanOptions(startLine, endLine), Context));
                    var selected = Slice(lines, startLine, endLine);
                    string Range(long from, long to) => DecodeWithBom(bytes[(int)from..(int)to], ignoreBom: from > 0);
                    Assert.StrictEqual(assert, scan.Newlines, (long)(lines.Length - 1));
                    Assert.StrictEqual(assert, Range(scan.Start, scan.End), string.Join("\n", selected));
                    Assert.StrictEqual(assert, scan.SelectedBytes, (long)Encoding.UTF8.GetByteCount(string.Join("\n", selected)));
                    Assert.StrictEqual(assert, Range(scan.Start, scan.FirstLineEnd), lines[(int)startLine]);
                    Assert.StrictEqual(assert, scan.FirstLineBytes, (long)Encoding.UTF8.GetByteCount(lines[(int)startLine]));
                }

                Assert.PartialDeepEqual(
                    assert,
                    GetOrThrow(await reader.ScanLinesAsync(new LineScanOptions(9), Context)),
                    new { Start = (long)bytes.Length, End = (long)bytes.Length, SelectedBytes = 0L });
                Assert.StrictEqual(assert, ErrorCode(await reader.ScanLinesAsync(new LineScanOptions(2, 2), Context)), "invalid");
            }
            finally
            {
                await reader.CloseAsync(Context);
            }
        });

        AddCase("binary reader keeps reading the file it opened after a rename", async env =>
        {
            GetOrThrow(await env.WriteFileAsync("a.txt", "one", Context));
            var reader = GetOrThrow(await env.OpenBinaryReaderAsync("a.txt", null, Context));
            try
            {
                GetOrThrow(await env.RenameFileAsync("a.txt", "b.txt", Context));
                GetOrThrow(await env.WriteFileAsync("a.txt", "two", Context));
                Assert.StrictEqual(assert, Decode(GetOrThrow(await reader.ReadAsync(0, 10, Context))), "one");
            }
            finally
            {
                await reader.CloseAsync(Context);
            }
        });

        AddCase("binary reader refuses directories, missing files and aborted opens", async env =>
        {
            GetOrThrow(await env.CreateDirAsync("dir", null, Context));
            GetOrThrow(await env.WriteFileAsync("file.txt", "x", Context));
            Assert.StrictEqual(assert, ErrorCode(await env.OpenBinaryReaderAsync("dir", null, Context)), "is_directory");
            Assert.StrictEqual(assert, ErrorCode(await env.OpenBinaryReaderAsync("missing.txt", null, Context)), "not_found");
            Assert.StrictEqual(assert, ErrorCode(await env.OpenBinaryReaderAsync("file.txt", null, AbortedContext())), "aborted");
        });

        // ─── directory reader ───

        AddCase("directory reader pages every entry exactly once", async env =>
        {
            var names = new[] { "a.txt", "b.txt", "c.txt", "d.txt", "e.txt" };
            foreach (var name in names) GetOrThrow(await env.WriteFileAsync(name, name, Context));
            GetOrThrow(await env.CreateDirAsync("sub", null, Context));
            var (pages, done) = await ReadAllAsync(env, ".", 2);
            Assert.Ok(assert, done, "directory reader reached the end");
            foreach (var page in pages) Assert.Ok(assert, page.Count <= 2, "page within maxEntries");
            var entries = pages.SelectMany(p => p).ToList();
            Assert.DeepEqual(
                assert,
                entries.Select(e => e.Name).OrderBy(n => n, StringComparer.Ordinal).ToList(),
                names.Append("sub").OrderBy(n => n, StringComparer.Ordinal).ToList());
            Assert.StrictEqual(assert, entries.First(e => e.Name == "sub").Kind, FileKind.Directory);
            var a = entries.First(e => e.Name == "a.txt");
            Assert.PartialDeepEqual(assert, new { a.Kind, a.Size }, new { Kind = FileKind.File, Size = 5L });
        });

        AddCase("directory reader reports the end and refuses use after close", async env =>
        {
            GetOrThrow(await env.CreateDirAsync("empty", null, Context));
            var reader = GetOrThrow(await env.OpenDirReaderAsync("empty", Context));
            Assert.DeepEqual(assert, GetOrThrow(await reader.NextAsync(10, Context)), new DirPage([], true));
            Assert.DeepEqual(assert, GetOrThrow(await reader.NextAsync(10, Context)), new DirPage([], true));
            Assert.StrictEqual(assert, ErrorCode(await reader.NextAsync(0, Context)), "invalid");
            Assert.StrictEqual(assert, ErrorCode(await reader.NextAsync(1, AbortedContext())), "aborted");
            await reader.CloseAsync(Context);
            await reader.CloseAsync(Context);
            Assert.StrictEqual(assert, ErrorCode(await reader.NextAsync(1, Context)), "invalid");
        });

        AddCase("directory reader refuses missing paths and files", async env =>
        {
            GetOrThrow(await env.WriteFileAsync("file.txt", "x", Context));
            Assert.StrictEqual(assert, ErrorCode(await env.OpenDirReaderAsync("missing", Context)), "not_found");
            var file = await env.OpenDirReaderAsync("file.txt", Context);
            Assert.StrictEqual(assert, ErrorCode(file), "not_directory");
            Assert.StrictEqual(assert, ErrorCode(await env.OpenDirReaderAsync(".", AbortedContext())), "aborted");
        });

        AddCase("directory reader skips entries removed during enumeration", async env =>
        {
            GetOrThrow(await env.CreateDirAsync("dir", null, Context));
            foreach (var name in new[] { "x", "y", "z" }) GetOrThrow(await env.WriteFileAsync($"dir/{name}", name, Context));
            var reader = GetOrThrow(await env.OpenDirReaderAsync("dir", Context));
            try
            {
                foreach (var name in new[] { "x", "y", "z" }) GetOrThrow(await env.RemoveAsync($"dir/{name}", null, Context));
                var entries = new List<FileInfo>();
                for (var page = 0; page < 10; page++)
                {
                    var next = GetOrThrow(await reader.NextAsync(10, Context));
                    entries.AddRange(next.Entries);
                    if (next.Done) break;
                }

                Assert.DeepEqual(assert, entries, new List<FileInfo>());
            }
            finally
            {
                await reader.CloseAsync(Context);
            }
        });

        // ─── watch ───

        AddWatchCase("watch reports a missing file's creation, changes, replacement and removal", async env =>
        {
            await WatchingAsync(env, [new WatchTarget { Path = "AGENTS.md" }], async helpers =>
            {
                await helpers.ExpectChange("AGENTS.md", async () =>
                {
                    GetOrThrow(await env.WriteFileAsync("AGENTS.md", "one", Context));
                });
                await helpers.ExpectChange("AGENTS.md", async () =>
                {
                    GetOrThrow(await env.WriteFileAsync("AGENTS.md", "two!", Context));
                });
                // 编辑器通过把新文件改名覆盖上来替换文件。
                await helpers.ExpectChange("AGENTS.md", async () =>
                {
                    GetOrThrow(await env.WriteFileAsync("AGENTS.md.tmp", "three", Context));
                    GetOrThrow(await env.RenameFileAsync("AGENTS.md.tmp", "AGENTS.md", Context));
                });
                await helpers.ExpectChange("AGENTS.md", async () =>
                {
                    GetOrThrow(await env.WriteFileAsync("AGENTS.md", "four", Context));
                });
                await helpers.ExpectChange("AGENTS.md", async () =>
                {
                    GetOrThrow(await env.RemoveAsync("AGENTS.md", null, Context));
                });
            });
        });

        AddWatchCase("watch reports a missing target whose ancestors are created", async env =>
        {
            await WatchingAsync(env, [new WatchTarget { Path = "a/b/c/AGENTS.md" }], async helpers =>
            {
                await helpers.ExpectChange("a/b/c/AGENTS.md", async () =>
                {
                    GetOrThrow(await env.WriteFileAsync("a/b/c/AGENTS.md", "x", Context));
                });
            });
        });

        AddWatchCase("watch follows directories created together with their contents", async env =>
        {
            GetOrThrow(await env.CreateDirAsync("skills", null, Context));
            await WatchingAsync(env, [new WatchTarget { Path = "skills", Recursive = true }], async helpers =>
            {
                // 早于新目录上任何 watcher 可存在之前写入。
                await helpers.ExpectChange("skills/a/b/SKILL.md", async () =>
                {
                    GetOrThrow(await env.WriteFileAsync("skills/a/b/SKILL.md", "one", Context));
                });
                await helpers.ExpectChange("skills/a/b/SKILL.md", async () =>
                {
                    GetOrThrow(await env.WriteFileAsync("skills/a/b/SKILL.md", "two!", Context));
                });
                await helpers.ExpectChange("skills/a/b/c/SKILL.md", async () =>
                {
                    GetOrThrow(await env.WriteFileAsync("skills/a/b/c/SKILL.md", "deeper", Context));
                });
            });
        });

        AddWatchCase("watch keeps watching a path whose parent is renamed and recreated", async env =>
        {
            GetOrThrow(await env.WriteFileAsync("proj/.pi/skills/x.md", "x", Context));
            await WatchingAsync(env, [new WatchTarget { Path = "proj/.pi/skills", Recursive = true }], async helpers =>
            {
                await helpers.ExpectChange("proj/.pi/skills", async () =>
                {
                    GetOrThrow(await env.RenameFileAsync("proj/.pi", "proj/old", Context));
                });
                await helpers.ExpectChange("proj/.pi/skills/y.md", async () =>
                {
                    GetOrThrow(await env.WriteFileAsync("proj/.pi/skills/y.md", "y", Context));
                });
                await helpers.ExpectChange("proj/.pi/skills/y.md", async () =>
                {
                    GetOrThrow(await env.WriteFileAsync("proj/.pi/skills/y.md", "yy", Context));
                });
            });
        });

        AddWatchCase("watch skips excluded entries and reports a rename out of them", async env =>
        {
            GetOrThrow(await env.CreateDirAsync("skills", null, Context));
            var targets = new WatchTarget[]
            {
                new()
                {
                    Path = "skills",
                    Recursive = true,
                    Exclude = new WatchExclude { Hidden = true, Names = ["node_modules"] },
                },
            };
            await WatchingAsync(env, targets, async helpers =>
            {
                GetOrThrow(await env.WriteFileAsync("skills/node_modules/dep/SKILL.md", "dep", Context));
                GetOrThrow(await env.WriteFileAsync("skills/.SKILL.md.tmp", "draft", Context));
                await helpers.ExpectChange("skills/SKILL.md", async () =>
                {
                    GetOrThrow(await env.RenameFileAsync("skills/.SKILL.md.tmp", "skills/SKILL.md", Context));
                });
                var hidden = new[] { await helpers.Absolute("skills/node_modules"), await helpers.Absolute("skills/.SKILL.md.tmp") };
                foreach (var change in helpers.Changes)
                {
                    if (change.Kind != WatchChange.KindKind.Paths) continue;
                    foreach (var path in change.Paths!)
                    {
                        Assert.Ok(
                            assert,
                            !hidden.Any(excluded => path == excluded || path.StartsWith(excluded, StringComparison.Ordinal)),
                            $"excluded {path}");
                    }
                }
            });
        });

        AddWatchCase("watch stops reporting once closed", async env =>
        {
            var changes = new List<WatchChange>();
            var watcher = GetOrThrow(await env.WatchAsync([new WatchTarget { Path = "file.txt" }], change => changes.Add(change), Context));
            Assert.Ok(assert, watcher.Mode is FileWatchMode.Native or FileWatchMode.Polling, "watcher reports its mode");
            await watcher.CloseAsync(Context);
            await watcher.CloseAsync(Context);
            GetOrThrow(await env.WriteFileAsync("file.txt", "x", Context));
            await Task.Delay(300);
            Assert.DeepEqual(assert, changes, new List<WatchChange>());
        });

        // ─── exec ───

        AddCase("argv exec passes arguments to the program without shell parsing", async env =>
        {
            const string hostile = "it's $(touch pwned) `touch pwned` *; touch pwned";
            var collected = await ExecCollectAsync(env, ShellCommand.FromArgv([.. shell, "printf \"%s|%s\" \"$1\" \"$2\"", "argv0", hostile, "a b"]));
            Assert.StrictEqual(assert, GetOrThrow(collected.Result).ExitCode, 0L);
            Assert.StrictEqual(assert, collected.Stdout, $"{hostile}|a b");
            Assert.StrictEqual(assert, GetOrThrow(await env.ExistsAsync("pwned", Context)), false);
        });

        AddCase("exec reports the stream of every chunk in both forms", async env =>
        {
            const string script = "printf out; printf err >&2; printf more";
            var argv = await ExecCollectAsync(env, ShellCommand.FromArgv([.. shell, script]));
            Assert.StrictEqual(assert, GetOrThrow(argv.Result).ExitCode, 0L);
            Assert.StrictEqual(assert, argv.Stdout, "outmore");
            Assert.StrictEqual(assert, argv.Stderr, "err");
            var str = await ExecCollectAsync(env, script);
            Assert.StrictEqual(assert, GetOrThrow(str.Result).ExitCode, 0L);
            Assert.StrictEqual(assert, str.Stdout, "outmore");
            Assert.StrictEqual(assert, str.Stderr, "err");
        });

        AddCase("argv exec honors cwd and exit codes", async env =>
        {
            GetOrThrow(await env.CreateDirAsync("sub", null, Context));
            var made = await ExecCollectAsync(env, ShellCommand.FromArgv([.. shell, "printf x > made.txt; exit 3"]), "sub");
            Assert.StrictEqual(assert, GetOrThrow(made.Result).ExitCode, 3L);
            Assert.StrictEqual(assert, GetOrThrow(await env.ReadTextFileAsync("sub/made.txt", Context)), "x");
        });

        AddCase("argv exec reports missing programs and empty argv as spawn errors", async env =>
        {
            Assert.StrictEqual(
                assert,
                ErrorCode(await env.ExecAsync(ShellCommand.FromArgv("pi-durable-conformance-missing-program"), null, Context)),
                "spawn_error");
            Assert.StrictEqual(assert, ErrorCode(await env.ExecAsync(ShellCommand.FromArgv(), null, Context)), "spawn_error");
        });

        AddCase("windowed exec keeps the exact tail and counts what it skips", async env =>
        {
            const int lines = 2000;
            var window = new ShellOutputWindow { MaxBytes = 200, MaxLines = 5, MinIntervalMs = 0, BytesPerSecond = 1_000_000_000 };
            long bytes = 0;
            long newlines = 0;
            var tail = "";
            var result = await env.ExecAsync(
                ShellCommand.FromArgv([.. shell, $"i=0; while [ $i -lt {lines} ]; do echo line-$i; i=$((i+1)); done"]),
                new ShellExecOptions
                {
                    Window = window,
                    OnOutput = (text, _, info) =>
                    {
                        if (info.Skipped is not null)
                        {
                            bytes += info.Skipped.Bytes;
                            newlines += info.Skipped.Newlines;
                            var after = Encoding.UTF8.GetByteCount(text);
                            var afterNewlines = text.Split("\n").Length - 1;
                            Assert.Ok(
                                assert,
                                after > window.MaxBytes || afterNewlines > window.MaxLines,
                                "a skip is followed by more than the window");
                            tail = "";
                        }

                        bytes += Encoding.UTF8.GetByteCount(text);
                        newlines += text.Split("\n").Length - 1;
                        tail += text;
                    },
                },
                Context);
            Assert.StrictEqual(assert, GetOrThrow(result).ExitCode, 0L);
            var expected = Enumerable.Range(0, lines).Select(index => $"line-{index}\n").ToList();
            Assert.StrictEqual(assert, bytes, (long)string.Join("", expected).Length);
            Assert.StrictEqual(assert, newlines, (long)lines);
            Assert.Ok(assert, tail.EndsWith(string.Join("", expected.Skip(expected.Count - (int)window.MaxLines)), StringComparison.Ordinal), "the delivered output ends with the tail");
        });

        AddCase("argv exec distinguishes timeout from abort", async env =>
        {
            var timedOut = await env.ExecAsync(
                ShellCommand.FromArgv([.. shell, "sleep 2"]),
                new ShellExecOptions { Timeout = 0.1 },
                Context);
            Assert.StrictEqual(assert, ErrorCode(timedOut), "timeout");

            using var source = new CancellationTokenSource();
            var running = env.ExecAsync(
                ShellCommand.FromArgv([.. shell, "sleep 2"]),
                null,
                Context.WithValue(AbortSignalKeyNull(), source.Token));
            _ = Task.Run(async () =>
            {
                await Task.Delay(100);
                source.Cancel();
            });
            Assert.StrictEqual(assert, ErrorCode(await running), "aborted");
        });

        if (symlinks)
        {
            AddCase("binary reader follows symlinks unless noFollow refuses the final one", async env =>
            {
                GetOrThrow(await env.WriteFileAsync("target.txt", "target", Context));
                GetOrThrow(await env.CreateDirAsync("sub", null, Context));
                GetOrThrow(await env.WriteFileAsync("sub/inner.txt", "inner", Context));
                var linked = await env.ExecAsync(
                    ShellCommand.FromArgv([.. shell, "ln -s target.txt link.txt && ln -s sub dirlink"]),
                    null,
                    Context);
                Assert.StrictEqual(assert, GetOrThrow(linked).ExitCode, 0L);

                var followed = GetOrThrow(await env.OpenBinaryReaderAsync("link.txt", null, Context));
                Assert.StrictEqual(assert, Decode(GetOrThrow(await followed.ReadAsync(0, 10, Context))), "target");
                await followed.CloseAsync(Context);

                Assert.StrictEqual(
                    assert,
                    ErrorCode(await env.OpenBinaryReaderAsync("link.txt", new OpenBinaryOptions { NoFollow = true }, Context)),
                    "invalid");

                // 只拒绝末级组件；更早的符号链接目录仍被解析。
                var inner = GetOrThrow(await env.OpenBinaryReaderAsync("dirlink/inner.txt", new OpenBinaryOptions { NoFollow = true }, Context));
                Assert.StrictEqual(assert, Decode(GetOrThrow(await inner.ReadAsync(0, 10, Context))), "inner");
                await inner.CloseAsync(Context);
            });
        }

        return cases;
    }

    // ─── 辅助 ──────────────────────────────────────────────────────────────

    private static Context AbortedContext()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        return Pi.Chord.Context.Context.Background.WithValue(Pi.Chord.Context.Context.AbortSignalKey, source.Token);
    }

    private static string? ErrorCode<T>(Result<T, FileError> result) => result.IsOk ? null : result.Error.CodeText;

    private static string? ErrorCode(Result<ShellExecResult, ExecutionError> result) => result.IsOk ? null : result.Error.CodeText;

    private static T GetOrThrow<T, TError>(Result<T, TError> result) where TError : Exception =>
        result.IsOk ? result.Value : throw result.Error;

    private static string Decode(byte[] bytes) => Encoding.UTF8.GetString(bytes);

    private static string DecodeWithBom(byte[] bytes, bool ignoreBom)
    {
        var text = Encoding.UTF8.GetString(bytes);
        return !ignoreBom && text.StartsWith('\uFEFF') ? text[1..] : text;
    }

    private static List<string> Slice(string[] lines, long startLine, long? endLine) =>
        [.. lines.Skip((int)startLine).Take((int)(Math.Min(endLine ?? lines.Length, lines.Length) - startLine))];

    private static async Task<(List<List<FileInfo>> Pages, bool Done)> ReadAllAsync(IExecutionEnv env, string path, int maxEntries)
    {
        var reader = GetOrThrow(await env.OpenDirReaderAsync(path, Context));
        var pages = new List<List<FileInfo>>();
        try
        {
            for (var page = 0; page < 1000; page++)
            {
                var next = GetOrThrow(await reader.NextAsync(maxEntries, Context));
                pages.Add([.. next.Entries]);
                if (next.Done) return (pages, true);
            }

            return (pages, false);
        }
        finally
        {
            await reader.CloseAsync(Context);
        }
    }

    /// <summary>变化是否上报了 <paramref name="path"/>：overflow，或上报了位于其上或其本身的路径。</summary>
    private static bool Covers(WatchChange change, string path)
    {
        if (change.Kind == WatchChange.KindKind.Overflow) return true;
        if (change.Kind != WatchChange.KindKind.Paths) return false;
        return change.Paths!.Any(reported =>
            path == reported
            || path.StartsWith($"{reported}/", StringComparison.Ordinal)
            || path.StartsWith($"{reported}\\", StringComparison.Ordinal));
    }

    /// <summary>watch 辅助：在 <paramref name="run"/> 改变文件期间观察 <paramref name="targets"/>。</summary>
    private static async Task WatchingAsync(
        IExecutionEnv env,
        IReadOnlyList<WatchTarget> targets,
        Func<WatchHelpers, Task> run)
    {
        var changes = new List<WatchChange>();
        var watcher = GetOrThrow(await env.WatchAsync(targets, change => changes.Add(change), Context));
        try
        {
            async Task<string> Absolute(string path) => GetOrThrow(await env.AbsolutePathAsync(path, Context));

            var helpers = new WatchHelpers(changes, Absolute, async (path, change) =>
            {
                var target = await Absolute(path);
                var from = changes.Count;
                await change();
                var deadline = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 3000;
                while (!changes.Skip(from).Any(entry => Covers(entry, target)))
                {
                    if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() > deadline)
                    {
                        throw new InvalidOperationException($"No change reported {target}; got {string.Join(",", changes.Skip(from))}");
                    }

                    await Task.Delay(20);
                }
            });
            await run(helpers);
        }
        finally
        {
            await watcher.CloseAsync(Context);
        }
    }

    private sealed class WatchHelpers(
        List<WatchChange> changes,
        Func<string, Task<string>> absolute,
        Func<string, Func<Task>, Task> expectChange)
    {
        public IReadOnlyList<WatchChange> Changes => changes;

        public Task ExpectChange(string path, Func<Task> change) => expectChange(path, change);

        public Task<string> Absolute(string path) => absolute(path);
    }

    private static async Task<(Result<ShellExecResult, ExecutionError> Result, string Stdout, string Stderr)> ExecCollectAsync(
        IExecutionEnv env,
        ShellCommand command,
        string? cwd = null)
    {
        var output = new Dictionary<ShellOutputStream, string> { [ShellOutputStream.Stdout] = "", [ShellOutputStream.Stderr] = "" };
        var result = await env.ExecAsync(
            command,
            new ShellExecOptions
            {
                Cwd = cwd,
                OnOutput = (text, _, info) => output[info.Stream] += text,
            },
            Context);
        return (result, output[ShellOutputStream.Stdout], output[ShellOutputStream.Stderr]);
    }

    private static ContextKey<CancellationToken?> AbortSignalKeyNull() => Pi.Chord.Context.Context.AbortSignalKey;
}
