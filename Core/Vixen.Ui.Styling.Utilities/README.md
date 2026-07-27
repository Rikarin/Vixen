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
| `ThemeTokens` | `vixen.ui.yaml` → colours, spacing, radii, font sizes and weights, breakpoints. |
| `UtilityParser` | `[variant:]*utility[-value][/opacity][!]`, bracket-aware throughout. |
| `UtilityFamilies` | What each family emits. Table-driven. |
| `Variants` | `hover:`, `md:`, `dark:`, `group-*`, `peer-*`, `data-*`, `aria-*`, `[&>*]`. |
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

## Deliberate limits

`text-` means alignment, then font size, then colour, resolved in that order — so a colour named
`center` or `lg` is unreachable through it. One prefix meaning three properties is what makes both
`text-lg` and `text-accent` read right, and this is the price.

Two media-query variants on one utility (`sm:md:p-4`) are dropped rather than nested, because Vixen's
`@media` support does not nest. `@apply` refuses variants, because a variant would have to invent a
rule with a different selector from the block it sits in, which is not what "apply this here" means.

Licensed under Apache-2.0.
