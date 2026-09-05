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
| `ThemeTokens` | An `@theme { --color-*: … }` block → colours, spacing, radii, type scale, weights, font stacks, shadows, breakpoints. Layered over the shipped default. |
| `UtilityParser` | `[-]?[variant:]*utility[-value][/opacity][!]`, bracket-aware throughout. |
| `UtilityFamilies` | What each family emits. Table-driven. `Family.Scope` is what a rule is *about* — `space-*` and `divide-*` are about the children. |
| `UtilityComposition` | The `--tw-*` fragments, and what each is worth unset. `from-*`/`via-*`/`to-*` + `bg-linear-*` is the worked case; the guide is [ui/utility-composition](../../docs/guide/ui/utility-composition.md). |
| `Variants` | `hover:`, `md:`, `dark:`, `ltr:`/`rtl:`, `group-*`, `peer-*`, `data-*`, `aria-*`, `[&>*]`. |
| `UtilityGenerator` | The stylesheet, into `@layer utilities`. |
| `CandidateScanner` | Deliberately over-inclusive extraction from source text; `ScanStyleSheet` skips a `.vcss` declaration's value, which is the one place a class name cannot be. |
| `ApplyExpander` | `@apply` inside VCSS. |
| Build step | `build/Vixen.Ui.Styling.Utilities.targets` + `Tools/Vixen.StyleGen`. A project gets its sheet with no code of its own; a `vixen.ui.vcss` is optional. |
| Token hot reload | ⏳ waits on the asset pipeline |

## The build step

`build/Vixen.Ui.Styling.Utilities.targets` is packed into `buildTransitive/` and imported by a
`PackageReference` to this assembly **or to anything that depends on it**, and that is the whole of
what a project has to do. Before `CoreCompile` it finds the project's
`vixen.ui.vcss`, scans **every source file and every `.vxml`** for class names, and writes the sheet
into `obj/` twice: as a `const string` added to `@(Compile)`, and as a `.vcss` file for the tools.
`Tools/Vixen.StyleGen/README.md` is the detail.

**Scanning the C# is not a bonus, it is the case that was broken.** Most of the editor's chrome is
built in code with `AddClass("flex")`, and the startup bootstrap this replaces could only ever see
embedded markup — so a utility asked for from C# was silently missing. The scanner does not parse
anything, which is exactly what lets it be pointed at a `.cs` file.

⚠ **It is a process rather than a source generator, and the reason it was one has expired.**
`ThemeTokens` used to read YAML through `Vixen.Core.Yaml`, which is YamlDotNet; a Roslyn analyzer's
dependencies do not travel with it, because `OutputItemType="Analyzer"` contributes one DLL, and
`Vixen.Ui.Markup.Generators` escapes the same trap by *linking* its front end's source — which works
because that front end is Vixen's own code and could not work for a package. Under `@theme` there is
no YamlDotNet: a token is a custom property and the reader is a text scan over this assembly's own
code, so the whole dependency argument is gone and this assembly has no package references left.
Making the step a generator is a separate change and a real one; the blocker is what has lifted.

**`Samples/14-Mmo/Mmo.Ui` was the last project doing it by hand, and it is converted.**
`Theme/MmoStyles.cs` was a hundred and thirty-five lines — embed the markup, walk the manifest, run
the scanner, run the generator — and it is deleted. What replaced it is `<VixenUi>true</VixenUi>`,
one `VixenStyleBase` naming `hud.vcss`, and the fifteen `VixenStyleSafelist` items that used to be a
`static readonly ImmutableArray<string>`. The generated class keeps the name `MmoStyles`, so the only
edit outside the project file was `Compile()` becoming `Css`.

⚠ **Outside this repository not even that line is needed.** This package's `.targets` ships in
`buildTransitive/`, so a `PackageReference` to `Vixen.Ui.Controls` — which reaches `Vixen.Ui`, which
reaches this — imports the step. It used to ship in `build/`, which NuGet imports only for a *direct*
reference, and nobody references this assembly directly.

## `@theme`, and the palette that ships with it

A design token is a custom property in an `@theme { … }` block, which is Tailwind v4's model rather
than a resemblance to it. Declaring `--color-mint-500` does two things at once: it makes the variable
available to hand-written CSS, and it tells the generator that `bg-mint-500`, `text-mint-500` and
`border-mint-500` exist.

**The engine ships v4.3.3's own `@theme` as the default** — `Theme/vixen.default.vcss`, transcribed
from `packages/tailwindcss/theme.css` rather than recalled: 26 colour ramps of eleven steps plus
black and white, `--spacing`, `--breakpoint-*`, `--text-*`, `--font-*`, `--font-weight-*`,
`--radius-*` and `--shadow-*`. 347 declarations. So `bg-blue-500` works in a project with no theme
file at all, and a project layers its own over the top:

