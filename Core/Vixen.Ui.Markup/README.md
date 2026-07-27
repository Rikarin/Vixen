# Vixen.Ui.Markup

VXML: the markup language the UI framework is written in. A `.vxml` file becomes a syntax tree,
then a component model, then a C# partial class — and every fragment of C# in it keeps a line
number, so the compiler talks about the file you wrote rather than the file it generated.

Nothing here is a tree implementation. `Vixen.Core.Syntax` already has one, and VXML brings a
grammar to it — which is the whole reason a second language is affordable at all.

## The pipeline

```
SourceText
  → VxmlLexer      content / tag / attribute-value modes, and one C# balancer
  → VxmlParser     recursive descent; every parse produces a tree that reproduces its file
  → SyntaxTree     green/red, generated from Syntax.xml
  → Binder         tags, attributes, slots, keys → BoundComponent + diagnostics
  → ComponentEmitter   → C# partial class, every expression under a #line
```

## State

| | |
|---|---|
| `VxmlLexer` | Modes, the `@` forms, verbatim capture of C#, CSS and `@code` bodies. |
| `VxmlParser` | The grammar, with recovery for unclosed tags, mismatched closes and mid-typing states. |
| `SyntaxTree` | Parse, diagnostics, and a full-fidelity tree. |
| `Binder` | `BoundComponent`: what each tag is, what each attribute means, where each expression is. |
| `ComponentEmitter` | The generated partial, with `#line` spans. |
| Incremental reparse | ⏳ |
| The source generator | ⏳ |

## The syntax

```html
@component Counter
@using Vixen.Ui.Controls

@code {
    private readonly Signal<int> _count = new(0);
    [Parameter] public required string Title { get; init; }
    private void Increment() => _count.Value++;
}

<div class="flex flex-col gap-2">
    <Text class="text-lg">@Title</Text>
    <Text>Clicked @_count.Value times</Text>

    @if (_count.Value > 10) {
        <Callout Kind="warning">That's a lot of clicking.</Callout>
    } else {
        <em>Keep going.</em>
    }

    @for (var i in Enumerable.Range(0, 3)) {
        <Button key="@i" on:click.stop="@Increment">+@i</Button>
    }

    <slot name="footer" />
</div>

<style scoped>.flex { display: flex; }</style>
```

A lowercase tag is an intrinsic element and an uppercase one is a component — the React and Blazor
rule, chosen because it is decidable from the characters. A parser cannot consult a registry of
component types: the types it would look up are being compiled beside it.

## The binder has no semantic model, and that is the design

The original sketch had the binder resolve `<Counter Title="x" />` against the C# type `Counter`
using Roslyn's `Compilation`, and typecheck `Title` against the property. **It does not need to.**
If the emitter writes the tag name where a type name goes and the attribute name where a property
name goes, both under a `#line`, then an unknown component, a misspelt parameter and a wrong
expression type are all reported by the C# compiler — at the right character of the `.vxml` — with
no type resolution on this side at all.

What is left for the binder is the set of mistakes Roslyn *cannot* catch, because they are about
markup rather than about C#: a duplicate attribute, an event handler given a string, two slots
claiming one name, a loop whose elements have no identity. Every `VXML2xxx` is one of those. There
is deliberately no `VXML3xxx` range, because a second and worse typechecker is exactly what this
design exists to avoid.

## Whitespace

**A whitespace run that crosses a line break is trivia; one that does not is text.** Indentation
between two elements is formatting and disappears. The space you typed between `@first` and `@last`
on one line is content and survives.

That is the entire whitespace policy. It costs nothing, because trivia already exists and the tree
still round-trips either way, and it means no later pass has to guess which spaces the author meant.
The one exception is directly inside an `@switch` body, where a whitespace run is always trivia:
between the brace and a label there is nothing else it could be, and requiring `case` on its own
line would be a grammar rule dressed up as a formatting preference.

