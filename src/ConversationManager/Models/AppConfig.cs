using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ConversationManager.Models;

/// <summary>How results are laid out when nothing is being searched for.</summary>
public enum GroupMode
{
    Branch,
    Folder,
    Recent,
}

/// <summary>Which layers of a transcript the search box looks at.</summary>
public enum SearchScope
{
    /// <summary>Titles, branches, folders and what the user typed. The default.</summary>
    Prompts,

    /// <summary>Everything, including Claude's replies and command output.</summary>
    Everything,
}

/// <summary>
/// User settings, persisted next to the exe so the whole tool stays portable
/// (copy the folder to another machine and it keeps working).
/// </summary>
public sealed class AppConfig
{
    /// <summary>The .claude directory holding projects\ and history.jsonl.</summary>
    public string ClaudeHome { get; set; } = "";

    public GroupMode GroupMode { get; set; } = GroupMode.Branch;

    public SearchScope Scope { get; set; } = SearchScope.Prompts;

    /// <summary>Conversations older than this are hidden; 0 means keep everything.</summary>
    public int MaxAgeDays { get; set; }

    [JsonIgnore]
    public string? LoadedFrom { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "config.json");

    public static AppConfig Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), JsonOptions);
                if (cfg is not null)
                {
                    cfg.LoadedFrom = path;
                    if (string.IsNullOrWhiteSpace(cfg.ClaudeHome)) cfg.ClaudeHome = DefaultClaudeHome();
                    return cfg;
                }
            }
        }
        catch (Exception ex)
        {
            // A corrupt config must never stop the app from starting.
            System.Diagnostics.Debug.WriteLine($"config load failed: {ex.Message}");
        }

        var fresh = new AppConfig { ClaudeHome = DefaultClaudeHome(), LoadedFrom = path };
        try
        {
            // Write the defaults out on first run so the file is there to inspect and edit.
            fresh.Save(path);
        }
        catch (Exception)
        {
            // Read-only location: run from memory instead of refusing to start.
        }
        return fresh;
    }

    public void Save(string? path = null)
    {
        path ??= LoadedFrom ?? DefaultPath;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
        LoadedFrom = path;
    }

    /// <summary>
    /// CLAUDE_CONFIG_DIR when the user has moved it, else %USERPROFILE%\.claude - the same
    /// resolution order Claude Code itself uses.
    /// </summary>
    public static string DefaultClaudeHome()
    {
        var env = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(env)) return env;
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
    }

    // Derived from ClaudeHome, and ignored so they stay out of config.json. Serialising them
    // would put two paths in the file that look editable and are not: they have no setters, so a
    // hand-edited value is silently dropped on load.
    [JsonIgnore]
    public string ProjectsDir => Path.Combine(ClaudeHome, "projects");

    [JsonIgnore]
    public string HistoryFile => Path.Combine(ClaudeHome, "history.jsonl");
}
