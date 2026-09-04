# Conversation Manager — codebase guide

For working *on* the app. If you just want to use it, see **[README.md](README.md)**.

The shape is deliberately the same as its sibling **BranchManager**: WPF, MVVM by hand, no
libraries, a plain console test runner, one self-contained exe. Theme and the base view-model
plumbing are ported from it, so a change that improves one is usually worth carrying to the other.

---

## 1. Files

```
src/ConversationManager/
  App.xaml/.cs               Startup, and a message box on any unhandled exception
  MainWindow.xaml/.cs        Header, results list, overview, status bar
  TranscriptWindow.xaml/.cs  The read-only preview of one conversation
  Theme.xaml                 Palette, text styles, buttons, the placeholder text box, segments
  Converters.cs              Visibility/resource converters, and Highlight (match run-splitting)
  app.ico                    Window and exe icon, drawn by tools\make-icon.ps1

  Models/                    Pure .NET - no WPF types (see §2)
    Conversation.cs          One past session, reduced to searchable facts
    SearchModels.cs          Layers, match kinds, hits, and the Scoring weights
    AppConfig.cs             config.json - claudeHome, scope, grouping, max age
    TimeText.cs              "yesterday", "1h 20m", day buckets
    TextSummary.cs           One-lining and snippet windows with match offsets

  Services/                  Pure .NET - no WPF types
    TranscriptParser.cs      One .jsonl -> one Conversation
    HistoryReader.cs         history.jsonl -> prompts with session, folder, time
    TranscriptStore.cs       Scans .claude, merges the two sources, applies the cache
    IndexCache.cs            index-cache.json, keyed by file size + write time
    QueryMatcher.cs          Finding one term in one string, and how well it fit
    ConversationSearch.cs    Ranking over the in-memory index
    DeepSearcher.cs          Ranking over the transcript files themselves
    ConversationGrouper.cs   The overview: by branch, folder, or recency
    ConversationDeleter.cs   Removing a conversation from both sources (see §2)
    TranscriptReader.cs      One .jsonl -> readable turns for the preview
    CommandLocator.cs        Is `claude` on PATH?

  Platform/Launcher.cs       Explorer, terminal, claude --resume, clipboard (WPF-dependent)
  Platform/Dialogs.cs        The delete confirmation - the app's only modal
  ViewModels/
    ObservableObject.cs      INotifyPropertyChanged, RelayCommand, AsyncRelayCommand (ported)
    MainViewModel.cs         Index, query, debounced deep scan, the two switches
    ConversationCardViewModel.cs   One card: facts, why it matched, six actions
    ConversationGroupViewModel.cs  One heading plus its cards
    TranscriptViewModel.cs   Preview turns, find-in-conversation, only-matching filter

tests/ConversationManager.Tests/      Parsing, merging, ranking, grouping, deleting (console runner)
tests/ConversationManager.UiTests/    Offscreen layout and highlighting (needs WPF)
tools/make-icon.ps1                   Draws app.ico from scratch, one frame per size
```

### The Models/Services rule

Nothing in `Models/` or `Services/` may reference WPF. The app targets `net10.0-windows`, and a
`net10.0` console project cannot reference it, so the test project compiles those two folders in
directly. Reach for `System.Windows` in a service and the test suite stops building — which is the
intended alarm, not an inconvenience. Anything that genuinely needs WPF goes in `Platform/`.

---

## 2. The two data sources

This is the fact the whole app is shaped around. Claude Code records the past in two places, and
neither is complete.

| | `projects\*\*.jsonl` | `history.jsonl` |
| --- | --- | --- |
| Holds | full transcripts | every prompt ever submitted |
| Per record | branch, cwd, timestamps, titles, messages, tool calls | text, session id, project, epoch ms |
| Retention | pruned | kept far longer |
| On the reference machine | **48** sessions | **171** sessions |

So `TranscriptStore.MergeHistory` does two jobs, keyed by session id:

1. A session with a transcript **gains any prompt that history has and the transcript does not** —
   which recovers prompts lost to compaction.
2. A session with no transcript **becomes a `Conversation` anyway**, marked
   `ConversationSource.HistoryOnly`: findable, dateable, locatable, but not resumable and with no
   branch. `Conversation.CanResume` is what the Resume button binds to, and it is false for these.

