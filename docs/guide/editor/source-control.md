---
title: The source-control service
slug: editor/source-control
kind: guide
area: Editor
summary: What a plugin can ask the project's version control by asking for ISourceControl — the status of a path, a file's history and diffs, and Revert and Restore — and when its answers arrive.
api: [T:Vixen.Editor.Assets.ISourceControl, T:Vixen.Editor.Assets.SourceControlStatus, T:Vixen.Editor.Assets.SourceControlRevision, T:Vixen.Editor.Assets.SourceControlDiff]
tags: [editor, assets, source-control, git, plugins, services]
since: 0.1
status: preview
related: [editor/index, editor/writing-a-plugin, editor/content-library, editor/project-session]
---

## What it is

`ISourceControl` is the editor's source-control context as one service. The editor publishes it
through `PluginServices`, and the content browser's status column, Assets ▸ Revert to Source Control,
Assets ▸ Show History and the Revisions panel all use the same object:

| Member | What it does |
|---|---|
| `Name` / `IsSought` | The provider's name (`"Git"`), or null. Once `IsSought` is true, a null name means the project is not in a working tree. |
| `IsKnown` / `Status(path)` | Whether a status sweep has landed, and what it said about a project-relative path. A folder takes the strongest status under it. |
| `Sweep()` / `Swept` | Asks for a fresh sweep of the whole working tree, and the event raised when one lands. |
| `HistoryAsync(path, limit)` / `DiffAsync(path, revision)` | A file's commits, newest first, and what changed at one of them. An empty revision means the working tree's own changes. |
| `Revert(paths)` / `Restore(path, revision)` / `Reverted` | Throw a file's changes away, or write an old version over it. `Reverted` is raised when the project has been rescanned afterwards. |

`SourceControlStatus` has six answers plus `Unknown`. `Unknown` draws nothing, and it is what every
path says before the first sweep and in a project with no provider. ⚠ It is never rounded down to
"unmodified": a status column that is sometimes right is worse than none.

`SourceControlRevision` carries both the full id, which is what `DiffAsync` and `Restore` take, and
the short one a person reads. `SourceControlDiff.IsText` says whether `Text` is a patch, or a
sentence about a binary file and how its size changed.

## When the answers arrive

The provider is found lazily, on the thread pool, by the first sweep. The editor takes that sweep
when it starts, and another on every [`IProjectSession.Rescanned`](project-session.md): when the file
watcher drains, on Refresh, and after a revert. So `IsKnown` does not depend on any panel being open.

⚠ **Every provider call runs on the thread pool.** Git is a process, and it can take seconds on a
cold cache or a locked index. `Swept` and `Reverted` are raised on the frame thread.
`HistoryAsync` and `DiffAsync` complete wherever the provider finishes, so a module that touches its
UI with the answer has to get back onto the frame thread first.

⚠ **`Revert` and `Restore` do not ask, and neither can be undone.** Both write files on disk, and the
undo stack holds documents. The editor's own commands ask first and name the files in the question.
A script putting back files it generated has nobody to ask. `Restore` leaves an ordinary uncommitted
change behind, and `Revert` undoes it.

What it deliberately does **not** do:

- **Commit, branch or pull.** Those are a git client's job. The editor reports the working tree and
  puts single files back.
- **Watch or poll.** If a cache refreshed on a timer, an external checkout would leave it wrong. The
  sweep follows the rescans instead, since those are already the moments the project changed.
- **Take a provider from a plugin.** Git, through the command-line client, is the only provider. The
  provider seam stays internal until a second one is wanted.

## Using it

Ask for it in `Activate`, the way any module asks for a service:

```csharp no-compile="a fragment of a module's Activate — the command is the plugin's"
public void Activate(PluginContext context) {
    var sourceControl = context.Services.Require<ISourceControl>();
    var project = context.Services.Require<EditorProject>();

    context.AddCommand(
        new EditorCommand("mine.show-modified", new StringId("mine.show-modified", "Select Modified"), () =>
            project.Selection.Set(project.Assets.Entries
                .Where(entry => sourceControl.Status(entry.Path) == SourceControlStatus.Modified)
                .Select(entry => entry.Guid))) {
            Enablement = () => sourceControl.IsKnown
        });
}
```

## Examples

Following the sweeps, for a panel that draws its own marks:

```csharp no-compile="a fragment; `sourceControl` is the service and `marks` the plugin's view"
sourceControl.Swept += () => marks.Refresh();
```

## See also

- [The content library](content-library.md) — the same shape for the project's assets.
- [The project session](project-session.md) — the rescans a sweep follows.
- [Writing a plugin](writing-a-plugin.md) — `Require`, `Owns` and the registration scope.
