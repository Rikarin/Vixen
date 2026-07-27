# Vixen.Ui.Text

Underestimating text is the classic UI-framework mistake, and the defence against making it is the
same one `Vixen.Ui.Layout` used: **be judged by somebody else's conformance suite.**

## State

**The UAX#29 conformance suite is in the repository and the segmentation is not.** That order is
deliberate — 2 710 red tests driving an implementation is a completely different experience from
writing the implementation and then finding out.

| | |
|---|---|
| `Tools/Vixen.UnicodeTableGen` | UCD → committed property tables, and the conformance suites as xunit tests. |
| `GraphemeBreakTable` / `WordBreakTable` | 1 542 and 1 257 merged ranges, binary-searched. Unicode 17.0.0. |
| UAX#29 grapheme and word segmentation | ⏳ next commit |
| UAX#14 line breaking, UAX#9 bidi | ⏳ |
| HarfBuzz shaping, MSDF atlas, font fallback | ⏳ |
| `TextEditor` model with IME | ⏳ |

## Why the tables are generated and committed

CI has no copy of the Unicode Character Database, and fetching one at build time would make a build
depend on a website. So the generator runs by hand when the UCD version moves, and its output is
committed with the version stamped in a header — a mismatch shows up in a diff rather than at runtime.

The ranges are sorted, merged and stored as one flat array rather than a table per property, because
segmentation asks for the class of every code point of every string it measures. The layout matters
more than the range count does.

Licensed under Apache-2.0. The generated tables are derived from Unicode data files, which carry the
[Unicode terms of use](https://www.unicode.org/terms_of_use.html).
