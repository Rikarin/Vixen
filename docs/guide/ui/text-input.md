---
title: Text input and the input method
slug: ui/text-input
kind: guide
area: Core
summary: How typed text reaches a control, why an input method's pre-edit is a different event from typed text and what a field does with it, why the pre-edit is shown but is not the value, where the caret goes while a composition is running, what a platform head has to do for any of it to arrive, and why a caret index alone cannot say where the caret is at a wrap or a change of direction.
api: [T:Vixen.Ui.TextInputEvent, T:Vixen.Ui.TextCompositionEvent, T:Vixen.Ui.Text.CaretAffinity]
tags: [ui, input, text, keyboard, ime, composition, caret, affinity, bidi]
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

## See also

- `Core/Vixen.Platform/Input/ITextInput.cs` — activation, the on-screen keyboard, and the candidate
  area.
- `Core/Vixen.Ui.Controls/TextField.cs` — the editing core under every field in the set.
- [Accessibility](accessibility.md) — what a field reports about itself while it is being typed into.
- `Core/Vixen.Ui.Text/README.md` — *The caret, and a function that cannot be inverted*: why the round
  trip is stated as "a caret is drawn where the click that found it was" rather than as an inverse.
