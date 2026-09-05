---
title: Text input and the input method
slug: ui/text-input
kind: guide
area: Core
summary: How typed text reaches a control, why an input method's pre-edit is a different event from typed text and what a field does with it, why the pre-edit is shown but is not the value, where the caret goes while a composition is running, what a platform head has to do for any of it to arrive, and why a caret index alone cannot say where the caret is at a wrap or a change of direction.
api: [T:Vixen.Ui.TextInputEvent, T:Vixen.Ui.TextCompositionEvent, T:Vixen.Ui.Text.CaretAffinity, T:Vixen.Ui.EditingCommands, T:Vixen.Ui.EditingCommand, T:Vixen.Ui.EditingKeymap]
tags: [ui, input, text, keyboard, ime, composition, caret, affinity, bidi, keymap, shortcuts]
since: 0.2
status: preview
related: [ui/commands, ui/accessibility]
---

## What it is

Two events, and the distinction between them is the whole of this page.

<xref:Vixen.Ui.TextInputEvent> is **text the user typed** — what the keyboard layout, the dead keys
and any input method between them decided, arriving as a `string` because one keystroke may produce
several characters and a great many produce none.

<xref:Vixen.Ui.TextCompositionEvent> is text an input method is **still composing**. A Japanese,
Chinese or Korean user types Latin letters and the input method turns them into a *pre-edit*: a
provisional string that is replaced in place on every keystroke, may be abandoned entirely, and only
becomes real when it commits — as a `TextInputEvent`.

Both are separate from `KeyEvent`, which is a *position on a keyboard*. A control that read
characters off key events works for the person who wrote it and for nobody with a different layout.

## What it is for

A field that handles only `TextInputEvent` is not broken in English and is unusable in Japanese: the
box shows nothing at all while somebody types into it, and the input method's candidate window —
which <xref:Vixen.Platform.ITextInput>'s `SetCandidateArea` does place under the caret — floats over
an empty box. Nothing logs and no counter moves.

## Using it

Both events go to whatever has the focus, and a control subscribes the same way it subscribes to
anything else:

```csharp no-compile="a fragment; `field` is a control that takes text"
field.AddHandler<TextInputEvent>((element, args) => { /* typed, and real */ });
field.AddHandler<TextCompositionEvent>((element, args) => { /* provisional */ });
```

`TextField` — and so every control built on it — handles both already.

⚠ **An empty `Text` on a composition is a *cancellation*, not "nothing happened".** Every platform
ends an abandoned composition by sending one. A handler that returns early on an empty string leaves
the last pre-edit drawn in the field for ever, belonging to an input method that has forgotten about
it.

⚠ **`Start` and `Length` are the input method's own cursor *inside the pre-edit*.** They are what put
the caret in the middle of a half-converted phrase, where the input method thinks it is. Dropped, the
caret sits in front of the whole pre-edit — a field that looks like it works and is wrong for exactly
the users the feature exists for.

## What a field does with a pre-edit

<xref:Vixen.Ui.Controls.TextField> keeps the composition **out of `Value` and in what it displays**.

- `Composition` is the pre-edit; `IsComposing` says whether there is one.
- `Value` never contains it, so `ValueChanged` is not raised per keystroke of a composition and
  `Coerce` is never handed a half-converted phrase. ⚠ That second one is not a nicety: a
  `NumericInput` that coerced each intermediate reading would reject them all and the field could not
  be typed into at all.
- What the field *shows* is the value with the pre-edit spliced in at the caret, because the
  alternative is a box that shows nothing while somebody types into it.
- `DisplayCaret` is the caret's index in that displayed string — `CaretIndex` plus how far into the
  pre-edit the input method's cursor has got. Every index into the value and every index into the
  display belongs to exactly one of the two, and mixing them is the way this goes wrong quietly.
- The pre-edit is underlined, in the caret's colour rather than the selection's, because it is not
  selected text: it is text that does not exist yet.
- A composition replaces the selection when it **starts**, exactly as typing would.
- Losing the focus abandons it. The platform sends the end of a composition to whatever has the focus
  *now*, so a field that has lost it never hears the end of its own.

## What a host has to do

Text input is **off by default**, and that is not an optimisation: while it is active the platform
hands keystrokes to the input method first, so `W` may produce a composition rather than a key a game
can bind. A field turns it on when it takes focus and off when it loses it, through
<xref:Vixen.Platform.ITextInput>.

A host that pumps platform events through `Vixen.Platform.Ui`'s `PlatformInput.Dispatch` gets both
events routed for it. ⚠ **That arm for `PlatformEventKind.TextEditing` was missing until 2026-09**,
and the gap was invisible from both ends: the event existed, was documented, was produced by the
desktop and web heads, and had a constructor test of its own — and the bridge dropped every one of
them through its `default`. Both halves were tested and correct; the join was neither.

## Where the caret is, which is not only a number

A caret index names a *boundary between two characters*, and a boundary is not always one place.
Usually it may as well be — the character before ends exactly where the character after begins. Two
situations break that, and a reader meets both:

- **A soft wrap.** The index that ends one row also starts the next. A caret walked right off the end
  of a line and a caret pressed Down onto that line arrive at the same number, a whole row apart.
- **A change of direction.** In `abcلسان` the index 3 is *after the `c`* and *before the first Arabic
  letter*, and those are at opposite ends of the Arabic run.

