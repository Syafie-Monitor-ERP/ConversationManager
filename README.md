# Conversation Manager

You had a Claude Code session that solved this exact problem. Two weeks ago, maybe three. You
remember roughly what you asked and roughly which branch it was on — and that is not enough to
find it again, because `/resume` shows you a list of titles and nothing else.

This searches every conversation you have ever had, by branch, by folder, by date, or by something
you typed, and hands the one you wanted back to `claude --resume`.

```
┌──────────────────────────────────────────────────────────────────────────────────────┐
│ Conversation Manager  [ net10                    ✕ ]  Search[your prompts|everything]│
│                                                       Group by [branch|folder|recent]│
├──────────────────────────────────────────────────────────────────────────────────────┤
│ 6 conversations - best match is a title                                              │
│ ┌──────────────────────────────────────────────────────────────────────────────────┐ │
│ │ Prepare changes for net10 migration                                      1w ago  │ │
│ │ 166597-Linux-db-migrator  ·  Dev2  ·  164 msgs  ·  655 KB                        │ │
│ │ in the title   also you said                    Preview   [ Resume ]             │ │
│ │ Prepare changes for net10 migration                                              │ │ ← net10 in gold
│ │ Wed 19 Aug  08:39–10:31  ·  1h 52m   Explorer  Terminal  Copy id  Delete         │ │
│ └──────────────────────────────────────────────────────────────────────────────────┘ │
│ ┌──────────────────────────────────────────────────────────────────────────────────┐ │
│ │ Monitor G5 server Linux container image                                  6h ago  │ │
│ │ 168969-Cross-Platform-Mobile  ·  Dev1  ·  47 msgs  ·  232 KB                     │ │
│ │ you said                                                                         │ │
│ │ …output is already portable: the repo targets net10.0 with no RuntimeIdentifier…  │ │
│ └──────────────────────────────────────────────────────────────────────────────────┘ │
├──────────────────────────────────────────────────────────────────────────────────────┤
│ 27 more conversations mention it in replies or command output    show                │
├──────────────────────────────────────────────────────────────────────────────────────┤
│ 158 conversations · 48 with transcripts · 110 prompts only · back to 27 Feb · 0.1s   │
└──────────────────────────────────────────────────────────────────────────────────────┘
```

Clear the box and you get the overview instead — one block per branch, one card per conversation:

```
166597-Linux-db-migrator                       12 conversations   Delete all 12
C:\src\Dev2
┌──────────────────────────────────────────────────────────────────────────────────┐
│ PR merge conflict verification                                          36m ago  │
│ 166597-Linux-db-migrator  +2 more  ·  Dev2  ·  70 msgs  ·  221 KB                │
│ opened with                                             Preview   [ Resume ]     │
│ 47611, 49489 and 49350 this 3 PR will be merge into master next week, in order…   │
│ Wed 26 Aug  20:24–21:27  ·  1h 2m                                                │
└──────────────────────────────────────────────────────────────────────────────────┘
```

One self-contained `.exe`. Nothing to install — it carries its own .NET runtime, reads the
`~\.claude` folder you already have, and drives the `claude` already on your PATH.

> Working on the code rather than using it? See **[CODEBASE.md](CODEBASE.md)** — file map, the two
> data sources, how ranking works, and how to test it.

## Run it

Double-click `dist\ConversationManager.exe`, or `run.cmd` (which publishes first if `dist` is
missing).

Windows only — WPF, and it opens Explorer and Windows Terminal.

| Script | What it does |
| --- | --- |
| `run.cmd` | Launch the app, publishing it first if needed |
| `publish.cmd` | Rebuild `dist\ConversationManager.exe` |
| `test.cmd` | Run both test suites |

The .NET SDK is needed only to *build*. Machines that just run the exe need nothing — copy the
`dist` folder to a colleague and it works.

## Finding a conversation

Type in the search box. A branch name, a work item number, a PR number, a folder, or a phrase you
remember typing.

- Matching is fuzzy on names, so `168dup` finds the conversation on `168987-Duplex-setting-is-not`.
- Every word has to match somewhere, so `duplex tray` means both, not either.
- Paste a session id and it finds that session.
- Results say **why** they matched — `in the title`, `you said`, `in the branch name` — and the
  matched words are picked out in gold, so you can tell a real hit from a coincidence at a glance.

### The two search scopes

This is the one setting worth understanding, because it is the difference between a search that
answers you and one that returns everything.

Your transcripts are not mostly conversation. Measured on the store this was built against:

| Layer | Size | What it is |
| --- | --- | --- |
| What you typed | **0.06 MB** | prompts |
| What Claude said | 3.5 MB | explanations, plans |
| Tool input and output | **22.3 MB** | file dumps, build logs, grep results |

So `your prompts` (the default) searches titles, branches, folders and your own words — 97% of the
store is skipped, and almost every question is still answered. `everything` adds Claude's replies
and command output, where a term like `migrator` appears in 31 of 54 transcripts and stops
narrowing anything.

