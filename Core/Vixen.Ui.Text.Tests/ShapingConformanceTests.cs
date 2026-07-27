// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Reflection;
using System.Text;
using Xunit;

namespace Vixen.Ui.Text.Tests;

/// <summary>The Unicode Consortium's text-rendering-tests, run against Vixen's shaping.</summary>
/// <remarks>
///     <para>
///         Vixen writes no shaping algorithm — HarfBuzz does that — which makes the obvious gate a
///         worthless one. Comparing Vixen's glyphs against <c>hb_shape</c>'s glyphs compares
///         HarfBuzz to itself, and stays green through any itemisation bug that hands the shaper the
///         same wrong arguments twice.
///     </para>
///     <para>
///         These cases are a real oracle: their expectations were written by hand from the OpenType
///         specification, and they are sensitive to precisely the part Vixen owns. A shaper's output
///         depends on the script, direction and language its buffer carries, so an itemiser that
///         calls Kannada "Latin", or that splits a run at a space, produces wrong glyphs from a
///         correct shaper. <c>GSUB-1</c> is called <i>Space Isn't Nothing</i> and exists for exactly
///         that second mistake.
///     </para>
/// </remarks>
public class ShapingConformanceTests {
    /// <summary>The suite's own tolerance, in its own units.</summary>
    const double MaximumDelta = 1.0;

    /// <summary>The em the expectations are written in.</summary>
    /// <remarks>
    ///     ⚠ Not the font's. The harness renders at a 1000-pixel size whatever the font's
    ///     units-per-em is, so nine of these fourteen fonts have their expectations scaled by
    ///     1000/2048 and the other five do not. Comparing design units against them directly makes
    ///     every case with two or more glyphs in it fail by a factor of 2.048 — and every
    ///     single-glyph case pass, because a lone glyph sits at the origin in any scale, which is
    ///     exactly the pattern that makes this look like a shaping bug rather than a units one.
    /// </remarks>
    const double SuiteUnitsPerEm = 1000.0;

    /// <summary>The 85 cases HarfBuzz itself does not conform on, pinned one by one.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>These are not Vixen's failures, and they are pinned rather than excused.</b> The
    ///         test fails if a quarantined case starts <i>passing</i> just as loudly as if a healthy
    ///         one starts failing, so a HarfBuzz upgrade that fixes Tai Tham arrives as a red build
    ///         asking for lines to be deleted rather than as nothing at all.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Listed case by case, not by group.</b> Quarantining <c>SHLANA-*</c> wholesale
    ///         would be four lines instead of eighty — and would hide the 131 Tai Tham cases that
    ///         now pass. The Consortium's own report for HarfBuzz, run in 2023, fails all 209 of
    ///         them; at 14.2.1.1 only 78 still fail. A group-level rule would have silently thrown
    ///         that away, and would go on hiding it as the number falls further.
    ///     </para>
    ///     <para>
    ///         Dropping them from the port instead would have been easier and worse: the count would
    ///         be silent, and "413 cases pass" would mean whatever the generator's filter allowed
    ///         through.
    ///     </para>
    /// </remarks>
    static readonly (string Reason, string[] Cases)[] Quarantine = [
        // HarfBuzz shapes Tai Tham through the Universal Shaping Engine, which inserts a dotted
        // circle where these expectations have none and reorders the vowel signs differently.
        ("HarfBuzz's Tai Tham shaping disagrees with the expectation", [
            "SHLANA-1/24", "SHLANA-1/25", "SHLANA-1/26", "SHLANA-1/27", "SHLANA-1/34", "SHLANA-1/35",
            "SHLANA-1/43", "SHLANA-1/44", "SHLANA-2/2", "SHLANA-2/3", "SHLANA-2/4", "SHLANA-2/7",
            "SHLANA-2/12", "SHLANA-2/14", "SHLANA-2/16", "SHLANA-2/30", "SHLANA-2/33", "SHLANA-2/34",
            "SHLANA-2/35", "SHLANA-2/36", "SHLANA-3/1", "SHLANA-3/2", "SHLANA-3/3", "SHLANA-4/1",
            "SHLANA-5/5", "SHLANA-5/8", "SHLANA-5/11", "SHLANA-5/13", "SHLANA-6/2", "SHLANA-7/1",
            "SHLANA-7/3", "SHLANA-7/4", "SHLANA-7/5", "SHLANA-7/6", "SHLANA-7/7", "SHLANA-7/9",
            "SHLANA-7/11", "SHLANA-7/12", "SHLANA-7/13", "SHLANA-7/14", "SHLANA-7/15", "SHLANA-7/17",
            "SHLANA-7/18", "SHLANA-8/1", "SHLANA-8/2", "SHLANA-8/4", "SHLANA-8/5", "SHLANA-8/6",
            "SHLANA-9/4", "SHLANA-9/6", "SHLANA-10/3", "SHLANA-10/4", "SHLANA-10/5", "SHLANA-10/6",
            "SHLANA-10/7", "SHLANA-10/8", "SHLANA-10/10", "SHLANA-10/11", "SHLANA-10/12", "SHLANA-10/13",
            "SHLANA-10/16", "SHLANA-10/21", "SHLANA-10/23", "SHLANA-10/24", "SHLANA-10/25", "SHLANA-10/27",
            "SHLANA-10/28", "SHLANA-10/29", "SHLANA-10/30", "SHLANA-10/36", "SHLANA-10/38", "SHLANA-10/39",
            "SHLANA-10/40", "SHLANA-10/42", "SHLANA-10/43", "SHLANA-10/45", "SHLANA-10/46", "SHLANA-10/47"
        ]),

        // The expectation comes from FreeType, which will select a Macintosh cmap subtable.
        // HarfBuzz reads Unicode subtables only, so these code points reach .notdef.
        ("HarfBuzz does not consult the Macintosh Turkish cmap", [
            "CMAP-3/5", "CMAP-3/7", "CMAP-3/9", "CMAP-3/15", "CMAP-3/16", "CMAP-3/19"
        ]),

        // The one case the Consortium's own 2023 HarfBuzz report also fails, and for the same
        // reason: a trailing zero-width non-joiner that the expectation keeps a glyph for.
        ("HarfBuzz drops a trailing ZWNJ the expectation keeps", ["SHKNDA-3/31"])
    ];

