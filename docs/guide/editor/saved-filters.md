---
title: Saved filters in the project browser
slug: editor/saved-filters
kind: guide
area: Editor
summary: A named search of the project, kept beside the layouts and the keymap — and why it stores the query rather than what the query matched.
api: [T:Vixen.Editor.App.SavedAssetFilter]
tags: [editor, project-browser, assets, preferences, search]
since: 0.1
status: preview
related: [editor/index, editor/external-edits]
---

## What it is

`SavedAssetFilter` is one line on the project browser's **Filters** menu: a name, whatever was in the
search box, and whichever importer tag the kind dropdown was on. The list of them lives on
`EditorPreferences.AssetFilters`, which is written to `presets`' neighbour `preferences.yaml` in the
user's data directory — beside the layouts and the keymap rather than in the project.

The browser's filter bar carries the two controls a filter *is* — a `SearchBox` and a `Select` over
the importer tags the project actually holds — and the Filters button drops a menu over them with
three things on it: **Save Filter…**, the saved names, and **Forget Filter**.

## What it is for

A content browser answers "which of these is the rock" for as long as somebody can find the rock. The
searches that get typed twenty times a day in a real project are not one word — they are a word plus
a kind, retyped every time the box is cleared — and a filter that has to be reassembled is one people
stop using.

⚠ **It stores the query and never its result, and that is the whole difference between a saved filter
and a collection.** Doc 20 § B1 asks for both and they are the same storage question with opposite
answers:

- A **filter** re-runs. A filter named `Widgets` before `widget-b.png` was imported finds it
  afterwards, because what was kept was the word and not the two files it happened to match on the
  day it was named.
- A **collection** is a set of assets somebody put there on purpose, so it keeps `AssetId`s and
  survives a file being moved on disk. Storing paths would break on the first move; storing a query
  would not be a collection at all.

⚠ **The kind is stored as the importer tag and never as the dropdown's `All types` label.** A filter
that kept the label would come back as a filter for assets whose importer is called "All types" — one
that matches nothing, silently, because an empty grid is exactly what a narrow filter looks like.
Empty means every kind.

⚠ **A saved kind the project no longer holds falls back to every kind.** The dropdown offers the tags
the project actually has, so deleting the last texture takes `TextureImporter` off it; a filter naming
it then applies as a search alone. That is the same answer the dropdown already gives, and it is
better than a filter that hides everything with no way to tell why.

You do not want a saved filter for "the assets in this level" or "the ones I am working on this
week". Those are collections, and they are not built yet.

## Using it

The type is a plain record on the preferences object, so a plugin or a test can read and write the
list directly:

```csharp no-compile="a fragment — `preferences` is EditorApplication's own, which is not public"
preferences.AssetFilters.Add(
    new SavedAssetFilter { Name = "Textures", Search = "rock", Kind = "TextureImporter" }
);
```

In the editor it is three gestures and no typing beyond the name:

1. Set the search box and the kind dropdown to whatever you want kept.
2. **Filters ▸ Save Filter…**, and give it a name.
3. **Filters ▸** the name, in this session or in any later one.

⚠ **Save is disabled when nothing is set.** An empty search over every kind is the browser's resting
state, and a menu offering to name it would be offering to keep a row that does nothing when it is
applied.

⚠ **Applying one writes the bar rather than only the rows.** The search box shows the word and the
dropdown shows the kind, so the way out of a filter is the way out of any search — clear the box.
A filter that narrowed the browser while the bar sat empty would be a panel showing a fifth of the
project with nothing on screen saying why.

⚠ **A name that is already taken replaces rather than doubling the menu.** Somebody who saves
`Textures` twice has said what they mean by it; two identical lines is the answer nobody wants. It is
`CurvePresetLibrary.Save`'s decision restated, and **Forget Filter** is how one goes away.

⚠ **Nothing is applied by a restart.** What comes back is the *offer*. An editor that re-applied the
last filter on the way up would be one that hides most of the project until somebody works out why.

## Examples

From a test, through the harness, which drives the same code the menu does:

```csharp no-compile="a fragment — EditorSession is Vixen.Editor.Testing's"
editor.SaveFilter("Widgets", search: "widget", kind: string.Empty);

// … and the name is on the browser's Filters menu from here on, in this session and the next.
Assert.Equal("Widgets", Assert.Single(editor.AssetFilters).Name);
```

`BrowserSavedFilterTests` is the suite, and its central assertion is the re-run: a filter is named
while one matching file exists, a second is imported, and applying the filter finds both. A stored
result cannot pass that test, which is the point of writing it that way round.

## See also

- [The editor shell](index.md) — the panels the browser sits among, and the user store its
  preferences share.
- [Edits made outside the editor](external-edits.md) — the rescan a filter re-runs against when a
  file arrives from somewhere else.
