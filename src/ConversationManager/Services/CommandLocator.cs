using System.IO;

namespace ConversationManager.Services;

/// <summary>
/// Finds an executable on PATH before trying to launch it, so a missing tool becomes a sentence
/// the user can act on instead of a terminal window that opens and vanishes.
/// </summary>
public static class CommandLocator
{
    /// <summary>The full path to a command, or null when PATH does not have it.</summary>
    public static string? Find(string command, string? pathVariable = null, string? pathExt = null)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;

        pathVariable ??= Environment.GetEnvironmentVariable("PATH") ?? "";
        pathExt ??= Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD";

        var extensions = Path.HasExtension(command)
            ? new[] { "" }
            : pathExt.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var dir in pathVariable.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var ext in extensions)
            {
                string candidate;
                try
                {
                    candidate = Path.Combine(dir.Trim('"'), command + ext);
                }
                catch (ArgumentException)
                {
                    // A malformed PATH entry is common enough that it must not throw here.
                    continue;
                }

                if (File.Exists(candidate)) return candidate;
            }
        }

        return null;
    }
}
