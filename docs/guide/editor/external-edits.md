---
title: Edits made outside the editor
slug: editor/external-edits
kind: guide
area: Editor
summary: What happens when a file the editor has open is changed by something else, and why the editor's own saves do not count.
api: [T:Vixen.Editor.Core.ExternalEdits, T:Vixen.Editor.Core.ExternalEdit, T:Vixen.Editor.Core.ExternalEditOutcome]
tags: [editor, documents, file-watching, hot-reload, undo]
since: 0.1
status: preview
related: [editor/index, editor/editing-pipeline, editor/frame-panel]
---

## What it is

`ExternalEdits` is the last few metres of the editor's file watcher: the object that turns "a path
under `Assets/` changed" into "this open document's file changed", and then decides what to do about
it. `ExternalEdit` is one document and what was decided; `ExternalEditOutcome` is the four answers —
`Reloaded`, `Kept`, `Unsupported`, `Failed`.

It is also the outward half of the same question. `EditorProject.DocumentSaving` fires *before* a
document writes itself back, and `ExternalEdits` is what turns that into `IFileWatcher.Suppress`, so
the editor's own saves never arrive back through the watcher as somebody else's edit.

The two document-side pieces are `EditorDocument.CanReload` and `EditorDocument.Reload`, with
`EditorDocument.IsStale` as the flag for a document that has not taken a change and has not refused
it either.

## What it is for

Everything else the editor hangs off the watcher reads the drained change list for its *length*. The
project browser rescans, the asset database rescans, the build panel refreshes; only `ReloadShaders`
looks at the paths at all, and it filters to `.rvn`. So a `.vxscene` or a `.vxcompositor` saved by a
text editor beside the running Vixen reached the tree, the GUID index and the build — and did not
reach the panel that had it open. This is that wire.

**And the reason a wire was not enough.** Reloading a document is destructive in one direction and
lying is destructive in the other, and the mechanism cannot choose between them:

- A document with **no unsaved edits** is reloaded, silently. What was in memory was the file's
  previous contents and nothing else, so nothing is lost and nobody needs to be asked.
- A document with **unsaved edits** is not reloaded. It is marked `IsStale`, reported, and left
  exactly as it is. The asymmetry decides it: an edit that exists only in this process is gone the
  moment it is overwritten, and a file on disk is not — so the reversible choice is to keep both
  copies and let a person pick. That is the same trade `EditorFrames.Reframe` makes when a rebuilt
  frame will not build: prefer the state somebody can still act on.

⚠ **Staleness is not dirtiness, and folding them together would be a data-loss bug.** Dirty means
memory is ahead of disk; stale means disk is ahead of memory. `EditorProject.SaveAll` writes every
dirty document — so a stale document that counted as dirty would have the editor's copy written over
the external edit by one Ctrl+Shift+S.

⚠ **A deleted file is not a reload.** `AssetFile.Read` answers a missing file with an empty string,
which is right for opening an asset somebody has just created and catastrophic for re-reading one.
A document whose file was deleted keeps what it has — that being the only copy left — and saving is
how it comes back.

You do not want `ExternalEdits` for a *derived* file. An artefact under `Library/` that the import
pipeline rewrites is not a document and has no undo stack; suppressing and reimporting is the asset
pipeline's own business.

## Using it

Construct one per project beside the watcher, and hand it the drained changes after the database has
rescanned.

```csharp no-compile="a fragment — the watcher, the project and the drain loop are the application's"
using var edits = new ExternalEdits(project, watcher);

edits.Applied += edit => {
    if (edit.Outcome == ExternalEditOutcome.Kept) {
        Notify($"'{edit.Document.Title.Peek()}' changed on disk and has unsaved edits.");
    }
};

// Once a frame, after the rescan.
changes.Clear();
watcher.Drain(changes);

if (watcher.HasOverflowed) {
    watcher.ClearOverflow();
    edits.Rescan();
} else {
    edits.Apply(changes);
}
```

**After the rescan, not before.** A path becomes a document through the GUID index, and a rename is
exactly the change that moves an entry in it — so routing before the scan looks the new path up in an
index that still holds the old one and finds nothing open. It is the only ordering constraint in the
whole seam.

**An overflow calls `Rescan`, not `Apply`.** Lost events mean the drained list cannot describe what
changed, so every reloadable clean document is re-read — `ReloadShaders` makes the same choice for
the same reason. What `Rescan` deliberately does *not* do is mark dirty documents stale: an overflow
says events were lost, not that this file changed, and a prompt that is usually wrong is one that
gets dismissed without reading.

A document type opts in by answering `CanReload` and overriding `ReloadCore`:

```csharp no-compile="a fragment — AssetPath and Replace are CodeDocument's"
public override bool CanReload => true;

protected override bool ReloadCore() {
    Replace(AssetFile.Read(AssetPath));
    return true;
}
```

**`CanReload` is `false` on the base class and that is a real answer.** Most documents read their
file once, in a constructor, and re-reading one means rebuilding whatever the constructor built. A
base class that claimed every document could re-read itself would be one whose `Reload` silently did
nothing for most of them — and a prompt offering to discard somebody's edits for a document that
would then decline is worse than no prompt.

**`Reload` clears the undo history, and it has to.** Every entry describes an edit to the file's
previous contents; undoing into a document that no longer has the members those entries name is how a
reload turns into a corruption. It then marks the stack clean, because a document that has just
re-read its file *is* the file — `CommandStack.Clear` alone would leave a discarded-and-reloaded
document claiming to differ from something it is identical to.

**`ReloadCore` should keep what it has when the file will not parse.** `StandardFrameDocument` does:
opening a broken frame has to produce something for the panel to draw, but reloading over a document
already on screen does not, and a tool that writes a file in two passes would otherwise blank the
panel on the first of them. Return `false`, and the document stays stale so the next change tries
again.

## Examples

Two implementors ship: `StandardFrameDocument`, so a `.vxcompositor` edited in a text editor moves
the viewport it is driving, and `CodeDocument`, which covers `.rvn`, `.vxml` and `.vcss`.

Answering a stale document is the two things a person was going to do anyway — there is no third verb:

```csharp no-compile="a fragment against an open document"
// Take the version on disk, discarding the unsaved edits.
document.Reload();

// Or keep this one, and make it the version on disk.
document.Save();
```

In the editor those are `file.save` and **File ▸ Revert to Saved** (`file.revert`), which is enabled
for a document that is dirty or stale and asks before it discards anything. The notification the head
posts when a document is kept names both.

⚠ **What is not built is the banner** — the offer sitting across the document itself rather than in
the corner of the window, so that the choice is made where the conflict is. That is a panel and not a
mechanism; everything it would need is already public.

## See also

- [The editor shell](index.md) — where a notification about a stale document is shown.
- [The editing pipeline](editing-pipeline.md) — the undo stack a reload throws away, and why.
- [The frame panel](frame-panel.md) — `StandardFrameDocument`, the first document to implement
  `ReloadCore`, and the live-apply path a reload arrives through.
