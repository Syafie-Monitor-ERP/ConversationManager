using ConversationManager.Models;
using ConversationManager.Services;
using ConversationManager.Tests;

var pass = 0;
var fail = 0;

void Check(string name, bool ok, string? detail = null)
{
    if (ok)
    {
        pass++;
        Console.WriteLine($"  PASS  {name}");
    }
    else
    {
        fail++;
        Console.WriteLine($"  FAIL  {name}{(detail is null ? "" : "  -> " + detail)}");
    }
}

void Section(string title) => Console.WriteLine($"\n{title}");

const string Dev2 = @"C:\src\Dev2";
const string Dev2Sub = @"C:\src\Dev2\src\Monitor.Net";
const string Branch = "166597-Linux-db-migrator";

// ---------------------------------------------------------------- transcript parsing

Section("Reading a transcript");
{
    var builder = new TranscriptBuilder("aaaaaaaa-0000-0000-0000-000000000001")
        .Human("the db migrator crashes on linux", "2026-08-05T02:38:00.000Z", Dev2, Branch)
        .Assistant("Looking at the loader now.", "2026-08-05T02:38:20.000Z", Dev2, Branch)
        .ToolCall("Bash", "dotnet build MonitorDatabaseMigrator.sln", "2026-08-05T02:39:00.000Z", Dev2, Branch)
        .ToolResult("error CS0103: AssemblyDiscoveryService", "2026-08-05T02:39:30.000Z", Dev2, Branch)
        .Meta("<system-reminder>ignore me</system-reminder>", "2026-08-05T02:40:00.000Z", Dev2, Branch)
        .LegacyHuman("<command-name>/clear</command-name>", "2026-08-05T02:41:00.000Z", Dev2, Branch)
        .LegacyHuman("check the timezone ids too", "2026-08-05T02:42:00.000Z", Dev2Sub, Branch)
        .Title("First title")
        .Title("Fix AssemblyDiscoveryService Linux DLL loading")
        .Human("and rerun the tests", "2026-08-05T13:11:00.000Z", Dev2, "linux-fix-timezone-ids");

    var c = TranscriptParser.Parse(builder.Lines, builder.SessionId, @"C:\fake\x.jsonl", 4242)!;

    Check("session id read from the records", c.SessionId == builder.SessionId, c.SessionId);
    Check("last ai-title wins", c.Title == "Fix AssemblyDiscoveryService Linux DLL loading", c.Title);
    Check("transcript path kept", c.TranscriptPath == @"C:\fake\x.jsonl");
    Check("bytes kept", c.Bytes == 4242);

    Check("only human prompts are indexed", c.Prompts.Count == 3,
        string.Join(" | ", c.Prompts.Select(p => p.Text)));
    Check("tool results are not prompts", c.Prompts.All(p => !p.Text.Contains("CS0103")));
    Check("meta records are not prompts", c.Prompts.All(p => !p.Text.Contains("ignore me")));
    Check("slash commands are not prompts", c.Prompts.All(p => !p.Text.Contains("command-name")));
    Check("a legacy prompt with no origin is still indexed",
        c.Prompts.Any(p => p.Text == "check the timezone ids too"));

    Check("messages counted", c.MessageCount == 8, c.MessageCount.ToString());
    Check("primary branch is the most used", c.PrimaryBranch == Branch, c.PrimaryBranch);
    Check("both branches recorded", c.Branches.Count == 2, string.Join(",", c.Branches));
    Check("primary cwd is the root, not a subfolder", c.Cwd == Dev2, c.Cwd);
    Check("subfolder still recorded", c.Cwds.Contains(Dev2Sub));
    Check("start is the first timestamp",
        c.Start == DateTimeOffset.Parse("2026-08-05T02:38:00.000Z").ToLocalTime(), c.Start.ToString());
    Check("end is the last timestamp",
        c.End == DateTimeOffset.Parse("2026-08-05T13:11:00.000Z").ToLocalTime(), c.End.ToString());
    Check("folder name derived", c.FolderName == "Dev2", c.FolderName);
}

