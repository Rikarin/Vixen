# Fonts

These fourteen files are **not Vixen's**. They are the fonts the Unicode Consortium's
[text-rendering-tests](https://github.com/unicode-org/text-rendering-tests) suite is written
against, copied here verbatim by `Tools/Vixen.TextRenderingTestGen`.

They are committed rather than fetched for the same reason the Unicode property tables are: a
conformance gate that silently skips when its data is missing is not a gate, and CI has no reference
clone. 458 KiB is what it costs to have 413 shaping cases actually run.

A shaping expectation is only meaningful against the exact font it was written for — glyph names and
positions are the font's, not the script's — so these cannot be substituted for whatever the machine
happens to have installed.

| | |
|---|---|
| `NotoSans/SerifKannada-Regular.ttf`, `NotoSansBalinese-Regular.ttf` | Copyright Google Inc., [SIL Open Font License 1.1](https://scripts.sil.org/OFL). Noto is a trademark of Google Inc. |
| `TestShapeAran.ttf`, `TestShapeEthi.ttf`, `TestCMAP*.ttf/otf`, `TestGPOS*.ttf/otf`, `TestGSUBOne.otf`, `TestKERNOne.otf` | Copyright © Unicode, Inc., SIL Open Font License 1.1. |
| `TestShapeLana.ttf` | Copyright © 2019 Unicode, SIL Open Font License 1.1, with Reserved Font Names *Da Lekh* and *ᨯᩣᩃᩮ᩠ᨡ*. |

The OFL's reserved-font-name clause is why these are shipped byte-for-byte and never subsetted,
renamed or regenerated — and it is also the right call on the merits, since subsetting a font
renumbers its glyphs and would invalidate every expectation written against it.

The suite itself is under the [Unicode licence](https://www.unicode.org/license.txt); the
expectations derived from it live in `Generated/ShapingConformance.data`.
