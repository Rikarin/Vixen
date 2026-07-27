# Vixen.Ui

The UI framework proper: an element tree, the stylesheets that describe it, and the pass that turns
one into geometry.

`Vixen.Ui.Styling` decides which declaration wins without knowing what a length measures.
`Vixen.Ui.Layout` measures without knowing where its numbers came from. Neither references the other,
which is what keeps a flexbox engine usable without a stylesheet and a cascade testable without a
layout — and it leaves a gap that something has to close. This closes it, and then puts a tree on
top.

## State

| | |
|---|---|
| `LengthContext` | What a relative length is relative to: the element's font size, the root's, the viewport. |
| `LayoutStyleBuilder` | `ComputedStyle` → `LayoutStyle`. Every layout-affecting property, the nine CSS edges, and the font-size chain. |
| `UiElement` | One node. A class, holding no geometry and no style — a handle into the two stores that do. |
| `UiDocument` | The tree, its stylesheets, and the four-walk pass. |
| `[UiProperty]` | Generated properties with defaults, coercion, change callbacks, inheritance and a runtime identity. |
| `UiDocument.HitTest` | What is under a point, front to back, with `pointer-events` and `overflow` honoured. |
| `EventRouter` | Capture, target, bubble, `Handled`, and pointer capture. |
| Focus, keyboard navigation, gestures | ⏳ |
| Draw list, batching, clipping | ⏳ |

## Input

**Front to back means the last child first.** A later sibling is painted over an earlier one, so it
is the one a click lands on; testing in document order returns whatever happens to be underneath.

⚠ **Being outside an element is not a reason to skip its children.** `overflow: visible` is CSS's
default and means precisely that a child may hang outside its parent and still be drawn — so it must
still be clickable. Returning early on a missed parent makes every dropdown, tooltip and popover
unhittable, and the bug looks like the click landing on whatever is behind them. The clip is asked
about on the *parent*, because it is the parent that clips and the child has no idea it is being cut.

⚠ **`pointer-events: none` is transparent without making its children so.** That asymmetry is what
makes an overlay usable; treating the subtree as one unit either blocks everything under a
full-screen layer or lets clicks through a modal.

**A captured pointer goes to the capturing element wherever it is** — a drag that leaves the
scrollbar it started on must keep reaching the scrollbar. Hit-testing during a drag is the bug
capture exists to prevent.

Handlers are invoked by index with the count re-read each step, because unsubscribing from inside a
handler is the ordinary case and a `foreach` throws part-way through delivering the event that
caused it.

Doc 09 asks for a quadtree over the top level. This descends the tree instead, entering only subtrees
that contain the point. The doc says the simple version was "measured to be sufficient"; **that
measurement has not been taken here**, and it should be before the quadtree is written.

## The property system

A plain C# property is invisible to everything that has to find it at runtime — a stylesheet naming
it, an animation targeting it, a binding writing it, an inspector listing it. `[UiProperty]` gives it
an identity without giving up the typed accessor: `element.Radius` is a field read, and
`key.SetValue(element, 4f)` reaches the same field.

Generated rather than reflected, and generated rather than rewritten — Stride builds the equivalent
with a runtime `DependencyPropertyFactory`, and ADR-002 rejects that whole category.

⚠ **Storage is a field, not a sparse table**, which is the opposite of what WPF does. A
dependency-property table pays a dictionary probe per read to save memory on the hundreds of
properties a WPF element declares and never sets; a Vixen control declares perhaps a dozen, there are
10⁴ elements, and reads happen every frame. The table is the more famous design and the slower one.

⚠ **Inheritance is a typed walk, not a name lookup.** Each inheriting property emits its own loop
testing `ancestor is TOwner`, so `Panel.Tint` finds the nearest `Panel` — and an `Overlay` that also
declares a `Tint` is not it. Keyed on the name, it would have found the wrong one and been
confidently right-looking.

⚠ **The old value is read through the property, not out of the field.** On an element that has only
ever inherited, the field is still empty, so comparing against it reports a change from zero on the
first write that agrees with the parent — a spurious invalidation on every element that matches its
ancestor.

**Construction and registration are two steps.** An element must be registered with both trees, which
needs a document; a base constructor taking one plus two internal node handles would put those
handles in every subclass's signature, in assemblies where they are not visible. So `UiElement()` is
parameterless and `UiDocument.Create<T>` binds it afterwards — which is also the shape markup needs,
since a generated `new Button()` cannot know a document either.

## The pass

Four walks, and they cannot be merged. The cascade needs parents resolved before children because
inheritance reads the parent's resolved table. Font size needs the same order for the same reason and
cannot fold into the cascade, because it is a *computed* value the cascade has no opinion about. The
layout style depends on the font size. And layout is the flexbox algorithm, which is not a walk.

**An unchanged document does nothing on the next frame**, and one changed class rebuilds one element.
That is what `ComputedStyle` being interned buys: two elements that resolved alike hold the same
object, so the test is a pointer comparison rather than a walk of a property table. `StylesApplied`
reports the count, because a claim about work avoided that cannot be measured is a claim nobody can
check.

⚠ The font size has to be part of that test as well as the style. An element whose own declarations
did not change still needs rebuilding when an ancestor's font size did, because every `em` on it
measures against a different number now — its computed style is the same interned object, so a check
on the style alone skips it and `2em` keeps meaning twenty pixels while the text around it doubles.

