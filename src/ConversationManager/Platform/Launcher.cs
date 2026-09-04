using System.Diagnostics;
using System.IO;
using System.Windows;
using ConversationManager.Services;

namespace ConversationManager.Platform;

/// <summary>
/// Everything that leaves the app: Explorer, a terminal, a resumed session, the clipboard.
/// Each returns the message to show rather than throwing, because none of these is worth a
/// dialog and all of them are worth reporting.
/// </summary>
public static class Launcher
{
    /// <summary>Result of an action: what to tell the user, and whether it went wrong.</summary>
    public readonly record struct Outcome(string Message, bool IsError)
    {
        public static Outcome Ok(string message) => new(message, false);
        public static Outcome Fail(string message) => new(message, true);
    }

    public static Outcome OpenFolder(string path)
    {
        if (!Directory.Exists(path)) return Outcome.Fail($"Folder is gone: {path}");
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
            return Outcome.Ok("Opened in Explorer");
        }
        catch (Exception ex)
        {
            return Outcome.Fail(ex.Message);
        }
    }

    public static Outcome OpenTerminal(string path)
    {
        if (!Directory.Exists(path)) return Outcome.Fail($"Folder is gone: {path}");
        return StartTerminal(path, null);
    }

    /// <summary>
    /// Hands the session back to Claude Code in its own folder. The id is what makes this work -
    /// `claude --resume &lt;id&gt;` reopens that exact conversation rather than the last one.
    /// </summary>
    public static Outcome Resume(string sessionId, string cwd)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return Outcome.Fail("No session id");
        if (!Directory.Exists(cwd))
            return Outcome.Fail($"Cannot resume: {cwd} no longer exists");

        // Checked up front so a missing CLI reads as a sentence, not a window that flashes shut.
        if (CommandLocator.Find("claude") is null)
            return Outcome.Fail("claude is not on PATH - cannot resume from here");

        var outcome = StartTerminal(cwd, $"claude --resume {sessionId}");
        return outcome.IsError ? outcome : Outcome.Ok("Resuming in a new terminal");
    }

    private static Outcome StartTerminal(string path, string? command)
    {
        try
        {
            // Windows Terminal when present, otherwise plain cmd.
            //
            // A command runs under `cmd /k` rather than directly: if claude refuses the session
            // id - the transcript was pruned, or the folder moved - the shell stays open with the
            // reason on screen instead of the window blinking shut.
            var args = command is null
                ? $"-d \"{path}\""
                : $"-d \"{path}\" cmd /k {command}";
            Process.Start(new ProcessStartInfo("wt.exe", args) { UseShellExecute = true });
            return Outcome.Ok(command is null ? "Opened a terminal" : "Started");
        }
        catch (Exception)
        {
            try
            {
                Process.Start(new ProcessStartInfo("cmd.exe", command is null ? "" : $"/k {command}")
                {
                    WorkingDirectory = path,
                    UseShellExecute = true,
                });
                return Outcome.Ok(command is null ? "Opened a terminal" : "Started");
            }
            catch (Exception ex)
            {
                return Outcome.Fail(ex.Message);
            }
        }
    }

    public static Outcome Copy(string text, string what)
    {
        try
        {
            Clipboard.SetText(text);
            return Outcome.Ok($"{what} copied");
        }
        catch (Exception ex)
        {
            // The clipboard can be held by another process; that is not a crash.
            return Outcome.Fail(ex.Message);
        }
    }
}