You rarely need to switch: while the default scope is on, the app still checks the deeper layers in
the background and offers them on one line at the bottom — *"27 more conversations mention it in
replies or command output"* — which you click on the rare occasions the good answers came up empty.

## What you can do with a result

| Action | What happens |
| --- | --- |
| **Resume** | Opens Windows Terminal in that folder running `claude --resume <id>` |
| **Preview** | Reads the conversation here — your prompts, Claude's replies, each tool call as one line — without resuming it |
| **Explorer** / **Terminal** | Opens that conversation's working folder |
| **Copy id** / **Copy resume cmd** | The session id, or the whole `claude --resume` command |
| **Delete** | Removes the conversation from your `.claude` folder — see [Deleting](#deleting) |

Preview carries your search term across and highlights it, with a **only matching turns** box to
strip the conversation down to the parts that mention it. It is the fastest way to remember what a
piece of work actually concluded.

## Conversations that are only half there

Claude Code prunes transcripts. It does not prune `history.jsonl`, which records every prompt you
ever submitted with its session id, folder and timestamp. On the machine this was built for, that
is the difference between **48** conversations and **158**.

So the app reads both, and says which is which. A conversation with no transcript left is marked
`transcript expired · prompts only`, has its border dimmed, and its Resume button disabled — you
can still find it by what you typed, see when and where it happened, and read your own prompts back
in Preview. There is simply nothing left to resume.

That is why the status bar counts them separately:

```
158 conversations  ·  48 with transcripts  ·  110 prompts only  ·  back to 27 Feb 2026
```

## Deleting

Every card has a **Delete**, every group heading a **Delete all N**, and a search that found
something has a **Delete all N** of its own — so a query is also a way of picking a set.

A delete removes the conversation from **both** places Claude Code recorded it:

1. the `.jsonl` transcript under `projects\`, which goes to the **Recycle Bin**, and
2. every line for that session in `history.jsonl`, which is rewritten without them.

Both, because either on its own is a lie. Delete only the transcript and the next scan rebuilds the
conversation out of `history.jsonl` — same title, same folder, marked `prompts only`. Delete only
the history lines and the transcript brings it straight back.

Before anything is touched you are told exactly what will go:

```
Delete 12 conversations?

166597-Linux-db-migrator  ·  C:\src\Dev2
3 of these also worked on another branch, and will go from that heading too.

11 transcripts (48.2 MB) go to the Recycle Bin.
Their prompts are removed from history.jsonl, which is backed up first as history.jsonl.bak.
1 conversation has no transcript left, so only the prompts go.

Claude Code cannot resume a deleted conversation.
```

**Getting something back.** The transcripts are in the Recycle Bin under their session ids —
restore the `.jsonl` and rescan. The prompts are in `history.jsonl.bak`, which holds the file as it
was immediately before the last delete; it is overwritten by the next one, so recover from it
before deleting anything else.

Anything that refuses to go — a file open elsewhere, a read-only folder — is left alone, keeps its
card, and says so in the status bar. The status bar reports every delete:

```
Deleted 12 conversations   ·   11 transcripts (48.2 MB)   ·   37 history lines removed
```

## Grouping

With the search box empty:

| Group by | Answers |
| --- | --- |
| **branch** | "which conversations touched this branch" — a session that switched branches mid-way is listed under both |
| **folder** | "what have I been doing in Dev2" |
| **recent** | "what was I working on yesterday" |

## Keys

| Key | Action |
| --- | --- |
| `Ctrl+F` | Jump to the search box |
| `Esc` | Clear the search, or close a preview |
| `F5` / `Ctrl+R` | Read the `.claude` folder again |

## Settings

`config.json` appears next to the exe on first run:

```json
{
  "claudeHome": "C:\\Users\\you\\.claude",
  "groupMode": "Branch",
  "scope": "Prompts",
  "maxAgeDays": 0
}
```

`claudeHome` only needs changing if you have moved your Claude config (it honours
`CLAUDE_CONFIG_DIR` automatically). `maxAgeDays` hides anything older than that many days; `0`
keeps everything. The scope and grouping switches in the header write themselves here, so the app
opens the way you left it.

`index-cache.json` also appears there. It remembers what each transcript parsed to, keyed by size
and write time, so a rescan only re-reads files that changed. Deleting it costs one slow launch and
nothing else.

## Known limitations

- **Windows only.** WPF, Explorer, Windows Terminal.
- **A term in a prompt and another in a build log will not match together.** Each search runs
  against the index or against the file, and the two do not combine.
- **Deep search re-reads the transcripts.** Roughly 0.5s over 46MB, in the background, cancelled on
  the next keystroke. It runs unasked only when the shallow search found fewer than 20 hits.
- **Nothing is written to your `.claude` folder unless you delete something.** Search, preview and
  grouping only ever read it. Its own two files live next to the exe.