```css
@theme {
    --color-*: initial;              /* none of v4's ramps, thanks */
    --color-brand: oklch(62% 0.2 165);
    --spacing: 4px;
}
```

`--<namespace>-*: initial;` empties one namespace, `--*: initial;` empties every one, and
`--color-blue-500: initial;` removes a single token. That is v4's mechanism verbatim, and the two
worked examples in the tree take opposite sides of it: `Editor/Vixen.Editor.Ui/Theming/vixen.ui.vcss`
clears the colours and the breakpoints, because the editor's palette is designed and a tool window is
not a page; `Samples/14-Mmo/Mmo.Ui/Theme/vixen.ui.vcss` keeps the ramps and adds its own names.

⚠ **Everything shipped is `oklch()`, and that is load-bearing rather than fashionable.** Two of every
three v4 colours are outside sRGB — `blue-500` is linear blue **+1.053**, `emerald-500` is linear red
**−0.039** — so a hex transcription would throw away the chroma before anything could use it. Carried
as written, `GamutMap` reduces chroma at presentation against the surface's *granted* gamut, so the
same token is the showable colour on an sRGB display and the vivid one on P3.
[doc 43 § D4](../../docs/plan/43-web-styling-parity.md) has the measurements.

⚠ **Three divergences from v4's emission, all of them at the boundary rather than in the model.**
Lengths are resolved to pixels at build time at 16px to the rem, because there is no root font size
downstream to resolve them against and a token that stayed relative would be a number nothing could
turn into one. A line height written as a ratio — v4's `calc(1.25 / 0.875)` — is multiplied out
against its size for the same reason. And a utility emits the token's *value* rather than
`var(--color-blue-500)`; the variable is emitted too, but only into a `root` rule holding what a
sheet actually references, because three hundred custom properties on every document's root to serve
the handful anyone says `var()` against is a cost with nothing on the other side of it.

⚠ **What is not shipped, and it is a list rather than an oversight.** `--tracking-*`,
`--leading-*`, `--inset-shadow-*`, `--text-shadow-*`, `--ease-*`, `--animate-*`,
`--blur-*`, `--perspective-*` and `--aspect-*` are v4 namespaces no family here reads.
`--drop-shadow-*` left this list when `drop-shadow-*` arrived, and `--container-*` left it when the
`@sm:` variants did — which is what the list is for. Shipping them
would emit variables that resolve and utilities that do not, which is the failure mode doc 43 spends
Part 1 measuring. `--font-*` is the one exception: the stacks are carried so a hand-written rule can
say `var(--font-mono)`, and `font-*` in this engine still resolves a *weight* — wiring the family
needs a `font-family` that can pick the first name the face registry knows.

## The ideas

**Everything lands in `@layer utilities`, under an `@layer base, components, utilities;` statement,**
and those two lines are what make the system behave. A generated `.p-4` is one class and a
hand-written `.card .body` is two, so on specificity alone the utility loses every time and the only
remedy is `!important` on everything the generator emits. With a layer the question is settled once,
declaratively, and specificity never enters into it.

