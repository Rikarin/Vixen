---
title: Strings and the catalogue
slug: ui/strings
kind: guide
area: Core
summary: A label is an id plus the English it was written as, so a missing translation shows the sentence rather than the id — and the catalogue in use is a signal, so a language change re-labels a running interface with no code at any call site.
api: [T:Vixen.Ui.StringId, T:Vixen.Ui.StringCatalog, T:Vixen.Ui.Strings, T:Vixen.Ui.Controls.ControlStrings, T:Vixen.Editor.Ui.StringCatalogYaml]
tags: [ui, localisation, strings, i18n, signals]
since: 0.2
status: preview
related: [ui/commands, ui/accessibility, editor/index]
---

## What it is

`StringId` is a pair: an id a catalogue calls the string by, and the source text it says. `Strings`
holds the catalogue in use and answers `Get`. `StringCatalog` is one language's worth of text, by
id — a flat map with no fallback chain in it.

```csharp no-compile="a fragment; the declaration class is whatever the application calls its own"
public static StringId Save { get; } = new("editor.command.file.save", "Save");
```

`Save.Text` is what a label is assigned. In the source language that is `"Save"`; with a Czech
catalogue in use it is whatever that catalogue has under `editor.command.file.save`, and if it has
nothing under that id it is `"Save"` again.

Three properties follow from the pair, and they are the whole design:

* **The fallback is in the source, not in a file.** An application whose English lives in an `en`
  catalogue shows `editor.command.file.save` to anybody whose install is missing that file — and a
  missing file is exactly what a localisation bug looks like. Here the worst case is English.
* **An id says where a string is used, not what it says.** `editor.menu.file`, not `file`. Two
  places that happen to both say "Open" can be translated differently where a language needs them
  to be, and a translator's file is diffable.
* **`Strings.Missing` is the worklist.** Every id asked for that the current catalogue did not have,
  in id order — gathered rather than logged, because a warning per missing string is a warning per
  menu rebuild. It is not recorded against the source catalogue, where every id is missing by
  construction.

## What it is for

Making localisation not a retrofit. `item.Label = EditorStrings.Save.Text` is no more work at the
call site than `item.Label = "Save"`, so there is never a reason to write the literal — which is the
failure that costs an application a sweep through every file it has, after the fact, looking for
words.

The second thing it is for is a language change that means something. `Strings.Catalog` reads a
`Signal<StringCatalog>`, and every `@expr` in a `.vxml` is a region-scoped effect — so an expression
that reads a string is a consumer of that signal without saying so. `Strings.Use` marks it dirty,
the document's next flush re-runs it, and the label changes. Nothing subscribes, nothing is rebuilt,
and no application writes a line of code for it.

⚠ **A label assigned once in C# is not an expression.** A control whose constructor writes
`Button.Label = ControlStrings.Close.Text` reads the signal outside any effect, so it shows whatever
language was in use when it was built — which is what the standard control set does today. Bind
through markup, or through `BuildContext.Bind`, where a label has to follow a language change on a
live element. `Strings.Changed` is the plain event a hand-built surface subscribes to in order to
rebuild itself whole; it is static, so a subscriber that outlives nothing must still unsubscribe.

The third thing is a translator's worklist that is a fact rather than a guess. `Strings.Missing` is
what the running application asked for and did not get, so a catalogue that claims to be complete
has a test that says so.

## Using it

Declare the ids in one static class per assembly, with an `All` list beside them:

```csharp compile
using Vixen.Ui;

public static class ShopStrings {
    public static StringId Buy { get; } = new("shop.action.buy", "Buy");
    public static StringId Cancel { get; } = new("shop.action.cancel", "Cancel");

    public static IReadOnlyList<StringId> All { get; } = [Buy, Cancel];
}
```

`All` is spelled out rather than reflected over: a list gathered by walking the fields at run time is
a list an application's trimming settings are entitled to shorten, and `Strings.Template` is what
turns it into a file a translator starts from.

Build a catalogue by hand, or read one from whatever format the application ships:

```csharp compile
using Vixen.Ui;

public static class Czech {
    public static void Use() =>
        Strings.Use(
            new StringCatalog("cs")
                .Set("shop.action.buy", "Koupit")
                .Set("shop.action.cancel", "Zrušit")
        );
}
```

