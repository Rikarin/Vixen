# Spike: ExCSS 4.3.2 as the VCSS front end — ✅ **PASSED, with one gap**

Run on macOS arm64, .NET SDK 10.0.301, ExCSS 4.3.2 — the version
[doc 01](../../01-technology-decisions.md) pins and ADR-009 names.

ADR-009 says VCSS "uses ExCSS for tokenizing/parsing and a Vixen-owned cascade/selector-matching
engine on top". That is a load-bearing assumption for the whole of Phase 4b and it had never been
checked against the library. It holds — with one exception that changes what has to be written, and
which is much cheaper to know now than in the middle of the cascade.

`Probe.cs` beside this file is what was run.

## What was proven

**The selector tree is fully reachable, and typed.** This was the real risk: a parser that hands back
only a selector *string* would force Vixen to write the selector parser anyway, which is most of the
work ADR-009 was avoiding. It does not.

```
.a > .b:hover .c + span
  ComplexSelector enumerates CombinatorSelector { Delimiter, Selector }:
    ">" .a          ← the delimiter is the combinator that FOLLOWS the selector
    " " .b:hover
    "+" .c
    ""  span        ← empty delimiter terminates
```

and a compound selector enumerates its parts as distinct types:

```
a:nth-child(2n+1):not(.x)::before
  CompoundSelector
    TypeSelector          { Name = "a" }
    FirstChildSelector    { Step = 2, Offset = 1 }     ← :nth-child, pre-parsed
    NotSelector           { Inner = ClassSelector }
    PseudoElementSelector { Name = "before" }
```

`IdSelector`, `ClassSelector`, `AttrMatchSelector`, `MatchesSelector` (`:is`) are all there too.
Translating this into Vixen's own matchable form is a visitor, not a parser.

**Specificity is computed already**, as a four-part tuple, so the cascade does not have to count.

**`var()` survives verbatim.** `width: var(--tok, 2px)` comes through as the literal value string,
and `--tok: 3px` is kept as a declaration. Custom properties are Vixen's to resolve, which is what
doc 09 wants.

**Properties ExCSS has never heard of survive verbatim.** `transition: 200ms spring(1,100,10)` — the
Vixen extension doc 09 adds because game UI wants springs and CSS still does not have them — comes
back unmangled. So does `aspect-ratio`, `flex-grow` and `gap` (the last expanded into `row-gap` /
`column-gap` longhands, which is correct and convenient).

**Shorthands are expanded.** `padding: 4px` arrives as four longhands. That is work the cascade does
not have to do.

## The gap: `@layer` is not parsed

```
@layer utilities { .p { padding: 4px } }   →   UnknownRule { Name = "layer", Text = "@layer …" }
```

ExCSS 4.3.2 predates cascade layers. It does not throw and it does not drop the text — the rule
arrives whole, as an unknown one — but it does not give the prelude or the nested rules.

This matters because [doc 09](../../09-ui-framework.md) does not treat `@layer` as a nicety. It is
how the generated utility stylesheet and hand-written component styles are meant to coexist: the
utility system emits into `@layer utilities` precisely so component styles in `@layer components`
and user overrides win by layer order rather than by `!important` wars. Losing it would mean losing
that argument.

**It is a bounded fix rather than a blocker.** `UnknownRule` exposes `Name` and the full `Text`, so
`Vixen.Ui.Styling` reads the prelude itself — `@layer a, b;` and `@layer name { … }` are a trivial
grammar — and hands the block body back to ExCSS as a nested stylesheet. Two things follow, and both
are recorded rather than discovered later:

- The layer *statement* form (`@layer base, components;`) has to be recognised too, because it is
  what fixes layer order before any rule is seen.
- Nested `@layer` inside `@media` needs the same treatment one level down.

## What this changes

- **ADR-009 stands.** ExCSS is the right front end and the selector work it saves is real.
- **Doc 01's dependency register gains a caveat** rather than a correction: the version is right, and
  what it does not do is now written down next to it.
- **Doc 09 gains a sentence** saying `@layer` is Vixen's to parse, so that whoever writes the
  stylesheet loader does not spend an afternoon finding out.

## What was not proven, and is not blocking

`@supports`, `@font-face` and `@import` were not exercised. None of them is on the critical path for
the cascade — the first two are declarations-in-a-condition and a resource hint, and `@import` is a
loader concern that Vixen has to own regardless because it resolves through `Vixen.Core.IO`'s virtual
paths rather than through a file system.
