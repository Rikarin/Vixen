---
title: Icons
slug: editor/icons
kind: guide
area: Editor
summary: Declaring what a type looks like with SVG path data, and the parser that made the attribute buildable.
api: [T:Vixen.Editor.Ui.EditorIconAttribute, T:Vixen.Editor.Ui.TypeIcon, T:Vixen.Editor.Ui.AssetIcon, T:Vixen.Editor.Ui.EditorArt, T:Vixen.Ui.SvgPath, T:Vixen.Ui.SvgPathException]
tags: [editor, icons, svg, plugins]
since: 0.2
status: preview
related: [editor/writing-a-plugin, editor/scene-menus]
---

## What it is

`SvgPath` reads SVG path data — the `d` attribute — into a `PathBuilder`. `SvgPathException` says
where it could not.

`EditorIconAttribute` is what a type carries to say what it looks like: `[EditorIcon("M12 2 2 22h20z")]`
on a component, a behaviour or an asset class. `TypeIcon` and `AssetIcon` are the registrations it
becomes; `EditorArt` is the lookup every surface uses to find one.

## What it is for

Doc 36 § D6 spelled this attribute out and nothing implemented it, for a stated reason: there was no
SVG path parser, and turning `M12 2L2 22h20z` into segments was held to belong to an asset pipeline
rather than to every application at start-up. There is a parser now and it is about a hundred and
fifty lines, which does not survive the argument it was resting on.

What that cost was concrete. Every icon set on earth — Material, Lucide, Feather, Fluent, Bootstrap,
Phosphor — ships a 24-square grid and a string per glyph, and the only way into the engine was to
transcribe one into `LineTo` calls by hand. A plugin's component had no picture in the outliner, the
inspector header, the Add Component list or the Project panel, and could not be given one without
writing geometry in C#.

⚠ **Every command in the grammar, including the two that are not lines or curves.** `H`/`V` are what
a rectangle is written with and `A` — the elliptical arc — is what a rounded corner is written with;
a reader that dropped either would render about a third of Material Symbols as a squashed polygon.
The arc is F.6.5 of the specification, converted to centre parametrisation and emitted as cubics.

## Using it

Two forms, told apart by the extension, because both are what people actually have. Anything ending
in `.svg` is a file relative to the declaring plugin's own directory — and is not allowed to leave
it. Anything else is the path data itself, which needs no file and no IO.

`Tint` takes `#rrggbb`, or a `--custom-property` to follow a retheme, or nothing at all — and nothing
is usually right. A component's icon sits on a row of text and wants that row's colour, including
when the row is selected and the background has gone dark under it; a literal is for the file-type
glyphs in a grid, where being scannable by hue is the whole job.

⚠ **A bad icon is a diagnostic and the plugin still loads.** Every other declaration a plugin can
carry throws when it is the wrong shape, and that is right for them — a `[CustomInspector]` on the
wrong kind of class makes the plugin's own code unreachable. An icon is decoration, and refusing to
load a terrain module because somebody fat-fingered a path string would be the editor holding a
feature hostage over a picture.

## Examples

Path data inline, which is what an icon copied out of a set looks like:

```csharp no-compile="the attribute is read by a scan of a loaded plugin assembly, which needs a shell"
[EditorIcon("M4 4h7v7H4zM13 4h7v7h-7zM13 13h7v7h-7zM4 13h7v7H4z", Tint = "#7cc4ff")]
public struct Health {
    public float Current;
    public float Maximum;
}
```

A file a designer drew, beside the plugin:

```csharp no-compile="as above"
[EditorIcon("Icons/stamina.svg")]
public struct Stamina {
    public float Current;
}
```

The parser on its own, for anything that wants geometry rather than an icon:

```csharp compile
using Vixen.Ui;

public static class Triangle {
    /// <summary>Four steps: a move, two lines and the close.</summary>
    public static int Steps() => SvgPath.Parse("M12 2 2 22h20z").Count;
}
```

`TryParse` is the same thing without the throw, for reading a string somebody else wrote.

## See also

- [Writing a plugin](writing-a-plugin.md) — the scan that reads the attribute, and the scope the
  registration lives in.
- [Scene menus](scene-menus.md) — one of the surfaces an `IconArt` is drawn on.
- `IconArt`, `IconPaint` — what an icon is once it has been read: several paths, each with its own
  paint, on a view box of its own.