## Braces, and the depth rule

Control flow uses braces rather than Razor's `}` sentinels. That leaves the obvious problem: a `}`
in ordinary text.

**A `}` closes a directive body only at the element depth its `{` was written at.** So

```html
@if (x) { <div>a } b</div> }
```

reads the first brace as text and the second as the close, and it does so without lookahead,
backtracking, or a rule about what text may contain. The same depth test is what keeps the word
`case` inside a `<p>` from starting a new switch section.

## Expressions are never parsed

`@expr`, an `@code` body, a `<style>` body, an `@if` condition, a `case` pattern — every one of them
is captured as a single token by balancing delimiters, with C# strings, characters, comments,
verbatim strings and raw strings skipped so that a brace inside a string cannot close a block.

`SkipCSharp` is the only C# knowledge in the assembly, and it knows how a literal ends and nothing
else. Roslyn reads the rest.

`@` in content follows Razor's implicit-expression rule: a name, then member accesses, calls and
indexers, stopping at the first character that cannot continue one. So `Clicked @count times` reads
the way it looks, and `@a + b` interpolates only `a` — the author who meant otherwise writes
`@(a + b)`.

## Recovery

Every parse produces a tree, and every tree reproduces its file byte for byte. That is not
politeness towards bad input: the editor reparses on every keystroke, and half of those keystrokes
land on a file nobody has finished writing. **Every prefix of a real file is a test case**, and it
is tested as one.

Missing tokens are fabricated zero-width, source the parser could not use travels as skipped-token
trivia, and an element the file never closed simply has no end tag.

The one place the parser looks around is a mismatched close tag, where it asks whether any *open
ancestor* answers to that name:

- `<div><span>x</div>` — an ancestor does, so `<span>` is the thing that was never closed and the
  tag belongs to `<div>`.
- `<div>x</span>` — nobody does, so it is `<div>`'s close tag with the wrong name on it.

Same characters, opposite mistakes, and telling them apart is the difference between one diagnostic
and a cascade of them.

## `#line`, and why it is the span form

Directives use `#line (l,c)-(l,c) offset "file"` rather than `#line N "file"`. The line form lands a
squiggle at the start of a generated line, which for `ctx.Bind(n3, "class", () => kind)` is several
tokens away from the one word that came out of the markup. The span form carries the exact
characters — including, where Roslyn narrows to a member name, a squiggle on that member name in the
`.vxml`.

## What the output looks like

Per ADR-010, imperative construction code and no virtual DOM: one statement per element, one effect
per dynamic expression. Setting a signal invalidates exactly that effect, which assigns exactly that
property.

`@if` and `@switch` emit the *same* runtime primitive — one selector saying which arm is live, one
builder constructing it. Two near-identical constructs for swapping a subtree in and out would mean
two places to get the disposal of a branch's effects wrong.

Without a `key`, a loop falls back to the item's own identity — never to its index. An index makes
every element after an insertion compare unequal, which is precisely the failure `VXML2004` warns
about; a fallback that quietly did it would make the warning a lie.

The runtime it calls is `Vixen.Ui.Composition`. The emitter's gate compiles its output against that
assembly, loads the result, builds it into a `UiDocument` and drives it with a signal — so what is
tested is markup to syntax tree to component model to C# to IL to an element tree, and not the shape
of a string.

## What is owed

- **Incremental reparse.** The shared `Blender` exists and Raven uses it, but node reuse needs a
  unit of reuse. Raven offers member declarations; VXML's is not obvious, because an element's green
  node is reusable only if nothing about its *enclosing* content changed — an unclosed tag anywhere
  above it changes what it is.
- **The source generator.** `Vixen.Ui.Markup.Generators`, which is this pipeline plus
  `IIncrementalGenerator` plumbing.
- **`bind:` update events** (`bind:value:oninput`) and **`@namespace`**.

Licensed under Apache-2.0.