Section("Transcripts that are not perfectly formed");
{
    var builder = new TranscriptBuilder("aaaaaaaa-0000-0000-0000-000000000002")
        .Human("first", "2026-08-05T02:38:00.000Z", Dev2, "HEAD")
        .Raw("{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"half a line")
        .Raw("")
        .Human("second", "2026-08-05T02:39:00.000Z", Dev2, "HEAD");

    var c = TranscriptParser.Parse(builder.Lines, builder.SessionId, null, 0)!;
    Check("a truncated last line does not lose the conversation", c.Prompts.Count == 2,
        c.Prompts.Count.ToString());
    Check("HEAD is not treated as a branch", c.Branches.Count == 0,
        string.Join(",", c.Branches));
    Check("no branch means no primary branch", c.PrimaryBranch is null);

    Check("an empty transcript parses to nothing",
        TranscriptParser.Parse(Array.Empty<string>(), "x", null, 0) is null);
}

// ---------------------------------------------------------------- prompt history

Section("Reading history.jsonl");
{
    var lines = new[]
    {
        HistoryBuilder.Line("/clear", Dev2, "s1", 1_772_150_456_383),
        HistoryBuilder.Line("!", Dev2, "s1", 1_772_150_456_400),
        HistoryBuilder.Line("#note to self", Dev2, "s1", 1_772_150_456_500),
        HistoryBuilder.Line("make the migrator cross platform", Dev2, "s1", 1_772_150_718_862),
        HistoryBuilder.Line("", Dev2, "s1", 1_772_150_718_900),
    };

    var entries = HistoryReader.Parse(lines);
    Check("commands and blanks are skipped", entries.Count == 1,
        string.Join(" | ", entries.Select(e => e.Text)));
    Check("text kept", entries[0].Text == "make the migrator cross platform");
    Check("project kept", entries[0].Project == Dev2);
    Check("epoch millis parsed",
        entries[0].When == DateTimeOffset.FromUnixTimeMilliseconds(1_772_150_718_862).ToLocalTime(),
        entries[0].When.ToString());
}

Section("Merging history into transcripts");
{
    var transcript = new Conversation
    {
        SessionId = "s1",
        TranscriptPath = @"C:\fake\s1.jsonl",
        Title = "Migrator work",
        Cwd = Dev2,
        Branches = new[] { Branch },
        Start = DateTimeOffset.Parse("2026-08-05T02:00:00Z"),
        End = DateTimeOffset.Parse("2026-08-05T03:00:00Z"),
        MessageCount = 10,
        Prompts = new[] { new Prompt(DateTimeOffset.Parse("2026-08-05T02:10:00Z"), "already known") },
    };

    var history = new List<HistoryEntry>
    {
        new("s1", Dev2, DateTimeOffset.Parse("2026-08-05T02:10:00Z"), "already known"),
        new("s1", Dev2, DateTimeOffset.Parse("2026-08-05T02:20:00Z"), "compacted away"),
        new("s2", @"C:\src\Dev1", DateTimeOffset.Parse("2026-07-01T09:00:00Z"), "old pruned session"),
        new("s2", @"C:\src\Dev1", DateTimeOffset.Parse("2026-07-01T09:30:00Z"), "second prompt"),
    };

    var merged = TranscriptStore.MergeHistory(new[] { transcript }, history);
    Check("both sessions present", merged.Count == 2, merged.Count.ToString());

    var s1 = merged.Single(c => c.SessionId == "s1");
    Check("a prompt missing from the transcript is recovered", s1.Prompts.Count == 2,
        string.Join(" | ", s1.Prompts.Select(p => p.Text)));
    Check("a prompt already in the transcript is not duplicated",
        s1.Prompts.Count(p => p.Text == "already known") == 1);
    Check("transcript facts survive the merge",
        s1.Title == "Migrator work" && s1.PrimaryBranch == Branch && s1.HasTranscript);
    Check("recovered prompts stay in time order",
        s1.Prompts[0].When <= s1.Prompts[1].When);

    var s2 = merged.Single(c => c.SessionId == "s2");
    Check("a pruned session becomes a history-only conversation",
        s2.Source == ConversationSource.HistoryOnly && !s2.HasTranscript);
    Check("its folder comes from history", s2.Cwd == @"C:\src\Dev1", s2.Cwd);
    Check("its span comes from its prompts",
        s2.Start == DateTimeOffset.Parse("2026-07-01T09:00:00Z") &&
        s2.End == DateTimeOffset.Parse("2026-07-01T09:30:00Z"));
    Check("a history-only conversation cannot be resumed", !s2.CanResume);
    Check("it still has a title to show", s2.DisplayTitle == "old pruned session", s2.DisplayTitle);
}

Section("Naming a conversation that has no generated title");
{
    Conversation FromPrompts(params string[] prompts) => new()
    {
        SessionId = "no-title-session",
        Cwd = Dev2,
        Prompts = prompts.Select(p => new Prompt(DateTimeOffset.Parse("2026-08-05T02:00:00Z"), p))
            .ToArray(),
    };

    Check("a generated title always wins",
        new Conversation { SessionId = "x", Title = "Real title", Prompts = new[]
            { new Prompt(default, "something typed") } }.DisplayTitle == "Real title");

    Check("otherwise the opening prompt names it",
        FromPrompts("make the migrator cross platform").DisplayTitle ==
        "make the migrator cross platform");

    // Sessions often open with a pasted link; naming the card after it says nothing.
    Check("a pasted link is skipped in favour of a sentence",
        FromPrompts("https://g5tfs.monitor.se:8081/tfs/Monitor/_wiki/3408/Linux-server",
                    "set up the linux server per this wiki page").DisplayTitle ==
        "set up the linux server per this wiki page",
        FromPrompts("https://g5tfs.monitor.se:8081/tfs/x", "set up the linux server").DisplayTitle);

    Check("a pasted path is skipped too",
        FromPrompts(@"C:\src\Dev2\src\Monitor.Net", "why does this project not build").DisplayTitle ==
        "why does this project not build");

    Check("a link is still better than nothing",
        FromPrompts("https://example.com/page").DisplayTitle == "https://example.com/page");

    Check("with no prompts at all it falls back to the id",
        new Conversation { SessionId = "abcdefgh-1234" }.DisplayTitle == "abcdefgh");

    Check("long prompts are cut to fit a card",
        FromPrompts(new string('a', 200)).DisplayTitle.Length <= 90,
        FromPrompts(new string('a', 200)).DisplayTitle.Length.ToString());
}

// ---------------------------------------------------------------- searching

Conversation Make(
    string id, string? title, string branch, string cwd, string end, params string[] prompts) => new()
{
    SessionId = id,
    TranscriptPath = @"C:\fake\" + id + ".jsonl",
    Title = title,
    Cwd = cwd,
    Cwds = new[] { cwd },
    Branches = branch.Length == 0 ? Array.Empty<string>() : new[] { branch },
    Start = DateTimeOffset.Parse(end).AddHours(-1),
    End = DateTimeOffset.Parse(end),
    MessageCount = 20,
    Prompts = prompts.Select(p => new Prompt(DateTimeOffset.Parse(end), p)).ToArray(),
};

Section("Which layer a hit came from decides the order");
{
    var titled = Make("c1", "Duplex printing fix", "166579-Cross-Platform-Web", Dev2,
        "2026-08-01T10:00:00Z", "nothing relevant here");
    var prompted = Make("c2", "Something else", "166597-Linux-db-migrator", Dev2,
        "2026-08-02T10:00:00Z", "the duplex setting is ignored");
    var foldered = Make("c3", "Third", "master", @"C:\src\duplex-experiment",
        "2026-08-03T10:00:00Z", "unrelated");

    var results = ConversationSearch.Search(new[] { prompted, foldered, titled }, "duplex");

    Check("all three match", results.Count == 3, results.Count.ToString());
    Check("the title hit is first", results[0].Conversation.SessionId == "c1",
        results[0].Conversation.SessionId);
    Check("the folder hit outranks nothing else wrongly",
        results[1].Conversation.SessionId == "c3" && results[2].Conversation.SessionId == "c2",
        string.Join(",", results.Select(r => r.Conversation.SessionId)));
    Check("the layer is reported", results[0].Best.Layer == SearchLayer.Title,
        results[0].Best.Layer.ToString());
    Check("a prompt hit says what was typed",
        results[2].Best.Snippet.Contains("duplex setting"), results[2].Best.Snippet);
    Check("shallow hits are never marked deep-only", results.All(r => !r.IsDeepOnly));
}

Section("How well a term fits matters too");
{
    var exact = Make("e", null, "duplex", Dev2, "2026-08-01T10:00:00Z");
    var prefix = Make("p", null, "duplex-setting-fix", Dev2, "2026-08-01T10:00:00Z");
    var middle = Make("m", null, "fix-the-duplex-thing", Dev2, "2026-08-01T10:00:00Z");
    var fuzzy = Make("f", null, "d-u-p-l-e-x", Dev2, "2026-08-01T10:00:00Z");

    var results = ConversationSearch.Search(new[] { fuzzy, middle, prefix, exact }, "duplex");
    Check("exact, then prefix, then word start, then fuzzy",
        string.Join(",", results.Select(r => r.Conversation.SessionId)) == "e,p,m,f",
        string.Join(",", results.Select(r => r.Conversation.SessionId)));
}

Section("Fuzzy matching is allowed on names, never on prose");
{
    var branch = Make("b", null, "168987-Duplex-setting-is-not", Dev2, "2026-08-01T10:00:00Z");
    var results = ConversationSearch.Search(new[] { branch }, "168dup");
    Check("a work item number plus a few letters finds the branch", results.Count == 1);
    Check("and it is reported as fuzzy",
        results.Count == 1 && results[0].Best.Kind == MatchKind.Fuzzy,
        results.Count == 1 ? results[0].Best.Kind.ToString() : "no match");

    var prose = Make("x", null, "master", Dev2, "2026-08-01T10:00:00Z",
        "please deal with the printer and then update everything else");
    Check("the same trick does not match a sentence",
        ConversationSearch.Search(new[] { prose }, "pdate").Count == 1 &&
        ConversationSearch.Search(new[] { prose }, "pxyz").Count == 0);
}

Section("Every term has to match");
{
    var both = Make("both", "Duplex printing", "master", Dev2, "2026-08-01T10:00:00Z",
        "the tray is wrong");
    var one = Make("one", "Duplex printing", "master", Dev2, "2026-08-01T10:00:00Z",
        "nothing relevant at all");

    var results = ConversationSearch.Search(new[] { both, one }, "duplex tray");
    Check("only the conversation with both terms matches", results.Count == 1,
        string.Join(",", results.Select(r => r.Conversation.SessionId)));
    Check("and it is the right one", results[0].Conversation.SessionId == "both");
    Check("corroboration across fields is recorded", results[0].Hits.Count >= 2,
        results[0].Hits.Count.ToString());
}

Section("Finding a conversation by its id");
{
    var c = Make("21e6a0f0-50a1-425c-81df-0a9879c3f8d4", "Branch search result clarity", "master",
        Dev2, "2026-08-01T10:00:00Z", "unrelated");

    Check("a pasted id prefix finds it",
        ConversationSearch.Search(new[] { c }, "21e6a0f0").Count == 1);
    Check("a couple of hex characters do not",
        ConversationSearch.Search(new[] { c }, "21e").Count == 0);
}

Section("Equal scores fall back to recency");
{
    var older = Make("old", "Duplex printing", "master", Dev2, "2026-08-01T10:00:00Z");
    var newer = Make("new", "Duplex printing", "master", Dev2, "2026-08-09T10:00:00Z");

    var results = ConversationSearch.Search(new[] { older, newer }, "duplex printing");
    Check("the newer conversation comes first", results[0].Conversation.SessionId == "new",
        results[0].Conversation.SessionId);
}

// ---------------------------------------------------------------- deep search

Section("Searching the layers the index leaves on disk");
{
    using var temp = new TempDir("deep");
    var projects = Path.Combine(temp.Path, "projects");

    var builder = new TranscriptBuilder("dddddddd-0000-0000-0000-000000000001")
        .Human("fix the build", "2026-08-05T02:38:00.000Z", Dev2, Branch)
        .Assistant("The nightly pipeline uses a stale toolchain.", "2026-08-05T02:39:00.000Z", Dev2, Branch)
        .ToolCall("Bash", "dotnet build", "2026-08-05T02:40:00.000Z", Dev2, Branch)
        .ToolResult("MSB3277 conflict in Newtonsoft.Json", "2026-08-05T02:41:00.000Z", Dev2, Branch);
    var path = builder.WriteTo(projects);

    var proseHits = DeepSearcher.ScanFile(path, new[] { "toolchain" });
    Check("a word Claude said is found", proseHits.Count == 1, proseHits.Count.ToString());
    Check("and attributed to the reply layer",
        proseHits.Count == 1 && proseHits[0].Layer == SearchLayer.Assistant,
        proseHits.Count == 1 ? proseHits[0].Layer.ToString() : "none");
    Check("with a readable snippet",
        proseHits.Count == 1 && proseHits[0].Snippet.Contains("stale toolchain"),
        proseHits.Count == 1 ? proseHits[0].Snippet : "none");

    var toolHits = DeepSearcher.ScanFile(path, new[] { "MSB3277" });
    Check("a word only in command output is found", toolHits.Count == 1);
    Check("and attributed to the tool layer",
        toolHits.Count == 1 && toolHits[0].Layer == SearchLayer.Tool,
        toolHits.Count == 1 ? toolHits[0].Layer.ToString() : "none");

    Check("a term that is nowhere finds nothing",
        DeepSearcher.ScanFile(path, new[] { "kubernetes" }).Count == 0);
    Check("one missing term is enough to reject the file",
        DeepSearcher.ScanFile(path, new[] { "toolchain", "kubernetes" }).Count == 0);
    Check("both terms present is a match",
        DeepSearcher.ScanFile(path, new[] { "toolchain", "MSB3277" }).Count > 0);

    // The ranking rule the whole design rests on: prose beats command output.
    Check("a reply hit outscores a command-output hit",
        Scoring.LayerWeight(SearchLayer.Assistant) > Scoring.LayerWeight(SearchLayer.Tool));
    Check("and both are far below a prompt hit",
        Scoring.LayerWeight(SearchLayer.Prompt) > Scoring.LayerWeight(SearchLayer.Assistant));
}

Section("Deep-only matches are marked as the weak evidence they are");
{
    var conversation = Make("weak", "Something", "master", Dev2, "2026-08-01T10:00:00Z", "nothing");
    var match = new ConversationMatch
    {
        Conversation = conversation,
        Hits = new[]
        {
            new FieldHit { Layer = SearchLayer.Tool, Kind = MatchKind.Substring, Term = "x" },
        },
        Score = 90,
    };
    Check("a tool-only match is deep-only", match.IsDeepOnly);

    var mixed = new ConversationMatch
    {
        Conversation = conversation,
        Hits = new[]
        {
            new FieldHit { Layer = SearchLayer.Prompt, Kind = MatchKind.Substring, Term = "x" },
            new FieldHit { Layer = SearchLayer.Tool, Kind = MatchKind.Substring, Term = "x" },
        },
        Score = 700,
    };
    Check("one prompt hit is enough to make it real evidence", !mixed.IsDeepOnly);
}

// ---------------------------------------------------------------- grouping

Section("Grouping the overview");
{
    var a = Make("a", "One", "166597-Linux-db-migrator", Dev2, "2026-08-05T10:00:00Z");
    var b = Make("b", "Two", "166597-Linux-db-migrator", Dev2, "2026-08-06T10:00:00Z");
    var c = Make("c", "Three", "168969-Cross-Platform-Mobile", @"C:\src\Dev1", "2026-08-07T10:00:00Z");
    var noBranch = Make("d", "Four", "", @"C:\src\BranchManager", "2026-08-08T10:00:00Z");

    var twoBranches = new Conversation
    {
        SessionId = "e",
        TranscriptPath = @"C:\fake\e.jsonl",
        Title = "Switched mid-session",
        Cwd = Dev2,
        Cwds = new[] { Dev2 },
        Branches = new[] { "166597-Linux-db-migrator", "linux-fix-timezone-ids" },
        Start = DateTimeOffset.Parse("2026-08-04T10:00:00Z"),
        End = DateTimeOffset.Parse("2026-08-04T11:00:00Z"),
    };

    var all = new[] { a, b, c, noBranch, twoBranches };
    var byBranch = ConversationGrouper.Group(all, GroupMode.Branch, DateTimeOffset.Parse("2026-08-09T10:00:00Z"));

    var migrator = byBranch.Single(g => g.Name == "166597-Linux-db-migrator");
    Check("a branch group holds every conversation on it", migrator.Conversations.Count == 3,
        migrator.Conversations.Count.ToString());
    Check("a session that switched branches appears under both",
        byBranch.Count(g => g.Conversations.Any(x => x.SessionId == "e")) == 2);
    Check("newest first inside a group",
        migrator.Conversations[0].SessionId == "b", migrator.Conversations[0].SessionId);
    Check("groups are ordered by their newest conversation",
        byBranch[0].Name == "168969-Cross-Platform-Mobile", byBranch[0].Name);
    Check("the unbranched pile sinks to the bottom",
        byBranch[^1].Name == ConversationGrouper.NoBranchLabel, byBranch[^1].Name);
    Check("a branch worked in one folder names that folder",
        byBranch.Single(g => g.Name == "168969-Cross-Platform-Mobile").Subtitle == @"C:\src\Dev1");

    var byFolder = ConversationGrouper.Group(all, GroupMode.Folder, DateTimeOffset.Parse("2026-08-09T10:00:00Z"));
    Check("folder groups are named by folder", byFolder.Any(g => g.Name == "Dev2"));
    Check("and carry the full path as a subtitle",
        byFolder.Single(g => g.Name == "Dev2").Subtitle == Dev2);

    var byTime = ConversationGrouper.Group(all, GroupMode.Recent, DateTimeOffset.Parse("2026-08-09T10:00:00Z"));
    Check("recency groups are time buckets", byTime.All(g => g.Name.Length > 0));
    // The heading already carries a count pill, so a bucket adds no subtitle to duplicate it.
    Check("and carry no subtitle", byTime.All(g => g.Subtitle.Length == 0));
    Check("buckets run newest to oldest",
        byTime.SequenceEqual(byTime.OrderByDescending(g => g.LastActive)));
}

// ---------------------------------------------------------------- the whole scan

Section("A scan of a whole .claude folder");
{
    using var temp = new TempDir("store");
    var projects = Path.Combine(temp.Path, "projects");

    new TranscriptBuilder("ffffffff-0000-0000-0000-000000000001")
        .Human("first session", "2026-08-05T02:38:00.000Z", Dev2, Branch)
        .Assistant("done", "2026-08-05T02:39:00.000Z", Dev2, Branch)
        .Title("Session one")
        .WriteTo(projects, "C--src-Dev2");

    new TranscriptBuilder("ffffffff-0000-0000-0000-000000000002")
        .Human("second session", "2026-08-06T02:38:00.000Z", @"C:\src\Dev1", "168969-Cross-Platform-Mobile")
        .Title("Session two")
        .WriteTo(projects, "C--src-Dev1");

    File.WriteAllLines(Path.Combine(temp.Path, "history.jsonl"), new[]
    {
        HistoryBuilder.Line("first session", Dev2, "ffffffff-0000-0000-0000-000000000001", 1_754_361_480_000),
        HistoryBuilder.Line("a pruned conversation about printers", @"C:\src\Dev3", "pruned-1", 1_753_000_000_000),
    });

    var config = new AppConfig { ClaudeHome = temp.Path };
    var cachePath = Path.Combine(temp.Path, "cache.json");
    var index = new TranscriptStore(config, IndexCache.Load(cachePath)).Load();

    Check("both transcripts found", index.TranscriptCount == 2, index.TranscriptCount.ToString());
    Check("the pruned session is found too", index.HistoryOnlyCount == 1,
        index.HistoryOnlyCount.ToString());
    Check("three conversations in total", index.Conversations.Count == 3);
    Check("newest first", index.Conversations[0].SessionId.EndsWith("002"),
        index.Conversations[0].SessionId);
    Check("no problem reported for a healthy folder", index.Problem is null, index.Problem);
    Check("transcript bytes measured", index.TranscriptBytes > 0);

    // Searching what the scan produced, end to end.
    var hits = ConversationSearch.Search(index.Conversations, "printers");
    Check("a pruned conversation is still findable by what was typed", hits.Count == 1,
        hits.Count.ToString());
    Check("and is honest about not being resumable",
        hits.Count == 1 && !hits[0].Conversation.CanResume);

    var missing = new TranscriptStore(new AppConfig { ClaudeHome = Path.Combine(temp.Path, "nope") },
        IndexCache.Load(Path.Combine(temp.Path, "cache2.json"))).Load();
    Check("a missing .claude folder is reported, not crashed on",
        missing.Problem is not null && missing.Conversations.Count == 0, missing.Problem);
}

Section("The cache only reuses a file that has not changed");
{
    using var temp = new TempDir("cache");
    var projects = Path.Combine(temp.Path, "projects");
    var path = new TranscriptBuilder("cccccccc-0000-0000-0000-000000000001")
        .Human("cached please", "2026-08-05T02:38:00.000Z", Dev2, Branch)
        .Title("Cached")
        .WriteTo(projects, "C--src-Dev2");

    var config = new AppConfig { ClaudeHome = temp.Path };
    var cachePath = Path.Combine(temp.Path, "cache.json");

    var first = new TranscriptStore(config, IndexCache.Load(cachePath)).Load();
    Check("nothing is cached on the first scan", first.CacheHits == 0, first.CacheHits.ToString());

    var second = new TranscriptStore(config, IndexCache.Load(cachePath)).Load();
    Check("the second scan reuses the parse", second.CacheHits == 1, second.CacheHits.ToString());
    Check("and produces the same conversation",
        second.Conversations.Count == 1 &&
        second.Conversations[0].Title == "Cached" &&
        second.Conversations[0].Prompts.Count == 1 &&
        second.Conversations[0].PrimaryBranch == Branch);

    // Appending to a live session must invalidate: the same size and time is the only safe reuse.
    File.AppendAllLines(path, new[]
    {
        "{\"type\":\"ai-title\",\"aiTitle\":\"Cached and then changed\",\"sessionId\":\"cccccccc-0000-0000-0000-000000000001\"}",
    });

    var third = new TranscriptStore(config, IndexCache.Load(cachePath)).Load();
    Check("an edited transcript is reparsed", third.CacheHits == 0, third.CacheHits.ToString());
    Check("and the new title is picked up",
        third.Conversations[0].Title == "Cached and then changed", third.Conversations[0].Title);
}

// ---------------------------------------------------------------- deleting

Section("Deleting a conversation removes it from both places it is recorded");
{
    using var temp = new TempDir("delete");
    var projects = Path.Combine(temp.Path, "projects");
    var historyPath = Path.Combine(temp.Path, "history.jsonl");

    const string Doomed = "dddddddd-0000-0000-0000-000000000001";
    const string Keeper = "dddddddd-0000-0000-0000-000000000002";
    const string Pruned = "dddddddd-0000-0000-0000-000000000003";

    var doomedPath = new TranscriptBuilder(Doomed)
        .Human("delete me", "2026-08-05T02:38:00.000Z", Dev2, Branch)
        .Assistant("all right", "2026-08-05T02:39:00.000Z", Dev2, Branch)
        .Title("Doomed")
        .WriteTo(projects, "C--src-Dev2");

    new TranscriptBuilder(Keeper)
        .Human("keep me", "2026-08-06T02:38:00.000Z", Dev2, Branch)
        .Title("Keeper")
        .WriteTo(projects, "C--src-Dev2");

    File.WriteAllLines(historyPath, new[]
    {
        HistoryBuilder.Line("delete me", Dev2, Doomed, 1_754_361_480_000),
        HistoryBuilder.Line("/clear", Dev2, Doomed, 1_754_361_481_000),
        HistoryBuilder.Line("keep me", Dev2, Keeper, 1_754_447_880_000),
        HistoryBuilder.Line("only in history", Dev2, Pruned, 1_753_000_000_000),
        "{ not json at all",
    });

    var config = new AppConfig { ClaudeHome = temp.Path };
    var index = new TranscriptStore(config, IndexCache.Load(Path.Combine(temp.Path, "cache.json"))).Load();
    Check("three conversations before the delete", index.Conversations.Count == 3,
        index.Conversations.Count.ToString());

    var target = index.Conversations.First(c => c.SessionId == Doomed);
    var report = ConversationDeleter.Delete(new[] { target }, config, DeleteMode.Permanent);

    Check("the conversation is reported gone",
        report.RemovedSessionIds.Count == 1 && report.RemovedSessionIds[0] == Doomed,
        string.Join(",", report.RemovedSessionIds));
    Check("the transcript is off the disk", !File.Exists(doomedPath));
    Check("the file and its bytes are accounted for",
        report.FilesRemoved == 1 && report.BytesRemoved > 0,
        $"{report.FilesRemoved} files, {report.BytesRemoved} bytes");
    Check("both history lines went, the slash command included",
        report.HistoryLinesRemoved == 2, report.HistoryLinesRemoved.ToString());
    Check("nothing failed", report.Errors.Count == 0, string.Join(" | ", report.Errors));
    Check("the summary says what happened",
        report.Summary.Contains("Deleted 1 conversation") && report.Summary.Contains("1 transcript"),
        report.Summary);

    var lines = File.ReadAllLines(historyPath);
    Check("only the doomed lines went", lines.Length == 3, lines.Length.ToString());
    Check("the other session's prompt is untouched", lines.Any(l => l.Contains("keep me")));
    Check("an unrelated pruned session is untouched", lines.Any(l => l.Contains("only in history")));
    Check("a line that cannot be parsed is kept rather than guessed at",
        lines.Any(l => l.Contains("not json at all")));

    Check("the history it replaced is kept as a .bak",
        File.Exists(historyPath + ".bak") &&
        File.ReadAllText(historyPath + ".bak").Contains("delete me"));
    Check("no temp file is left behind", !File.Exists(historyPath + ".tmp"));

    // Claude Code reads this file back. Windows line endings or a BOM in front of the first
    // record would break its parse, and the damage would not show up here.
    var raw = File.ReadAllBytes(historyPath);
    Check("the rewrite keeps bare LF endings", !raw.Contains((byte)13));
    Check("and writes no BOM",
        raw.Length > 3 && !(raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF));

    // The trap the deleter exists for: remove only the .jsonl and the next scan rebuilds the
    // conversation out of history.jsonl as a prompts-only card, title and all.
    var after = new TranscriptStore(config, IndexCache.Load(Path.Combine(temp.Path, "cache2.json"))).Load();
    Check("a rescan does not bring it back",
        after.Conversations.All(c => c.SessionId != Doomed),
        string.Join(",", after.Conversations.Select(c => c.SessionId)));
    Check("and leaves everything else alone", after.Conversations.Count == 2,
        after.Conversations.Count.ToString());
}

Section("Deleting what is left of a pruned conversation");
{
    using var temp = new TempDir("delete-pruned");
    var historyPath = Path.Combine(temp.Path, "history.jsonl");

    File.WriteAllLines(historyPath, new[]
    {
        HistoryBuilder.Line("the printer driver again", Dev2, "pruned-1", 1_753_000_000_000),
        HistoryBuilder.Line("and the duplex setting", Dev2, "pruned-1", 1_753_000_001_000),
        HistoryBuilder.Line("something else entirely", Dev2, "pruned-2", 1_753_000_002_000),
    });

    var config = new AppConfig { ClaudeHome = temp.Path };
    var index = new TranscriptStore(config, IndexCache.Load(Path.Combine(temp.Path, "cache.json"))).Load();
    var target = index.Conversations.First(c => c.SessionId == "pruned-1");

    var report = ConversationDeleter.Delete(new[] { target }, config, DeleteMode.Permanent);
    Check("there is no file to remove", report.FilesRemoved == 0, report.FilesRemoved.ToString());
    Check("but every prompt of that session goes", report.HistoryLinesRemoved == 2,
        report.HistoryLinesRemoved.ToString());
    Check("and it counts as deleted", report.RemovedSessionIds.Count == 1);
    Check("the other session is still there",
        File.ReadAllLines(historyPath).Length == 1, File.ReadAllLines(historyPath).Length.ToString());

    // A transcript can disappear between the scan and the click - Claude Code prunes on its own
    // schedule, and the app can sit open for a day.
    var stale = new Conversation
    {
        SessionId = "vanished",
        TranscriptPath = Path.Combine(temp.Path, "projects", "gone.jsonl"),
    };
    var second = ConversationDeleter.Delete(new[] { stale }, config, DeleteMode.Permanent);
    Check("a transcript that has already vanished is not an error",
        second.Errors.Count == 0 && second.RemovedSessionIds.Count == 1,
        string.Join(" | ", second.Errors));
    Check("and history is not rewritten for nothing", second.HistoryLinesRemoved == 0);
    Check("nothing deleted at all says so", new DeleteReport().Summary == "Nothing was deleted");
}

Section("Deleting a whole group in one pass");
{
    using var temp = new TempDir("delete-group");
    var projects = Path.Combine(temp.Path, "projects");
    var historyPath = Path.Combine(temp.Path, "history.jsonl");

    var ids = new List<string>();
    for (var i = 1; i <= 3; i++)
    {
        var id = "eeeeeeee-0000-0000-0000-00000000000" + i;
        ids.Add(id);
        new TranscriptBuilder(id)
            .Human("session " + i, "2026-08-0" + i + "T02:38:00.000Z", Dev2, Branch)
            .Title("Session " + i)
            .WriteTo(projects, "C--src-Dev2");
    }

    File.WriteAllLines(historyPath,
        ids.Select((id, i) => HistoryBuilder.Line("session " + (i + 1), Dev2, id, 1_753_000_000_000 + i)));

    var config = new AppConfig { ClaudeHome = temp.Path };
    var index = new TranscriptStore(config, IndexCache.Load(Path.Combine(temp.Path, "cache.json"))).Load();

    var group = index.Conversations.Where(c => c.SessionId != ids[0]).ToList();
    var report = ConversationDeleter.Delete(group, config, DeleteMode.Permanent);

    Check("both go together", report.RemovedSessionIds.Count == 2, report.RemovedSessionIds.Count.ToString());
    Check("two transcripts removed", report.FilesRemoved == 2, report.FilesRemoved.ToString());
    Check("one rewrite covers the batch", report.HistoryLinesRemoved == 2,
        report.HistoryLinesRemoved.ToString());
    Check("the one left out is still on disk",
        File.Exists(index.Conversations.First(c => c.SessionId == ids[0]).TranscriptPath!));
    Check("and still in history",
        File.ReadAllLines(historyPath).Length == 1, File.ReadAllLines(historyPath).Length.ToString());

    // Dropping them from the index in place, rather than paying for a rescan of the whole store.
    var trimmed = index.Without(report.RemovedSessionIds.ToHashSet(StringComparer.OrdinalIgnoreCase));
    Check("the index can shed them without a rescan", trimmed.Conversations.Count == 1,
        trimmed.Conversations.Count.ToString());
    Check("and keeps the facts the scan established",
        trimmed.Elapsed == index.Elapsed && trimmed.CacheHits == index.CacheHits);
    Check("session ids are matched without case",
        index.Without(ids.Select(i => i.ToUpperInvariant()).ToHashSet(StringComparer.OrdinalIgnoreCase))
            .Conversations.Count == 0);
}

Section("What the confirmation is allowed to promise");
{
    Conversation Fake(string id, string? path, long bytes) => new()
    {
        SessionId = id,
        TranscriptPath = path,
        Bytes = bytes,
    };

    var mixed = new[]
    {
        Fake("a", Path.Combine("x", "a.jsonl"), 3 * 1024 * 1024),
        Fake("b", Path.Combine("x", "b.jsonl"), 512 * 1024),
        Fake("c", null, 0),
    };

    var plan = DeletePlan.For(mixed);
    Check("the plan counts what is really there",
        plan.Count == 3 && plan.TranscriptCount == 2 && plan.HistoryOnlyCount == 1,
        $"{plan.Count}/{plan.TranscriptCount}/{plan.HistoryOnlyCount}");
    Check("and adds up only the transcripts it will remove",
        plan.Bytes == (3 * 1024 * 1024) + (512 * 1024), plan.Bytes.ToString());
    Check("the question names the number", plan.Question == "Delete 3 conversations?", plan.Question);
    Check("one conversation is asked about in the singular",
        DeletePlan.For(mixed.Take(1)).Question == "Delete this conversation?");

    var detail = plan.Detail("166597-Linux-db-migrator", DeleteMode.Recycle);
    Check("the detail names the set being deleted", detail.Contains("166597-Linux-db-migrator"));
    Check("says where the transcripts go", detail.Contains("Recycle Bin"), detail);
    Check("sizes them", detail.Contains("3.5 MB"), detail);
    Check("promises the history backup", detail.Contains("history.jsonl.bak"));
    Check("owns up to the ones with nothing left to delete but prompts",
        detail.Contains("no transcript left"), detail);
    Check("and says resuming is over", detail.Contains("cannot resume"));

    Check("a permanent delete does not promise a Recycle Bin",
        !plan.Detail("x", DeleteMode.Permanent).Contains("Recycle"),
        plan.Detail("x", DeleteMode.Permanent));

    var single = DeletePlan.For(mixed.Take(1)).Detail("one of them", DeleteMode.Recycle);
    Check("one transcript goes, it does not go",
        single.Contains("1 transcript (3 MB) goes to the Recycle Bin"), single);
    Check("and two of them go",
        detail.Contains("2 transcripts (3.5 MB) go to the Recycle Bin"), detail);

    var promptsOnly = DeletePlan.For(new[] { Fake("c", null, 0) });
    Check("with no transcript, nothing is said about files",
        !promptsOnly.Detail("x", DeleteMode.Recycle).Contains("Recycle Bin"),
        promptsOnly.Detail("x", DeleteMode.Recycle));
}

// ---------------------------------------------------------------- reading one back

Section("Rebuilding a conversation for reading");
{
    using var temp = new TempDir("reader");
    var projects = Path.Combine(temp.Path, "projects");
    var path = new TranscriptBuilder("bbbbbbbb-0000-0000-0000-000000000001")
        .Human("why is the build failing", "2026-08-05T02:38:00.000Z", Dev2, Branch)
        .Assistant("Checking the toolchain.", "2026-08-05T02:39:00.000Z", Dev2, Branch)
        .ToolCall("Bash", "dotnet build Monitor.sln", "2026-08-05T02:40:00.000Z", Dev2, Branch, "toolu_9")
        .ToolResult("MSB3277 conflict", "2026-08-05T02:41:00.000Z", Dev2, Branch, "toolu_9")
        .WriteTo(projects);

    var turns = TranscriptReader.Read(path);

    Check("the prompt is a turn", turns.Any(t => t.Role == TurnRole.You && t.Text.Contains("build failing")));
    Check("the reply is a turn", turns.Any(t => t.Role == TurnRole.Claude));
    var tool = turns.SingleOrDefault(t => t.Role == TurnRole.Tool);
    Check("the tool call is one line", tool is not null && tool.Text == "dotnet build Monitor.sln",
        tool?.Text);
    Check("named by its tool", tool?.ToolName == "Bash", tool?.ToolName);
    Check("carrying the head of what it returned",
        tool is not null && tool.HasResult && tool.Result!.Contains("MSB3277"), tool?.Result);
    Check("tool results are not shown as if someone said them",
        turns.Count(t => t.Role == TurnRole.You) == 1);
    Check("turns are in transcript order",
        turns.Select(t => t.When).SequenceEqual(turns.Select(t => t.When).OrderBy(w => w)));
}

// ---------------------------------------------------------------- small pieces

Section("Snippets keep the match visible");
{
    var text = "we should probably " + new string('x', 400) + " duplex " + new string('y', 400);
    var idx = text.IndexOf("duplex", StringComparison.Ordinal);
    var (snippet, start, length) = TextSummary.Snippet(text, idx, "duplex".Length);

    Check("the snippet is trimmed to a line", snippet.Length <= 152, snippet.Length.ToString());
    Check("the match is inside it", snippet.Contains("duplex"));
    Check("and the reported offsets point at it",
        snippet.Substring(start, length) == "duplex", snippet.Substring(start, length));

    var flat = TextSummary.OneLine("  lots\n\nof   space  ", 100);
    Check("whitespace is collapsed", flat == "lots of space", $"'{flat}'");
    Check("long text is cut with an ellipsis",
        TextSummary.OneLine(new string('a', 50), 10).EndsWith("…"));

    // A hit at the very start of a short prompt is the common case; it must not be mangled.
    var (short1, s1, l1) = TextSummary.Snippet("duplex is broken", 0, 6);
    Check("a hit at the start survives", short1 == "duplex is broken" && s1 == 0 && l1 == 6,
        $"'{short1}' {s1} {l1}");
}

Section("Ages read the way a person would say them");
{
    var now = DateTimeOffset.Parse("2026-08-26T12:00:00Z");
    Check("minutes", TimeText.Relative(now.AddMinutes(-14), now) == "14m ago");
    Check("hours", TimeText.Relative(now.AddHours(-5), now) == "5h ago");
    Check("under a day stays in hours",
        TimeText.Relative(DateTimeOffset.Parse("2026-08-25T23:00:00Z"), now) == "13h ago",
        TimeText.Relative(DateTimeOffset.Parse("2026-08-25T23:00:00Z"), now));
    Check("a calendar day back is yesterday, not 2d",
        TimeText.Relative(DateTimeOffset.Parse("2026-08-25T09:00:00Z"), now) == "yesterday",
        TimeText.Relative(DateTimeOffset.Parse("2026-08-25T09:00:00Z"), now));
    Check("days", TimeText.Relative(now.AddDays(-4), now) == "4d ago");
    Check("weeks", TimeText.Relative(now.AddDays(-15), now) == "2w ago");
    Check("older falls back to a date",
        TimeText.Relative(now.AddDays(-60), now) == "27 Jun",
        TimeText.Relative(now.AddDays(-60), now));

    Check("a short session", TimeText.Duration(TimeSpan.FromMinutes(43)) == "43m");
    Check("a long one", TimeText.Duration(TimeSpan.FromMinutes(80)) == "1h 20m");
    Check("one picked up the next day says so",
        TimeText.Duration(TimeSpan.FromHours(31)) == "spans 2 days",
        TimeText.Duration(TimeSpan.FromHours(31)));

    Check("today bucket", TimeText.DayBucket(now.AddHours(-2), now) == "Today");
    Check("buckets are ordered", TimeText.DayBucketRank(now, now) < TimeText.DayBucketRank(now.AddDays(-40), now));
}

Section("Settings round-trip");
{
    using var temp = new TempDir("config");
    var path = Path.Combine(temp.Path, "config.json");

    var written = new AppConfig
    {
        ClaudeHome = @"C:\Users\someone\.claude",
        GroupMode = GroupMode.Folder,
        Scope = SearchScope.Everything,
        MaxAgeDays = 90,
    };
    written.Save(path);

    var read = AppConfig.Load(path);
    Check("the .claude folder survives", read.ClaudeHome == @"C:\Users\someone\.claude", read.ClaudeHome);
    Check("the grouping survives", read.GroupMode == GroupMode.Folder, read.GroupMode.ToString());
    Check("the scope survives", read.Scope == SearchScope.Everything, read.Scope.ToString());
    Check("the age limit survives", read.MaxAgeDays == 90, read.MaxAgeDays.ToString());

    var json = File.ReadAllText(path);
    Check("enums are written by name, not as numbers", json.Contains("\"Folder\""), json);
    // Derived paths in the file would look editable and be ignored on load.
    Check("derived paths stay out of the file",
        !json.Contains("projectsDir") && !json.Contains("historyFile"), json);
    Check("and are still available on the object",
        read.ProjectsDir.EndsWith(@"\projects") && read.HistoryFile.EndsWith("history.jsonl"));

    File.WriteAllText(path, "{ not json at all");
    var recovered = AppConfig.Load(path);
    Check("a corrupt config falls back to defaults instead of crashing",
        recovered.ClaudeHome.Length > 0, recovered.ClaudeHome);

    var missing = AppConfig.Load(Path.Combine(temp.Path, "nope", "config.json"));
    Check("a missing config writes itself out with a sensible .claude home",
        missing.ClaudeHome == AppConfig.DefaultClaudeHome(), missing.ClaudeHome);
}

Section("Finding a command before trying to run it");
{
    using var temp = new TempDir("path");
    var binDir = Path.Combine(temp.Path, "bin");
    Directory.CreateDirectory(binDir);
    File.WriteAllText(Path.Combine(binDir, "claude.exe"), "");

    var fakePath = @"C:\does\not\exist;" + binDir;
    Check("found via PATHEXT", CommandLocator.Find("claude", fakePath, ".EXE;.CMD") is not null);
    Check("full path returned",
        CommandLocator.Find("claude", fakePath, ".EXE")
            ?.EndsWith("claude.exe", StringComparison.OrdinalIgnoreCase) == true,
        CommandLocator.Find("claude", fakePath, ".EXE"));
    Check("a missing command is null", CommandLocator.Find("nosuchtool", fakePath, ".EXE") is null);
    Check("a broken PATH entry does not throw",
        CommandLocator.Find("claude", "\"|<>;" + binDir, ".EXE") is not null);
}

// ---------------------------------------------------------------- against real data

Section("The real .claude folder on this machine (skipped if absent)");
{
    var home = AppConfig.DefaultClaudeHome();
    if (!Directory.Exists(Path.Combine(home, "projects")))
    {
        Console.WriteLine($"  SKIP  no transcripts at {home}");
    }
    else
    {
        var cachePath = Path.Combine(Path.GetTempPath(), "convmgr-tests-real-cache.json");
        if (File.Exists(cachePath)) File.Delete(cachePath);

        var index = new TranscriptStore(new AppConfig { ClaudeHome = home }, IndexCache.Load(cachePath))
            .Load();

        Check("conversations found", index.Conversations.Count > 0, index.Conversations.Count.ToString());
        Check("titles were read", index.Conversations.Any(c => !string.IsNullOrWhiteSpace(c.Title)));
        Check("branches were read", index.Conversations.Any(c => c.Branches.Count > 0));
        Check("prompts were read", index.Conversations.Any(c => c.Prompts.Count > 0));
        Check("no conversation is missing a folder",
            index.Conversations.Where(c => c.HasTranscript).All(c => c.Cwd.Length > 0));
        Check("HEAD never leaks through as a branch",
            index.Conversations.All(c => !c.Branches.Contains("HEAD")));
        Check("a scan of the real store stays under 10s",
            index.Elapsed < TimeSpan.FromSeconds(10), index.Elapsed.ToString());

        Console.WriteLine(
            $"        {index.Conversations.Count} conversations · " +
            $"{index.TranscriptCount} transcripts · {index.HistoryOnlyCount} prompts-only · " +
            $"{index.TranscriptBytes / 1024 / 1024}MB · {index.Elapsed.TotalSeconds:0.00}s");

        var withPrompts = index.Conversations.Count(c => c.Prompts.Count > 0);
        Console.WriteLine($"        {withPrompts} conversations have searchable prompts");
    }
}

Console.WriteLine($"\n{pass} passed, {fail} failed");
return fail == 0 ? 0 : 1;
