---
title: Collections in the project browser
slug: editor/collections
kind: guide
area: Editor
summary: A named set of assets kept with the project — why it holds ids rather than paths, and why it lives under ProjectSettings while a saved filter lives beside your keymap.
api: [T:Vixen.Editor.App.AssetCollections, T:Vixen.Editor.App.SavedAssetSet]
tags: [editor, project-browser, assets, collections, project-settings]
since: 0.1
status: preview
related: [editor/saved-filters, editor/index]
---

## What it is

A **collection** is a named set of assets that shows in the project browser's folder column, under a
**Collections** heading, and narrows the grid the way a folder does. `SavedAssetSet` is one of them —
a name and a list of `AssetId` — and `AssetCollections` is the settings asset holding all of them,
written to `ProjectSettings/AssetCollections.vxsettings` in the project.

Three gestures make and change one, all in the folder column beside the grid:

- **Drop a selection on the Collections heading** — asks for a name and makes one.
- **Drop a selection on a collection's row** — adds it.
- **Right-click a row** — New Collection…, Add Selection, Remove Selection, Forget Collection.

## What it is for

A folder is where a file *is* and a collection is what somebody is *working on*. The two rarely agree:
the eleven things being reviewed for a level are a mesh, three textures, a material, two prefabs and a
sound, and no directory tree puts those together without moving them somewhere they do not belong.

⚠ **It holds the result and a saved filter holds the query, which is the whole difference between the
two.** Doc 20 § B1 asks for both and they are the same storage question with opposite answers:

- A [saved filter](saved-filters.md) re-runs, so one named `Widgets` before `widget-b.png` was
  imported finds it afterwards.
- A collection is a set somebody assembled on purpose, so it keeps exactly what was put in it — and
  keeps it after those files have been moved, renamed and re-imported, because it stores `AssetId`s
  and that is what an id is for. A collection storing paths would break on the first move, which is
  the move a content browser exists to make cheap.

⚠ **And that is why only one of them is a preference.** `EditorPreferences` is *user-wide* — one file
across every project you open — and an `AssetId` means nothing in another project. So the collections
are a per-project settings asset, read and written through `ProjectSettingsStore` like every other
one, committed with `ProjectSettings/` and shared with whoever else has the checkout.

⚠ **`Library/` would have been the wrong per-project home even though it is the one that is not
committed.** Everything under it is reproducible from a source file plus its `.meta` — that is what
makes deleting it safe — and a collection is authored data nothing can rebuild.

⚠ **An id that no longer resolves is kept rather than pruned.** A file missing today is one somebody
may restore, or a branch they may switch back to; a collection that quietly emptied itself over a
checkout is worse than one showing fewer tiles than it lists.

## Using it

From code — a plugin, a test, or the application itself:

```csharp no-compile="a fragment — `collections` is the project's settings asset"
var collections = project.Settings.Get<AssetCollections>();

collections.Add("Level art", [meshId, textureId]);
project.Settings.Save<AssetCollections>();
```

`Add` makes the collection if it is not there, ignores an asset already in it, and answers whether
anything changed so a caller knows whether to write the file. `Remove` takes assets out and leaves the
collection; `Forget` drops the whole collection and **touches no file it named** — which is why the
menu line says Forget rather than Delete, the same word the saved filters use.

In the editor:

⚠ **The column is the grid's, not the panel's.** In list mode the browsing tree already shows the
folders, so the second column is hidden with the tile-size picker — switch to **Grid** to see the
shelf.

⚠ **Standing in a collection is not selecting it.** The verbs act on the project's selection, exactly
as they do for a folder row; a column that wrote to the selection would make walking around the
project change what Delete would delete.

⚠ **The search box narrows inside a collection.** The two views and the shelf share the search, the
kind filter, the selection and the verbs — a collection with a filter of its own would be a second
browser that disagrees with the first about what is in the project.

⚠ **Choosing a folder is the way out of a collection**, and deliberately the only one. The grid can
only be showing one thing, so a column marking a folder and a collection at once would be two answers
to what is on screen.

## Examples

From a test, through the harness, which drives the same code the menu and the drop do:

```csharp no-compile="a fragment — EditorSession is Vixen.Editor.Testing's"
editor.AddToCollection("Level art", widget);

// … and after the file has been moved on disk and the project rescanned:
Assert.Equal(["widget-a.png"], Shown(editor));
```

`BrowserCollectionTests` is the suite, and its central assertion is that move: a file is collected,
then moved with its `.meta` the way a file manager moves it, then found in the same collection
afterwards. A path-keyed store cannot pass that test — which is the point of writing it that way
round, and it is the mirror of the saved filters' central assertion, where a file imported *after* the
filter was named is found by it.

## See also

- [Saved filters in the project browser](saved-filters.md) — the other half of doc 20 § B1, and the
  same storage question answered the other way.
- [The editor shell](index.md) — the panels the browser sits among, and the two stores an editor
  writes to.
