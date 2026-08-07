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
| `ComponentEmitter` | The generated partial, with `#line` spans — a `Component`, or a `UiElement` when the file wrote `@inherits`. |
| Incremental reparse | ⏳ |
| The source generator | [`Vixen.Ui.Markup.Generators`](../Vixen.Ui.Markup.Generators/README.md) — this pipeline, run by Roslyn over a project's `.vxml` files. |

## The syntax

```html
@component Counter
@tag counter-panel
@inherits Vixen.Ui.Controls.Control
@using Vixen.Ui.Controls

@code {
    private readonly Signal<int> _count = new(0);
    private UiElement _body = null!;
    [Parameter] public required string Title { get; init; }
    private void Increment() => _count.Value++;
    partial void OnComposed() => _body.AddClass("ready");
}

<div class="flex flex-col gap-2" ref="@_body">
    <Text class="text-lg">@Title</Text>
    <Text>Clicked @_count.Value times</Text>

    @if (_count.Value > 10) {
        <Callout Kind="warning">That's a lot of clicking.</Callout>
    } else {
        <em>Keep going.</em>
    }

    @for (var i in Enumerable.Range(0, 3)) {
        <Button key="@i" Variant="Subtle" on:click.stop="@Increment">+@i</Button>
    }

    <slot name="footer" />
</div>

<style scoped>.flex { display: flex; }</style>
```

A lowercase tag is an intrinsic element and an uppercase one is a component — the React and Blazor
rule, chosen because it is decidable from the characters. A parser cannot consult a registry of
component types: the types it would look up are being compiled beside it.

⚠ **An uppercase tag may also name a *control*,** and this side cannot tell: `<Callout />` is a
`Component` and `<ProgressBar />` is a `UiElement`, and resolving which would mean the type
resolution this design exists to avoid. So the emitter writes one call for both —
`ctx.Child<Tag>(…)`, plus `BuildContext.Host`/`Inner` where the two differ — and C# overload
resolution settles it at the use site. Without that the control library would be unreachable from
the markup language it is meant to be written in.

**A parameter may be a property *path*.** `LeadingIcon.Geometry="@Icons.Close"` emits
`n1.LeadingIcon.Geometry = …` under the attribute name's own `#line`, because the control library
has properties that are objects and there is no flat name for them. Nothing here checks that the
path exists — the binder's rule is only that it will parse as C#, which is the same bargain the tag
name is emitted under.

⚠ **Two attribute names are universal**, meaning they mean the same on a component tag as on an
element and are never assigned as properties: `class`, and `binding-path`. The second is doc 36's:
it names a member of whatever an editor is editing, and the join happens *after* the tree is built,
by a pass that walks it — which is Unity's rule and the only one available, since a `Build` body
cannot name a C# type without the markup naming one too. Both land as style-tree attributes, and
`UiElement.Attribute` is how the pass reads them back.

**And `ref` is a third that is not one of them.** `ref="@Parts"` hands the thing the tag named back
to a member of the generated class:

```html
<TreeView ref="@Parts" />
<Callout ref="@Note">…</Callout>
```

It means the same on both sorts of tag and is never a property, which is what `class` and
`binding-path` have in common — but unlike them it lands nowhere in the document at all. It is one
assignment in the `Build` body, `Parts = n0;`, written under the *member's* own `#line`. So nothing
here knows what `Parts` is: a member that does not exist, one that is readonly, and one of the wrong
type are all reported by Roslyn on the characters between the quotes, which is the same bargain the
tag name is emitted under. It takes an expression rather than a bare name for the same reason `key`
and `on:` do — and because `ref="@this.Rows[0]"` then costs no rule.

⚠ **On a capitalised tag it hands back the *component*, where `class` and `on:` reach the element it
drew.** That is deliberate and it is the asymmetry: what a caller holds a component for is its
methods, and the element it drew is `BuildContext.Host` away.

Four questions it has to answer, and the answers are in the language rather than in a convention:

- **When is it assigned?** As soon as the element exists, so a statement later in the body may use
  it — and definitely by `OnComposed`, the partial method both flavours declare and call once the
  whole body has run. That is where wiring belongs.
- **What about a re-`Build`?** Nothing to do. A hot reload re-runs the body, and the body contains
  the assignment, so every `ref` points at the new elements. The old ones are `IsRemoved`.
- **What about `@for`?** `VXML2010`, an error, at every depth of the body. The body runs once per
  item and there is one member; keeping the last is a trap, and a list would be worse — a surviving
  key's body is not re-run at all, so the list would be a history of first appearances. What the
  author wants is the element the loop is *inside*.
- **What about `@if`?** Allowed. A `ref` in an arm that is not live is simply not assigned, because
  the arm built no element; when the arm becomes live the assignment runs. When it leaves, the member
  points at a removed element rather than at null — clearing it would mean the region knowing the
  member's name, and `UiElement.IsRemoved` already answers the question. A panel whose caller asks
  what a part is showing wants the part present and classed, not absent.

