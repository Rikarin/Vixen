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

⚠ **Three attribute names are universal**, meaning they mean the same on a component tag as on an
element and are never assigned as properties: `class`, `style`, and `binding-path`. The last is doc
36's: it names a member of whatever an editor is editing, and the join happens *after* the tree is
built, by a pass that walks it — which is Unity's rule and the only one available, since a `Build`
body cannot name a C# type without the markup naming one too. It lands as a style-tree attribute,
and `UiElement.Attribute` is how the pass reads it back.

**`class` and `style` are the two that do not**, and the second is the interesting one.

```html
<div style="left: @Left; top: @Top" />
<ProgressBar style="width: 42%" />
```

A `style` is a *cascade origin*, not data: it outranks every author rule, including one marked
`!important` in a user stylesheet. The engine has had that origin all along —
`CascadeRanks.NormalInline`, `UiElement.SetStyle`, an `InlineStyleStore` the resolver already reads —
and what was missing was only the route from markup to it. Until then `style="width: 42%"` reached
`StyleTree.SetAttribute`, so it became a string a `[style]` selector could match on and nothing could
read, and the element came out however wide the stylesheet said, with no diagnostic. `StatisticsView`
moved its budget bar onto a `ProgressBar` because of it, which was an improvement; `GpuTimelineView`,
whose bars are a measured width times a timestamp, had no such control to move onto.

Three things about it are worth knowing:

- **It takes back the properties it wrote and no others.** `class`'s rule, and it matters more here:
  a `DataGrid` writes its rows' `top` and a `DockingHost` its panes' `flex-grow`, from their own code
  and after the markup has been applied. An attribute that owned the element's whole inline set would
  silently unposition all of them.
- **The value goes through the same parser a rule body does.** So `style="padding: 4px 8px"` becomes
  the four longhands the layout actually reads, and means exactly what those characters mean in a
  stylesheet. A splitter on `;` and `:` would be four lines and would disagree about a `;` in a
  string, a `:` in a `url()`, and every shorthand.
- **A brace is refused**, with a diagnostic, because the parse wraps the text in a throwaway rule and
  `style="} tabs { display: none"` would otherwise close it.

⚠ **It is still the escape hatch and not the first answer.** Everything a rule *can* say should be
said in one — a `display: none` toggle is a class, and `UiElement.OffsetX` is cheaper than either
whenever moving a box is enough. What this is for is the lengths no stylesheet was given: a
virtualised row's offset, a splitter's ratio, a bar whose left edge is a fraction of a width nobody
knew at build time. And because the parse is a real one, a caller moving one number sixty times a
second is better served by `SetStyle` directly; the binding skips the parse only when the text is
unchanged.

### There is no `id`, and that is a decision rather than an omission

The runtime has every piece of one: `StyleTree` stores an identifier per node, `#id` compiles in
`SelectorCompiler`, `SelectorMatcher` matches it, it carries the specificity CSS gives it, and
`UiElement.Add(tag, id, classes)` takes one. Adding `id="…"` to the binder would be four lines.

**Nothing would use it.** Across the whole tree there is not one `#id` selector in any `.vcss`, and
every caller that has ever passed an `id` is a test in `Vixen.Ui.Tests` — nine call sites, no
production code. The two things an `id` is for in a browser are both answered here already: styling
one particular element is a class, and *getting* one in C# is `ref`, which hands back the object
rather than a name to look up and is checked by the compiler. A third spelling of "this element",
with a specificity tier of its own for the cascade to reason about and no caller to justify it, is
worth less than the gap.

