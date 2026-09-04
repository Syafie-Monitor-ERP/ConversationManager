using System.Text;
using System.Text.Json;

namespace ConversationManager.Tests;

/// <summary>
/// Builds transcript and history lines in the exact shape Claude Code writes them, so the parsers
/// are tested against the real record layout rather than a convenient imitation of it.
/// </summary>
internal sealed class TranscriptBuilder
{
    private readonly List<string> _lines = new();
    private readonly string _sessionId;

    public TranscriptBuilder(string sessionId = "11111111-2222-3333-4444-555555555555")
    {
        _sessionId = sessionId;
    }

    public string SessionId => _sessionId;

    public IReadOnlyList<string> Lines => _lines;

    /// <summary>A prompt the user typed, as the current CLI records it.</summary>
    public TranscriptBuilder Human(string text, string when, string cwd, string branch)
    {
        _lines.Add(Json(new Dictionary<string, object?>
        {
            ["type"] = "user",
            ["message"] = new Dictionary<string, object?> { ["role"] = "user", ["content"] = text },
            ["origin"] = new Dictionary<string, object?> { ["kind"] = "human" },
            ["promptSource"] = "typed",
            ["timestamp"] = when,
            ["cwd"] = cwd,
            ["gitBranch"] = branch,
            ["sessionId"] = _sessionId,
        }));
        return this;
    }

    /// <summary>A prompt from an older CLI build, which carried no origin at all.</summary>
    public TranscriptBuilder LegacyHuman(string text, string when, string cwd, string branch)
    {
        _lines.Add(Json(new Dictionary<string, object?>
        {
            ["type"] = "user",
            ["message"] = new Dictionary<string, object?> { ["role"] = "user", ["content"] = text },
            ["timestamp"] = when,
            ["cwd"] = cwd,
            ["gitBranch"] = branch,
            ["sessionId"] = _sessionId,
        }));
        return this;
    }

    /// <summary>A tool result being fed back to the model - a user record, but nobody spoke.</summary>
    public TranscriptBuilder ToolResult(string text, string when, string cwd, string branch,
        string toolUseId = "toolu_1")
    {
        _lines.Add(Json(new Dictionary<string, object?>
        {
            ["type"] = "user",
            ["message"] = new Dictionary<string, object?>
            {
                ["role"] = "user",
                ["content"] = new object[]
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = "tool_result",
                        ["tool_use_id"] = toolUseId,
                        ["content"] = text,
                    },
                },
            },
            ["timestamp"] = when,
            ["cwd"] = cwd,
            ["gitBranch"] = branch,
            ["sessionId"] = _sessionId,
        }));
        return this;
    }

    public TranscriptBuilder Assistant(string text, string when, string cwd, string branch)
    {
        _lines.Add(Json(new Dictionary<string, object?>
        {
            ["type"] = "assistant",
            ["message"] = new Dictionary<string, object?>
            {
                ["role"] = "assistant",
                ["content"] = new object[]
                {
                    new Dictionary<string, object?> { ["type"] = "text", ["text"] = text },
                },
            },
            ["timestamp"] = when,
            ["cwd"] = cwd,
            ["gitBranch"] = branch,
            ["sessionId"] = _sessionId,
        }));
        return this;
    }

    public TranscriptBuilder ToolCall(string name, string command, string when, string cwd,
        string branch, string toolUseId = "toolu_1")
    {
        _lines.Add(Json(new Dictionary<string, object?>
        {
            ["type"] = "assistant",
            ["message"] = new Dictionary<string, object?>
            {
                ["role"] = "assistant",
                ["content"] = new object[]
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = "tool_use",
                        ["id"] = toolUseId,
                        ["name"] = name,
                        ["input"] = new Dictionary<string, object?> { ["command"] = command },
                    },
                },
            },
            ["timestamp"] = when,
            ["cwd"] = cwd,
            ["gitBranch"] = branch,
            ["sessionId"] = _sessionId,
        }));
        return this;
    }

    public TranscriptBuilder Title(string title)
    {
        _lines.Add(Json(new Dictionary<string, object?>
        {
            ["type"] = "ai-title",
            ["aiTitle"] = title,
            ["sessionId"] = _sessionId,
        }));
        return this;
    }

    public TranscriptBuilder Meta(string text, string when, string cwd, string branch)
    {
        _lines.Add(Json(new Dictionary<string, object?>
        {
            ["type"] = "user",
            ["isMeta"] = true,
            ["message"] = new Dictionary<string, object?> { ["role"] = "user", ["content"] = text },
            ["timestamp"] = when,
            ["cwd"] = cwd,
            ["gitBranch"] = branch,
            ["sessionId"] = _sessionId,
        }));
        return this;
    }

    public TranscriptBuilder Raw(string line)
    {
        _lines.Add(line);
        return this;
    }

    /// <summary>Writes the transcript where Claude Code would put it, under a temp .claude tree.</summary>
    public string WriteTo(string projectsDir, string projectKey = "C--src-Demo")
    {
        var dir = Path.Combine(projectsDir, projectKey);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, _sessionId + ".jsonl");
        File.WriteAllLines(path, _lines, new UTF8Encoding(false));
        return path;
    }

    private static string Json(Dictionary<string, object?> record) => JsonSerializer.Serialize(record);
}

internal static class HistoryBuilder
{
    /// <summary>One history.jsonl line: what was typed, in which folder, at which epoch ms.</summary>
    public static string Line(string display, string project, string sessionId, long epochMs) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["display"] = display,
            ["pastedContents"] = new Dictionary<string, object?>(),
            ["timestamp"] = epochMs,
            ["project"] = project,
            ["sessionId"] = sessionId,
        });
}

/// <summary>A throwaway directory that cleans itself up.</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir(string name)
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "convmgr-tests", name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception)
        {
            // A leftover temp folder is not worth failing a test run over.
        }
    }
}
