<!--
SPDX-FileCopyrightText: Copyright (c) Rikarin
SPDX-License-Identifier: Apache-2.0
-->

# Vixen for VS Code

Syntax highlighting for the three languages a Vixen project is written in.

| | | |
|---|---|---|
| `.vxml` | markup | C# in `@code`, `@if`, `@(…)` and every `@`-valued attribute; VCSS in `<style>` |
| `.vcss` | stylesheets | CSS, plus `@theme`, `@apply`, `@layer` and the control library's hyphenated element names |
| `.rvn` | Raven shaders | a grammar of its own |

## Installing it

There is nothing to compile — it is grammars and JSON.

```bash
ln -s "$PWD/Tools/Vixen.VSCode" ~/.vscode/extensions/vixen
```

Reload the window. To package it instead:

```bash
npx --yes @vscode/vsce package
```

## What it knows, and how that was decided

**The keyword lists are transcribed from the compilers, not remembered.** Raven's are
`Raven/Vixen.Raven/Parsing/RavenLexer.cs`, which is one dictionary and is the whole of what its lexer
treats as a keyword; VXML's directives and attribute prefixes are `Core/Vixen.Ui.Markup/Binding/Binder.cs`.
A word missing here is a word missing there, which is a thing to check rather than a thing to guess.

Three decisions are worth knowing about because they are the language's rather than the grammar's:

**An uppercase tag is a component and a lowercase one is an element.** That is the React and Blazor
rule, and VXML uses it because it is decidable from the characters — a parser cannot consult a
registry of the types being compiled beside it. So the two get different scopes here, which is the
whole reason the rule exists.

**`bind:` and `change:` are not events.** `on:` maps a name through a table of routed gestures, and no
entry in that table can hand a handler a value; `change:` names a `[UiProperty]` and rides
`UiElement.PropertyChanged`. They are coloured alike because they are alike — a binding — and apart
from `on:`, which carries dot modifiers.

**Almost every type selector in a `.vcss` is a hyphenated name CSS has never heard of.** `menu-bar`,
`dock-panel`, `progress-bar`, and whatever a component's `@tag` says. A plain CSS editor colours
those as unknown tags; this one calls them element names, which is what they are.

## What it does not do

**No diagnostics, no completion, no go-to-definition.** This is a TextMate grammar and nothing else —
it cannot resolve a type, so it cannot tell you that `Variant="Subtel"` is a misspelt enum member.
That one in particular is worth saying out loud: VXML emits `Literals.Of(…)` and lets the C#
compiler pick the conversion from the target's type, so **a misspelt enum member is a run-time
failure** and neither this extension nor the build will mention it.

The real answer is a language server over `Vixen.Core.Syntax`, which already has the parsers —
[doc 09](../../docs/plan/09-ui-framework.md) specifies a `CodeEditor` with exactly this for `.rvn`.
Until then, the compiler's own errors land on the right character of the right file, because every
fragment is emitted under a `#line` span.

**A `}` is not matched by depth.** VXML closes a directive body at the element depth its `{` was
written at, so `@if (x) { <div>a } b</div> }` reads the first brace as text and the second as the
close. TextMate has no notion of depth, so a `}` inside markup text may end a block early here and
will not in the compiler. It is rare and it is cosmetic.

## Tests

```bash
npm install
npm test
```

The tests tokenise real files out of this repository and assert the scopes. ⚠ **They are written to
fail against the bugs that were actually in these grammars**, not to describe them: `<Menu>` reading
as an element rather than a component, and an attribute rule that stopped at the name — so the stray
quote of `change:Value="@(v => Write(v))"` opened a string and swallowed the two attributes after it.
Both were found by tokenising rather than by looking, because a wrong grammar still colours the file,
and it colours it plausibly.

⚠ `source.cs` and `source.css` are VS Code's own and are **stubbed** in the harness. That is not a
detail: vscode-textmate silently drops a begin/end rule whose `patterns` contain only an include it
cannot resolve, so `@(…)` came back as one unscoped token and looked exactly like a broken regex. The
stub matches one character at a time, because a greedy one swallows the enclosing rule's terminator.