⚠ **It is worth saying what would change the answer**, because "the web has it" will not. An `id`
earns its place the day something needs to name an element it cannot hold — a stylesheet shipped
separately from the panel it styles, or an accessibility relation like `aria-labelledby`, where the
reference is by name because the two ends are written apart. Neither exists yet.

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
  author wants is [`refs`](#refs-is-the-loops-answer-and-it-is-keyed-not-listed), or the element the
  loop is *inside*.
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

## `refs` is the loop's answer, and it is keyed, not listed

`ref` refuses to be inside a `@for`, and until this existed that refusal was the end of the road: a
list of controls could not be reached from C# at all, which is what made `AudioMixerView` — whose
every strip's fader handler reads *its own* mute — unportable rather than merely awkward. It is
ported (2026-08-23), and `Editor/Vixen.Editor.AssetEditors/Audio/AudioMixerView.vxml` is what the
snippet below looks like at full size.

```xml
@code {
    public ElementRefs<Slider> Faders { get; } = new();
}

@for (var bus in Buses.Value) {
    <Slider key="@bus" refs="@Faders" change:Value="@(v => Write(bus, v))" />
}
```

⚠ **The handle is keyed on the iteration, not filled by the body**, and that is the whole
correctness argument. A `List<T>` the body appended to would be appended to once per key *ever* —
`BuildContext.For` reuses a surviving key's region and does not re-run its body — so after a filter
or a reorder `rows[2]` is a different control from the third row, silently. `refs` files the element
under the identity the reconciler matched on, taken from the loop rather than recomputed at the tag,
and drops the entry with the row's region. So the key you look up with is the expression you wrote in
`key=`, and it is the item itself when the loop declares none.

`refs` outside a `@for` is `VXML2013`, the mirror of `VXML2010`: there is no key out there to file
under. One element held once is what `ref` is for.

⚠ **A handle is filled by an effect, so it is empty until the next flush** — the one asymmetry with
`ref`, which is assigned in the straight-line body. `ElementRefs<T>`'s indexer throws and says so
rather than answering null, because a null would arrive as a `NullReferenceException` somewhere
else; `TryGet` is the quiet form. In the use that matters this cannot bite: a row's handler runs long
after its own row was built.

## `on:keydown.capture`, and the two ways an event name can be missing

`capture` has been in the modifier list since the list was written, and `BuildContext.On` has turned
it into `RoutingStrategy.Capture` for as long as it has taken modifiers. What made
`on:keydown.capture` throw was neither: `BuildContext.Subscriptions` had ten entries and every one of
them was a pointer gesture, so the name resolved to nothing and the runtime said *"'keydown' is not
an event"* at compose. **The syntax was never the limitation — the table was.** Three editor pickers
kept a hand-written `AddHandler<KeyEvent>(…, RoutingStrategy.Capture)` in `OnComposed` on the
strength of a diagnosis that was wrong.

`keydown` and `keyup` are two names over one `KeyEvent`, split on `KeyAction` the way `pointerdown`
and `pointerup` are split on `PointerAction`: a handler that had to test for itself would fire twice
per keystroke until somebody noticed. `textinput` is registered beside them, and is not a
convenience. `KeyEvent.Key` is a physical position by its US-QWERTY legend, so a handler reading a
letter out of `on:keydown` types `q` when an AZERTY keyboard says `a` — and an author who cannot name
the event that carries characters will use the one that is there.

⚠ **A handler that wants the event must be an explicitly typed lambda, and a method group will not
do.** The emitter writes one call —

```csharp
ctx.On(n1, "keydown", (KeyEvent e) => Keyed(e), "capture");
```

— for both of `On(…, Action, …)` and `On<TEvent>(…, Action<TEvent>, …)`, because *which* event type
a name delivers is the table's business and the binder resolves no types. So `TEvent` is inferred
from the argument, and `@Keyed` supplies nothing to infer it from: a method group has no natural type
until the delegate's parameter types are known, which is exactly what is being solved for. However
singular `Keyed` is, `on:keydown="@Keyed"` is *"cannot convert from 'method group' to
'System.Action'"* — on the handler's own characters, which is at least the right place. `@(() => …)`
and `@Increment` keep working unchanged; they are `Action`s and want no argument.

## `change:` is a value binding, and `on:change` could not have been one

`on:` maps a name through a table of `Action<UiElement, Action<UiEvent>, RoutingStrategy>` — a routed
gesture. **No entry in it can hand a handler a value**, so `on:change` was never a missing
registration. Six controls do also raise a routed `ValueChangedEvent<T>`, but they are six of about
thirty and name a different `T` each, so one name could not have subscribed to them either.

So `change:Value="@(v => …)"` is not an event at all. It names a `[UiProperty]` — the same thing
`bind:Value` names, resolved the same way — and rides `UiElement.PropertyChanged`, which fires for a
drag, a key, an access key and the panel's own code alike. Nothing is registered per control and
nothing is reflected over. The emitter writes

```csharp
ctx.Changed(n3, "Value", () => n3.Value,
    v => Write(bus, v));
```

⚠ **The property is read back as well as named, and the reader is what types the handler.**
`Changed<T>` can infer nothing from `v => …`, so `() => n3.Value` fixes `T` first — the same
two-lambda shape `bind:` emits, buying the same three things: the property must exist, it must be
readable, and no cast or box appears in the delivery path. It is the tag object rather than
`BuildContext.Host(…)`, unlike `bind:`, because a `Component` has no `[UiProperty]` and so has to
fail — which it does, as "cannot convert", on the attribute's own characters.

⚠ **And a selection is a value, which is not the same as saying a control's selection is a
property.** `change:Selection` on a `TreeView` throws, and correctly: `Selection` is a read-only view
over a `HashSet` the control mutates in place, so it is the same instance before and after every
change and nothing riding `PropertyChanged` could ever have reported it. The remedy is not on this
side of the line — the control publishes a snapshot beside the set, `TreeView.SelectedNodes`, and
`change:SelectedNodes` then works like any other value. The general rule for a control author is that
**a collection is only bindable as a value**, and that the value has to be written where the mutation
happens rather than computed on read.

⚠ **A change made while the document's effects are draining is not reported**, and that is the one
rule `change:` does not share with `bind:`. Such a write came *from* a binding, which means it came
from the model. It is not merely redundant to send it back: the forward binding of
`<Slider Value="@bus.Gain" change:Value="…" />` first writes one flush *after* the subscription
exists, so without the rule every mixer would post an undo entry for a gain nobody touched, on open.
The hand-written C# this replaces cannot have that bug, because there the value is assigned before
the `+=`. What it costs is a change the control makes to itself *during* a binding's write — a
coerce that clamps — which the model is not told about.

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

### ⚠ The same rule governs `@if`, where nothing diagnoses it

**`@for` and `@if` are one mechanism** — `Switch` and `For` are deliberately the same construct, for
the reason `Switch`'s own remark gives — and `Switch` rebuilds its arm **only when the arm index
changes**. So an arm is a surviving region on exactly the terms a row is, and the rule reads the same
way: *a binding may close over a region's identity and never over its content.* For a row the
identity is the key. For an arm it is the **predicate**, which usually identifies far less.

```html
<!-- Wrong. Choosing a different cell does not change which arm is live, so the arm is not rebuilt
     and `shown` is whatever was selected the first time anything was. -->
@if (Chosen is { } shown) { <FactValue Text="@shown.Label" /> }

<!-- Right. The condition may be a shape; every readout goes back through the signal. -->
@if (Chosen is null) { … } else { <FactValue Text="@ChosenLabel" /> }
```

⚠ **This is the sharper edge of the two, because there is no `VXML2011` for it.** A `ref` in a loop
is `VXML2010`, a `refs` outside one is `VXML2013`, and a projected key is `VXML2011` — the loop shape
is watched from three sides. A pattern variable in an `@if` arm is ordinary, legal C# that compiles,
runs, and is correct for the first value it ever sees. It is not decidable here for `VXML2011`'s
reason and one more: the arm's own condition *must* be allowed to read the thing the arm is about, so
the mistake and the correct spelling mention the same variable.

Found writing `VariationHarnessView`, where it survived the whole existing suite because every test
selected exactly one cell. **If a panel has a detail pane over a selection, the test that catches
this is the one that selects a second thing.**

The runtime it calls is `Vixen.Ui.Composition`. The emitter's gate compiles its output against that
assembly, loads the result, builds it into a `UiDocument` and drives it with a signal — so what is
tested is markup to syntax tree to component model to C# to IL to an element tree, and not the shape
of a string.

## What is owed

- ~~**Incremental reparse.**~~ Landed, and this bullet used to break off mid-sentence saying it had
  not. `VxmlParser` takes the shared `Blender` and `TryReuseContent` reuses a previous tree's green
  node at a content boundary when the blender has one whose new position and width line up with the
  token stream. ⚠ **The unit of reuse is a content node whose subtree reported no diagnostic**, which
  is what makes it sound: `openElements` is the only enclosing state a content node's parse reads,
  and every branch that reads it reports. `IncrementalTests` pins that an incremental reparse equals a
  full one, including over a run of edits.
- **`bind:` update events** (`bind:value:oninput`).
- **A generic base.** `@inherits` takes a `NameToken`, which carries dots and not angle brackets, so
  `@inherits Row<T>` does not lex. Same limit `@using` has, and nothing has needed it.

## `OnComposed` is the build-time hook, and it was owed a paragraph rather than a feature

It is easy to read `Component`'s surface — `Build`, and a virtual `OnUnmounted` — and conclude that
a markup panel has nowhere to do something once when it is built. It has: the emitter declares
`partial void OnComposed()` on both flavours and calls it as the last statement of the body, so a
`.vxml` that wants to subscribe to something, take a first reading or wire two parts together writes
it there. `GpuTimelineView` does exactly that — it hooks `LayoutFinished` and takes its first
measurement — and `OnUnmounted`, which the element flavour also declares as a partial, is where it
lets go.

Two things about its timing are load-bearing and neither is a defect:

- **It re-runs after a hot reload,** deliberately: it is emitted *inside* `Build`, and
  `BuildContext.Rebuild` re-enters `Build`. That is right for wiring, which has to point at the new
  elements, and it means anything expensive or non-idempotent does not belong in it.
- ⚠ **It runs before the panel's caller has configured it,** and this is the one that has bitten.
  A parent assigns a component's parameters *after* `Child<T>` returns, and a host assigns them after
  `BuildContext.Build<T>` returns — so a hook here cannot see them. `MemoryView.Take()` was moved to
  `DiagnosticsModule` for this reason, and moving it fixed a real bug: `Control.OnCreated` called it
  and the host called it again after assigning `Providers`, so the panel took two readings and threw
  away the one that had them. **No build-time hook could have fixed that**, because the moment it
  wants is "after my caller has finished with me" and nothing in the framework knows when that is.
  The two answers that do work are the ones the panels use: a signal-backed parameter, which handles
  re-assignment as well as the first one, or the host saying so — which for a reading somebody takes
  once is the honest shape.

## What `Component` has and an element-flavoured class has

| | `Component` | `@inherits` |
|---|---|---|
| Build | `Build(ctx)`, abstract | generated `OnCreated` → `BuildContext.Compose` |
| After the body | `partial void OnComposed()` | `partial void OnComposed()` |
| Leaving | `protected virtual void OnUnmounted()` | `partial void OnUnmounted()`, from generated `OnRemoved` |

⚠ **An `@code` block may not write `OnCreated` or `OnRemoved`:** the scaffold owns both, and writing
one is a duplicate-member error from Roslyn rather than a `VXML2xxx`. `OnComposed` and `OnUnmounted`
are what the file gets instead, and they are the same two names on both flavours on purpose.

⚠ **A `Component` used to leak every region it opened against a nested element, and the note that
said so named one cause where there were two.** A region hangs off the element its content has as a
*parent*, so a loop written inside a `<div>` opens against that div — nothing above it pointed at
it, and clearing the enclosing branch removed the div while every row went on reading signals. That
is now fixed by `BuildContext.RegionOf` linking what it opens into the region being built, which
also fixes plain `@if` and `@for`: the defect was never a property of components, it reached any
control flow written one level in, and `A_branch_that_leaves_takes_its_effects_with_it` missed it
only by putting its effect at the top of the arm.

The second cause is the one the panels actually hit. Nothing ended a component whose host was
*removed* — a component tracks its teardown against the region that built it, and one built onto a
mount has no region above it, so `InspectorView.Rebuild` removing the body's children left a whole
form subscribed on every selection change. `UiDocument` now announces a host's removal to whatever
mounted there, which is the `Component` counterpart of the `OnRemoved` an `@inherits` element gets.
Of the seven shipped panels, `UndoHistory`, `TaskCenter`, `StatisticsView` and `MemoryView` were
leaking; `TerrainBrushInspector`, `StandardFrameInspector` and `LookInspector` were not, and the
reason is worth keeping — they contain no dynamic expression at all, so they build no effects and
had nothing to leave running. Adding one `@` to any of them would have made them leak.

⚠ **Two bugs in this project were found by compiling it into a source generator rather than by any
test here.** `VXML1002` and `VXML1003` read their span off a node still under construction — a node
with no parent, whose position is relative to itself — so an unclosed element was reported a few
characters into the file whichever one it was about. Every test here asserts *which* diagnostics
were reported and none asserted where, which is why it survived. And every diagnostic message was
formatted with `CultureInfo.CurrentCulture`, which localises nothing (the templates are English) and
makes one machine's compiler output differ from another's; fixed in `Vixen.Core.Syntax`.

Licensed under Apache-2.0.
