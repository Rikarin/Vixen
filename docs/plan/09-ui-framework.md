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
`:last-child`/`:nth-child()`/`:empty`/`:not()`/`:is()`/`:where()`, pseudo-elements `::before`/`::after`,
custom properties (`--x`) with `var()` and fallbacks, `@media` (width/height/orientation/
prefers-color-scheme/dpi), `@supports`, `@keyframes`, `@font-face`, `@import`, `@layer` (cascade
layers — worth having, it is how the utility system and component styles coexist cleanly).

Not supported, and documented: floats, tables, `position: fixed` relative to viewport (there is no
viewport in a game overlay), CSS filters beyond a curated set, `calc()` beyond `+ - * /` on
compatible units, container queries (P2), `:has()` (P2 — expensive to match incrementally).

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
effects (`shadow`, `opacity`, `blur`, `mix-blend`), transforms (`translate`, `scale`, `rotate`,
`origin`), transitions/`duration`/`ease`, interactivity (`cursor`, `select`, `pointer-events`,
`overflow`, `scroll`), and `aspect`.

**Why build this rather than hand-write CSS.** The editor has ~200 distinct visual components. A
utility system means the design-token change ("accent is now teal") is one file, and the styling of a
new panel is zero new CSS. It is the same argument that made Tailwind win, and it applies more
strongly to a monolithic application than to a website.

**Hot reload of tokens**: changing `vixen.ui.yaml` regenerates utilities and re-resolves `var()`
values without a restart — a live theme editor becomes trivial, and is a good demo.

✅ Built, apart from the build-step integration and token hot reload, both of which wait on the asset
pipeline. The generator, the grammar, the variants, the scanner and `@apply` are all there and tested
against the style engine rather than against expected text.

Two limits that are decisions rather than gaps. `text-` resolves as alignment, then font size, then
colour, so a colour named `center` or `lg` is unreachable through it — the price of one prefix meaning
three properties, and worth paying for both `text-lg` and `text-accent` reading right. Two media-query
variants on one utility (`sm:md:p-4`) are dropped rather than nested, because Vixen's `@media` support
does not nest.

⚠️ **[Doc 43](43-web-styling-parity.md) reopens the family list above and measures it.** The ✅ on this
section is true of the *machinery* and not of the *coverage*: five of the families this section names
for 1.0 — `space`, `divide`, `mix-blend`, `origin`, `scroll` — were never written, and against
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
| Text | Shaping conformance against the Consortium's [text-rendering-tests](https://github.com/unicode-org/text-rendering-tests) — **not** against HarfBuzz reference output, which would be HarfBuzz judging itself and would survive any itemisation bug that handed the shaper the same wrong arguments twice (see [doc 14](14-roadmap.md), 4c); line-break conformance against UAX#14 test data; segmentation against UAX#29; bidi against UAX#9; MSDF glyph rendering golden images |
| Rendering | Draw-list golden tests (element tree → expected primitive list) — pure CPU, no GPU needed, which makes control rendering unit-testable. Plus golden images per control on the Null/lavapipe path |
| Input/events | Routing order, capture, focus traversal, gesture recognition state machines |
| Controls | Per control: keyboard interaction matrix, ARIA-role snapshot, virtualisation (a 10⁶-row grid realises O(viewport) elements), and a golden image in light and dark themes |
| Hot reload | Automated: mutate a `.vxml`/`.vcss` on disk, assert the running tree updated and that scroll/focus/selection/state survived; assert a deliberately broken file leaves the previous UI intact |
| Perf | **Editor-shell benchmark is the gate**: 5 panels + viewport + 500-node graph + a 10⁶-row virtualised grid holds the [00](00-vision-and-principles.md) budget. Per the decided audience order, the editor — not a sample — is the application-platform proof. |
