---
title: Secure text input
slug: ui/secure-text-input
kind: guide
area: Core
summary: SecureTextBox holds what was typed and draws bullets — the field a login screen needs, which this control set did not have. The masking is one seam on TextField, the pre-edit is masked with the value, and there is deliberately no reveal button.
api: [T:Vixen.Ui.Controls.SecureTextBox]
tags: [ui, controls, text, security, accessibility, vxml]
since: 0.2
status: preview
related: [ui/text-input, ui/accessibility]
---

## What it is

`SecureTextBox` is a single-line field that holds a real string and draws one bullet per character.

```vxml
<SecureTextBox Placeholder="Password" bind:Value="@Model.Password.Value" />
```

Everything else about it is `TextField`: the caret, the selection, the placeholder, `Submitted`, the
theme. It is `TextBox` with one method overridden.

## Why it exists

Until it did, an application that wanted a password asked for one with a `TextBox`, in front of
whoever was standing behind the user. Nothing in `Core/Vixen.Ui.Controls` matched `secure` or
`password` — so a login screen was the first thing a new application could not build.

## How the masking works

`TextField.Shown` is the seam: the field's value goes through it on the way to the text part, and the
default returns it unchanged. `SecureTextBox` returns bullets instead.

⚠ **An override must return one UTF-16 unit for every unit it was given.** The caret, the selection,
the hit test and the composition underline are all indices into the value and are measured against
this layout, so a mask that collapsed surrogate pairs or grapheme clusters would put the caret in
front of a different character than the one it is in front of. Masking per code unit keeps every
index identical, and on a run of identical bullets a grapheme would have bought nothing.

⚠ **The pre-edit is masked with the value.** An input method's intermediate reading of a password is
the password being typed; a field that masked what was committed and showed what was being composed
would leak the same secret one keystroke earlier.

⚠ **The bullet is a character rather than a drawing.** It goes through the same shaping, font
fallback and measurement as any other glyph — a field that painted circles itself would put the caret
in the wrong place the first time somebody changed the font size.

## What it does not do

**There is no clipboard in `Vixen.Ui` at all** — nothing copies, cuts or pastes — so the selection
this field allows cannot carry anything out of it, and `SelectedText` is left alone rather than
blanked. That is why the control is as small as it is. ⚠ When a clipboard arrives, this is the type
it has to ask before it reads.

**There is no reveal button, and its absence is a decision.** Showing the value is one assignment
away for an application that wants it, and a reveal built into the control would be a control that
can be made to display a secret by anything that can reach a property on it.

**Nothing here hardens the value in memory.** `Value` is a `string`, which the runtime may move, copy
and leave behind in the heap until a collection; there is no `SecureString` and this control does not
pretend otherwise. A credential that must not sit in managed memory is not something a text field can
promise.

## Accessibility

The field reports its mask as its accessible value, not the password and not `null`.

⚠ **Not `null`**, which looks like the safer answer and is not: reporting nothing makes an empty field
and a full one sound the same, which is how somebody typing blind loses track of whether their
keystrokes are arriving at all. A platform's own secure field reports the bullets for exactly that
reason.

⚠ **There is no "protected" state to report yet.** `AccessibleStates` has no member for it and
`AccessibleRole.TextBox` is what a secure field carries, so a screen reader is told this is an
editable text box whose contents are bullets — which is true, and is less than the platform-specific
"password edit" a real bridge would announce. See the accessibility guide for what a bridge still
owes.

## See also

* [Text input and the input method](text-input.md) — how typed text and a pre-edit reach a field
* [Accessibility](accessibility.md) — what the tree reports and what it does not