**And a quoted value is not necessarily a string.** `Variant="Subtle"` is an enum member,
`Value="0.5"` is a float, `Loud="true"` is a flag — and this side cannot tell which, for the same
reason it cannot tell a component from a control. So the emitter writes

```csharp
n1.Variant =
    Literals.Of(n1.Variant, "Subtle");
```

and the *C# compiler* picks the conversion, from the type of the property being assigned. The first
argument is there to be inferred from and is never read: C# infers nothing from what an expression
is assigned to, and there is no other way to get the target's type into a generic method.

⚠ **The property is therefore named twice, under two `#line` directives.** One would map its own
fragment and *extrapolate* the rest of the line — which put the second error from a misspelt
parameter at a column several words past the end of the line the author wrote. Two directives put
both on the attribute's name, which is the price of the shorthand along with one duplicated
diagnostic.

⚠ **And a misspelt enum member is a run-time failure**, because nothing on this side knows the
member names either. `Literals.Of` says what they were; `@ControlVariant.Subtle` is still accepted
and is still checked by the compiler, for anyone who would rather have the error at build time.

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

## `@tag`, and what a component is called

```html
@component TaskCentre
@tag task-center
```

The default host tag is the type's name in lower case, and it cannot produce a hyphen — so every
component whose stylesheet spells its tag the way CSS does, which is every compound one, would
otherwise override a virtual property to say a single word. A header is where a component already
says what it is.

It emits `protected override string TagName => "task-center";` and nothing else, so a component
written by hand says the same thing the same way.

## `@inherits`, and the two things a `.vxml` can be

Without it the generated class is a `Component`, which is what a `.vxml` is for and is still the
default. With it the class is whatever the header named — in practice a `Control` — and the emitter
writes an element-flavoured scaffold instead: build in `OnCreated`, stop in `OnRemoved`.

**The distinction is not about reactivity, it is about who holds the thing.** A `Component` is a
builder of elements and is not one, so it is not in the tree: `panel.Add<T>()` cannot make one
(`where T : UiElement, new()`), `Descendants(…).OfType<T>()` cannot find one, and a caller that
wants `view.Tree` or `button.Disabled` has nowhere to get it. That is right for a panel nobody reads
the insides of, and wrong for the four editor panels whose public surface *is* their parts.

⚠ **The rejected alternative was to widen `Descendants`/`OfType`/`Add<T>` to see components**, on
the grounds that the complaint is discoverability rather than the base class. It does not work.
`Add<T>` cannot gain a `where T : Component` overload — two methods differing only in a constraint
is CS0695 — so the affordance would have had to be a differently spelled method, and every existing
call site changes anyway. And `Descendants` walks `UiElement.Children`; no relaxation of a
constraint puts an object that is not an element into a list of elements. The join that does exist,
`UiDocument.ComponentAt`, is what wave 1a's tests had to grow a second finder for. Measured on the
two panels wave 1b ported: `@inherits` changed no consumer and no test at all.

⚠ **An element-flavoured class gets the *same* `BuildContext`, and that was the condition of
choosing it.** `BuildContext.Compose` hands it the identical object a component's `Build` gets, so
`Bind`, `Switch`, keyed `For` reconciliation and region-scoped disposal are the same code and not a
second implementation. Had any of it been weaker the markup would have been a worse way to write the
imperative code it replaces, and the header would not have been worth having.

Two differences remain, and both are the base class being honest:

- **`<style scoped>`** works, but the sheet is loaded by the generated `OnCreated` rather than by
  `Component.Mount`, because a `UiElement` has no `Style` property to read.
- **`<slot />`** becomes `ContentHost`, which is the control library's existing answer to "where
  does a caller's content go". A *named* slot is `VXML2012`: a component has as many slots as it
  declares because `Inner(Component)` reads a dictionary, and an element has one because
  `ContentHost` is one property. A second name would be an element nothing can address.

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

⚠ **A node prints its children in the order it stores them, which is why the four headers are one
list.** They were a field each — `Component`, `Namespace`, `Tag`, `Usings` — and a node's slots come
out in *field* order, so a file that wrote `@using` above `@namespace` came back with them the other
way round. `DocumentSyntax.Directives` holds them in source order and the four properties are
searches over it; the cost is a walk of four nodes and the return is that the tree cannot disagree
with the file about something no reader would notice.

**It was invisible to the tests that should have caught it**, and the shape of that is worth
copying: `The_namespace_may_sit_anywhere_among_the_usings` asserted the parsed values — the right
namespace, the right usings — and never `ToFullString()`. Every value it checked was correct. Round
trip is now asserted on each pair of headers in both orders.

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

## The `@for` key rule, which is the opposite of what `VXML2004` teaches

