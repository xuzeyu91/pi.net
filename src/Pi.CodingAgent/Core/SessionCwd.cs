// ============================================================================
// Missing session working directory — port of core/session-cwd.ts (4e-1)
// ============================================================================
//
// A session stores the working directory it was created in. When that directory is gone (the project
// was moved or deleted) resuming must fail with a message that names both paths, rather than silently
// running in the wrong place.

namespace Pi.CodingAgent.Core;

/// <summary>What a session recorded versus where the process actually runs. TS <c>SessionCwdIssue</c>.</summary>
public sealed record SessionCwdIssue(string SessionCwd, string FallbackCwd)
{
    /// <summary>The session file that carried the stale directory, when the session has one.</summary>
    public string? SessionFile { get; init; }
}

/// <summary>The slice of the session manager this module reads. TS <c>SessionCwdSource</c>.</summary>
public interface ISessionCwdSource
{
    /// <summary>TS <c>getCwd()</c>.</summary>
    string GetCwd();

    /// <summary>TS <c>getSessionFile()</c>.</summary>
    string? GetSessionFile();
}

/// <summary>Raised when a session's stored working directory no longer exists. TS <c>MissingSessionCwdError</c>.</summary>
public sealed class MissingSessionCwdException : Exception
{
    /// <summary>The exception name TS sets explicitly on the error object.</summary>
    public const string ErrorName = "MissingSessionCwdError";

    /// <summary>What the session recorded versus the current directory.</summary>
    public SessionCwdIssue Issue { get; }

    /// <summary>Builds the message the same way <see cref="SessionCwd.FormatMissingSessionCwdError"/> does.</summary>
    public MissingSessionCwdException(SessionCwdIssue issue)
        : base(SessionCwd.FormatMissingSessionCwdError(issue))
    {
        Issue = issue;
    }
}

/// <summary>Port of <c>core/session-cwd.ts</c>.</summary>
public static class SessionCwd
{
    /// <summary>
    /// The stale-directory problem, or <see langword="null"/> when there is nothing to report: no
    /// session file, no recorded directory, or the directory still exists.
    /// </summary>
    public static SessionCwdIssue? GetMissingSessionCwdIssue(ISessionCwdSource sessionManager, string fallbackCwd)
    {
        var sessionFile = sessionManager.GetSessionFile();
        if (string.IsNullOrEmpty(sessionFile))
        {
            return null;
        }

        var sessionCwd = sessionManager.GetCwd();
        // TS `existsSync` is true for files and directories alike.
        if (string.IsNullOrEmpty(sessionCwd) || File.Exists(sessionCwd) || Directory.Exists(sessionCwd))
        {
            return null;
        }

        return new SessionCwdIssue(sessionCwd, fallbackCwd) { SessionFile = sessionFile };
    }

    /// <summary>The full error text, used for the thrown exception and for display.</summary>
    public static string FormatMissingSessionCwdError(SessionCwdIssue issue)
    {
        var sessionFile = !string.IsNullOrEmpty(issue.SessionFile) ? $"\nSession file: {issue.SessionFile}" : "";
        return $"Stored session working directory does not exist: {issue.SessionCwd}{sessionFile}"
               + $"\nCurrent working directory: {issue.FallbackCwd}";
    }

    /// <summary>The one-line prompt shown when offering to continue in the current directory.</summary>
    public static string FormatMissingSessionCwdPrompt(SessionCwdIssue issue) =>
        $"cwd from session file does not exist\n{issue.SessionCwd}\n\ncontinue in current cwd\n{issue.FallbackCwd}";

    /// <summary>Throws <see cref="MissingSessionCwdException"/> when the recorded directory is gone.</summary>
    public static void AssertSessionCwdExists(ISessionCwdSource sessionManager, string fallbackCwd)
    {
        var issue = GetMissingSessionCwdIssue(sessionManager, fallbackCwd);
        if (issue is not null)
        {
            throw new MissingSessionCwdException(issue);
        }
    }
}
