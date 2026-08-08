# 44 — Lore, as the editor's version control

⚠️ **Amends [20 § B7](20-editor-parity.md#b7--build-deploy-and-extend)'s Source control row and
[21 § Part A](21-realtime-collaboration.md)'s two rejections; extends [08](08-asset-pipeline-and-addressables.md),
[11](11-editor.md) and [36](36-an-extensible-editor.md).**

## The problem, stated from evidence

Three documents have now touched version control and all three pushed it away, each for a reason
that was correct when written and only one of which still holds.

[20 § B7](20-editor-parity.md#b7--build-deploy-and-extend) has the row: **Source control · Revision
Control / Version Control · ⛔ · P2. Status column in the browser, and check-out/revert/diff/history
over a provider interface with a git implementation.** That is the whole specification, and its last
five words are the part this document changes.

[21 § Part A](21-realtime-collaboration.md) rejected two Multi-User Editing features on the same
grounds twice: *"Vixen has no SCM integration and should not grow one here; git over a text scene
format is the user's, and doc 08 already made it work"*, and *"the tool for 'reconcile two people's
week' is git, and it is better at it than this would be."* Both rejections stand — a live session is
not a version control system and archived sessions are still version control in a session's
clothing. What has changed is the second clause. **"The user's git" is a defensible answer for the
scene files and an indefensible one for everything beside them**, and doc 08 is where the evidence
is: a Vixen project is `Assets/` containing `hero.png`, `hero.fbx`, `hero.png.meta`, `hero.fbx.meta`,
`Level1.vxscene` and `Level1.vxscene.meta`. The `.vxscene` merges. The `.fbx` does not, the `.png`
does not, the `.wav` does not, and by mass they are the project. A team of five with one artist
discovers this in week two.

The repository's own `.gitignore` carries the scar tissue of the third piece of evidence — two
paragraphs of comment explaining that `Library/` had to be negated for `Raven/Library/` and that a
bare `Build/` line made the entire Nuke build project invisible on a case-insensitive filesystem.
Ignore rules are load-bearing and get written wrong by people who know the system well. A project
template that generates them is worth more than a document that explains them.

**What is new is that there is now something to integrate with.** In June 2026 Epic open-sourced
[Lore](https://lore.org/) under MIT — the version control system behind UEFN, previously Unreal
Revision Control — designed from the first line for exactly the file mix above. This document is the
plan for making it a first-class citizen of the editor, and for keeping the provider seam doc 20 asked
for so that git remains a supported answer for the projects that want it.

## What Lore actually is

Read the [system design](https://epicgames.github.io/lore/explanation/system-design/) for the whole
of it. Five facts decide everything below.

**1. It is centralized, and it is offline.** The remote is the source of truth for durability, access
control and conflict resolution. *Staging, committing, branching, switching and diffing never require
a round-trip.* Push and sync reconcile by compare-and-swap on the remote's branch pointer. This is the
fact that makes an editor integration tolerable: the status column does not need a network, and the
one thing a laggy connection can ruin is the one thing users already expect to be slow.

**2. Content is addressed, chunked and lazily fetched.** A *fragment* is bytes under a 48-byte
address — a 32-byte BLAKE3 hash plus a 16-byte *context* tag that carries file identity, so a moved
file deduplicates against itself. Large files are split by FastCDC into 32–256 KiB chunks; fragment
reference lists are themselves chunked recursively. A *view* (`.lore/view`) is a glob-based inbound
filter declaring which paths materialise to disk at all. **There is no LFS to bolt on and no
`.gitattributes` to get wrong**, which deletes roughly a third of what a git integration would have
had to teach the user.

**3. The client is native.** The canonical interface is a C header (`lore-capi/lore.h`) over a Rust
library. [`LoreVcs`](https://github.com/EpicGames/lore-dotnet) 0.8.6 is on nuget.org with a fluent
C# API and per-RID native payloads: `LoreVcs.runtime.osx-arm64`, `.win-x64`, `.linux-x64`. ⚠ **Those
three and no others** — no `osx-x64`, no `linux-arm64`, and nothing that could run in a browser or on
a phone. It is also pre-1.0, and its own README says on-disk formats and APIs may change between
releases.

**4. The vocabulary is not git's, and the differences are the point.** Branches are named pointers
with a stable UUIDv7 identity underneath, so renaming does not disturb history. `sync` is `pull`.
There is *no* `HEAD` — there is a per-branch *latest*. Files carry typed metadata and declared
dependencies. There is a server-recorded, file-level `lock`. And a *link* mounts another repository's
revision at a path, each link its own partition with its own access control, which is how Lore does
per-directory permissions.

**5. Diffs and conflicts are computable server-side, without a working copy.** `ThinClientService`
exposes `RevisionTree`, `RevisionDiff` (streaming `DiffChange` / `DiffConflict`, with an `autoresolve`
flag) and `ContentDiff`. The editor does not need this — it has a working copy. It is recorded here
because it is what makes the companion plan (Forge) possible at all, and because a future *review a
change request without syncing it* panel is a client of it rather than a new subsystem.

### Two gaps worth naming before anyone plans around them

⚠ **The CLI reference lists no `rebase` and no `squash`,** though the glossary defines both. What
exists is `revision cherry-pick`, `revision revert`, `revision restore` and `branch merge`. Any
milestone below that assumed a linear-history workflow is assuming a command that is not shipped.

⚠ **Locking informs; it does not block.** The FAQ is explicit — *"the current implementation informs
rather than blocks"* — and scaling it to millions of files is a 2026 roadmap item still in progress.
Everything in [Part D](#part-d--asset-locking) is built on an advisory signal, and the editor must say
so in the words the user sees.

## The shape: a seam Lore fills, not a Lore-shaped editor

Doc 20 asked for a provider interface. It gets one — but not the one that would have been written
from git.

**A provider interface designed against git is designed against the weaker system**, and the two
capabilities that make Lore worth adopting are exactly the two git has no word for: a server-recorded
lock, and a working tree that does not contain the whole repository. An `ISourceControlProvider`
whose shape is `Add/Commit/Push/Pull/Branch/Diff` forces both into out-of-band extensions, which is
how integrations end up with a `ProviderSpecificCommand(string)` escape hatch and no design at all.

So: **the interface is the union, and a provider declares what it does not have.**

```csharp
[Flags]
public enum SourceControlCapabilities {
    None            = 0,
    Locking         = 1 << 0,   // Lore: yes (advisory). Git: no.
    SparseView      = 1 << 1,   // Lore: yes. Git: sparse-checkout, partially.
    LazyHydration   = 1 << 2,   // Lore: yes. Git: no (LFS is not this).
    FileMetadata    = 1 << 3,   // Lore: yes. Git: no.
    ProtectedBranch = 1 << 4,   // Lore: `branch protect`. Git: server-side only.
    Rebase          = 1 << 5,   // Lore: ⛔ not in the CLI. Git: yes.
}
```

The editor asks before it offers. A verb whose capability is absent is **greyed with the provider
named in the reason** — the pattern [20 § B7](20-editor-parity.md#b7--build-deploy-and-extend)'s
device-manager row already establishes, and the same one the plugin manager uses for the two
switches. A dialog that appears and then fails is worse than a menu line that explains itself.

⚠ **The interface is asynchronous and reports progress, at every entry point.** `LoreVcs`'s own shape
is `Lore.RepositoryClone(globals, args).Callback(handler).Wait()` — a callback stream of typed events
with a blocking join. A clone of a real game project is minutes of network. Every provider method
takes a `BackgroundTask` from `Vixen.Editor.Ui`'s `BackgroundTaskManager` and reports into it, which
is machinery that already exists and already renders in the Task Center.

## Where the code lives

| Project | What is in it |
|---|---|
| `Editor/Vixen.Editor.SourceControl` | `ISourceControlProvider`, the capability flags, the status model, the registry, and every panel and command. No provider. |
| `Editor/Vixen.Editor.SourceControl.Lore` | The `LoreVcs` implementation. The only project in the repository that references a native payload it did not build. |
| `Editor/Vixen.Editor.SourceControl.Tests` | Contract tests every provider must pass, run against a fake and against a real `loreserver` in a container. |

**Not a plugin, and the reason is a gap this document owes doc 36 rather than a disagreement with
it.** [36](36-an-extensible-editor.md) is right that built-ins should go through the front door a
third party uses, and four of the five things this feature needs are already there:
`PluginContext.AddCommand`, `AddPanel`, `AddMenuItem` and `OnUnload`. The fifth is not. **There is no
extension point that decorates a Project Browser row** — no overlay icon, no status column, no
tooltip contribution — and the status column is the single most-used surface in the whole feature.

So this document adds one, and it is a general one rather than a source-control one:

```csharp
// Vixen.Editor.Core
public interface IAssetDecorator {
    AssetDecoration? Decorate(in AssetEntry entry);
}
public readonly record struct AssetDecoration(
    StandardIcon? Overlay, ColorRgba? Tint, string? Column, StringId? Tooltip);
```

registered through `PluginContext.AddAssetDecorator` and undone with everything else on unload.
Source control is its first client; an importer that wants to mark a stale artefact is its second.
⚠ **The decorator is called on the UI thread while a row is drawn, so it reads a snapshot and never
does I/O** — the status model is refreshed on a background task and swapped in whole, which is also
what keeps a slow provider from making the browser scroll badly.

With that in place the Lore provider is a plugin on paper. It stays in-tree anyway, because a
plugin carrying a per-RID native payload and a ~40 MB download is precisely the reach
[38 § the plugin question](38-learned-terrain-generation.md) says nobody has tested, and betting the
editor's first source-control integration on it would test two unproven things at once. **The
registration path is the plugin path; only the shipping vehicle is not.** When a second provider
arrives from outside, the front door has already been walked through by the built-in.

## Part A — the twelve features

Every row names the Lore surface it is built on. `lore …` is the CLI reference; `lore_…` is the C API
that `LoreVcs` wraps, which is what the provider actually calls.

| # | Feature | Lore surface | Editor surface | M |
|---|---|---|---|---|
| 1 | **Repository initialization** | `lore repository create` / `lore_repository_create` | *File ▸ Source Control ▸ Initialize…*, and a checkbox on New Project. Generates `.loreignore` and `.lore/view` from the project template ([Part B](#part-b--the-five-invariants)) | M1 |
| 2 | **Clone** | `lore clone` / `lore_repository_clone` | The Open Project dialog grows a *From source control…* tab. A `BackgroundTask` with real byte progress from the clone event stream | M2 |
| 3 | **Staging** | `lore stage`, `lore unstage`, `lore file stage-move`, `lore dirty` | Checkboxes in the Changes panel; *Stage* on the browser context menu; a rename in the browser becomes `lore_file_stage_move` rather than a delete-plus-add | M1 |
| 4 | **Commits** | `lore commit`, `lore revision amend` | The Changes panel's message box, with amend on the last local revision. ⚠ Amend is offered only while the revision is unpushed, because the remote's latest is a compare-and-swap target | M1 |
| 5 | **Push / pull** | `lore push` (`lore branch push`), `lore sync` (`lore revision sync`) | Toolbar with an ahead/behind badge. A push that loses the CAS race reports *the remote moved* and offers sync, not a retry loop | M2 |
| 6 | **Branches** | `lore branch list/create/switch/info/archive/protect` | A Branches panel: list, create-from, switch, archive. Protected branches are shown as such and the commit button says why it is disabled | M2 |
| 7 | **Conflict resolution** | `lore_branch_merge_start` and the seven that follow it | [Part C](#part-c--conflict-resolution) | M3 |
| 8 | **Authentication** | `lore auth login/info/list/logout/clear`, `lore_auth_login_interactive` | [Part F](#part-f--authentication) | M2 |
| 9 | **Asset locking** | `lore lock acquire/status/query/release` | [Part D](#part-d--asset-locking) | M3 |
| 10 | **Large asset sync** | Nothing. It is the storage model. | [Part E](#part-e--large-assets) | M2 |
| 11 | **History** | `lore history` (`lore revision history`), `lore file history` | A History panel over the repository, and *Show History* on any asset — which is `lore_file_history` and therefore cheap enough to be a context-menu line rather than a report | M3 |
| 12 | **Diffs** | `lore diff`, `lore file diff`, `lore revision diff`, `lore branch diff` | [Part C](#part-c--conflict-resolution), and the asset-kind-aware viewers | M3 |

Two rows in that table are the ones a game team will judge the feature by, and neither is the one an
engineer would guess: **9**, because it is the only thing that stops two artists ruining a Tuesday,
and **10**, because it is the reason they left the tool they were on.

## Part B — the five invariants

These are the things that are silently wrong if nobody writes them down, and four of the five produce
a project that works on the machine that made it.

**1. A `.meta` travels with its asset, atomically or not at all.** `hero.png.meta` carries the GUID
every reference in the project resolves through. Staging `hero.png` without it hands the next person
an asset the database will assign a *new* GUID to on import, and every reference to it breaks — in
scene files, silently, as a null. So: **the provider stages a sidecar pair as one operation**, the
Changes panel shows the pair as one row, and unstaging either unstages both. The one legitimate
exception — a `.meta` edited alone, because an import setting changed — is a distinct row kind and is
labelled *import settings*.

**2. `Library/` is never staged, and the `.loreignore` is generated rather than authored.** This
repository's own `.gitignore` spent two paragraphs on why `Library/` needed `!Raven/Library/` beside
it and why a bare `Build/` line ate the build system on a case-insensitive filesystem. A project
template writes the `.loreignore`; *Source Control ▸ Diagnose Ignore Rules* prints what each rule
excludes and how many files it matched, so a rule that matches nothing and a rule that matches
everything are both visible. ⚠ **The generated file is checked in and then owned by the user** — it
is regenerated only on explicit request, with a diff, because a tool that silently rewrites a file a
team has tuned is a tool people stop trusting.

**3. Sync is followed by an import, and the editor must not read the world in between.** A sync
rewrites files under `Assets/` behind the asset database's back. The provider raises
`WorkingTreeChanged(paths)`, `AssetDatabase` rescans exactly those paths, and open documents whose
asset changed on disk take doc 11's existing *changed outside the editor* path — the same one an
external tool already triggers. **Nothing here is new machinery**; the requirement is that the
provider never returns from a sync before the rescan has been queued, or the first thing the user
does is act on a stale tree.

**4. A sparse view means GUIDs that resolve to nothing, and that is not corruption.** `.lore/view`
can leave whole directories unmaterialised. The asset database then has references to files that are
not on disk — which is exactly the shape of a *missing asset*, and reporting it that way would make
the editor scream at a correctly-configured workspace. `AssetIssueKind` gains `NotMaterialised`, the
browser draws it as a distinct greyed state with a *Fetch* action, and it is not an issue in the
`ScanReport`. ⚠ **The default view for a new project is everything**, and narrowing it is a deliberate
act with a panel of its own; a sparse-by-default editor would be the single fastest way to make Lore
look broken.

**5. Dirty belongs to the filesystem, not to the editor.** Lore's own model is that the filesystem is
ground truth for dirty detection, and that *staging does not produce fragments; committing does*. The
editor must not try to be clever: it does not mark a file dirty because a document has unsaved
changes (it has not been written yet), and it does not un-mark one because it thinks it wrote the same
bytes back. Status comes from `lore_repository_status`, on a background task, coalesced.

## Part C — conflict resolution

Lore's merge is a state machine and the C API hands the whole of it over, which means the editor
implements a UI rather than an algorithm:

```
lore_branch_merge_start        →  enter the merge; conflicts enumerated
lore_branch_merge_resolve      →  a path resolved with a caller-supplied result
lore_branch_merge_resolve_mine    /  …_theirs   →  the two one-click answers
lore_branch_merge_unresolve    →  take one back
lore_branch_merge_restart      →  begin again from the base
lore_branch_merge_abort        →  leave, working tree restored
lore_file_stage_merge          →  stage a merged file
lore_file_reset_to_last_merged →  discard an edit made during the merge
```

The panel is a list of conflicted paths with three resolvers, picked by what the asset *is* rather
than by what its bytes are:

- **Text** — `.vxscene`, `.vxmat`, `.vxcompositor`, `.rvn`, `.cs`, `.vxml`, `.vcss`. A three-pane
  merge view. Lore does an ordinary three-way merge over the common ancestor for text, so the
  conflicts arrive already narrowed to the hunks that actually collide.
- **Binary with a thumbnail** — textures, models, audio. Mine / theirs, side by side, using
  `ThumbnailCache` for the preview and the importer's own metadata (dimensions, format, poly count,
  duration) for the numbers. There is no third option and the panel does not pretend there is.
- **Anything else** — mine / theirs with file size and modification time, which is the honest floor.

⚠ **A semantic merge of two `.vxscene` files is out of scope, and the reason is doc 21.** A merge
that understood entities and components would be a good feature and it is the *same* feature as
Multi-User Editing's operational transform, built twice from opposite ends. Doc 21 already decided
that intent replicates better than diffs; a scene-aware merge here would be diffs pretending to be
intent, and the version of it that works is the one that arrives when doc 21's model does. What text
merge gives us in the meantime is genuinely good, because doc 08 made the scene format text with
stable GUID references specifically so that it would be.

⚠ **The conflicted set is enumerated once, at `merge_start`.** A file that becomes conflicted because
the user edited it during the merge is a different thing, and `lore_file_reset_to_last_merged` is what
answers it. The panel must not silently re-enumerate, because a list that grows while somebody is
working down it is a list nobody finishes.

## Part D — asset locking

This is the feature the tool is adopted for and the one Lore is weakest at today. Both halves of that
sentence go in the UI.

`lore_lock_file_acquire / _status / _query / _release` map to `LockService`, whose `Resource` is
`(branch, hash, description)` and whose `Lock` carries `owner` and `locked_at`. Query is by branch, by
owner, or by description, which is enough for a *Locks* panel that answers *what is Sarah holding*
and *who has this file* without a scan.

**Check-out on edit, for the asset kinds that cannot merge.** Opening a texture, a model or an audio
file for editing acquires a lock; the Project Browser draws the overlay from `IAssetDecorator`; the
lock is released on commit or on explicit release. Text assets are never auto-locked — a lock on a
`.vxscene` costs a team the thing that makes text scenes worth having.

⚠ **Lore's lock informs; it does not block.** Two people *can* both hold an edit on the same file and
Lore will let them. So the editor's own words are *"Sarah has this checked out"* with a *Break lock*
that names who it takes it from and asks twice — never *"locked"* unqualified, which promises an
enforcement that does not exist. When Epic's scalable enforcing lock lands (2026 roadmap, in progress),
the capability flag gains an `Enforcing` bit and the wording follows it. **The wording is a test**, not
a convention, because this is exactly the kind of string that gets softened in a later refactor.

⚠ **A lock is per-branch.** The `Resource` carries `branch` bytes. Two people on two branches are not
in conflict and the panel must not claim they are — which also means the *Locks* panel has a branch
filter defaulting to the current one, and the cross-branch view is the expensive query it looks like.

## Part E — large assets

**There is nothing to build, and that is the finding.** Chunked content-addressed storage with
on-demand hydration is what Lore *is*; there is no LFS, no pointer file, no `.gitattributes`, no
`smudge`/`clean` filter and no track-this-extension decision for a user to get wrong. A 400 MB `.fbx`
is a file.

Two things follow that do need building, and both are small:

**Hydration on demand meets an importer that needs bytes.** A file inside the view but not yet local
is fetched when read. The import pipeline reading forty of them serially turns a background import
into a network stall with no explanation. So `ImportContext` gains a *materialise* step that asks the
provider for the whole batch up front, reports it as one `BackgroundTask` with a byte count, and only
then imports. ⚠ This is the one place the provider seam reaches into `Vixen.Editor.Assets`, and it is
capability-gated on `LazyHydration` — a git provider's implementation is `return`.

**A size guard on staging.** Staging a 2 GB file is legitimate here in a way it is not under git, so
the guard is not a refusal — it is the Changes panel showing what a commit weighs before it is made,
and a warning above a project-configured threshold. The number that matters to a team is the one they
are about to push, and no other tool shows it to them.

## Part F — authentication

`lore auth login` opens a browser against an auth service reached by a URL with its own scheme
(`ucs-auth://auth.example.com`), and the flow is the ordinary device-code one: the service returns a
`login_url` and a `session_code`, the client polls until the user has finished. Tokens are JWTs
carrying `resources: [{ resource_id: "urc-<repository-id>", permission: [...] }]`, and the Lore server
verifies them against a JWKS endpoint it is configured with.

For the editor this means three things and no more:

1. **The editor never handles a password.** It calls `lore_auth_login_interactive`, opens the URL the
   event stream hands it through the platform's default browser, and shows a *waiting for the
   browser* task with a cancel. Non-interactive sign-in (`--token-type api-key`) exists for CI and is
   surfaced only in the CLI.
2. **Credentials are Lore's to store, not ours.** `lore-credential` already uses the OS keychain.
   The editor calls `lore_auth_list` to render *who am I on which remote* and
   `lore_auth_logout`/`_clear` for the two ways out. ⚠ **We do not add a `Vixen` keyring entry**,
   because two stores means one of them is stale and the user cannot tell which.
3. **A 401 mid-operation is a re-login prompt, not an error toast.** Tokens expire during long
   operations; a push that fails after eight minutes of upload because a token aged out and then
   discards the upload is the worst possible failure. The provider catches unauthenticated, prompts,
   and resumes — which content addressing makes nearly free, because the fragments already uploaded
   are already there.

Where the auth service comes from is the companion plan's problem. **Forge is that service**; a team
that does not want to run one points at any JWKS-issuing IdP the Lore server will accept.

## Part G — milestones

Effort in engineer-months, at this repository's usual density.

| M | Name | Contents | EM |
|---|---|---|---|
| **M1** | **Local Lore** | The three projects; `ISourceControlProvider` and the capability flags; `IAssetDecorator` in `Vixen.Editor.Core` and the browser's use of it; init, status, stage, commit, ignore-file generation; the Changes panel; invariants 1, 2 and 5. **No network at all.** | 1.6 |
| **M2** | **The remote** | Auth; clone; push; sync; branches; the ahead/behind badge; the hydration batch and the size guard; invariants 3 and 4. The contract test suite runs against a containerised `loreserver`. | 1.8 |
| **M3** | **The hard half** | Merge and the three resolvers; locking and the Locks panel; history (repository, branch and per-file); the diff viewers. | 2.2 |
| **M4** | **The second provider** | A git provider, written against the same interface by someone who did not write the interface. This is the milestone that proves M1's seam, and it is scheduled last on purpose. | 1.0 |
| | | | **6.6** |

⚠ **M1 has no network and that is a deliberate sequencing choice**, not a phasing convenience.
Everything users touch hourly — status, stage, commit, the browser column — is offline in Lore's
design, so it is buildable and testable with no server, no auth and no flake. If the plan is cut, it
is cut after M2 and the result is a usable single-team tool.

The cut list, ordered in advance: **M4** first (the seam is designed either way, and a git provider
that nobody has asked for is the cheapest thing to defer), then **M3's history panel** (`lore history`
in a terminal is a real answer), then **M3's cross-branch lock view**. Merge and locking are not on
the cut list; a source control integration without them is a demo.

## Part H — what this costs us, honestly

**A native dependency on a pre-1.0 library, on three RIDs.** `LoreVcs.runtime.*` covers `osx-arm64`,
`win-x64` and `linux-x64`. The editor already ships to exactly those three, so today this costs
nothing — but it makes the set *load-bearing*, and an editor on `linux-arm64` now needs Lore to have
built one. ⚠ **The provider assembly must be optional at load**, not a hard reference from
`Vixen.Editor.App`: a missing native payload greys the Source Control menu with the RID named and the
editor starts. The alternative is an editor that will not open on a platform whose only fault is that
a third party has not built for it.

**Pre-1.0 means the format can move.** Lore's own docs say interfaces and on-disk formats may change
before 1.0. The mitigation is the version check the plugin loader already models: the provider records
the `lore_version()` it was built against, refuses a library whose major differs, and warns on a minor.
It is not a mitigation for a repository written by 0.8 and read by 0.9 — that is Lore's promise to
keep, and it is one of the four things [15](15-risks-and-open-questions.md) should carry as a risk we
do not control.

**Two systems that both think they own the working tree.** The editor writes files; so does sync.
Invariant 3 is the whole answer and it is thin — one event, one rescan, one existing reload path. It
is also the most likely source of the first real bug, so the contract test suite includes *sync while
a document is open and modified* as a named case rather than leaving it to be found.

**A second network stack in the editor process.** `LoreVcs` brings QUIC and gRPC in native code. It
does not share the engine's transport, its threads or its telemetry, and it will show up in a profile
as time nobody can attribute. `lore_log_configure` routes Lore's own log into a callback on the first
call, so its events land in the editor's Console with a source of `lore` — cheap, and the difference
between a hang somebody can diagnose and one they report as *the editor froze*.

## What this amends

- **[20 § B7](20-editor-parity.md#b7--build-deploy-and-extend), Source control** — ⛔ becomes M1–M3
  above. The row's *"with a git implementation"* becomes *"with a Lore implementation, and a git one
  at M4"*, and the reason for the swap is [the problem statement](#the-problem-stated-from-evidence).
  Its P2 priority stands.
- **[21 § Part A](21-realtime-collaboration.md)** — both rejections stand and the reasoning that
  supported them is corrected. *"Vixen has no SCM integration and should not grow one here"* was a
  statement about where a feature belongs, and it was right; *"git … is better at it than this would
  be"* was a statement about a tool, and it is now only true for the text half of a project.
- **[08](08-asset-pipeline-and-addressables.md)** — the `.meta` sidecar becomes a *source-control
  atom* rather than a convention. Invariant 1 is a rule the pipeline now has to state, because a
  sidecar staged apart from its file is silent data loss and nothing in doc 08 currently says so.
- **[36](36-an-extensible-editor.md)** — gains `IAssetDecorator` and `PluginContext.AddAssetDecorator`,
  which is a Project Browser extension point doc 36's audit did not have and which its second client
  (a stale-artefact marker) needs anyway.
- **[11](11-editor.md)** — the *changed outside the editor* path acquires a second caller. No change
  to it; the requirement is that it stays the only one.