**Key on the item's value when the item is immutable data. Key on the object only when that object
holds signals.**

Every `.vxml` in the tree obeys it and none of them stated it, and getting it wrong cost wave 1a
five failing tests. The mechanism is one line of `BuildContext.For`: a key that survives an update
**keeps its region and the body is not re-run**. The body's per-item bindings closed over the item as
it was when that key first appeared, so they go on reading that value for ever.

```html
<!-- Wrong. `StatisticRow` is a readonly record struct; the label never changes, so this row's
     number, bar and over-budget class are frozen at the first count. -->
@for (var row in Rows) { <statistic-row key="@row.Label"> … }

<!-- Right. For an immutable snapshot the value *is* the identity: change the count and it is a
     different key, the old region goes and a new one is built with the new number in it. -->
@for (var row in Rows) { <statistic-row key="@row"> … }

<!-- Also right, and for the opposite reason. `BackgroundTask`'s properties are signal-backed, so
     the object is stable and its bindings update themselves — which is what a stable key is for. -->
@for (var task in Tasks) { <task-row key="@task"> … }
```

A reader who has only met `VXML2004` concludes the reverse: it warns against keying on the *index*,
from which any stable field looks like the safe answer. For immutable data it is exactly the wrong
one, and the failure is silent — the list has the right number of rows, in the right order, showing
stale values.

### Can it be a diagnostic?

**The rule cannot; the mistake can.** Deciding whether an item "holds signals" means resolving its
type and asking whether its properties are `Signal<T>`, which is the semantic model this binder
deliberately does not have — and the section above is the reason it does not want one.

What is decidable from characters alone is the *shape* the mistake always takes: a key that is a
member access off the loop variable. `key="@row.Label"` throws away precisely the part of the item's
identity that changing it would have shown, and `key="@row"` is the right answer whether the model is
immutable or signal-backed — which is why the fix the warning names is the same either way. That is
`VXML2011`, and it is a warning rather than an error because the binder cannot see the case where a
projection is genuinely wanted.

⚠ **It under-approximates on purpose.** `@(row.A, row.B)` and `@Key(row)` are the same mistake and
are not caught, because the syntactic evidence runs out — and a rule that guessed at anything
mentioning the variable would fire on `@(row, generation)`, which is a correct compound key and one
of the fixes. A warning that is right whenever it speaks is worth more than one that is complete.

The honest statement underneath all of it is that `For` reusing a region without re-running its body
is a real limitation and the diagnostics are a guard rail over it. The alternative — re-running the
body for a surviving key — would throw away the elements and therefore the focus, scroll offset and
animation state that keys exist to preserve, so it is not a fix, it is the other trade.

The runtime it calls is `Vixen.Ui.Composition`. The emitter's gate compiles its output against that
assembly, loads the result, builds it into a `UiDocument` and drives it with a signal — so what is
tested is markup to syntax tree to component model to C# to IL to an element tree, and not the shape
of a string.

## What is owed

- **Incremental reparse.** The shared `Blender` exists and Raven uses it, but node reuse needs a
- **`bind:` update events** (`bind:value:oninput`).
- **An inline `style`.** There is no way to write one, and there is not meant to be — a `style="…"`
  would land in the selector engine's attribute arena rather than in the cascade. What that costs is
  real, though: a panel that wanted a computed width had to move onto a `ProgressBar`, and hiding a
  part of a control is still a `SetStyle` call from `OnComposed`.
- **A generic base.** `@inherits` takes a `NameToken`, which carries dots and not angle brackets, so
  `@inherits Row<T>` does not lex. Same limit `@using` has, and nothing has needed it.
- **A `Component` unmounting does not stop the effects inside a nested `@for`.** A region hangs off
  the element its content has as a *parent*, so a loop written inside a `<div>` opens its region
  against that div and `BuildContext.Unmount` — which clears the host's — never reaches it. The loop
  stops reconciling and every row's own bindings go on running against removed elements. An
  `@inherits` element does not have this, because `Compose` gives it a context of its own and can
  stop every region in it; a component shares the document's and cannot. Pinned by
  `A_component_leaves_the_effects_inside_a_nested_loop_running_when_it_unmounts`, whose assertion is
  written the wrong way round on purpose and is waiting to be inverted.

⚠ **Two bugs in this project were found by compiling it into a source generator rather than by any
test here.** `VXML1002` and `VXML1003` read their span off a node still under construction — a node
with no parent, whose position is relative to itself — so an unclosed element was reported a few
characters into the file whichever one it was about. Every test here asserts *which* diagnostics
were reported and none asserted where, which is why it survived. And every diagnostic message was
formatted with `CultureInfo.CurrentCulture`, which localises nothing (the templates are English) and
makes one machine's compiler output differ from another's; fixed in `Vixen.Core.Syntax`.

Licensed under Apache-2.0.