<xref:Vixen.Ui.Text.CaretAffinity> is the bit that says which — `Upstream` for the character before
the index, `Downstream` for the one after — and <xref:Vixen.Ui.Controls.TextField>'s `CaretAffinity`
is where a field keeps it. It is **carried state, not a derivation**: nothing about the text can say
how the caret got where it is, so a field that stored only `CaretIndex` cannot draw it in the right
place.

Every caret method has an index-only overload that means `Upstream`, so existing code is unaffected;
reach for the pair when you are *placing* a caret rather than counting characters.

⚠ **One index maps to two places, and that is fixed. Two indices can also share one place, and that
is not.** The caret after the `c` and the caret at the end of `abcلسان` are the same x, so a click
there must answer with one of them. An editor resolves that by remembering its caret, never by asking
the text — which is exactly why the affinity lives on the field.

## Examples

Taking committed text and a pre-edit from the same field, which are two different events and must
stay so:

```csharp no-compile="a fragment; `field` is a control that takes text"
field.On<TextInputEvent>((_, typed) => field.Insert(typed.Text));

field.On<TextCompositionEvent>(
    (_, editing) => field.ShowPreedit(editing.Text, editing.Start, editing.Length)
);
```

⚠ A pre-edit is **not** typed text: it is what the input method is still deciding, it replaces
whatever it showed last time, and committing it as input would leave every intermediate reading in
the field. The two events exist to keep them apart.

## Whether what it holds is acceptable

Three seams act on a value and they answer three different questions. `Coerce` decides what the
field will *hold* — `NumericInput` refuses letters outright. `Shown` decides what it *draws* —
`SecureTextBox` draws bullets. `Validate` decides whether what it holds is *acceptable*, and unlike
the other two it changes nothing: the value stays where it is, with the mistake visible, so that the
person who made it can correct it.

```csharp no-compile="a fragment; `field` is a TextField"
field.Required = true;
field.Validator = value => value?.Contains('@') == true ? null : "Needs an at-sign";
```

`null` means acceptable and anything else is the reason. `TextField.IsValid` and
`TextField.ValidationMessage` are the answer; `Required` is applied first, and it is the only rule
this assembly can put words to — every other rule is a fact about what the field is *for*, which
lives in the application.

⚠ **A rule about acceptability must not be written as a `Coerce`.** A field that silently dropped
what was typed because it was too short could never be typed into at all: the user would watch their
own keystrokes vanish with nothing on screen to explain it.

⚠ **`Revalidate()` is public because validity can turn on something that is not the value** — a name
checked against a list that has just arrived, a confirmation that has to match another field.
Nothing about those changes when a keystroke lands here, so a control that only revalidated on its
own edits would sit there green.

An invalid field writes an `invalid` class, which is what the theme draws a ring with, and reports
`AccessibleStates.Invalid`; a required one reports `AccessibleStates.Required`. Those two flags had
no producer anywhere in the repository until this seam existed, so a form's mandatory fields sounded
exactly like its optional ones.

⚠ **The message is not written into the accessibility tree by the control.** ARIA pairs
`aria-invalid` with a *separate* element holding the words, reached by `aria-describedby` — so the
error text a form shows is a label in the layout and pointing at it is one
`field.AddAccessibleRelation(AccessibleRelation.DescribedBy, message)`. Folding the string into
`AccessibleDescription` from inside the control would overwrite whatever the application put there.

## The editing keymap

A chord is not a verb. `EditingCommands.Resolve(key, modifiers, keymap)` turns one into an
`EditingCommand` — `MoveWordLeft`, `DeleteToLineEnd`, `SelectAll` — and both text controls switch on
the verb rather than on the key. `EditingCommands.Id` gives each one its canonical string
(`text.move-word-left`, and `edit.copy` for the three verbs an application shares).

⚠ **There are two tables, and there have to be.** `TextField` used to take `Control || Meta` for
every verb with a comment saying the assembly could not know which platform it was on, and
`CodeEditor` had a second copy of the same switch that took `Control` only — so ⌘← moved by a word in
a text box and by a single character in the code editor. Neither could grow the AppKit emacs
bindings, because ⌃A cannot be Select All and "move to the start of the line" in one table:

| | Windows / Linux | macOS |
|---|---|---|
| Word left | Ctrl-← | ⌥← |
| Line start | Home | ⌘←, Home, ⌃A |
| Select all | Ctrl-A | ⌘A |
| Delete to line end | — | ⌃K |

`UiDocument.EditingKeymap` says which table a document reads; it defaults to
`EditingCommands.Current`, which is the platform's.

⚠ **Pin it in a test.** A suite that took the default would assert one keyboard on a Mac and another
in CI, which is a red build whose cause is in neither the diff nor the test. Both control fixtures
set `EditingKeymap.Windows`, and the platform question is answered by naming both tables directly.

⚠ **Shift is stripped before the lookup and every other modifier must match exactly.** Extending a
selection is orthogonal to every motion, so it is a bit the control reads rather than a second half
of the vocabulary — and exactness is what lets ⌥← and ⌘← mean two different things at all. The
switches this replaced used `HasFlag`, which made ⌃⌥← word motion.

## See also

- `Core/Vixen.Platform/Input/ITextInput.cs` — activation, the on-screen keyboard, and the candidate
  area.
- `Core/Vixen.Ui.Controls/TextField.cs` — the editing core under every field in the set.
- [Accessibility](accessibility.md) — what a field reports about itself while it is being typed into.
- `Core/Vixen.Ui.Text/README.md` — *The caret, and a function that cannot be inverted*: why the round
  trip is stated as "a caret is drawn where the click that found it was" rather than as an inverse.