Prompt de-duplication compares whitespace-collapsed text, because the same prompt is not stored
byte-identically in both files.

### Deleting has to hit both

The same fact, read backwards, is why `ConversationDeleter` exists. Deleting the `.jsonl` alone
looks like it worked: the card goes, the disk space comes back — and the next scan puts the
conversation straight back on screen, rebuilt from `history.jsonl`, with its title and folder
intact and `prompts only` under it. So a delete removes the transcript **and** rewrites
`history.jsonl` without that session id, in one pass over the file for the whole batch.

Three details in there that are not decoration:

- **The rewrite matches on `sessionId`, not on text.** Slash commands and shell lines are skipped
  by `HistoryReader` and so never appear as prompts, but they are still that session's lines and
  still have to go.
- **A line that will not parse is kept.** Nothing is deleted on the strength of a guess.
- **LF endings, no BOM, and the old file kept as `history.jsonl.bak`.** Claude Code reads this
  file back; `File.WriteAllLines` would give it CRLF on Windows and break its parse. The rewrite
  goes through a temp file and `File.Replace`, which produces the backup as a side effect.

Transcripts go to the Recycle Bin rather than straight off the disk (`DeleteMode`), because a
mis-clicked *Delete all* on a branch heading would otherwise take twenty conversations with no way
back. The tests use `DeleteMode.Permanent`; they have nothing worth keeping.

`index-cache.json` is deliberately left alone: its entries are keyed by file path, and one whose
file has gone is simply never matched again, then dropped when the next scan rewrites the cache.

### What a transcript record looks like

One JSON object per line. The fields that matter, all optional:

```jsonc
{ "type": "user",            // user | assistant | ai-title | attachment | file-history-* | ...
  "message": { "role": "user", "content": "…" },   // string, or an array of blocks
  "origin": { "kind": "human" },                   // human | task-notification | …
  "timestamp": "2026-08-05T02:38:00.000Z",
  "cwd": "C:\\src\\Dev2",
  "gitBranch": "166597-Linux-db-migrator",         // or "HEAD"
  "sessionId": "…", "isMeta": false }
```

Four traps, each one pinned by a test:

- **A `user` record is usually not a person.** On the reference store, 3,965 of them are
  `tool_result` blocks against 273 real prompts. `origin.kind == "human"` is the reliable filter.
- **Older transcripts have no `origin` at all.** For those, fall back on shape: slash commands and
  their output arrive wrapped in `<command-name>`, `<local-command-stdout>` and friends
  (`TranscriptParser.MachinePrefixes`).
- **`gitBranch` is `"HEAD"`** when the folder is detached or not a repo. It names nothing anyone
  would search for, so it is dropped at parse time, and `Branches` is then empty.
- **`ai-title` is rewritten as the session goes.** Take the last one.

**Naming a conversation with no `ai-title`** — every history-only one, and any session too short
to earn a title — falls to `Conversation.DisplayTitle`, which takes the first prompt that is not a
bare URL or path (`IsBareReference`). Sessions often open with a pasted link, and using it as the
card's name leaves a wall of URLs where the titles should be.

`cwd` varies within a session as Claude moves around. The primary is the most frequent, tie-broken
by the shortest path, so a session that dips into `src\Monitor.Net` still belongs to `C:\src\Dev2`.

### What is deliberately thrown away

A `Conversation` holds metadata plus prompts, and nothing else. The reasoning, in bytes measured
across the reference store:

| Layer | Size |
| --- | --- |
| Prompts | 0.06 MB |
| Assistant prose | 3.5 MB |
| Tool input/output | 22.3 MB |
| Attachments | 2.0 MB |

Keeping the last three in memory would cost hundreds of MB, grow ~60MB a month, and buy a search
that mostly returns noise. `DeepSearcher` goes back to the files instead, on demand.

---

## 3. Ranking

`Models/SearchModels.cs → Scoring` holds every weight, so ranking behaviour is one file to read and
one file to change.

```
score(hit) = LayerWeight + KindBonus + min(extra hits, 5) × 8

LayerWeight   Title 1000 · SessionId 950 · Branch 900 · Folder 650
              Prompt 600 · Assistant 250 · Tool 90
KindBonus     Exact 300 · Prefix 200 · WordStart 160 · Substring 100 · Fuzzy 40
```

