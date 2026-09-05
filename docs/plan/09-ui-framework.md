# 09 — UI Framework

The largest net-new subsystem, and the one that makes the "framework for Photoshop/Blender-class
applications" claim either true or marketing. It is built from six independently testable pieces:

```
Vixen.Ui.Markup      .vxml → syntax tree → bound component model → generated C#
Vixen.Ui.Reactive    signals: Signal<T>, Computed<T>, Effect  (ADR-007)
Vixen.Ui.Layout      flexbox (Yoga algorithm) + grid + block  (ADR-006)
Vixen.Ui.Styling     .vcss via ExCSS + cascade + selector matching + transitions
  └─ .Utilities      the Tailwind-like utility generator and design-token system
Vixen.Ui.Text        HarfBuzz shaping + MSDF atlas + line breaking + bidi
Vixen.Ui             element tree, property system, event routing, input, rendering, focus
  └─ .Controls       the widget library
  └─ .Controls.Advanced  DataGrid, TreeView, Docking, PropertyGrid, Timeline, Canvas
  └─ .HotReload      dev-only watcher + state-preserving reload
```

## VXML — markup

### Syntax

```html
<!-- Assets/Ui/Counter.vxml -->
@component Counter
@using Vixen.Ui.Controls

@code {
    private readonly Signal<int> _count = new(0);
    private readonly IReadOnlySignal<string> _label;

    [Parameter] public required string Title { get; init; }
    [Parameter] public int Step { get; init; } = 1;
    [Event]     public Action<int>? CountChanged { get; init; }

    public Counter() => _label = Computed(() => _count.Value == 1 ? "time" : "times");

    private void Increment()
    {
        _count.Value += Step;
        CountChanged?.Invoke(_count.Value);
    }
}

<div class="flex flex-col gap-2 p-4 rounded-lg bg-surface-2">
    <Text class="text-lg font-semibold">@Title</Text>

    <Text class="text-sm text-muted">
        Clicked @_count.Value @_label.Value
    </Text>

    @if (_count.Value > 10) {
        <Callout kind="warning">That's a lot of clicking.</Callout>
    }

    <div class="flex flex-row gap-1">
        @for (var i in Enumerable.Range(0, 3)) {
            <Button key="@i" variant="ghost" onclick="@Increment">+@Step</Button>
        }
    </div>

    <slot name="footer" />
</div>

<style scoped>
    .bg-surface-2 { background-color: var(--color-surface-2); }
    :host:hover   { outline: 1px solid var(--color-accent); }
</style>
```

Design choices, each with a reason:

| Choice | Reason |
|---|---|
| `@component Name` header, not a wrapping element | Keeps the file's top level a valid element list; avoids the Razor `@page`/`@inherits` soup |
| `@code { }` block for C# | Familiar from Razor; keeps logic out of attributes. Multiple `@code` blocks concatenate |
| `@expr` for interpolation, `@if`/`@for`/`@switch` with **braces, not `}` sentinels** | Razor's `}`-terminated blocks are the single worst part of its syntax and the hardest to parse and error-recover |
| `[Parameter]`/`[Event]` attributes on properties | Explicit public surface; the generator produces a typed builder so `<Counter Title="x" Step="2" />` is compile-checked |
| PascalCase tag ⇒ component, lowercase tag ⇒ intrinsic element | Same rule as React/Blazor; unambiguous for the parser, no registry lookup needed at parse time |
| `onclick="@Handler"` | Attribute-shaped event binding; `@` marks the value as an expression. Also `on:click` accepted as an alias for symmetry with modifiers (`on:click.stop`, `on:keydown.escape`) |
| `key="@i"` on `@for` children | Required for the keyed reconciler; a missing `key` in a `@for` is a **warning** with a documented perf consequence, not a silent index-keyed fallback |
| `<slot name="…" />` + `bind:` two-way | Content projection and two-way binding are table stakes for a widget library |
| `<style scoped>` in-file | Component-local CSS with an auto-generated scope attribute; also `class="…"` utilities from the global system |

### Parser (`Vixen.Ui.Markup`)

