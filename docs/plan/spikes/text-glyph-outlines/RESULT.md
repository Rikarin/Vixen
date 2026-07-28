# Spike: a managed glyph-outline parser — ✅ **PASSED**

Run on macOS arm64, .NET SDK 10.0, HarfBuzzSharp **14.2.1.1** — the version
[doc 01](../../01-technology-decisions.md) pins.

[Doc 09](../../09-ui-framework.md) asks for an MSDF atlas, and 4c stopped short of one for a reason
worth restating: **HarfBuzzSharp exposes no glyph outlines.** The assembly has
`TryGetGlyphExtents`, which is a bounding box, and no draw, paint or outline surface at all.
Distance fields need contours, so something else has to produce them. The recorded direction was a
managed `glyf`/`CFF` parser fed by `Face.ReferenceTable` — cheaper than FreeType (a second native
dependency whose WebAssembly story would have to be re-run) and than SkiaSharp (heavy, and it
duplicates HarfBuzz). Sequencing rule 3 says find out before planning around it.

`Probe.cs` beside this file is what was run.

## What was proven

**`Face.ReferenceTable` hands back the raw bytes**, and `Face.Tables` enumerates what a font has.
That is the whole API the approach needs and it is already in the dependency.

**Both outline formats parse.** TrueType `glyf`/`loca` including composite glyphs with the full
2×2 transform, and CFF Type 2 charstrings including subroutines, `hintmask`, the four flex
operators, and CID fonts with per-FD private dictionaries and subroutines.

**242 fonts, 259,298 glyphs with outlines, and every font read without an exception.**

| | glyphs | agreement with HarfBuzz |
|---|---|---|
| `glyf` | 241,364 | **99.999 %** |
| `CFF` | 17,934 | **99.777 %** |

43 glyphs disagree, and both groups are accounted for below.

**The oracle is the point.** HarfBuzz computes those extents from the same font by a completely
separate implementation, so agreeing with it over a quarter of a million glyphs says the contours
are the font's own — which is the one question a spike about a binary format has to answer, and
which no hand-written expectation can answer at this scale.

## Three findings, and none of them is about whether it works

⚠ **HarfBuzz reports *positioned* extents, and the outline is not positioned.** For `glyf` it
shifts the glyph so `xMin` lands on the left side bearing from `hmtx`. Where a font's stored `xMin`
disagrees with its own `lsb` — which is common, and universal in italics — the extents come back
translated. Before the correction the agreement read 95.3 %; after it, 99.999 %. **The same shift
will be needed when an atlas places a glyph**, so this is a fact about the pipeline and not only
about the test: it is the difference between a glyph drawn where the shaper said and a glyph drawn
`lsb − xMin` units to the left.

⚠ **For `glyf`, HarfBuzz returns the box the font stores rather than one it computes.** So the
comparison checks *point decoding*, not curve evaluation — and where a font's stored box is simply
wrong, disagreeing is correct. All three remaining `glyf` misses are that: glyph 274 of Arial, Arial
Bold and Times New Roman, whose stored box claims `xMax` 676 where the two components it is built
from reach 417. Checked by hand — component 79 spans [131, 311] and component 257 spans [238, 443]
shifted by −26, giving [131, 417] exactly.

⚠ **Comparing curve bounds instead measures the font, not the parser.** Sampling the curves gives
95.0 % on `glyf` against 99.999 % on control points, because a TrueType font's stored box is the
box of the *points*. On CFF, where HarfBuzz runs the charstring itself, the two agree — 99.43 %
curve against 99.78 % points. That asymmetry is how the previous finding was established rather
than assumed.

## Two bugs, and what each one looked like

**A compound assignment that reads its target first.** `r.Position += r.U16()` skips the
instructions from where the *length* started, two bytes short, on every glyph in every font. It
reads perfectly on the page. Agreement was **8.6 %** before and **95.3 %** after — one line.

**A width test that was inverted for stem operators.** A Type 2 stem operator takes pairs, so an
*odd* argument count is the one carrying a width; the first version tested the parity the other way.
That miscounts the stems, so `hintmask` skips the wrong number of bytes, so the rest of the
charstring is read as garbage — a wrong shape rather than an error, and only in fonts hinted heavily
enough to have a `hintmask`. It cost STIX's math fonts several hundred units per glyph and nothing
else. The residual after fixing it is 40 glyphs in `STIXTwoMath.otf`, worst case **7 units**, cause
not chased further.

## What was not built, and is not owed yet

**Point-matched composites.** A component may position itself by matching a point index instead of
carrying an offset. Not implemented — and **not one glyph in 242 fonts used it**, which is why that
is a note rather than a gap.

**`seac`.** The `endchar` accent composition: no font in the corpus used it either.

**Variable fonts.** `gvar` deltas are not applied, so a variable font parses at its default instance.
That is correct for an atlas keyed on a static instance and wrong the moment an axis moves;
[doc 09](../../09-ui-framework.md)'s variable-font axes are owed with it.

**Vertical metrics**, `VORG`, and CFF2. None of them is needed to rasterise a glyph.

## The conclusion

The managed route stands, and the estimate it was chosen on holds: **~600 lines for both formats**,
no new native dependency, and the WebAssembly path exactly as the HarfBuzz spike left it. The MSDF
atlas can be planned against a parser that is known to read what the font actually says, with the
positioning rule above written down before anything depends on getting it wrong.

Licensed under Apache-2.0.