A conversation's score is the **average** of the best hit per term (not the sum — otherwise a
two-word query outranks a one-word query on the same conversation just for having more terms), plus
25 per additional distinct layer that matched, capped at 3. Ties break on recency.

Two rules that keep results honest:

- **All terms must match** (`ConversationSearch.Score` returns null on the first miss). Narrowing is
  the point.
- **Fuzzy matching is only allowed on name-like layers** — `Scoring.AllowsFuzzy`. A subsequence
  search over prose matches nearly everything: `pxyz` would hit half the store.

Session ids need a term of 6+ characters and never match fuzzily, or any hex-ish query would hit
every conversation.

### Shallow versus deep

| | `ConversationSearch` | `DeepSearcher` |
| --- | --- | --- |
| Reads | the in-memory index | the .jsonl files |
| Layers | Title, SessionId, Branch, Folder, Prompt | Assistant, Tool |
| Cost | microseconds, runs per keystroke | ~0.5s over 46MB, debounced 450ms, cancellable |

`DeepSearcher.ScanFile` gates on the raw JSON line before parsing it: `line.Contains(term)` fails
for almost every line and costs one scan. Only survivors get parsed, classified as prose or tool
output, and turned into a snippet. It stops after 200 matched lines in one file — counting every hit
in a build log changes nothing on screen.

A conversation matched only in those two layers is `ConversationMatch.IsDeepOnly`. `MainViewModel`
keeps those out of the list and offers them on one footer line instead. It runs the deep scan
unasked only when the shallow search returned fewer than `DeepScanThreshold` (20) hits — below that
the user clearly has not found what they wanted; above it, re-reading everything buys nothing.

---

## 4. Snippets and highlighting

`TextSummary.Snippet` returns the text **and where the match landed inside it**, because a bound
string cannot carry formatting. `Converters.cs → Highlight` turns that into three `Run`s on a
`TextBlock` through attached properties.

The trap, and the reason `TextSummary.Flatten` exists: flattening the prefix, match and suffix
separately looks equivalent and is not. Each piece gets its own edges trimmed, which welds the match
to its neighbour — `"stale toolchain"` renders as `staletoolchain`. So the whole string is flattened
in one pass, carrying the match offsets through it.

`Highlight.Apply` clamps every offset it is given rather than trusting them: a recycled card can
still be carrying the previous snippet's numbers when the new text arrives.

---

## 5. The UI layer

Two windows, both plain `DataTemplate` work over `ObservableCollection`s.

- **Search results** and the **overview** share one card template. The only difference is whether
  the snippet line is showing a match (`Match.Best`) or the opening prompt.
- A deep-only card is dimmed to 0.78 opacity and its match label is muted rather than accented —
  the `DeepBrush` converter. Weak evidence must not look like strong evidence.
- A history-only card gets an amber-ish border and an explicit `transcript expired` line.
- **Delete** sits last on the card's link row behind a divider, **Delete all N** on every group
  heading, and another on the search summary — a query is a way of picking a set, so it is also a
  way of deleting one. All three funnel into `MainViewModel.DeleteAsync`: confirm, delete off the
  UI thread, then `DropFromIndex` rather than a rescan, which would cost a second and the scroll
  position to learn something already known. The links are muted at rest and only redden under the
  pointer (`DangerLinkButton`); a row of red buttons would shout about the rarest action on screen.
- The header's two switches are **segmented buttons**, not combo boxes. Each button binds its own
  "am I active" flag into `Tag`, which a `DataTrigger` inside the template reads. This replaced two
  `ComboBox`es: WPF's default combo chrome is light and ignores a `Background` setter, so selected
  text rendered near-white on near-white. A UI test now pins the active fill and its contrast.
- `PlaceholderTextBox` is ported from BranchManager, including the 2px `HintMargin` compensation for
  WPF's `TextBoxView` inset. Do not add `Margin="{TemplateBinding Padding}"` to
  `PART_ContentHost` — `TextBoxBase` already applies Padding, and setting both indents text twice.

The preview window renders `TranscriptReader` turns: your prompts in semibold, Claude's prose plain,
tool calls as one monospace line with the head of their result in a muted block, and thinking in
italics. Tool results are collected in a first pass and matched by `tool_use_id`, since a result
arrives in a later record than the call it answers.

### Threading