    [Fact]
    public void The_shaping_conformance_suite_is_green() {
        var fonts = new Dictionary<string, FontFace>(StringComparer.Ordinal);
        var unexpectedFailures = new List<string>();
        var unexpectedPasses = new List<string>();
        var total = 0;

        try {
            foreach (var (id, font, text, expected) in Cases()) {
                total++;

                if (!fonts.TryGetValue(font, out var face)) {
                    face = FontFace.Load(FontBytes(font));
                    fonts[font] = face;
                }

                var failure = Check(face, text, expected);
                var quarantined = QuarantineReason(id);

                if (failure is null) {
                    if (quarantined is not null) {
                        unexpectedPasses.Add($"  {id} passes — remove it from the quarantine ({quarantined})");
                    }

                    continue;
                }

                if (quarantined is null && unexpectedFailures.Count < 8) {
                    unexpectedFailures.Add($"  {id} [{font}] {failure}");
                }
            }
        } finally {
            foreach (var face in fonts.Values) {
                face.Dispose();
            }
        }

        Assert.True(total > 400, $"only {total} cases were read — the conformance data is not being embedded");

        Assert.True(
            unexpectedFailures.Count == 0 && unexpectedPasses.Count == 0,
            $"{unexpectedFailures.Count}+ unexpected failures, {unexpectedPasses.Count} unexpected passes, of {total}:\n"
            + string.Join("\n", unexpectedFailures.Concat(unexpectedPasses))
        );
    }

    /// <summary>Shapes one case and says how it differs, or <c>null</c> if it does not.</summary>
    /// <remarks>
    ///     The whole string goes in, not a run: itemisation is the part under test, so telling the
    ///     shaper which script and direction to use would remove the only thing this suite can see
    ///     that a HarfBuzz-versus-HarfBuzz comparison cannot.
    /// </remarks>
    static string? Check(FontFace font, string text, List<(string Name, double X, double Y)> expected) {
        var shaped = TextShaper.Shape(font, text);
        var actual = new List<(string Name, double X, double Y)>();
        var scale = SuiteUnitsPerEm / font.UnitsPerEm;

        foreach (var placement in shaped.Placements()) {
            actual.Add((font.GlyphName(placement.GlyphId), placement.X * scale, placement.Y * scale));
        }

        if (actual.Count != expected.Count) {
            return $"expected {expected.Count} glyphs, got {actual.Count}: [{Describe(actual)}] want [{Describe(expected)}]";
        }

        for (var i = 0; i < actual.Count; i++) {
            if (actual[i].Name != expected[i].Name
                || Math.Abs(actual[i].X - expected[i].X) > MaximumDelta
                || Math.Abs(actual[i].Y - expected[i].Y) > MaximumDelta) {
                return $"glyph {i}: got {Describe([actual[i]])} want {Describe([expected[i]])}";
            }
        }

        return null;
    }

    static string Describe(IReadOnlyList<(string Name, double X, double Y)> glyphs) =>
        string.Join(
            " ",
            glyphs.Select(glyph => string.Create(CultureInfo.InvariantCulture, $"{glyph.Name}@{glyph.X:0.#},{glyph.Y:0.#}"))
        );

    static string? QuarantineReason(string id) {
        foreach (var (reason, cases) in Quarantine) {
            if (Array.IndexOf(cases, id) >= 0) {
                return reason;
            }
        }

        return null;
    }

    static byte[] FontBytes(string name) {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream($"Vixen.Ui.Text.Tests.Fonts.{name}")
            ?? throw new InvalidOperationException($"the font '{name}' is not embedded");

        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    static IEnumerable<(string Id, string Font, string Text, List<(string Name, double X, double Y)> Expected)> Cases() {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Vixen.Ui.Text.Tests.Generated.ShapingConformance.data")
            ?? throw new InvalidOperationException("the conformance data is not embedded");

        using var reader = new StreamReader(stream);

        while (reader.ReadLine() is { } line) {
            if (line.Length == 0 || line[0] == '#') {
                continue;
            }

            var fields = line.Split('\t');
            if (fields.Length < 4) {
                continue;
            }

            var text = new StringBuilder();
            foreach (var codePoint in fields[2].Split(' ', StringSplitOptions.RemoveEmptyEntries)) {
                text.Append(char.ConvertFromUtf32(int.Parse(codePoint, NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
            }

            var expected = new List<(string, double, double)>();
            foreach (var glyph in fields[3].Split(' ', StringSplitOptions.RemoveEmptyEntries)) {
                // ⚠ Split from the right: a glyph name contains dots and may contain colons, and
                // only the last two fields are numbers.
                var y = glyph.LastIndexOf(':');
                var x = glyph.LastIndexOf(':', y - 1);

                expected.Add((
                    glyph[..x],
                    double.Parse(glyph[(x + 1)..y], CultureInfo.InvariantCulture),
                    double.Parse(glyph[(y + 1)..], CultureInfo.InvariantCulture)
                ));
            }

            yield return (fields[0], fields[1], text.ToString(), expected);
        }
    }
}
