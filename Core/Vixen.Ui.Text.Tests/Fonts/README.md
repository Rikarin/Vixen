# Fonts

These twenty-two files are **not Vixen's**. They are the fonts the Unicode Consortium's
[text-rendering-tests](https://github.com/unicode-org/text-rendering-tests) suite is written
against, copied here verbatim by `Tools/Vixen.TextRenderingTestGen`.

They are committed rather than fetched for the same reason the Unicode property tables are: a
conformance gate that silently skips when its data is missing is not a gate, and CI has no reference
clone. 580 KiB is what it costs to have 413 shaping cases and 100 variable-font cases actually run.

A shaping expectation is only meaningful against the exact font it was written for — glyph names and
positions are the font's, not the script's — and a variation expectation is the font's own outlines
at a point along its own axes, so these cannot be substituted for whatever the machine happens to
have installed.

| | |
|---|---|
| `NotoSans/SerifKannada-Regular.ttf`, `NotoSansBalinese-Regular.ttf` | Copyright Google Inc., [SIL Open Font License 1.1](https://scripts.sil.org/OFL). Noto is a trademark of Google Inc. |
| `TestShapeAran.ttf`, `TestShapeEthi.ttf`, `TestCMAP*.ttf/otf`, `TestGPOS*.ttf/otf`, `TestGSUBOne.otf`, `TestKERNOne.otf` | Copyright © Unicode, Inc., SIL Open Font License 1.1. |
| `TestShapeLana.ttf` | Copyright © 2019 Unicode, SIL Open Font License 1.1, with Reserved Font Names *Da Lekh* and *ᨯᩣᩃᩮ᩠ᨡ*. |
| `TestAVAR.ttf`, `TestGVARFour.ttf`, `TestGVARNine.ttf` | Copyright © 2016–2017 Unicode, Inc. |
| `TestGVAROne.ttf`, `TestGVARTwo.ttf`, `TestGVARThree.ttf` | Copyright © 2016 Monotype Hong Kong Ltd. and Monotype Imaging Inc. |
| `TestGVAREight.ttf` | Copyright © 1992, 2017 Thomas A. Rickner. |
| `Zycon.ttf` | Copyright © 1993–2016 The Font Bureau, Inc., with Reserved Font Name *Zycon*. |

⚠ **The three Monotype faces carry a proprietary notice in their own `name` table**, saying that use
is covered by a licence agreement. They are here because they are part of text-rendering-tests, which
is distributed under the [Unicode licence](https://www.unicode.org/license.txt) — that licence grants
redistribution of the Data Files, and these fonts are Data Files of that suite. That is the same
basis on which the other nineteen are committed. It is recorded here rather than left implicit
because the string inside the font says something narrower than the suite that ships it.

The OFL's reserved-font-name clause is why these are shipped byte-for-byte and never subsetted,
renamed or regenerated — and it is also the right call on the merits, since subsetting a font
renumbers its glyphs and would invalidate every expectation written against it.

The suite itself is under the Unicode licence; the expectations derived from it live in
`Generated/ShapingConformance.data` and `Generated/VariationConformance.data`.
