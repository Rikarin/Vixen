# references/

Read-only third-party source, cloned here so that "how did *they* solve this" is a `grep` away
rather than a browser tab away. **Nothing here is built, restored, referenced or shipped** — the
directory is excluded from the solution, from every glob, and from CI.

## Why this is not a set of submodules

[Doc 02](../docs/plan/02-repository-layout.md) describes these as submodules. They are deliberately
not committed as such, because a submodule makes every clone of Vixen pull several gigabytes of other
people's history before it can build — including on a CI runner that will never read a line of it.
The trade only pays if you are actually consulting them, so cloning is left as a local decision.

The `.gitignore` already excludes everything here except this file, so a clone leaves no trace in
`git status`.

## Getting them

```bash
cd references

git clone --depth 1 https://github.com/stride3d/stride.git         stride
git clone --depth 1 https://github.com/genaray/Arch.git            arch
git clone --depth 1 https://github.com/facebook/yoga.git           yoga
git clone --depth 1 https://github.com/DioxusLabs/taffy.git        taffy
git clone --depth 1 https://github.com/ru-ace/Flexbox.git          flexbox
git clone --depth 1 https://github.com/fedeAlterio/SignalsDotnet.git signals-dotnet
git clone --depth 1 https://github.com/PurrNet/PurrNet.git         purrnet

git clone --depth 1 https://github.com/unicode-org/text-rendering-tests.git text-rendering-tests
```

The Unicode Character Database is not a repository, so it is fetched file by file. These are the ones
`Tools/Vixen.UnicodeTableGen` reads — the conformance suites, and the property tables the
implementation is built from:

```bash
mkdir -p references/unicode && cd references/unicode
base=https://www.unicode.org/Public/17.0.0/ucd
for f in auxiliary/GraphemeBreakTest.txt auxiliary/GraphemeBreakProperty.txt \
         auxiliary/WordBreakTest.txt auxiliary/WordBreakProperty.txt \
         auxiliary/LineBreakTest.txt LineBreak.txt \
         DerivedCoreProperties.txt extracted/DerivedBidiClass.txt BidiBrackets.txt BidiCharacterTest.txt \
         EastAsianWidth.txt Scripts.txt PropertyValueAliases.txt SpecialCasing.txt \
         emoji/emoji-data.txt ReadMe.txt; do curl -sSO "$base/$f"; done
```

⚠ **The version is pinned rather than `latest`, and the partial re-run below is why.** A full fetch of
`latest` gives ten files that agree with each other whatever release it is, so the version gate would
be happy; the `SpecialCasing`-only command underneath would not be, because it writes *one* table from
whatever is on disk. The day 18.0.0 ships, the blessed one-table command run against a `latest` fetch
lands an 18.0.0 header beside nine 17.0.0 siblings and turns `GeneratedUnicodeVersionTests` red — which
is the failure that gate was built for, arrived at by following the instructions. Moving to a new
release is an edit here and a full run, deliberately.

**`SpecialCasing.txt` is the one file whose table can be regenerated on its own**, because it is also
the one that arrived after the rest:

```bash
dotnet run --project Tools/Vixen.UnicodeTableGen -- \
    references/unicode Core/Vixen.Ui.Text/Generated /tmp/unused SpecialCasing
```

The generator takes the version from that file's own header rather than from GraphemeBreakTest.txt,
which is what made it visible that the committed table had come from 13.0.0 while its nine siblings
came from 17.0.0. ⚠ **All ten now say 17.0.0, and regenerating changed the header and nothing else** —
the unconditional rows of `SpecialCasing.txt` had not moved across four major versions. That is worth
knowing before anyone spends a run chasing a suspected casing difference: the table has been right the
whole time, and it was the header that lied.

⚠ **Two files the loop above deliberately does not fetch, and the one task that wants them.**
`UnicodeData.txt` (field 3 is the canonical combining class) and `PropList.txt` (`Soft_Dotted`) are
not in the list because no generated table reads them today — and that absence is
[#913](https://github.com/Rikarin/Vixen/issues/913), which blocks all four remaining UAX #21 casing
conditions at once. `After_I`, `Not_Before_Dot`, `lt More_Above` and `lt After_Soft_Dotted` are each
phrased as *"with no intervening character of combining class 0 or 230"*, so one table unblocks four
rows and no subset of them can be done first. .NET exposes no public canonical-combining-class API, so
there is no way to answer the question without the data. Whoever does it fetches these beside the
rest, at the same pinned release, and runs the generator whole:

```bash
for f in UnicodeData.txt PropList.txt; do curl -sSO "$base/$f"; done
```

## What each one is for

| Clone | Consulted for | ADR |
|---|---|---|
| `stride` | The architectural spine — asset pipeline, render features, effect system. Read, not copied. | — |
| `arch` | The archetype-chunk ECS model, and the benchmark suite Vixen's ECS has to match. | ADR-004 |
| `yoga` | **The conformance fixtures**, which get ported before the flexbox implementation is written. The most valuable thing in this list. | ADR-006 |
| `taffy` | **`tests/xml/` — 5 524 more conformance fixtures**, and the only oracle that exists for `display: block` and CSS Grid. Language-neutral XML rather than code, generated from HTML laid out by Chrome-for-Testing, so `Tools/Vixen.TaffyTestGen` vets and consolidates them rather than translating them. ⚠ **MIT only, not the ecosystem's usual dual MIT/Apache-2.0** — no patent grant, which is a reason to take the fixtures and not the algorithms. | doc 43 § B0 |
| `flexbox` | A C# flexbox implementation, as an algorithm reference. It is .NET Framework 4.6 and allocation-heavy; the *algorithm* is what is wanted. | ADR-006 |
| `signals-dotnet` | The reactive graph model behind the UI framework's signals. | ADR-007 |
| `purrnet` | Networking: replication, interest management, the RPC surface. MIT. | doc 16 |
| `unicode` | **The UAX#29, #14 and #9 conformance suites**, ported before the segmentation code is written — the same bet as `yoga` and the reason 4c's gate is phrased as "UAX conformance data green". Also the property tables the implementation needs. | doc 09 |
| `text-rendering-tests` | **The shaping and variable-font conformance suites.** Its expectations are written by hand from the OpenType specification, which is what makes it an oracle rather than a recording — Vixen delegates shaping to HarfBuzz, so a HarfBuzz-versus-HarfBuzz comparison would prove nothing, and its `GVAR`/`AVAR` cases carry drawn contours for a delta reader that is entirely Vixen's. Unlike everything else in this table, its fonts *are* redistributed: an expectation is only meaningful against the exact font it was written for. See `Core/Vixen.Ui.Text.Tests/Fonts/README.md`. | doc 09, doc 12 |

[Doc 14](../docs/plan/14-roadmap.md) singles these out for a reason: *external oracles judge
correctness without you having to*, which is the specific defence against AI-assisted code that reads
plausibly and is wrong. The Yoga conformance suite is the clearest case — a red test suite driving an
implementation is a completely different experience from writing three thousand lines and then
finding out.

Every repository above is under its own licence. Nothing here is redistributed except the shaping
test fonts noted above, which carry the SIL Open Font License and their own attribution; the
attribution that matters otherwise is for what Vixen actually *depends on*, which is tracked
separately per ADR-015.