`MainViewModel.RunDeepScanAsync` is started from the UI thread and awaited without
`ConfigureAwait(false)`, so its continuation is back on the UI thread and can touch
`ObservableCollection`s directly. `DeepSearcher` and `TranscriptStore` do their work inside
`Task.Run` with `Parallel.ForEach`. Keep it that way, or add marshalling.

---

## 6. Tests

`test.cmd` runs both suites. Neither needs a test framework installed.

```
dotnet run --project tests\ConversationManager.Tests        186 checks - logic
dotnet run --project tests\ConversationManager.UiTests       44 checks - layout, needs WPF
```

`ConversationManager.Tests` builds synthetic transcripts through `TranscriptBuilder`, which emits
the real record shapes (`Human`, `LegacyHuman`, `ToolResult`, `ToolCall`, `Title`, `Meta`, `Raw`).
Add a case there rather than hand-writing JSON.

Its last section runs against **the real `~\.claude` folder** if one exists, asserting that
conversations, titles, branches and prompts all come back, that `HEAD` never leaks through as a
branch, and that a full scan stays under 10 seconds. It prints what it found:

```
158 conversations · 48 transcripts · 110 prompts-only · 49MB · 0.11s
```

It skips cleanly on a machine with no transcripts, so CI is fine.

`ConversationManager.UiTests` links `Theme.xaml` and `Converters.cs` — the real files, not copies —
and measures elements offscreen. It exists for the two things that break invisibly: placeholder
alignment, and run-splitting in `Highlight`.

---

## 7. Recipes

**Add a searchable field.** Add it to `Conversation`, populate it in `TranscriptParser`, add a
`SearchLayer` and its weight in `Scoring`, and emit a hit for it in `ConversationSearch.HitsFor`. If
it is short and name-like, add it to `Scoring.AllowsFuzzy`. Then a test in the ranking section.

**Change how results are ordered.** `Scoring` and `ConversationSearch.Order`, nowhere else.

**Add an action to a card.** A `RelayCommand` in `ConversationCardViewModel` calling a `Launcher`
method that returns an `Outcome`; report it through `Report` so the message lands on the card.

**Support a new record type in the preview.** `TranscriptReader.Read`'s block switch, plus a
`TurnRole` and a `DataTrigger` in `TranscriptWindow.xaml`.

**Change the icon.** Edit the shapes in `tools\make-icon.ps1` and run it — it redraws `app.ico`
at every size from 16 to 256. Check the 16px frame: dots and thin strokes turn to smudge there,
which is why the current one drops its dots below 24.

**The cache is suspect.** Delete `index-cache.json` next to the exe. If a stale parse is being
served, `IndexCache.Match` is the place: it requires size *and* write time to agree.

---

## 8. Editing traps

Two that cost an afternoon and leave no trace in a diff:

- **The `.cmd` files must keep CRLF line endings.** Written with bare LF - which is what a
  Unix-style heredoc or a `sed -i` produces - `cmd.exe` reports
  `'test.cmd' is not recognized as an internal or external command`, which reads like a missing
  file rather than a malformed one.
- **Writing C# through a shell heredoc mangles backslashes.** An escaped backslash character
  literal collapses to a single backslash on the way through, turning `TrimEnd` on path separators
  into an unterminated character literal — which then fails to compile with a confusing error two
  lines further down. Use a file-writing tool for anything containing escape sequences. (This very
  bullet was mangled that way on its first write.)

---

## 9. Known limitations

- Mixed shallow/deep queries do not combine: a term in a prompt plus a term only in a build log
  finds nothing. Making that work means indexing the deep layers, which §2 argues against.
- A history-only conversation has no branch, because `history.jsonl` does not record one. They land
  in the `no branch recorded` group.
- `MaxTurns` (3,000) caps the preview. The largest real transcript here is 563 messages, so this has
  not bitten, but a very long session would be truncated silently.
- Windows only, and `Launcher` shells out to `wt.exe` with a `cmd.exe` fallback.
- Only one delete is recoverable from `history.jsonl.bak`: the next delete overwrites it. The
  transcripts themselves are in the Recycle Bin and keep piling up there, so the two halves of an
  older delete are not equally recoverable.
- A delete that empties a `projects\<key>\` folder leaves the folder behind. It costs a directory
  entry and Claude Code recreates it anyway.
- There is no undo inside the app, and no multi-select: a set is picked by grouping or by
  searching, never by ticking boxes.