⚠ **`StringCatalog` has no file format, deliberately.** It is `Set`, `Find`, `Ids` and `Count`, and
how a catalogue got its entries is the application's business. The editor reads and writes YAML
through `StringCatalogYaml`, in `Vixen.Editor.Ui`; an application publishing NativeAOT is free to
read JSON through a source-generated reader instead. Attaching a parser to the catalogue would put a
serialiser in the package closure of every application that shows a word, including the ones that
never load a catalogue at all.

### The control set's own words

`ControlStrings` is the standard control set's declarations, in exactly the shape above: eighteen
labels — "Clear" in a search box, "Dismiss" on a toast, "Previous tab" on a docked group, "Search" in
a property grid — with `All` beside them. Thirteen were English literals in control constructors
until doc 46 § A3, which meant a localised window had an untranslatable seam in the one place a user
cannot avoid looking.

⚠ **Five of them are never drawn.** A scroll bar, a colour picker's hex field and a gradient
editor's colour-space select have no caption on screen at all, so their only words are the ones a
screen reader says — and a literal there is an English announcement in a localised window that
nobody can see to report. `ScrollBar` reads its two in a *virtual* rather than in `OnCreated`, which
makes it the one name in the set that follows a language change on a control already on screen.

⚠ **Two ids for the two "Close"s.** A dialog's dismiss button and a dock tab's are the same English
word and are not the same string; a language that distinguishes closing a question from closing a
document needs to say so. That is what an id saying *where* a string is used buys, and merging them
cannot be undone without a translator's file changing shape.

⚠ **A control reads its labels in `OnCreated`**, so it shows the language that was in use when it was
built. Choose the language before building the interface. A control set that re-labelled itself would
need an effect per label and somewhere to dispose it; what is here today makes the words translatable
at all.

⚠ **A translated label is not by itself a translated control.** What a screen reader says comes from
[the accessibility tree](accessibility.md), and an accessible name is *computed*: `ButtonBase` answers
with its `Label`, so eleven of the thirteen declarations above reach a screen reader with nothing
written for them. The two that did not are the shape to watch for — a string that went to a
`Placeholder` (which is deliberately not a name) and a string on a caption element that nothing
related its slider to. Both showed the translation and announced nothing at all, and neither a
localisation test nor an accessibility test could see it, because each asserts its own half.
`AccessibilitySnapshot.Untranslated` is the assertion that spans them.

`Strings` is static, and it is the one service here that is. Every other is an instance a shell owns,
because a document may have two of them; a language is a property of the person using the
application rather than of a window, and threading a localiser through every control that shows a
word is the design that makes people write the literal instead.

## Examples

A template for a translator to fill in — every declared id, with its source text as the starting
value:

```csharp compile
using Vixen.Ui;

public static class Templates {
    public static StringCatalog For(string language, IReadOnlyList<StringId> declared) =>
        Strings.Template(language, declared);
}
```

A markup label that follows the language, which is the case the signal exists for — the whole of
what the application writes is the expression:

```vxml no-compile="one element of a sheet; the whole file is Core/Vixen.Ui.Controls.Tests/Markup/LocalisedSheet.vxml"
<Button ref="@Close" Label="@CloseText.Text" />
```

`Strings.Use(other)` between two frames re-labels that button on the second one. Nothing subscribes,
nothing is rebuilt, and no code in the application mentions the change.
`Core/Vixen.Ui.Controls.Tests/LocalisationTests.cs` is that sentence as an assertion.

Asserting a language is complete, which is the test a shipping catalogue earns:

```csharp no-compile="a fragment; `Catalogue` and `ShopStrings` are the application's own"
Strings.Use(Catalogue.Czech());

foreach (var id in ShopStrings.All) {
    _ = id.Text;
}

Assert.Empty(Strings.Missing);
```

## See also

* [Commands and the focus route](commands.md) — the other half of a menu item: what it says comes
  from here, who handles it comes from there.
* [The accessibility tree](accessibility.md) — where a control's words go after they are shown, and
  the assertion that they arrive in the same language.
* [The editor shell](../editor/index.md) — `EditorStrings` is this shape, hand-written, and
  `StringCatalogYaml` is the editor's answer to where a catalogue lives.