⚠ **A layer only wins if something else is in a lower one.** For a release this sheet was the only
layered thing in the tree, and a lone `@layer utilities` against unlayered component sheets is
strictly worse than no layers at all: an unlayered rule beats every layer, so `.p-4` lost to a bare
tag selector it would have beaten on specificity. The engine's ten theme sheets are `@layer
components` now, which is what the `utilities` layer was always for. The statement is emitted
alongside because layer *order* is fixed by whoever names a layer first — a sheet that only opened its
own block would sort wherever the load order happened to put it. See
[the cascade layers page](../../docs/guide/ui/cascade-layers.md).

**Only what is used is emitted.** Every family crossed with every token is a stylesheet in the tens of
megabytes; the scanner exists so nobody has to think about that.

**The scanner is over-inclusive on purpose.** It does not parse VXML or C# — it pulls out every run of
characters that *could* be a class name and lets the generator discard the rest. A false positive costs
one unused rule that no element matches; a false negative is a style silently missing at runtime, which
someone debugs for an hour. The asymmetry is enormous and the design follows it. It also means the
scanner cannot be defeated by cleverness the way a parser can: a class name built by concatenation is
invisible to analysis, but its pieces are usually literals.

⚠ **One input is narrowed, by structure and not by judgement: a `.vcss` has its declaration values
skipped.** `CandidateScanner.ScanStyleSheet` is the entry point and `Vixen.StyleGen` picks it by
extension. **A class name cannot be *used* from the right of a colon** — the text between a
declaration's `:` and its terminator is a value — so this takes nothing away from the asymmetry above,
and it is deliberately not generalised to C# or `.vxml`, where a colon means whatever the language says
it means. The rule is `identifier`-then-`:`, not any `:`, and the two exceptions that shape says are
both real. `@apply p-4 hover:bg-accent flex;` is a statement inside a block whose value *is* a list of
class names, and a bare-colon rule loses `flex` from it. And a comment in this tree can end where
nobody meant it to — CSS closes at the first `*/`, which prose about `**/*.vcss` contains — so the text
after one is loose sentences, colons and all; a bare-colon rule swallowed a paragraph of the editor's
own theme file and lost `rounded-md` with it.

⚠ **A malformed declaration is not the same free thing an unused rule is.** The scanner handed
`text[1..]` — a C# range expression — to the arbitrary-value path, which emitted
`.text\[1\.\.\] { font-size: 1..; }` in every build of `Vixen.Editor.Ui`; ExCSS dropped the declaration
and said nothing. A rule nothing matches costs nothing, but a dropped declaration is noise in every
diagnostic a reader of the generated sheet will run and is indistinguishable from the real parse
failure they are looking for. `UtilityFamilies` refuses a bracketed value that is not CSS *shaped* —
parentheses balance, strings and `url()` are closed, and every `.` outside them belongs to a number —
and refusing means no rule at all, reported alongside the misspelt utilities. It is a token-shape test
and must not become a value parser: `font-size: red` is nonsense CSS refuses and this accepts, because
deciding otherwise needs a table of every property's grammar.

**Spacing is one base number, not a named scale.** `p-4` is twice `p-2` and everyone can see it.
`p-md` reads better in a design tool and worse in a stylesheet, because nothing about it says whether
it is bigger than `p-sm` by a little or a lot.

⚠ **The set here is smaller than Tailwind's, and that is a gap rather than a principle.**

> An earlier version of this section read: *"A family is worth having when the engine reads what it
> sets. The set here is chosen against `LayoutStyleBuilder` and `DrawListBuilder` rather than against
> Tailwind's index."* That is a description of a constraint promoted to a design decision, and it is
> the wrong way round. The requirement is Tailwind-equivalent utilities, so **Tailwind's index is the
> specification and the renderer is what has to grow** — a family that emits a property no consumer
> reads names a hole in the engine, and the answer is a task against the engine, not a shorter table.
> [doc 43](../../docs/plan/43-web-styling-parity.md) measures the distance across **328 Tailwind v4
> roots**. ⚠ The distribution is deliberately not copied here — it was, and it was a week out of date
> within a week. It is generated from the consumption probe on every test run and lives in
> [doc 43's Part 0](../../docs/plan/43-web-styling-parity.md#the-six-states-and-why-the-four-in-the-brief-were-not-enough),
> which `ParityLedgerTests` asserts against the table beside it.

What the family set *is* chosen against is order of work. The border edges, the logical edges,
`flex-1` and `box-sizing` came first because the engine already read every longhand they emit, which
`UtilityGenerationTests` checks by resolving an element rather than by comparing text. That is a
sequencing argument — do the families whose properties already land — and it stops being a reason the
moment the property lands too.

⚠ **Some of the properties these families emit reach no consumer, and the list is no longer written
here, because every hand-written copy of it has gone stale within the month.** It is measured, on every
test run, by `Core/Vixen.Ui.Styling.Utilities.Tests/UtilityConsumptionGateTests`, and the current list
with a task number against each line is
[`InertProperties.txt`](../../Core/Vixen.Ui.Styling.Utilities.Tests/InertProperties.txt) beside it. A
rule that resolves to a property no consumer looks at is not a bug in the generator — it is a utility
waiting for an engine feature — and the gate is what makes a waiting utility name what it is waiting
for, rather than waiting quietly.

⚠ **"Interned" is not the test, and "it resolves" is even less of one.** The gate establishes
consumption by *changing the property and running frames*: a scene is built twice, once with one extra
declaration, and the layout, the draw list, the cursor and the hit test are compared either side. A
property that moves none of them at any value a utility can give it, in any of six arrangements, is not
read by anything — however many `Intern("…")` calls name it. That distinction is not academic:
[doc 43](../../docs/plan/43-web-styling-parity.md) measured it at seven properties, and the gate's first
run found three more that this file and the plan document had both called supported.

⚠ **One case is worse than inert and is not on that list.** The per-edge border *widths* are read by
the layout and ignored by the draw list, which takes one thickness from `Edge.Top` and one colour from
`border-top-color`: so `border-l-2` insets the content box and paints nothing, and `border-t-2` paints
all four sides. A property that is *half* read is harder to find than one that is not read at all —
the geometry moves and the picture does not follow.

**`overflow-x-*` and `overflow-y-*` are read now, and `overflow-*-auto` reaches the layout.** They
were the most misleading pair in the set: the unprefixed `overflow` was read, the two per-axis names
were interned by nobody, and `overflow-y-auto` therefore resolved cleanly and did nothing at all.
`Vixen.Ui.OverflowReader` is the one place all three are resolved, for the clip stack and the hit test
alike, and `LayoutStyleBuilder` maps `auto` onto the layout's `Overflow.Scroll` — the same layout CSS
gives it, since the only thing `auto` and `scroll` disagree about is a scrollbar gutter nothing here
draws. Two things follow that a web author should be told. A named axis beats the shorthand whatever
order they were written in, because nothing expands `overflow` into longhands on the way in and the
computed style keeps no source order. And there is **no coercion between the axes**: CSS turns a
`visible` into an `auto` when its partner is not visible, and this does not, so `overflow-x-auto`
clips sideways and leaves the top and bottom edges alone — which is what the class name says and what
a rectangle with one pair of edges past the viewport expresses exactly.

⚠ **A clip is not a scrollbar.** `overflow-y-auto` cuts the content off; nothing offers to scroll it.
Scrolling is `ScrollView`, a control that owns its bars and offsets its content — so a panel that
needs to reach what it clipped needs one of those, and the utility alone will hide the rest.

⚠ **`float-*` and `clear-*` are short two classes each, and the two are missing on purpose.**
Tailwind v4's `float-start` / `float-end` and `clear-start` / `clear-end` emit the *logical*
`inline-start` and `inline-end`, which CSS resolves against the writing mode. `FloatSide` and
`Clear` hold CSS 2.1 §9.5's **physical** keywords, which do not flip with `direction` — the layout
corpus proves it by shipping RTL variants of ten `float_bfc_*` families whose expectations are
identical to their LTR twins. That leaves three shapes and only one honest one: emit the logical
keyword and let the bridge drop it, which is a class that resolves and does nothing; alias it onto
`left`, which is right in LTR and wrong in RTL inside the same declaration; or leave it unspelt and
record the gap. This is the third, and it is why both roots read `partial` in the parity ledger with
`value_gap` naming the four class names rather than reading `works`.

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

## The boundary: what stays hand-written, and why

The layer ladder made a real question askable for the first time. While control defaults were
unlayered they beat every utility whatever the specificity, so "should this be a utility?" had one
answer — it could not be — and the sheets were written accordingly. `@layer base, components,
utilities` removed that constraint, and the honest finding is that **it moved the boundary far less
than it looks like it should have.** This section is the boundary, written down, so that the next
sweep does not have to rediscover it.

**A tag-keyed control default stays.** `button { padding: … }` gives a control its look with no
classes on the element, which is what a user-agent sheet is for and what makes a game that loads
`ControlTheme` and nothing else get coherent controls. A utility cannot express it, because there is
no element to put the class on. This is most of every sheet in the tree and none of it is debt.

**A rule whose CSS is more legible than its utility spelling stays.** `@apply` on a five-declaration
block reads worse than the five declarations and hides what it sets behind names the reader must
expand. The argument that makes `p-4` worth having runs the other way here: `p-4` is twice `p-2` and
everyone can see it — the utility *carries* the scale relationship, which is information the raw
`16px` loses. `display: none` carries nothing that `hidden` carries; `position: relative` carries
nothing that `relative` carries. Convert where the utility name says something the value does not.
That is the whole test, and single-declaration rules almost never pass it.

⚠ **A semantic class name is not a utility with a worse name.** `.parked`, `.closed` and `.hidden`
all compile to `display: none` and are not interchangeable: the tag-qualified rules that use them say
*why* a thing is not showing, and collapsing the three into `hidden` would lose the distinction the
sheets are built on. A duplicate declaration is not a duplicate rule.

**`@apply` works in any sheet a document installs, and it did not use to.** For a release
`ApplyExpander` ran in exactly one place — `Tools/Vixen.StyleGen`, over files named by
`@(VixenStyleBase)` — and no project in the tree set that item, so every hand-written sheet reached
`document.Load` verbatim and its `@apply` was dropped by the parser as an unknown at-rule, silently.
It is now expanded at **install** time, by `UiDocument` through `StyleEngine.Preprocessor`, which is
the one door every sheet already comes through. See `Core/Vixen.Ui/StyleApply.cs`.

Install time rather than the build path, and the two reasons are settled. `Vixen.Ui.Controls` and
`Vixen.Ui.Controls.Advanced` hold the two largest sheets in the tree and are Core assemblies that
doc 00 forbids from referencing `Tools/Vixen.StyleGen`, so the build path can never serve them. And
requiring an MSBuild item before an at-rule stops being ignored is the friction doc 09's "as
straightforward as web development" rules out: a rule that works in one project and does nothing in
the next is worse than one that does not exist.

⚠ **The design question was token *ordering*, and getting it wrong is silent.** At build time
StyleGen holds every `@theme` before it expands anything; at install time the sheets arrive one at a
time, and `EditorShell` installs `ControlTheme` before the sheet that declares the editor's tokens.
An `@apply p-4` expanded against a half-built theme does not fail — `ThemeTokens.CreateDefault()`
answers every namespace — it silently resolves to the *shipped* number. So the expansion is against
the document's **merged** theme, and a sheet arriving with an `@theme` after a sheet that used
`@apply` triggers a reload that expands every sheet again. The guard is exact: `ThemeTokens.Apply`
reads nothing but `@theme` blocks, so a sheet without one cannot invalidate an earlier expansion,
and a document with no `@apply` in it — every document in this repository today — never builds the
merge at all. `ApplyAtInstallTests` is the gate, and its ordering case asserts against the generated
utility as an oracle rather than against a literal.

What the expander still refuses is unchanged: a variant. `@apply hover:bg-accent` would have to
invent a rule with a different selector from the block it sits in, and every refusal is now reported
— see the drain below.

⚠ **A refused `@apply` used to be as silent as an unexpanded one.** `ApplyExpander.Diagnostics` is
cleared at the start of every `Expand`, so an expander reused across a document's sheets keeps only
the last sheet's refusals. `UiDocument` drains it per sheet onto log event 7005, beside the
`StyleSheetLoader` and `SelectorCompiler` diagnostics it drains onto 7004 — see
`Core/Vixen.Ui/StyleDiagnostics.cs` and `docs/manual/log-events.md`.

⚠ **A duplicate the generator cannot reach is not a duplicate.** `AdvancedTheme.vcss` hand-writes
`.hidden { display: none; }` and must keep it: ten controls in `Vixen.Ui.Controls.Advanced` call
`AddClass("hidden")`, and that assembly is Core, which doc 00's layer rules forbid from referencing
`Tools/Vixen.StyleGen` — `CheckArchitecture` enforces it by reading the `.csproj`. There is no wiring
that would let the build step emit the class for that project, so the rule is load-bearing wherever a
consumer does not generate a `.hidden` of its own. Before deleting a hand-written class that looks
generated, check that the assembly needing it can actually run the step.

⚠ **A class name in a comment becomes a rule, and that is now the *only* way most of them get there.**
`CandidateScanner` deliberately parses nothing, so prose is indistinguishable from a `class` attribute.
`.absolute`, `.block`, `.hidden` and `.static` used to be harvested from `position: absolute` and
friends in the sheets themselves; `ScanStyleSheet` closed that door, and all four are still in the
editor's sheet — out of the word "absolutely" in a comment, the C# keyword `static`, a dictionary key
`["grid"]`, and sentences about blocks and hidden commands. The consequence for the boundary is
practical: **rule count in a generated sheet is not a progress signal**, in either direction. Closing
the declaration-value hole moved it by nothing at all.

## The gate

`UtilityFamilyTests` is the one [doc 14](../../docs/plan/14-roadmap.md) names for 4b: one case per
family, saying what each emits.

The assertion worth the most is elsewhere, in `UtilityGenerationTests`: **a generated utility computes
to what the hand-written rule would**, checked by loading the generated sheet into the style engine and
resolving an element. That checks the generator against the *engine* rather than against an expectation
of the text it ought to produce — a generator emitting syntactically valid CSS that the cascade then
read differently would pass every string comparison and fail this.

⚠ **`VariantCoverageTests` is that assertion applied to the variant table, and it exists because the
table did not have it.** Until it was written, four of the twenty-odd variant families had an
end-to-end test — `hover:`, `focus:` (only stacked with `hover:`), `md:` and `[&>*]:` — and the rest
had `Assert.Contains` on the generated text or nothing at all. `peer-*` and `aria-*` had nothing of any
kind; `ltr:`/`rtl:` emitted `[dir=…]` and no code anywhere in the repository had ever set a `dir`
attribute. Generated text is exactly what a *dead* variant still produces correctly, which is how
every breakpoint in the engine stayed inert through a green suite until `UiDocument` started handing
the cascade a `MediaContext`.

Two things make it a gate rather than a list. Every case asserts a **computed property value in a
built document, positive and negative** — a rule that applied unconditionally would pass every
positive assertion in the file. And the cases are **enumerated off the engine's own tables**:
`Variants.StateVariants` and `ThemeTokens.Screens`, checked both ways, so a variant added without a
scene fails the build and a scene naming a variant that no longer exists fails it too.

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

⚠ **And the fix is a hand-kept list, which means it is a fix that has to be remembered.** `NotNegatable`
names the six keywords `TrySize` had when it was written; the six viewport ones added since —
`w-svw` and its siblings, resolving to `100vw` and `100vh` — begin with a digit in exactly the same
way and would have gone straight through. They are in the set, and `Only_a_number_can_be_negated`
asserts them. *A guard that is a list of names is a guard that is wrong by default whenever the thing
it guards grows*, which is the argument for the class of check that enumerates rather than
remembers — and the reason this one is worth re-reading every time a keyword lands in `TrySize`.

**A breakpoint that could not be spelled.** `2xl:` was emitting `.2xl\:p-4`, which is not a selector —
CSS Syntax 3 § 4.3.8 requires a leading digit to be escaped as a code point, `\32 `. ExCSS refused the
rule and the generated sheet contributed nothing, in every project using the shipped theme, for as long
as `--breakpoint-2xl` has been in it. Nothing caught it because the only escaping tests were
`hover:w-1/2`, `w-[37px]` and `p-4`, and the only breakpoint tested end to end was `md:`. The lesson is
narrower than "test more": *a table with five entries tested at one entry is tested at none of the
interesting ones, and the interesting one is always the entry whose shape differs.*

**A range expression that became a stylesheet rule.** `text[1..]` in `EditorIconAttribute` parsed as the
utility `text` with the arbitrary value `1..`, and `font-size: 1..` went into the editor's sheet on every
build. Nothing caught it because the arbitrary path was written as "goes straight through" and meant it
— and because a dropped declaration is exactly as quiet as a rule nobody matches. *The escape hatch has
to refuse what is not CSS, and it has to refuse by emitting nothing rather than by emitting less.*

**An exclusion that would have eaten the thing it was excluding for.** Skipping a `.vcss` declaration's
value is safe, and "skip from any `:` to the next `;`" is not the same rule: `@apply p-4 hover:bg-accent
flex;` loses `flex`, and a comment that ended early at a `**/*.vcss` loses the paragraph after it. Both
were found by sabotage — the exception deleted, the suite red on two tests, the exception restored —
and the fix in both cases is that a declaration's colon comes straight after an identifier. *A rule
about delimiters still has to be a rule about the delimiters the language actually has.*

**An ARIA state treated as a presence flag.** `aria-expanded:` shared `data-`'s shorthand and emitted
`[aria-expanded]`, so it matched a collapsed disclosure — which carries `aria-expanded="false"` rather
than no attribute — exactly as it matched an expanded one. The coverage that existed asserted `"true"`
matches and *absent* does not, and both of those hold under the broken implementation as well as the
fixed one; the discriminating case is `"false"`, and nothing asserted it. It emits
`[aria-<state>="true"]` now, per WAI-ARIA 1.2 § 6.3, and the arbitrary form stays verbatim. *A negative
assertion is only worth what it excludes: "absent" and "false" are one negative in a boolean attribute
and two different ones in a tri-state, and picking the wrong one buys nothing.*

**A `~` that a test could not tell from a `+`.** The first version of `peer-*`'s coverage put the peer
immediately before the element, where the subsequent-sibling combinator and the adjacent one select the
same thing — so replacing `~` with `+` left it green. One filler sibling between them is the whole
difference. *A combinator test needs a scene the weaker combinator fails.*

**A gate that would have been blind to a whole family, rather than wrong about one.** The consumption
probe measured what the family table puts on *the element carrying the class*, and `space-x-4` puts
nothing there — it is `> :not(:last-child) { … }`. So a scoped family emitted no properties by that
reading, entered neither the consumer column nor the inert one nor the composed one, and was
unclassified in a file whose entire claim is that nothing escapes classification. The probe's element
has two children now, and the baseline has two as well because the child *inherits* — a childless
baseline would have credited the family with every inherited property. Two children rather than one
because `:not(:last-child)` matches nothing under a single child, which is the same blindness one step
further in. *An instrument that measures the wrong element reports zero and looks like a pass.*

## Deliberate limits

`text-` means alignment, then font size, then colour, resolved in that order — so a colour named
`center` or `lg` is unreachable through it. One prefix meaning three properties is what makes both
`text-lg` and `text-accent` read right, and this is the price. `border-` and its eight edge families
do the same with width and colour, where the order costs nothing — no colour is plausibly named `2`.

⚠ **Neither overload is a Vixen invention.** Tailwind v4 resolves `text-*` against `--text-*` for a
size and `--color-*` for a colour with `text-center` a static utility beside them, and its `border-*`
sets a width *and* a colour, and its `font-*` a family *and* a weight. A colour named `lg` is exactly
as unreachable there. What is Vixen's own is next door and is a real defect: **the longest-prefix
split has no fallback.** `rounded-tl-lg` reaches the family `rounded` with the value `tl-lg`, no token
table answers it, and the class is reported unknown — where Tailwind would go on to try `rounded-tl`
as a root of its own. Until `SplitName` retries the next-longest prefix on failure, adding a
per-corner or per-axis family is blocked by the shorter family that shadows it.

⚠ **`transition` on its own animates nothing here, where in Tailwind it animates.** Vixen's family
emits `transition-property` and stops; `duration-*` and `ease-*` are separate families over
`transition-duration` and `transition-timing-function`. Tailwind's `transition` shorthand sets all
three, defaulting to 150ms and a timing function, so a class that is a complete instruction there is
half of one here — `transition` without a `duration-*` computes a `0s` duration and the property
snaps. The divergence is worth knowing before it is worth fixing: nothing in the tree writes the
class today, and the two occurrences in generated sheets are the English word out of comments.

An arbitrary value on a border edge is read by its shape: `border-[#f00]` is a colour and
`border-[3px]` is a width, and `border-[var(--x)]` is a width because there is genuinely no way to
tell and a width is the commoner one. The shape test is `#`, `rgb` or `hsl` and not a named-colour
table, so `border-[red]` and `divide-x-[red]` are widths — `IsPlausibleValue`'s remark is the argument
for keeping the escape hatch a token-shape test rather than a value parser.

⚠ **`space-*` and `divide-*` are two classes of specificity, where Tailwind v4's are one.** They are
the only families whose rule is about the *children* — `.space-y-4 > :not(:last-child)` — and v4 wraps
that scope in `:where()` so the rule stays at one class and a child's own `mb-0` still wins.
`SelectorCompiler` charges a class for `:where()` exactly as it does for `:is()`, so no spelling
available here reaches zero: the emitted rule is `(0,2,0)` and it beats a child's single-class
utility. **That is v3's behaviour, and it shipped for four major versions** — the escape is v3's too:
put the exception on the container, or do not reach for `space-*` on a list whose items set their own
margins. Closing it is a change in `Vixen.Ui.Styling`, and
`ChildScopedFamilyTests.A_child_margin_utility_loses_to_the_containers_space_and_that_is_the_v3_behaviour`
fails the day it lands.

⚠ **`space-y-*` emits `margin-bottom` where v4 emits `margin-block-end`, and it is not a shortcut.**
`LayoutStyleBuilder.EdgeNames` interns `-left`, `-top`, `-right`, `-bottom`, `-inline-start` and
`-inline-end` — no block pair — so v4's spelling resolves, computes, and moves nothing, which is the
inert family the gate exists to keep out. Nor is the physical pair an approximation of it:
`Vixen.Ui.Layout` has no writing mode, so the block axis *is* top-to-bottom in every configuration the
engine can be in. `space-x-*` keeps v4's logical `margin-inline-end`, because that one is read and
mirrors under `direction: rtl`, which is the whole point of it.

⚠ **`space-x-reverse`, `divide-*-reverse` and the `divide-<style>` keywords are absent.** The reverse
spellings are how v4 flips which edge of the pair carries the width, and they need `calc()` to
multiply an edge by a `--tw-*-reverse` flag; `StyleValueParser` has none, so the flag would be a
custom property nobody reads. The style keywords need a reader for `border-style`, and there is not
one — measured through the consumption probe, not assumed. `@apply` refuses a scoped family for the
same reason it refuses a variant: it is a rule with a selector of its own, not declarations to drop
into the block it was written in.

`ltr:` and `rtl:` match a `dir` attribute on an *ancestor*, the same shape `dark:` has under the class
strategy. `direction` is a CSS property here, so there is nothing else in the tree for a selector to
match — and the consequence is that an element cannot select on its own direction, only on one it
inherits.

**Two conditional variants on one utility (`sm:md:p-4`, `dark:md:p-4`) nest.** They used to be
dropped, and the reason recorded here and in doc 43 § D3 — "Vixen's `@media` support does not nest" —
was not true of the cascade. `StyleSheetLoader.LoadMedia` recurses into the rule it has just matched,
so a conditional group rule inside another has always loaded and always conjoined, exactly as CSS
Conditional 5 § 3 describes. What could not nest was `UtilityGenerator`, which carried one `string?`
for the whole variant stack and gave up when a second at-rule disagreed with the first. It carries an
ordered, deduplicated list now, and the emitter is a trie over those chains so that `sm:m-2` and
`sm:md:p-4` share one `@media (min-width: 640px)` rather than opening two.

⚠ **That is also the prerequisite for `@container`, and it is why the list is a stack and not an
`and`-joined string.** Flattening `sm:md:` to `@media (min-width: 640px) and (min-width: 768px)` would
have worked and would have been a dead end: a container query is a different at-rule and cannot be
conjoined into a media query, so the moment `@sm:` joins `md:` the flattened form has nowhere to put
it. Nesting generalises; conjunction does not.

`@apply` refuses variants, because a variant would have to invent a rule with a different selector
from the block it sits in, which is not what "apply this here" means.

⚠ **A leading digit in a class name is escaped as a code point, and until recently was not.**
CSS Syntax 3 § 4.3.8: `\2` *begins* a hex escape, so the backslash form `.2xl\:p-4` is not a selector
and `.\2xl\:p-4` is a selector for something else. The correct form is `.\32 xl\:p-4`, space included
— the space terminates the escape. The engine ships `--breakpoint-2xl`, so every `2xl:` utility in
every project was emitting a rule ExCSS then refused, with a diagnostic nobody read. `VariantCoverageTests`
enumerates the theme's own breakpoints now, so a breakpoint that stops resolving fails the build.

**Composition is faithful, not folded.** A composed utility really does emit a custom property, and
the cascade resolves the `var()` references at use time — it is not the generator assembling them as
it writes. That costs a `var()` substitution per element and it is the only design that can be right:
`from-accent hover:from-accent-hover` is two selectors, and which one supplies the colour is a
question about where the pointer is. The full argument, and the reason an unset fragment degrades
instead of erasing the declaration, is on `UtilityComposition`.

⚠ **`bg-linear-<angle>` and the radial and conic forms are not registered.** Only the eight named
directions are, because the keyword table is what `bg-linear` resolves against and an angle is not a
keyword — `bg-linear-45` is reported unknown rather than emitted wrong. The fragments are already
shared, so adding them is a value kind and three keywords.

**Three escape hatches, and all three work.** `w-[37px]` is the arbitrary *value* — a registered
family given something the theme does not hold. `[mask-type:luminance]` is the arbitrary *property*,
for a property that has no family at all: it parses to an empty utility name with the property in
`UtilityCandidate.Property`, and it is the one candidate `UtilityFamilies.TryResolve` answers without
consulting the registry. And `bg-(--brand)` is v4's CSS-variable shorthand, rewritten in the parser
into the `bg-[var(--brand)]` it stands for so that nothing downstream knows there were two spellings.
The last two were missing until [doc 43](../../docs/plan/43-web-styling-parity.md) F7; both were
silent, which is the same as a typo from the outside.

⚠ **An arbitrary property is exempt from the consumption gate, and no code makes it exempt.** Nothing
validates what `[mask-type:luminance]` emits — there is no family to say what it means, which is the
entire point — so it collides with the rule that a change may not emit a property nothing acts on.
It needs no exemption clause: `UtilityConsumptionProbe` enumerates `UtilityFamilies.Surface`, which is
computed from the registry, and an arbitrary property is never registered. It is not on the surface,
contributes nothing to `Emitted`, and can appear in neither `Inert` nor `InertProperties.txt`. **A
branch saying "skip the gate for this" would be the hole** — the gate is strong because its domain is
defined positively, by what the registry holds, rather than negatively by a list of escapes. Nor can
the hatch launder a family's debt, which is the real test: registering a `--tw-*` fragment *was* a way
to move a property out of `Inert`, and needed an explicit guard, but writing an arbitrary property in
a `.vxml` changes `Surface` by nothing, and the only way off the surface is deleting a registration —
which stops every use of that family generating, loudly. What the gate protects is a *promise*, and
an arbitrary property makes none: the author typed the property name themselves, and "dropped by the
cascade if no consumer interns it" is the documented behaviour of the hatch. The structural claim is
pinned by `ArbitraryPropertyTests`, so a probe rewritten to scan generated sheets fails there rather
than quietly widening the gate.

⚠ **Both new hatches are shape-tested on both halves, and a malformed one produces no rule at all.**
`UtilityParser.IsPropertyName` refuses `[1..:red]` and `[mask type:red]`; `IsPlausibleValue` refuses
the value half exactly as it does for `w-[1..]`. The `text[1..]` defect above had two more ways in and
both are closed. A negated `-[color:red]` and an opacity-suffixed `[color:red]/50` are refused rather
than emitted with the sign or the opacity silently dropped. And the variable shorthand takes only a
custom property — `bg-(brand)` and `Foo(bar)` are left alone — because the scanner is over-inclusive
and hands the parser every `f(x)` in every C# file.

⚠ **The build step is per-project, so a utility written in another assembly resolves to nothing.**
`build/Vixen.Ui.Styling.Utilities.targets` finds `**/vixen.ui.vcss` inside the consuming project and
scans only that project's own sources. One theme spanning several assemblies is not expressible
today, and a class in one of the others is silently unstyled. ⚠ The shipped default makes the second
half of this *worse* rather than better: a project with no theme file used to generate nothing at
all, which was at least loud, and now generates v4's palette against its own sources — so an
assembly that meant to share the editor's tokens and forgot to say so gets Tailwind's instead of
nothing. [doc 43](../../docs/plan/43-web-styling-parity.md) § Part 3 proposes the shape:
per-assembly sheets over a *referenced* token source rather than a copied one.

Licensed under Apache-2.0.