Hand-written recursive descent (ADR-009), built on `Vixen.Core.Syntax` — the same green/red tree
infrastructure Raven already has, extracted in Phase 0. This means VXML gets, for free: full trivia
fidelity (so a formatter can round-trip), precise spans (so squiggles land on the right character),
`WithChanges` incremental reparse (so hot reload is fast), and the shared `Diagnostic` model (so the
editor's error list has one implementation for Raven, VXML, and VCSS).

Pipeline:

```
SourceText
  → VxmlLexer      (element/attribute/text/interpolation modes; a small mode stack, not regex)
  → VxmlParser     (recursive descent; error recovery: unclosed tag, unknown attribute,
                    dangling @, mid-typing states — each has a targeted diagnostic and a
                    recovery that keeps the rest of the file parseable)
  → VxmlSyntaxTree (green/red, from Syntax.xml via the shared generator)
  → Binder         (resolves tags → component-or-intrinsic, attributes → parameters, events,
                    bindings and keys; produces BoundComponent with diagnostics. No semantic
                    model — see below)
  → Emitter        (BoundComponent → C# partial class)
```

The **binder is the interesting part**, and it turned out to be interesting for the opposite reason
to the one given here. ~~It must resolve `<Counter Title="x" />` against the C# type `Counter` and
typecheck `Title`, running inside the source generator where the Roslyn `Compilation` is
available.~~ — **corrected in the build: it resolves no types at all.** If the emitter writes the
tag name where a type name goes and the attribute name where a property name goes, each under a
`#line`, then an unknown component, a misspelt parameter and a wrong expression type are *all*
reported by Roslyn — against the right character of the `.vxml` — with no type resolution on the
binder's side and no dependency on a `Compilation`. The conclusion the original paragraph reached
was right; the mechanism it proposed was more machinery than the conclusion needed.

What is left for the binder is the set of mistakes Roslyn cannot catch, because they are about
markup rather than about C#: a duplicate attribute, an event handler given a string literal, two
slots claiming one name, a loop whose elements have no identity. There is deliberately no diagnostic
range for type errors — a second and worse typechecker is what this design exists to avoid.

That last detail — `#line` mapping — is what makes the whole approach viable. Without it, a typo in an
interpolation produces an error in generated code the user has never seen.

### Compilation output (ADR-010 — fine-grained, no VDOM)

```csharp
// generated: Counter.g.cs
partial class Counter : Component
{
    protected override void Build(BuildContext ctx)
    {
        var root = ctx.Element("div", static e => e.Class("flex flex-col gap-2 p-4 rounded-lg bg-surface-2"));

        var title = ctx.Child<Text>(root, static t => t.Class("text-lg font-semibold"));
        ctx.Bind(title, static (t, s) => t.Content = s.Title, this);          // no closure alloc

        var counterText = ctx.Child<Text>(root, …);
        ctx.Effect(() => counterText.Content = $"Clicked {_count.Value} {_label.Value}");

        ctx.If(root, () => _count.Value > 10, static (c, p) => c.Child<Callout>(p, …));

        var row = ctx.Element(root, "div", …);
        ctx.For(row, Enumerable.Range(0, 3), static i => i, (p, i) => { … });  // keyed

        ctx.Slot(root, "footer");
    }
}
```

- `ctx.Effect(…)` registers one effect per dynamic expression. Setting `_count.Value` invalidates
  exactly that effect, which assigns exactly that property. **No tree walk, no diff, no allocation.**
- `ctx.If` swaps a subtree in/out on a boolean signal, disposing the branch's effects on exit.
- `ctx.For` is a keyed reconciler over a collection signal: computes a minimal
  move/insert/remove set (longest-increasing-subsequence based, as Solid and Vue Vapor do) and touches
  only changed children.
- Static lambdas plus explicit state parameters throughout, so a steady-state UI allocates nothing.

## Signals (`Vixen.Ui.Reactive`)

Per ADR-007: own implementation, SignalsDotnet's API as the reference, Angular's push-invalidate /
pull-evaluate semantics.

```csharp
Signal<T>            // writable; .Value get/set; optional custom comparer
IReadOnlySignal<T>   // read-only view
Computed<T>          // derived; lazy; memoised; auto-tracked dependencies
Effect               // side effect; runs on dependency change at a scheduled frame phase
CollectionSignal<T>  // fine-grained add/remove/move notifications (for @for)
AsyncComputed<T>     // async derivation with loading/error/value states, cancellation on re-run
LinkedSignal<T>      // writable but resets when its source changes (Angular's linkedSignal)
Untracked(() => …)   // read without subscribing
Batch(() => …)       // coalesce writes; effects run once at the end
```

Implementation:

- **Versioned push-pull.** A global `uint` version counter; writing a signal bumps its own version and
  walks its dependent list marking them `Dirty` (no evaluation). Reading a `Computed` checks whether any
  dependency's version changed since last evaluation; if so it re-runs, then compares the result and
  only bumps its own version if the value actually differs (equality short-circuit stops propagation —
  the "glitch-free" property).
- **Auto-tracking** via an ambient `[ThreadStatic] ConsumerNode? _activeConsumer`; reading a signal
  while a consumer is active adds the edge.
- **Pooled edge storage.** ~~Dependency lists are slices of a shared `ChunkedArray<Edge>` with free
  lists~~ — **corrected in the build**: they are free-listed `Edge[]` arrays bucketed by power-of-two
  length, not slices of an arena. A slice has to be one contiguous `Span` and chunks are not
  contiguous with each other, so an arena needs either a cap on edges per node at the chunk size or a
  second allocation path for the nodes that exceed it. Pooling whole arrays gives the property that
  was actually wanted with no cap and no special case. Either way: not `List<T>` per node, and a
  steady-state UI does zero allocation on signal reads/writes.
- **Liveness.** A producer notifies only consumers that something is *watching*, transitively; a
  computed nobody reads registers no edge back from its dependencies at all and is verified by
  polling on the next read. This was not in the original sketch and is not optional — without it,
  every computed ever created is retained forever by whatever signal it read once.
- **Effects are queued, not immediate.** `EffectScheduler` drains in a defined frame phase
  (`UiSystem.FlushEffects()` between input and layout), with a per-frame budget and a "runaway effect"
  detector (an effect that re-dirties itself > N times in a frame is logged with its origin and
  suspended, instead of hanging the app). The run count is per *flush*, not per lifetime: an effect
  that runs once a frame forever is correct. An effect that throws is suspended and reported the same
  way, because a UI framework where one bad binding takes the window down is one nobody can develop
  against.
- **The equality short-circuit reaches the effects too.** Being woken means a dependency *may* have
  changed; the effect polls its dependencies on the way in and does not run if none of them moved.
- **Diamond correctness** test: `a → b, a → c, b+c → d` evaluates `d` exactly once per `a` change.
- **Thread affinity.** Signals are single-threaded. The check is a runtime opt-in
  (`ReactiveGraph.OwningThread`) rather than the debug-mode assertion originally specified: it costs
  one comparison against a usually-null static, a plug-in touching the graph from a worker thread is
  worth catching in a shipping editor, and a library-level default would force a parallel test host —
  or an editor with two independent graphs — onto one thread. `AsyncComputed` handles off-thread work
  and marshals results back through `EffectScheduler.Post`, which is the only member of the assembly
  another thread may call.
- **`AsyncComputed` is two functions, not one.** A synchronous, tracked *request* and an
  asynchronous, untracked *load*. Dependency tracking cannot survive an `await` — the ambient
  consumer is thread-local and the continuation is elsewhere — so a single `async` computation would
  silently record half its dependencies.
- **`Batch` is about flush ordering.** Not about coalescing writes, which is what `batch` is for in
  every other signal library and which is already true here without it: effects are queued and drained
  once per frame, and computeds are lazy, so a hundred writes between two frames cost one run and one
  recomputation. What a batch adds is that an explicit `Flush()` asked for inside it happens after the
  group rather than in the middle of it.

Signals also serve non-UI use: the editor's document model, the inspector's property bindings, and
`Vixen.Ecs` change-version bridging (`world.Observe<Position>(entity)` yields a signal).

## Layout (`Vixen.Ui.Layout`)

Per ADR-006: Yoga's flexbox algorithm, re-implemented over a struct-of-arrays node store, validated
against Yoga's own conformance suite.

### Storage

```csharp
// SoA: parallel NativeArrays indexed by LayoutNodeId (dense int). As built:
sealed class LayoutTree
{
    NativeArray<LayoutStyle>     Styles;  // ~400 bytes, all values as (float, Unit) pairs — the
                                          //   120 above predated counting the nine CSS edges
    NativeArray<LayoutResult>    Results; // position[4], dimensions, margin/border/padding[4],
                                          //   direction, and the 8-entry measure cache
    NativeArray<LayoutLinks>     Links;   // parent, and (offset, count, capacity) into ChildArena
    NativeArray<LayoutNodeState> State;   // Live, Dirty, HasNewLayout, HasMeasureFunction, …
    ChildArena                   Children;// every node's child ids, contiguous, power-of-two blocks
}
```

One allocation per array, growing geometrically. 100 000 nodes ≈ 40 MB and zero GC objects, versus the
reference port's ~400 000 heap objects for the same tree — which is the comparison that matters, and
which the corrected style size does not change.

**Children are a contiguous run of ids, not a `firstChild`/`nextSibling` list.** The algorithm
addresses children by index inside its inner loops — a flex line *is* a range of them — and a linked
list makes each of those a walk, turning several O(n) passes into O(n²) on the widest nodes in the
tree. The list is in a shared arena with power-of-two blocks and free lists, so it is still no heap
object per node.

**The measure cache is 8 entries per node, not 16.** Eight is Yoga's own figure from measuring real
layouts; the 16 here came from an older revision of the same comment, and doubling the largest term
in a node's footprint for the last 2 % of cases is not a trade worth making.

### Algorithm scope

- **Flexbox**, complete: `flex-direction`, `wrap`, `justify-content`, `align-items`, `align-self`,
  `align-content`, `flex-grow/shrink/basis`, `gap`/`row-gap`/`column-gap`, `order`, `position`
  (relative/absolute/static), `inset`, `margin`/`padding`/`border` (with `auto` margins),
  `width`/`height`/`min`/`max` (px, %, `auto`), `aspect-ratio`, `overflow`, `display: none/flex`,
  `direction: ltr/rtl`, box-sizing, baseline alignment.
- **Measure functions** for leaf content (text, images) with Yoga's measure cache — text measurement is
  the dominant cost in a real UI and the cache is what makes it tractable.
- **Dirty propagation**: setting a style marks the node and its ancestors dirty; layout descends only
  into dirty subtrees. A static panel costs zero per frame.
- **Grid** as a *separate* algorithm on the same store (Yoga has no grid): `grid-template-rows/columns`
  with `fr`/`minmax`/`repeat`/`auto-fill`, named lines and areas, `grid-auto-flow`, item placement,
  `align/justify-items/self`. This is a genuinely large piece of work (CSS Grid is a harder spec than
  flexbox) and is scheduled explicitly.
- **Block/inline flow**: minimal — enough for a paragraph of mixed inline text and images. A full
  CSS inline formatting context is out of scope and stated as such.
- **Parallel layout**: independent subtrees (those with a fixed available size) layout as jobs. Text
  measurement of siblings parallelises well and is where the win is. Not built: it needs a
  measurement of the serial version to beat, and there is no layout benchmark yet.

### Correctness

Yoga's repository generates its test suite from HTML fixtures rendered in a real browser. That
generator is run against Vixen (`references/yoga` → `Tools/Vixen.YogaTestGen`, emitting into
`Vixen.Ui.Layout.Tests/Generated/`), producing several hundred conformance tests. **Flexbox is not
"implemented" until that suite is green.** This is the single most important de-risking decision in the
UI plan: it turns "re-implement a subtle CSS algorithm" from a research project into a red/green loop.

✅ **Done: 534 fixtures, green.** It paid for itself on the first run — 530 passed immediately, and
of the four that did not, one was a real rule the port had missed (a degenerate `aspect-ratio`
behaves as `auto`; css-sizing-4). Nine of Yoga's 543 are `display: contents` and are skipped by
name, which is the scope stated below.

**And a limit of it, worth stating because the plan leans on external oracles so heavily.** Deleting
the CSS Flexbox §4.5 automatic minimum size leaves all 534 green: Yoga's generator emits no fixture
that shrinks a measured leaf past its own content. An oracle answers the questions it was built to
ask and no others, so the sections it does not reach still need tests written by hand —
`AutomaticMinimumSizeTests` is that, for this one.

## Styling (`Vixen.Ui.Styling`)

### VCSS

CSS as understood by ExCSS 4.3.2, with a documented supported-subset. Parsing is ExCSS; **everything
after parsing is Vixen's**, because ExCSS is a parser, not a style engine.

✅ **Verified before it was built on** — [spikes/vcss-excss](spikes/vcss-excss/RESULT.md). The
selector tree is fully typed and reachable (so Vixen writes a visitor, not a parser), specificity is
computed for us, shorthands are expanded, and both `var()` and properties ExCSS has never heard of —
including this document's own `spring()` transition — survive verbatim.

⚠ **`@layer` is Vixen's to parse.** ExCSS 4.3.2 predates cascade layers and hands the whole rule back
as an unknown one with its text intact. Both forms need reading — the statement `@layer a, b;` that
fixes order, and the block `@layer name { … }` whose body is handed back to ExCSS — and the same
applies one level down inside `@media`. This is a bounded piece of the stylesheet loader rather than
a hole in the design, but it is work the plan did not know about. ✅ Written, in `LayerRuleParser`.
It turned out to need brace matching that skips strings and comments: a body containing
`content: "}"` would otherwise be cut in half and the rules after the cut would load into no layer
at all.

⚠ **ExCSS normalises what it can see, and it cannot see through a `var()`.** A second finding, from
building the cascade rather than from the spike. `color: red` reaches Vixen already normalised to
`rgb(255, 0, 0)`, but `color: var(--c)` with `--c: red` reaches it as `red`, because any value
containing a `var()` is left verbatim and substitution happens afterwards, in Vixen. Both forms are
correct CSS and they are not the same string, so **every value parser in the property system must
accept both**. Cheap to know before the property system is written; expensive to find inside it.

Supported: type/class/id/universal selectors, descendant/child/sibling combinators, attribute
selectors, `:hover`/`:active`/`:focus`/`:focus-visible`/`:disabled`/`:checked`/`:first-child`/
`:last-child`/`:nth-child()`/`:empty`/`:not()`/`:is()`/`:where()`,
custom properties (`--x`) with `var()` and fallbacks, `@media` (width/height/orientation/
prefers-color-scheme/dpi), `@supports`, `@keyframes`, `@font-face`, `@import`, `@layer` (cascade
layers — worth having, it is how the utility system and component styles coexist cleanly).

Not supported, and documented: floats, tables, `position: fixed` relative to viewport (there is no
viewport in a game overlay), CSS filters beyond a curated set, `calc()` beyond `+ - * /` on
compatible units, container queries (P2), `:has()` (P2 — expensive to match incrementally),
pseudo-elements.

⚠ **`::before` and `::after` were in the supported list above and were never supported — corrected
here rather than left standing.** This is [doc 43](43-web-styling-parity.md)'s finding **F6**, and
what the code did was worse than nothing: `SelectorCompiler` interned the pseudo-element's name onto
the compiled `Selector`, compiled the rest of the compound as though it were absent, and *nothing
anywhere read the field*. So `p::before { content: "→"; color: red }` matched the paragraph and
turned **the paragraph** red — a rule that looked like it worked, with this document vouching for
it. The compiler now refuses the selector with a diagnostic, which reaches the log through
`UiDocument`'s drain.

A pseudo-element is a **generated box**: a box in the layout tree with no element behind it.
Materialising one is planned as doc 43's **A12**, not as a fix to the cascade.

⚠ **It is no longer the one-node-one-box invariant that blocks it, which is what this said.** Both
of the things named alongside it have landed: inline fragmentation relaxed the invariant so that a
node may have *more* boxes than one, and anonymous block boxes needed *no* stored box at all — they
take initial values for every non-inherited property, so they are never painted or hit-tested and
are a line walk over a sub-range of a container's children.
`Core/Vixen.Ui.Layout.Tests/InlineKnownGaps.txt` records both. What A12 is left with is the half
that was always its own: a generated box carries a **style** of its own, so it needs a second style
slot rather than a second rectangle.

### Cascade and matching

The performance-critical part. Naive selector matching is O(elements × rules).

- **Rule bucketing** by rightmost simple selector into hash maps keyed by id, class, and type. An
  element only tests rules whose rightmost key it could match — the standard browser technique, and it
  reduces candidate rules from thousands to single digits.
- **Ancestor bloom filter**: each element carries a 128-bit bloom of its ancestors' ids/classes/types;
  a descendant combinator is rejected without walking the tree if the bloom says the ancestor cannot
  exist. This is Gecko/Servo's technique.
- **Right-to-left matching** of the remaining candidates.
- **Style sharing cache**: elements with identical (**parent element**, tag, id, class set, inline
  style, pseudo state) share a `ComputedStyle` instance by hash.

  ⚠ **Corrected.** This originally said *parent computed style*, and that is unsound — found while
  building it, and now covered by a test that fails if the key is widened back. Two parents can hold
  the same computed style and still be told apart by a selector: given `.a { color: red }` and
  `.b { color: red }`, an `.a` and a `.b` intern to one identical style, and then `.a .row` matches
  one of their children and not the other's. Keyed on the parent *element*, sharing happens between
  siblings and every descendant and child combinator is sound for free, because the two elements
  have literally the same ancestor chain. Gecko does this, for this reason.

  Sharing is additionally refused whenever any rule matches on something the key cannot carry — a
  position pseudo-class, a sibling combinator, an attribute selector, or `:empty`.

  ⚠ **`:empty` is about contents, and Vixen's contents are not the DOM's.** CSS means "no child
  *nodes*", and a run of text is a node — so a paragraph with words in it is not empty. Text here is
  a property of the element rather than a node of its own (the departure recorded under *Text*
  below), so a `:empty` that counted children would call every label in the document empty. The
  style tree carries a has-text bit alongside the child count, `Vixen.Ui` sets it from
  `UiElement.Text`, and `:empty` reads both. No invalidation entry is owed: a text change is already
  a cold pass.

  **What this does not cost.** Two mechanisms were conflated under one heading and they separate
  cleanly. *Interning* is what gives every identical cell the same `ComputedStyle` reference, and it
  is untouched by the narrower key: 10 000 cells across 100 rows still hold **one** object, so the
  reference-compared invalidation below still works exactly as designed. *Sharing* is what lets the
  cascade be **skipped**, and that now happens per row rather than per grid — 102 cascades for
  10 001 elements rather than 1. Still the reason a Vixen `DataGrid` can render, and now also
  correct.
- **Invalidation**, not recomputation: a class change on an element invalidates only that element's
  computed style plus descendants whose rules could depend on it (determined from the rule set's
  descendant-dependency map). Toggling `.selected` on one row does not restyle the grid.

  ✅ Built. Two bounds, and conflating them is what makes an invalidator either wrong or useless.
  The **dependency map** bounds what the *rules* reach — and it narrows by the far end's names, so
  `.selected .cell` reaches the cells rather than the subtree. **Inheritance** bounds what a
  *changed value* reaches, and no dependency map can see it: the descent continues only while the
  properties a child would have inherited actually differ. Testing "did anything differ" instead is
  what makes selecting one row restyle its hundred cells, since a highlight setting `background`
  cannot possibly reach one.

  So the headline claim holds with a qualifier worth stating: toggling `.selected` restyles **one**
  element when the rule sets a non-inherited property, and the row plus its cells when it sets an
  inherited one. The second is not a failure of invalidation — every cell's inherited colour
  genuinely did change.

  Nothing has to look *upward*, and that is the second thing the `:has()` P2 decision buys after
  match cost. `:focus-within` looks like an exception and is not: it is stored as element state and
  set explicitly, so it arrives as an ordinary change.
- **`ComputedStyle` is immutable, interned, and reference-compared.** Layout reads it and only marks
  itself dirty when the reference changed *and* a layout-affecting property differs.

### Transitions and animations

`transition` and `@keyframes`/`animation` with a fixed-timestep animator driven from the UI system,
interpolating on a per-property basis (colours in OkLab so gradients and fades look right, lengths
numerically, transforms decomposed). Springs (`transition: 200ms spring(1, 100, 10)`) as a Vixen
extension, because game UI wants them and CSS still does not have them.

✅ Built. `Oklab` lives in `Vixen.Core.Mathematics` and is checked against Ottosson's published
values. `StyleValue` is the typed, interpolatable value — the cascade keeps working on interned
strings, and only the properties actually being animated get typed, which is what still lets a
stylesheet carry a property this engine has never heard of.

⚠ **A third thing ExCSS leaves to Vixen.** It expands the `transition` shorthand into longhands
**only when it recognises every part**, so `transition: opacity 200ms ease-in` arrives as four
declarations and `transition: opacity 200ms spring(1, 100, 10)` arrives as one unexpanded string.
Whether the longhands exist therefore depends on whether the author used a Vixen extension. Vixen
parses the shorthand itself as well as reading the longhands. `@keyframes`, by contrast, ExCSS *does*
parse, with `from`/`to` normalised — established by probing rather than assumed.

**Springs are solved in closed form**, not integrated, which buys more than accuracy: a value
depending only on elapsed time cannot drift, so a dropped frame does not change where the spring ends
up. A spring has no duration of its own, so one is derived — the time by which the oscillation
envelope decays to a thousandth — which is what lets it sit where CSS expects a timing function
rather than needing its own integrator plumbed through the animator.

Not built: `animation-name: a, b` runs only the first, and transforms are not decomposed because
there is no transform property yet.

### The utility preprocessor (`Vixen.Ui.Styling.Utilities`)

A Tailwind-shaped system, written for the engine, running as part of the build.

**Design tokens** in a config asset, not a JS file:

```yaml
# Assets/Ui/vixen.ui.yaml
theme:
  colors:
    surface:  { 1: "#101014", 2: "#17171d", 3: "#1f1f26" }
    accent:   { DEFAULT: "#4f7cff", hover: "#6a91ff" }
    muted:    "#8a8a99"
  spacing:    { base: 4 }          # spacing scale unit → p-4 == 16px
  radius:     { sm: 2, md: 4, lg: 8, full: 9999 }
  fontSize:   { xs: [11,16], sm: [12,18], base: [14,20], lg: [17,24], xl: [21,28] }
  fontWeight: { normal: 400, medium: 500, semibold: 600, bold: 700 }
  screens:    { sm: 640, md: 768, lg: 1024, xl: 1280 }
darkMode: media
content: ["Assets/**/*.vxml", "Assets/**/*.cs"]
```

**Generation** happens in the build, driven by a scanner:

1. Scan `content` globs for class-name-shaped string literals (in VXML `class` attributes and in C#
   string literals — the same "candidate extraction" heuristic Tailwind uses; deliberately
   over-inclusive, since a false positive costs one unused rule).
2. Parse each candidate against the utility grammar: `[variant:]*utility[-value][/opacity][!important]`.
   Variants: `hover:`, `focus:`, `active:`, `disabled:`, `first:`, `last:`, `odd:`, `even:`, `dark:`,
   `sm:`/`md:`/`lg:`/`xl:`, `group-hover:`, `peer-checked:`, `aria-*:`, `data-*:`, arbitrary
   `[&>*]:`.
3. Emit only the used rules into a generated VCSS stylesheet in cascade layer `@layer utilities`, so
   component styles in `@layer components` and user overrides win predictably without `!important`
   wars.
4. Arbitrary values: `w-[37px]`, `text-[#ff0000]`, `grid-cols-[1fr_auto]` — parsed and emitted
   directly.
5. `@apply` support inside VCSS so components can compose utilities.

**Utility families for 1.0** (the set an editor actually needs): layout (`flex`, `grid`, `hidden`,
`inline`), flex/grid properties, `gap`, spacing (`p`/`m`/`space`), sizing (`w`/`h`/`min`/`max`),
position/`inset`/`z`, typography (`text`, `font`, `leading`, `tracking`, `truncate`, `whitespace`,
`align`), colours (`bg`, `text`, `border`, `ring`, `fill`, `stroke`), borders/`rounded`/`divide`,
effects (`shadow`, `opacity`, `blur`), transforms (`translate`, `scale`, `rotate`),
transitions/`duration`/`ease`, interactivity (`cursor`, `select`, `pointer-events`, `overflow`), and
`aspect`.

⚠ **Three names were struck from that list rather than built, and the strikings are the point of the
entry.** It said `mix-blend`, `origin` and `scroll` as well, and each is a family whose property
*nothing in this engine reads* — measured by `UtilityConsumptionProbe.Channels`, over all twelve of
its scenes and at every value the family could emit, not inferred. Writing them would have produced
classes that resolve, compute a value, and change nothing a person can see, which is the failure mode
[doc 43](43-web-styling-parity.md) exists to end and is strictly worse than the honest absence:

- **`mix-blend-*`** — eighteen keywords onto `mix-blend-mode`. A blend mode is a compositing operation
  and `DrawCommand` has no blend channel to carry one; there is no offscreen target to blend *into*
  either, which is the same missing compositor `rotate` and `scale` are already filed against (`#23`).
  Blending would have to land in the renderer first, and the family the day after.
- **`origin-*`** — nine keywords onto `transform-origin`, and this one is inert for a reason no scene
  can fix. `transform-origin` moves the fixed point of a transform, so it says something only where
  there *is* one — and the only transform this engine implements is `translate`, which is
  origin-independent by definition. `scale` and `rotate`, the two that would notice, are refused
  (`#23`): a rotated box is not a rectangle and a scaled one holds glyphs shaped at the wrong size.
  So `origin-*` cannot be observed here even in principle, and the probe's `translated` scene — added
  for exactly this class of property — confirms it: zero channels, at every value.
- **`scroll-*`** — ✅ **22 of the 32 are written**, and the deferral's own wording is why they took a
  while. It said the behaviour had to come first; scrolling in this engine is `ScrollView`, a control
  that owns its bars and offsets its content, and `scroll-margin` means something only to a scroll
  container that honours it — all true, and all read as though `ScrollView` were unbuilt. It was
  finished, and the gap was four property reads inside it. `scroll-m-*`, `scroll-p-*` and their axes
  are read by `ScrollIntoView` (the margin off the target, the padding off the container);
  `scroll-behavior` eases off `UiDocument.Ticked`; `overscroll-*` decides whether a wheel that has run
  out chains outwards. ⚠ The point the original bullet made still stands and is now the *other* half:
  per-axis `overflow` and `overflow: auto` *clip*, a clip is not a scrollbar, and `overflow-y-auto` on
  a plain `div` still cuts the content off with nothing offering to scroll it — put a `ScrollView`
  there. Still out: the four block roots (`scroll-mbs-*` and friends, for `space-y-*`'s reason),
  `snap-*`, which needs a snapping algorithm rather than a reader, and `scrollbar-*`, which would be a
  second way to say what `scrollbar { … }` already says. See doc 43 Part 8 § 3 and A18.

**`space` and `divide` are built**, and they were the two worth building: every longhand they emit is
already read. Both are unlike every other family in the table — Tailwind implements them as a rule
over *children*, `& > :not(:last-child)`, setting a margin or a border on all but one — so they
needed the generator to emit a compound selector rather than a bare class. It does, through
`Family.Scope`; the selector engine needed nothing, having compiled child combinators, `:not()` and
`:last-child` all along. Two divergences from v4, both deliberate and both recorded in
`ChildScopedFamilyTests`: `space-y-*` emits the physical `margin-bottom` where v4 emits
`margin-block-end`, because the block pair is interned by nobody and this engine has no writing mode
for them to differ in; and the scope is not wrapped in `:where()`, which v4 uses to keep the rule at
one class of specificity, because `SelectorCompiler` charges a class for `:where()` as it does for
`:is()`. The second is v3's behaviour and shipped for four major versions. `space-x-reverse` and
`divide-*-reverse` are absent — ⚠ **and neither reason originally given for them is the reason any
more.** `StyleValueParser` folds `+ - * /` on compatible units, and `ReverseFlagTests` measures that
the `--tw-*-reverse` flag *is* read: written by one class, read by another class's declaration,
inherited down to the descendants the child-scoped rule matches, at both values of the flag. What
holds those four back is the one-edge decision in `UtilityFamilies` — a reverse flag flips which of
two written edges carries the width, and these families write one edge on purpose so as not to
out-specify a child's own utility. ⚠ The `divide-<style>` keywords were on this list and are not any
more: they needed a reader for `border-style`, and doc 43 § A3 gave them one.

**Why build this rather than hand-write CSS.** The editor has ~200 distinct visual components. A
utility system means the design-token change ("accent is now teal") is one file, and the styling of a
new panel is zero new CSS. It is the same argument that made Tailwind win, and it applies more
strongly to a monolithic application than to a website.

**Hot reload of tokens**: changing `vixen.ui.yaml` regenerates utilities and re-resolves `var()`
values without a restart — a live theme editor becomes trivial, and is a good demo.

✅ Built, apart from the build-step integration and token hot reload, both of which wait on the asset
pipeline. The generator, the grammar, the variants, the scanner and `@apply` are all there and tested
against the style engine rather than against expected text.

⚠ **That last clause was true of the generator and false of the variants, for as long as it has been
written here.** Four of the twenty-odd variant families had a test that resolved anything — `hover:`,
`focus:` stacked with it, `md:` and `[&>*]:`; the rest asserted on emitted text or on nothing, and
`peer-*` and `aria-*` had neither. It read as a coverage claim and was a claim about one file. Doc 43
§ D6 has the family-by-family audit and `VariantCoverageTests` is the standing gate; the sentence above
is safe to trust again, which it was not before.

One limit that is a decision rather than a gap. `text-` resolves as alignment, then font size, then
colour, so a colour named `center` or `lg` is unreachable through it — the price of one prefix meaning
three properties, and worth paying for both `text-lg` and `text-accent` reading right.

⚠ ~~Two media-query variants on one utility (`sm:md:p-4`) are dropped rather than nested, because
Vixen's `@media` support does not nest.~~ **Wrong on the reason and no longer true.** The cascade has
always nested conditional group rules — `StyleSheetLoader.LoadMedia` recurses into the rule it matched —
so what could not nest was the *generator*, which carried one at-rule for the whole variant stack. It
carries a chain now. See doc 43 A15 and § D3; the same paragraph is what had `@container` sized against
a prerequisite that already existed.

⚠️ **[Doc 43](43-web-styling-parity.md) reopens the family list above and measures it.** The ✅ on this
section is true of the *machinery* and not of the *coverage*: ~~five of the families this section names
for 1.0 — `space`, `divide`, `mix-blend`, `origin`, `scroll` — were never written~~ — **settled, and
in three different ways.** `space` and `divide` are written, and the list above no longer names the
other three: `mix-blend` and `origin` emit properties nothing in this engine reads and were refused
with the measurement written down, `scroll` is re-homed against `ScrollView` under A18. The list is a
statement about the code again rather than a wish. Against
Tailwind v4.3.3's own registry only 51 of 328 utility roots work end to end. Doc 43 also corrects the
first of the two "limits" above: the `text-` overload is Tailwind's own design and costs there exactly
what it costs here, while a genuine defect sits next to it — the longest-prefix split has no fallback,
so `rounded-tl-lg` is swallowed by `rounded` and reported unknown. The scope line in § *Algorithm
scope* that puts a full inline formatting context out of scope is reopened there too, because
`truncate`, `line-clamp` and `text-overflow: ellipsis` all sit behind it.

## Text (`Vixen.Ui.Text`)

Underestimating text is the classic UI-framework mistake.

- **Shaping**: HarfBuzzSharp. Non-negotiable for ligatures, kerning, Arabic/Hebrew/Indic/Thai, emoji
  clusters, and variable fonts. **Built.** What is Vixen's is not the shaping but the *itemisation*
  around it — UAX#24 script runs crossed with bidi levels, the direction and script each run is
  given, the order runs are drawn in, and the cluster-to-character mapping. A correct shaper given
  the wrong arguments produces wrong glyphs, which is why the gate is an external one.
- **Bidi**: UAX#9 implementation (or ICU4X bindings if the size cost is acceptable; measure first).
  **Built**, as Vixen's own — all 91 707 of the Consortium's cases pass, so the ICU4X alternative
  was never needed and the size cost never paid.

  ⚠ **And for a while nothing in `Vixen.Ui` asked it the right question.** A conformant algorithm
  reached by a caller that never states the base direction is worth nothing to a localised
  interface, and that is precisely what shipped: `ParagraphDirection` had no reference outside
  `Vixen.Ui.Text`, so `UiElement.Runs` shaped every paragraph at `Auto` and the CSS `direction`
  property — parsed, inherited, and already honoured by `text-align`, the logical insets and
  `ScrollView` — reached the layout of the box and never the order of the glyphs. An element styled
  `direction: rtl` whose text began with a Latin word laid out left to right.

  **Fixed.** `UiDocument.DirectionOf` resolves the property once per style pass into
  `UiElement.ParagraphDirection`, the block cache and the layout-dirty test both watch it, and
  `Runs` hands it to the shaper. ⚠ **An element that states nothing stays `Auto` rather than
  becoming `LeftToRight`**, though CSS's initial value is `ltr`: pinning every unstyled label to
  level 0 would mean no unstyled string in the engine could ever lay out right to left, which is
  the same defect inside out. `BidiDirectionTests` asserts the flip in both directions, and asserts
  it as *which glyph is leftmost* — the only form of the assertion that a plausible-looking wrong
  answer cannot pass.

  ⚠ **And the reordering did not cross a font-fallback boundary**, which is the same defect one level
  down. Two things cut a line into runs — `FontRegistry.Cover` where the *face* changes, UAX#9 where
  the *level* changes — and only the first was cutting. Runs were laid down in logical order, so a
  line whose Arabic and whose Latin came from different files drew both words correctly, at the right
  total width, in the wrong order.

  **Fixed.** `TextRun` carries a `Level`, `UiElement.Runs` intersects the coverage spans with the
  level runs, and `TextLine` lays its pens down in visual order — L2 delegated to
  `TextItemizer.VisualOrder`, not copied — while `Runs` stays in logical order so that the caret walk,
  `Start` and `Length` are untouched. ⚠ **The two halves are load-bearing separately**: reverting the
  pens fails four tests, and merging the levels back fails exactly the two that turn on a neutral
  between two opposite runs. That second failure is the instructive one — the words come out in the
  right order and the *space* between them does not, which is precisely the kind of wrongness that
  survives a reviewer who does not read the script.

  ⚠ **Level boundaries are safe to split the shaper's input at and script boundaries are not**, which
  is why the itemiser's items are merged back where only their script differs. A shaper finds out
  whether an Arabic letter's neighbour joins by seeing the whole string; a level change is a change of
  strong direction, and no script joins across one.

  ⚠ **"Still owed: nothing *mirrors*" was written here and is no longer true of any of the three
  items it named** — and two of them were closed by fixing something other than what the sentence
  says. Caret affinity landed whole (2026-09-03, see the `TextEditor` row in `docs/overview.md`), and
  the other two were never a missing feature: `text-align`'s logical keywords *were* resolved against
  `direction`, and the hit test *did* go through a bidi-aware caret. What was wrong was one line
  each. `DrawListBuilder.Indent` read a **missing** `text-align` as zero, and the initial value of
  `text-align` is `start` rather than `left` — so a right-to-left paragraph nobody had written an
  alignment for, which is every paragraph in a plain interface, sat flush against the left edge with
  perfectly ordered text in it. And `TextLine.CaretPositionAt` searched the runs in **logical** order
  over pens stored in **visual** order, so on a line that changes direction it stopped at the run that
  reads first rather than the one drawn under the cursor. Both are held down by
  `Vixen.Ui.Tests.BidiMirroringTests`; ⚠ the click assertion there is on *which half of the line the
  caret is drawn in* and not on the index, because the wrong run's clamped edge answers with the same
  index at the opposite end of the line.

  ⚠ **And a third one line, a layer up, which the first two could not see because the caret is not a
  glyph.** A `TextLayout` places every line from zero and knows nothing about the box around it, so
  `CaretOffset`, `VisualRanges` and `CaretPositionAt` are all *line-local* while the draw path puts
  the glyphs at `left + TextAlignShift(…)`. `TextField` drew its caret, its selection band and its
  input-method underline at the line-local number and hit-tested with it — so a wrapped RTL area drew
  the caret against the **left** edge of the block while the short line it belonged to sat flush
  against the right, fifty pixels away, and clicking on the text put the caret somewhere else again.
  The rule now lives once, on `UiDocument.TextAlignShift`, with `DrawListBuilder` a caller of it
  rather than its owner; `Vixen.Ui.Controls.Tests.RtlFieldMirroringTests` holds it, and its oracle is
  *containment* — the caret is inside the horizontal span of the glyphs on its own row — rather than a
  coordinate anybody would have to recompute.
- **Line breaking**: UAX#14 with a compact rule table; UAX#29 grapheme/word segmentation for cursor
  movement and double-click selection.
- **Rasterisation**: **MSDF** atlas — multi-channel signed distance fields give crisp text at any
  scale with one texture and one shader, and support outlines/glows/shadows for free. Atlas is
  dynamically packed with an LRU eviction; CJK's glyph count makes a static atlas impossible.
  A subpixel-AA raster path exists for small desktop text where MSDF is visibly softer, selected per
  font size.

  ⚠ **This paragraph assumed contours were available and they are not.** HarfBuzzSharp exposes no
  outline API whatsoever — `TryGetGlyphExtents` is a bounding box, and there is no draw, paint or
  outline surface — so the atlas needs a glyph source of its own. **Settled by
  [spikes/text-glyph-outlines](spikes/text-glyph-outlines/RESULT.md)**: a managed `glyf`/`CFF`
  parser over `Face.ReferenceTable`, ~600 lines for both formats, checked against HarfBuzz's own
  extents over 259,298 glyphs of 242 fonts. No new native dependency, and the WebAssembly path is
  untouched.

  ⚠ **And a rule the atlas has to obey, learned there rather than here**: HarfBuzz reports extents
  for a glyph *positioned* so that its `xMin` sits on the left side bearing, while the outline the
  parser produces is in the font's own coordinates. Where a font's stored `xMin` disagrees with its
  `lsb` — universal in italics — placing the parsed outline without that shift puts every glyph
  `lsb − xMin` units off.
- **Font fallback chains** per script, with a system-font enumerator per platform.
- **Rich text**: an inline model (runs with per-run style) supporting bold/italic/colour/size/link/
  inline image/inline component. Needed by the console, the inspector, and any tooltip.
- **Editing**: `TextEditor` model with grapheme-correct cursor movement, selection, IME composition
  (all six platforms have different IME plumbing — this is real work and is scheduled), undo/redo,
  and platform clipboard.

## The element tree and property system (`Vixen.Ui`)

```csharp
public abstract class UiElement
{
    internal LayoutNodeId LayoutNode;      // index into LayoutStore
    internal ComputedStyle Style;          // interned, shared
    internal ElementFlags Flags;           // dirty bits: style, layout, render, transform
    public   UiElement? Parent { get; }
    public   ChildCollection Children { get; }
}
```

- **Elements are classes** (unlike ECS components) — a UI node has identity, virtual behaviour, and
  event handlers, and there are 10⁴ of them, not 10⁶. The struct-of-arrays discipline lives in the
  layout store and the render list, which is where the loops are.
- **Property system**: source-generated attached/dependency properties (`[UiProperty]`) with change
  callbacks, coercion, inheritance (font, colour, direction), and animation targets. Generated, not
  reflection-based — Stride's `DependencyPropertyFactory` does this at runtime; a generator is strictly
  better.
- **Event routing**: capture → target → bubble, with `Handled`, plus explicit `PointerCapture`,
  focus management with a focus scope tree, keyboard navigation (tab order, arrow navigation,
  `accesskey`), and gesture recognisers (tap, double-tap, long-press, drag, pinch, rotate, flick)
  shared with `Vixen.Input`.
- **Hit testing** against the layout results with a per-frame spatial acceleration (a simple quadtree
  over the top-level, then linear within a panel — measured to be sufficient) and `pointer-events`
  honoured.
- **Rendering**: the element tree emits a retained **draw list** of primitives (rounded rect, border,
  gradient, texture quad, MSDF text run, path fill/stroke, clip push/pop, blur backdrop, custom
  callback). The draw list is diffed against the previous frame at the *command* level, so a static UI
  re-submits a cached command buffer.


## Accessibility (`Vixen.Ui`)

⚠ **This section is written because it was missing, and its absence was a defect rather than an
omission.** [46](46-what-an-application-needs.md) § A2 went looking for the base-API line this
document was said to carry, and found that accessibility appeared exactly once here — in the Testing
table below, as *"ARIA-role snapshot"* — with nothing in § The element tree and property system, no
role, no name, no value and no relations, and nothing at all in the code. A promise about a test with
no API under it is a promise nobody could keep, and 46's word for the state of it was the right one:
**greenfield**.

**Six things on `UiElement`, and one event on `UiDocument`.** `Core/Vixen.Ui/Accessibility.cs`.

| What | Where it comes from |
|---|---|
| `Role` | The type, through `NativeRole`. Assigning it overrides the native role, as the web's `role` attribute overrides an implicit one; `ClearRole()` hands it back |
| `AccessibleName` | An explicit assignment, then the element this one is `LabelledBy`, then `NativeAccessibleName` — accname 1.2's order, to the three steps that decide a control set's answer |
| `AccessibleDescription` | An explicit assignment, or the element this one is `DescribedBy` |
| `AccessibleValue` | `NativeAccessibleValue` — a field's text, the label of the option a `Select` shows. `null` for an action |
| `AccessibleState` | `NativeAccessibleState` ∪ `DeclaredAccessibleState` ∪ `Disabled`/`Focused`/`Focusable`, which the framework adds for every element |
| Relations | `LabelledBy`, `DescribedBy`, `Controls`, `Owns`, `FlowsTo`, `ActiveDescendant` — ARIA's relationship attributes |
| `UiDocument.AccessibilityInvalidated` | One coalesced raise per frame, from `Tick`, structurally the same object as `CommandsInvalidated` |

**The vocabulary is [WAI-ARIA 1.2](https://www.w3.org/TR/wai-aria-1.2/#role_definitions)'s and none of
it was invented here.** Member names are the role tokens PascalCased and nothing else — which is why
`img` is `AccessibleRole.Img` and not `Image`: the rule is that lowercasing a member name is the ARIA
token for *every* member, so no mapping table exists for the next role added to be forgotten from.
Every bridge this tree will have already maps ARIA — AT-SPI2 documents a correspondence, UIA's control
types are mapped from ARIA by the HTML-AAM, and `NSAccessibility`'s roles are what WebKit maps ARIA
onto — so a vocabulary shaped to the controls that happen to exist here would be a third one that no
specification could settle an argument about.

⚠ **A control's accessible view is computed, never stored, and this is the decision the rest hangs
off.** `NativeRole`, `NativeAccessibleName`, `NativeAccessibleValue` and `NativeAccessibleState` are
virtual members answered by the type from what it already holds. There is no second copy to update, no
change callback to remember, and no state in which a checkbox is ticked on screen and unticked to a
screen reader. `ButtonBase`'s two overrides give every button, menu item, tab, option and link in the
assembly a role and a name at once.

⚠ **Three states are the framework's and no control declares them.** `Disabled` comes from
`ElementState.Disabled`, `Focused` from `ElementState.Focus`, `Focusable` from `UiElement.Focusable`.
Fifty controls cannot each forget one, and the symptom of a forgotten one — a screen reader saying a
greyed button is available — is invisible to whoever writes the control.

**The cost is one nullable reference per element**, on `CommandBindings`' terms: eight bytes, and no
allocation at all unless an application sets a name, a role, a value or a relation on that particular
element. A control that only overrides virtuals allocates nothing.

**Relations exist because the tree is the wrong shape for them.** A `TabItem` is in the strip and its
panel is in the panel area; a `Select`'s option list is a child of the document *root*, because an
overlay inside the field that opens it would be clipped by every scrolling ancestor between the two.
Neither pairing is recoverable by any walk over `Parent`, and both are established where the control
already reconciles the two halves — `Tabs.Adopt` and `SelectBase.OnCreated`, so the markup path and
the code path cannot drift.

**Not a node by default.** `Role` is `AccessibleRole.None` — ARIA's `none` — for every element,
including `Panel`, `Card`, `Tabs` itself and every part a control draws itself out of. A bridge walks
through them and reads their children in their place, which is what stops a four-field form being
announced as thirty nested groups. `IsInAccessibilityTree` is the question.

**The keyboard half was already here and is not duplicated.** `Focus.cs` has `TabOrder`,
`IsFocusScope` and the scope walk; `Focusable` plus `TabIndex = -1` is `acceptsFirstResponder` without
key-view participation; "what has the focus" is what `UiDocument.Focused` and `CommandRoute.Origin`
already answer. What A2 added to `Focus.cs` is one line: the invalidation, raised *outside* the
command-transparency branch, because a focus move into a menu cannot have changed which view answers
a verb and certainly did change what has the focus.

**No platform bridge, deliberately.** AT-SPI2, UIA and `NSAccessibility` are the platform's and are
out of scope for this document — 46 § A2 says so twice, and the tree is what they all read.

**The gate is `Vixen.Ui.Testing.AccessibilitySnapshot`**, which is what makes the Testing table's
promise below writable: `Render` for the tree as comparable text, `Unnamed` for the assertion that
cannot pass vacuously — every widget-role element has a non-empty name, and every focusable element
has a role.

✅ **Not owed any more, and the measure that said it was has been retired.** Both assemblies are
populated, and `AccessibilityCoverageTests` in each control test project now says so with a number
rather than with a file count: it builds one of every public element type the assembly offers and
holds each to *a role, or a written reason for not having one*. **60 element types and 44 roles in
`Vixen.Ui.Controls`; 40 and 17 in `.Controls.Advanced`.**

⚠ **A file count could never have answered this, which is why two documents disagreed about it for
so long.** "17 of 30 files declare an accessible view" is compatible with the population being
finished *and* with it being half done, because roughly a third of both assemblies is `None` on
purpose — a `Panel`, a `CodeLine`, a paint layer. The sweep's exemption table is where that decision
is now written down, one reason per type, and it fails in both directions: a control with no role
and no entry fails by name, and an entry for a control that has since been given a role fails as an
expired exemption. See 46 § A2 for the two things this turned up.


## Control library

**`Vixen.Ui.Controls`** — Text, Button, IconButton, ToggleButton, RadioGroup, CheckBox, Switch,
TextBox, TextArea, NumericInput (with drag-scrub), SearchBox, Slider, RangeSlider, ProgressBar,
Spinner, ComboBox, Select, MultiSelect, Tabs, Accordion, Expander, Card, Panel, Separator, ScrollView,
Tooltip, Popover, ContextMenu, MenuBar, Dialog, Drawer, Toast, Badge, Avatar, Breadcrumb, Pagination,
Image, Icon, Link, Skeleton, EmptyState, Alert/Callout, KeyboardShortcut.

**`Vixen.Ui.Controls.Advanced`** — the ones that prove the framework:

| Control | Why it is the proof |
|---|---|
| `DockingHost` | Splitters, tab groups, float/dock/undock, drag preview, layout serialisation. The editor shell. No other single control exercises as much of the framework. |
| `DataGrid` | Virtualised rows *and* columns, frozen columns, resize/reorder, sort, group, inline edit, cell templates. Exercises style sharing and virtualisation to their limits. |
| `TreeView` | Virtualised, lazy children, drag-reorder with drop indicators, multi-select, rename-in-place. The project browser and hierarchy. |
| `PropertyGrid` | Attribute-driven editor generation, nested objects, multi-object editing with mixed-value states, reset-to-default, search. The inspector. |
| `NodeCanvas` | Infinite pan/zoom canvas, bezier wires, marquee select, snapping, minimap, groups. Shader and VFX graphs. |
| `Timeline` | Tracks, keyframes, curves, playhead, zoom, snapping. Animation and VFX. |
| `CurveEditor` | Bezier handles, presets, tangent modes. |
| `ColorPicker` | HSV/OkLCH wheel, eyedropper, palettes, alpha, HDR values. |
| `GradientEditor` | Stop editing, interpolation-space selection. |
| `Viewport` | Hosts a 3D render target with input capture and gizmo overlay. |
| `CodeEditor` | Syntax highlighting via `Vixen.Core.Syntax` (Raven/VXML/VCSS/C#), line numbers, folding, diagnostics gutter, autocomplete popup. Needed for the in-editor shader editor. |
| `Canvas2D` | Layers, huge scrollable surface, tool overlays, selection marching ants. P2 — no editor consumer; see `Samples/06-CanvasStress`. |

## Hot reload (`Vixen.Ui.HotReload`)

Three independent reload channels, because they fail differently:

| Channel | Trigger | Mechanism | Preserved |
|---|---|---|---|
| **Style** | `.vcss` / `vixen.ui.yaml` saved | Reparse stylesheet, regenerate utilities, invalidate all `ComputedStyle`s, re-layout | Everything — no tree change |
| **Markup** | `.vxml` saved | Incremental reparse → rebind → **re-execute `Build`** for affected component instances, reconciling against the existing element tree by key/position | Component field state, scroll offsets, focus, selection, expansion state, signal values |
| **Code** | `.cs` saved | .NET Hot Reload (`dotnet watch` / the IDE's EnC) + `[MetadataUpdateHandler]` to clear caches and rebuild affected components | Whatever EnC preserves; a rude-edit falls back to a full component rebuild with `[HotReloadState]`-marked fields round-tripped |

Details that make it actually work:

- **`[HotReloadState]`** on a field marks it for serialise-out/serialise-in across a rebuild. Everything
  else is reconstructed. Users opt in per field, which is honest about what can survive.
- **Keyed identity.** Every element gets a stable identity from its source span + `key`. Reconciliation
  matches old and new trees on that, so inserting an element above a scrolled list does not reset the
  scroll.
- **`MetadataUpdateHandler`** clears: the component-type cache, the utility scanner's results, the
  style-sharing cache, generated-property metadata, and the effect dependency graph for rebuilt
  components.
- **Failure is visible and non-fatal.** A reload that throws leaves the previous UI running and shows
  the error in an overlay plus the log. Hot reload that can crash the editor is hot reload nobody
  turns on.
- **The engine's other hot-reload channels** (shaders per [07](07-raven-shader-pipeline.md), assets per
  [08](08-asset-pipeline-and-addressables.md)) share the same file-watch infrastructure and the same
  "reload is a first-class, tested operation" discipline.
- **`Vixen.Ui.HotReload` is not referenced in release builds** — the whole assembly is
  `Condition="'$(Configuration)'!='Release'"`.

## Testing

| Area | Test |
|---|---|
| Lexer/parser | Golden syntax trees over a corpus (as Raven already does); round-trip byte fidelity; one error-recovery test per diagnostic, including mid-typing states |
| Binder | Positive/negative fixtures; `#line` mapping verified by asserting a deliberate expression error reports the `.vxml` line |
| Generator | Snapshot tests on emitted C#; compile-and-run tests asserting the generated component behaves correctly |
| Signals | ✅ Diamond evaluates once; equality short-circuit stops propagation, including at the effect; `Batch` defers the flush to its close; **zero** allocation after warm-up, asserted by `GC.GetAllocatedBytesForCurrentThread` in a test rather than by a benchmark, so it fails the build rather than a report; runaway-effect detection fires; disposal removes all edges in both directions; and a brute-force oracle over random DAGs |
| Layout | **The ported Yoga conformance suite** (several hundred cases) — the primary gate. Plus: dirty propagation (a static tree costs 0 measured nodes), parallel layout equals serial layout, 100 k-node throughput benchmark |
| Grid | Ported WPT (web-platform-tests) CSS Grid cases where they can be expressed without a full browser |
| Styling | Cascade/specificity/`@layer` order tests against known CSS semantics; selector-matching oracle (bucketed matcher vs. brute-force over randomised trees); style-sharing correctness (shared instances are genuinely identical); invalidation minimality (toggling a class restyles exactly N elements) |
| Utilities | Candidate extraction over fixture files; each utility family emits the expected declarations; arbitrary values; variant combinations; unused utilities are absent from output |
| Text | Shaping conformance against the Consortium's [text-rendering-tests](https://github.com/unicode-org/text-rendering-tests) — **not** against HarfBuzz reference output, which would be HarfBuzz judging itself and would survive any itemisation bug that handed the shaper the same wrong arguments twice (see [doc 14](14-roadmap.md), 4c); line-break conformance against UAX#14 test data; segmentation against UAX#29; bidi against UAX#9; MSDF glyph rendering golden images — ⚠ **and the golden is the second assertion, not the first**: three letters drawn at 96 px from a 32 px field look identical whether the atlas is multi-channel or not, so `GlyphMsdfVisualTests` asserts that the three channels of a placed glyph's field disagree over a tenth of its texels before it compares any picture |
| Rendering | Draw-list golden tests (element tree → expected primitive list) — pure CPU, no GPU needed, which makes control rendering unit-testable. Plus golden images per control — ⚠ **landed and on the CPU rather than "on the Null/lavapipe path"**: `ControlVisualTests` commits thirty-nine references rendered by `SoftwareUiRasterizer`, which needs no device at all and can therefore compare *exactly*, where a driver-rendered suite has to compare perceptually |
| Input/events | Routing order, capture, focus traversal, gesture recognition state machines |
| Controls | Per control: keyboard interaction matrix, **ARIA-role snapshot** — writable since § Accessibility landed, through `Vixen.Ui.Testing.AccessibilitySnapshot`; `Core/Vixen.Ui.Controls.Tests/AccessibilityTreeTests.cs` is it for the controls populated so far, and ⚠ it asserts `Unnamed` before `Render` because a snapshot matches an empty tree perfectly — virtualisation (a 10⁶-row grid realises O(viewport) elements), and a golden image in light and dark themes — ⚠ **the theme half is a `ControlThemeVisualTests` gallery in both palettes, and its first test is not a golden**: doc 43 found all 43 baselines byte-identical after the oklch palette landed, which was read as reassurance and was actually the picture suites having no theme at all, so the pair is held to *most of the frame changed colour, and the dark one is darker* before either reference is trusted. ⚠ **And a golden per control at both themes is refused with evidence rather than owed**: `ControlTheme.vcss` has exactly one `root.dark` block and it declares tokens and nothing else, so thirty-nine dark references would be thirty-nine pictures of one substitution the gallery already proves reaches the frame. The five `root.dark .tok-*` rules in `AdvancedTheme.vcss` are the tree's **only** per-control dark rules, and they are what the theme dimension was actually missing — held by `SyntaxThemeTests`, on a contrast oracle rather than a picture, because the failure has a shape: a dark rule that stops matching leaves `#8250df` on `#1b1d21` at 3.3:1, which is unreadable code rather than merely different bytes |
| Hot reload | Automated: mutate a `.vxml`/`.vcss` on disk, assert the running tree updated and that scroll/focus/selection/state survived; assert a deliberately broken file leaves the previous UI intact |
| Perf | **Editor-shell benchmark is the gate**: 5 panels + viewport + 500-node graph + a 10⁶-row virtualised grid holds the [00](00-vision-and-principles.md) budget. Per the decided audience order, the editor — not a sample — is the application-platform proof. ⚠ **Landed as two things, because a benchmark cannot fail.** `Benchmarks/Vixen.Benchmarks.Ui/EditorShellBenchmarks.cs` is the measurement (a settled frame is 217 µs and 504 B; one row selected is 1.26× it, where the same interaction was 41× before the incremental cascade was wired); `Vixen.Ui.Controls.Advanced.Tests.EditorShellBudgetTests` is the gate, over the same scene from the same linked source file, and every assertion in it is *work* rather than milliseconds — 24 elements realised for 10⁶ items, a settled `Update` doing nothing at all, and a constant allocation per frame. ⚠ Five panels added to a docking host are five tabs of **one** group, four of which lay out to nothing, so the scene docks them into four regions; a fixture that skipped that reports an excellent frame time for a document drawing one tree view. |
