# Vixen.Ui.Styling.Utilities

A Tailwind-shaped utility system written for the engine. Design tokens in a config asset, a scanner
that finds which utilities a project actually uses, and a VCSS stylesheet containing only those.

## Why this exists

The editor has around two hundred distinct visual components. A utility system means the design-token
change — "accent is now teal" — is one line of one file, and the styling of a new panel is zero new
CSS. It is the argument that made Tailwind win, and it applies *more* strongly to one monolithic
application than it ever did to a website, because there is no page-weight budget to trade against and
every component is in the same repository as every token.

## State

| | |
|---|---|
| `ThemeTokens` | `vixen.ui.yaml` → colours, spacing, radii, font sizes and weights, shadows, breakpoints. |
| `UtilityParser` | `[-]?[variant:]*utility[-value][/opacity][!]`, bracket-aware throughout. |
| `UtilityFamilies` | What each family emits. Table-driven. |
| `Variants` | `hover:`, `md:`, `dark:`, `ltr:`/`rtl:`, `group-*`, `peer-*`, `data-*`, `aria-*`, `[&>*]`. |
| `UtilityGenerator` | The stylesheet, into `@layer utilities`. |
| `CandidateScanner` | Deliberately over-inclusive extraction from source text. |
| `ApplyExpander` | `@apply` inside VCSS. |
| Build-step integration, token hot reload | ⏳ waits on the asset pipeline |

## The ideas

**Everything lands in `@layer utilities`,** and that one line is what makes the system behave. A
generated `.p-4` is one class and a hand-written `.card .body` is two, so on specificity alone the
utility loses every time and the only remedy is `!important` on everything the generator emits. With a
layer the question is settled once, declaratively, and specificity never enters into it.

**Only what is used is emitted.** Every family crossed with every token is a stylesheet in the tens of
megabytes; the scanner exists so nobody has to think about that.

**The scanner is over-inclusive on purpose.** It does not parse VXML or C# — it pulls out every run of
characters that *could* be a class name and lets the generator discard the rest. A false positive costs
one unused rule that no element matches; a false negative is a style silently missing at runtime, which
someone debugs for an hour. The asymmetry is enormous and the design follows it. It also means the
scanner cannot be defeated by cleverness the way a parser can: a class name built by concatenation is
invisible to analysis, but its pieces are usually literals.

**Spacing is one base number, not a named scale.** `p-4` is twice `p-2` and everyone can see it.
`p-md` reads better in a design tool and worse in a stylesheet, because nothing about it says whether
it is bigger than `p-sm` by a little or a lot.

**A family is worth having when the engine reads what it sets.** The set here is chosen against
`LayoutStyleBuilder` and `DrawListBuilder` rather than against Tailwind's index — Tailwind ships
around five hundred utility roots and most of them name properties this renderer has never heard of.
The border edges, the logical edges, `flex-1` and `box-sizing` are here because the engine already
reads every longhand they emit, which `UtilityGenerationTests` checks by resolving an element rather
than by comparing text.

⚠ **Some families still emit properties nothing reads**, and knowing which is the point. `opacity`,
`cursor`, `text-align`, `tracking`, `leading`, `z` and `font` were all in this list until the engine
learned them; `ring`, `fill`, `stroke`, `blur`, the transforms and the per-axis `overflow` are still
in it. A rule that resolves to a property no consumer looks at is not a bug in the generator — it is
a utility waiting for an engine feature.

**A shadow token is a whole declaration, not a set of numbers to assemble.** A shadow is a designed
thing: its offset, blur and alpha are chosen together to read as one height above the surface, and a
scale that let them be picked apart would invite exactly the combinations that do not. `shadow-none`
is in the family rather than the theme, so turning one off never depends on somebody having
remembered to define it.

⚠ **`leading-6` and `leading-normal` are not the same kind of thing**, and the family emits them
differently on purpose. The numeric form is a length every descendant inherits as written; the named
form is a *ratio* each descendant multiplies by its own font size. A heading inside a body with
`leading-relaxed` wants the ratio, and the same value in pixels would give it the body's line height.

`font-semibold` needs the theme's `fontWeight` tokens *and* a face registered at that weight — the
token gives `font-weight: 600` and the registry answers it with the nearest face it was given, so a
family shipped only at 400 draws regular and says nothing. That is CSS's behaviour rather than a gap,
but it is the reason `font-bold` can look like it did nothing.

**Border widths are pixels where padding is spacing steps.** `p-2` is two steps of the base and
`border-2` is two pixels, which looks inconsistent written down and is right: a border is a hairline
or it is not, and scaling it with the base would mean a theme with a larger base silently thickened
every rule in the editor.

## The gate

`UtilityFamilyTests` is the one [doc 14](../../docs/plan/14-roadmap.md) names for 4b: one case per
family, saying what each emits.

The assertion worth the most is elsewhere, in `UtilityGenerationTests`: **a generated utility computes
to what the hand-written rule would**, checked by loading the generated sheet into the style engine and
resolving an element. That checks the generator against the *engine* rather than against an expectation
of the text it ought to produce — a generator emitting syntactically valid CSS that the cascade then
read differently would pass every string comparison and fail this.

## What it found

**A bracket-aware search that could never find a bracket.** The parser splits on `:`, `/` and `[` while
respecting bracket depth, and the depth was updated in the same `switch` that tested for the separator —
so searching for `[` hit the depth-increment arm and returned nothing. Every arbitrary value silently
stopped being one. Twelve tests fail with it reintroduced.

**A layer test that was asserting document order.** The check that `@layer utilities` loses to an
unlayered component rule loaded the component rule *second*, where source order gives the same answer —
so it passed with the entire `@layer` wrapper replaced by `@media all`. The component rule is loaded
first now, where only the layer can produce the right result. Found by sabotage, and the same mistake
the cascade suite's important-origins test made: *a test that asserts a winner where the rules differ
in more than one respect is testing whichever difference happens to be implemented.*

**A negation that read the wrong thing.** `-w-full` came out as `width: -100%`. The guard tested the
shape of the *resolved* value — reject anything not starting with a digit — and `full` resolves to
`100%`, which starts with a digit. Nothing about the output distinguishes a keyword's percentage from
a fraction's, so the check moved to the value as written. The general form is worth keeping in mind:
*a validity test applied after resolution can only see what survived it.*

## Deliberate limits

`text-` means alignment, then font size, then colour, resolved in that order — so a colour named
`center` or `lg` is unreachable through it. One prefix meaning three properties is what makes both
`text-lg` and `text-accent` read right, and this is the price. `border-` and its eight edge families
do the same with width and colour, where the order costs nothing — no colour is plausibly named `2`.

An arbitrary value on a border edge is read by its shape: `border-[#f00]` is a colour and
`border-[3px]` is a width, and `border-[var(--x)]` is a width because there is genuinely no way to
tell and a width is the commoner one.

`ltr:` and `rtl:` match a `dir` attribute on an *ancestor*, the same shape `dark:` has under the class
strategy. `direction` is a CSS property here, so there is nothing else in the tree for a selector to
match — and the consequence is that an element cannot select on its own direction, only on one it
inherits.

Two media-query variants on one utility (`sm:md:p-4`) are dropped rather than nested, because Vixen's
`@media` support does not nest. `@apply` refuses variants, because a variant would have to invent a
rule with a different selector from the block it sits in, which is not what "apply this here" means.

Licensed under Apache-2.0.
