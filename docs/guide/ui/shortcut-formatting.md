---
title: Writing a shortcut down
slug: ui/shortcut-formatting
kind: guide
area: Core
summary: The modifier order, the key-name table and the one process-wide hook that changes how every shortcut in an application reads — none of which needs an element, a document or a font, and all of which used to live on a control.
api: [T:Vixen.Ui.Shortcuts]
tags: [ui, input, shortcuts, keyboard, commands, macos]
since: 0.2
status: preview
related: [ui/commands, ui/application-bars, ui/accessibility]
---

## What it is

`Shortcuts` turns a key and its modifiers into the text a menu shows:

```csharp
Shortcuts.Describe(InputKey.S, ModifierKeys.Control | ModifierKeys.Shift);   // "Ctrl+Shift+S"
Shortcuts.Name(InputKey.Number1);                                            // "1"
```

Three members and no state but one:

- **`Describe`** — the whole combination, modifiers first, in the order Ctrl, Alt, Shift, Meta.
- **`Name`** — the key alone, by its legend rather than by its enum member name.
- **`Formatter`** — a settable `Func<InputKey, ModifierKeys, string>`, defaulted to `Describe`, that
  every shortcut in the process is written through.

The modifier order is not alphabetical and it is not the flag order. It is what Windows, GTK and Qt
all write, and a menu that used a different one would look wrong beside every other application on
the machine.

## What it is for

⚠ **Formatting a chord is not view state, and it lived on a control anyway.**
`Vixen.Ui.Controls.KeyboardShortcut` is an element that *draws* a combination, and it carried both
the formatter and the key-name table as statics. So anything that only wanted the string —
a command palette row, a keymap editor, a log line, a tooltip built by hand — had to reference the
control library and, through it, an element tree it had no use for.

⚠ **What that cost was a layering answer nobody could reach.** `Vixen.Editor.Ui`'s `KeyChord` is a
`readonly record struct` over an `InputKey` and a `ModifierKeys` and is the most obviously movable
type in that assembly — and it could not move down into `Vixen.Ui`, because four of its lines went
through those two statics and `Vixen.Ui` does not reference `Vixen.Ui.Controls` and must not. One
`using` was the whole of the blockage.

`KeyboardShortcut.Formatter` and `KeyboardShortcut.Describe` are still there and still mean what they
meant. They forward here, so there is **one** formatter in the process rather than two that can
disagree about how the same chord is written.

## Using it

`Formatter` is the hook, and it is process-wide on purpose: a shortcut is drawn by menus, by toolbar
tooltips and by the command palette, and an application that adapted each call site would have to
find all three and would still miss whichever one was added next.

```csharp
// Called once, by the shell, during start-up.
Shortcuts.Formatter = (key, modifiers) => MyOwnFormat(key, modifiers);
```

⚠ **The default is deliberately not platform-adapted.** `Vixen.Ui` sits below `Vixen.Platform` and
does not know what it is running on, so `Describe` writes `Meta+S` — which is what the modifier is
called in a `KeyEvent` and not what it is called on the key. A Mac reading `⌘S` is something the
application says; `KeyChord.UsePlatformFormat` in the editor is that sentence, and it is where the
decision belongs.

⚠ **A platform formatter wants `Name` and not `Describe`.** The macOS form is glyphs for the
modifiers in a fixed order and then the ordinary legend for the key, so the part it needs is the part
after the modifiers. Asking for it by calling `Describe(key, ModifierKeys.None)` works and requests a
whole rendering to get a fragment of one; `Name` is that fragment, which is why it is public.

## Examples

The macOS form, written against the two pieces rather than around them:

```csharp no-compile="a fragment; the glyph order is the platform's, not the table's"
static string MacFormat(InputKey key, ModifierKeys modifiers) {
    var text = new StringBuilder();

    if (modifiers.HasFlag(ModifierKeys.Control)) { text.Append('⌃'); }
    if (modifiers.HasFlag(ModifierKeys.Alt)) { text.Append('⌥'); }
    if (modifiers.HasFlag(ModifierKeys.Shift)) { text.Append('⇧'); }
    if (modifiers.HasFlag(ModifierKeys.Meta)) { text.Append('⌘'); }

    return text.Append(Shortcuts.Name(key)).ToString();
}
```

The keys whose member name is a description rather than a legend — the reason the table exists at
all:

```csharp
Shortcuts.Name(InputKey.Number1);   // "1", not "Number1"
Shortcuts.Name(InputKey.Grave);     // "`"
Shortcuts.Name(InputKey.Slash);     // "/"
Shortcuts.Name(InputKey.F5);        // "F5" — everything else is the enum's own name
```

## See also

- [Commands](commands.md) — what a shortcut is bound to, and how one is dispatched.
- [Toolbars and status bars](application-bars.md) — one of the three places a chord is drawn.
- [Accessibility](accessibility.md) — the text a shortcut is written as is also what is read out.