⚠ **The tree is append-only**, because `StyleTree` is. Elements are created parents-first and never
removed. Enough to lay out a document, not enough to run an application; removal is owed.

## What the bridge is for

**`em` on `font-size` means the parent's; everywhere else it means the element's own.** So font size is
resolved first and separately, and the caller walks the tree passing each element's resolved size to
its children. Conflating the two compounds: three nested `font-size: 1.2em` come out at 1.2× rather
than 1.728×, and the error grows with depth, so it reads as a rendering quirk rather than an
arithmetic one.

**Percentages are not resolved.** A percentage measures against the containing block, which only the
layout pass knows, so `50%` is handed on as `LayoutUnit.Percent` untouched. This is the one place
where doing less is the correct behaviour rather than an omission.

**An unparseable declaration leaves the initial value alone.** Zero is a perfectly good answer that
happens to be invisible, so using it for "I did not understand this" turns one typo into a missing
element with nothing said about it.

## What it found

⚠ **Yoga's initial values are not CSS's, and they differ in four places.** `flex-direction` is
`column` against `row`, `align-content` `flex-start` against `stretch`, `position` `relative` against
`static`, `box-sizing` `border-box` against `content-box`. `Vixen.Ui.Layout` is right to start where
Yoga starts — it is judged by Yoga's conformance suite — and this is the boundary where a VCSS
author's expectations take over, so `LayoutStyleBuilder.CssInitial` exists and `LayoutStyle.Default`
is not what an element with no declarations gets. Starting from the wrong one produces stylesheets
full of redundant declarations by an author who decided the engine was quirky and never reported it.

⚠ **ExCSS expands the box shorthands, and the gap that was predicted does not exist.** The bridge was
first written to expand `margin`, `padding`, `border-width`, `gap` and `flex` itself, on the
reasoning that the cascade stores shorthand and longhand as separate properties and the layout store
resolves edges by fixed precedence rather than document order — so `margin-left: 0; margin: 8px`
would give zero where a browser gives eight. **Its tests said every one of those paths was dead.**
ExCSS expands on parse, exactly as a browser does, so by the time the cascade runs that is two
`margin-left` declarations and the later one wins. The prediction was reasonable and wrong, and the
only reason it did not become a documented "known limitation" is that the test was written before the
claim was believed. `inset` is the exception, because ExCSS does not know the property.

⚠ **CSS has a unit that begins with the exponent character.** The value parser scanned `e` as part of
a number unconditionally, so `2em` scanned as `2e`, failed to parse, and came back `Unknown` — every
`em` in the document silently dropped. `1e2px` still has to work, so the fix is to test whether digits
follow rather than to drop the exponent.

⚠ **`aspect-ratio: 16 / 9` arrives as `16/9`.** ExCSS normalises the spaces away, so a parser that
splits on whitespace sees one token. Read here rather than by teaching `StyleValueParser` that `/`
separates values — it does in CSS, but making it a general separator changes how every shorthand
parses.

⚠ **This cascade inherits specified values; CSS inherits computed ones.** A child inheriting the text
`font-size: 1.5em` resolves that `em` against its own parent a second time, so a size meant to apply
once compounds at every level — two deep comes out at 2.25× where CSS says 1.5×, and the error grows
with depth. CSS avoids it by computing `font-size` to an absolute length before anyone inherits it,
so `font-size` was removed from `InheritedProperties` and is inherited here in computed form instead.
An element that declares none simply keeps its parent's resolved pixel size, which is both what CSS
means and simpler than what was there.

The same gap stays open, narrowly, for the other inherited properties that take relative units —
`line-height`, `letter-spacing`, `word-spacing`, `text-indent`. None of them feeds back into the unit
it is written in, so the error is bounded at one level rather than growing. The general fix is a
computed-value stage, recorded in doc 14.

**And relative units belong in `StyleValue` after all.** They were deliberately left out, on the
argument that resolving them needs a context that does not exist at parse time. That was right about
resolution and wrong about representation, and **transitions settled it**: the animator interpolates
`StyleValue`, so a unit the type cannot express is a unit that cannot animate. `width: 2em` under a
`transition` snapped while its neighbours eased, with nothing said about it.

## How it is tested

Through the whole path — write CSS, read a `LayoutStyle` — rather than against a hand-built
`ComputedStyle`. A wire is worth testing with something plugged into both ends: a property name no
rule can ever set shows up as a test that will not pass.

Verified by sabotage. Starting from Yoga's defaults, resolving `font-size`'s `em` against the
element's own size, resolving percentages here, swapping `vw` and `vh`, and dropping the
leave-the-initial-value guard each fail it.

⚠ That last one took two attempts, and the failure is the interesting part. Written against a
stylesheet, `width: 4furlongs` never reaches the bridge at all — **ExCSS validates as it parses and
drops what it does not recognise** — so the test passed whatever the bridge did with a bad value,
including overwriting a good one. Rewritten against inline declarations, which are interned directly
and get no such vetting, it still passed: the value has to be one that *parses* but is not a length,
because an unparseable one is already filtered a step earlier. A bare `5` is the case that reaches
the code being tested.

Licensed under Apache-2.0.
