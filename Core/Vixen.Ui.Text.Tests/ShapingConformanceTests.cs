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
    /// <summary>The suite's own tolerance, in font design units.</summary>
    const double MaximumDelta = 1.0;

    /// <summary>Cases that fail because HarfBuzz does not conform, listed with the reason.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>These are not Vixen's failures and they are not excused, they are pinned.</b>
    ///         The test below fails if a quarantined case starts passing, just as loudly as if a
    ///         healthy one starts failing — so a HarfBuzz upgrade that fixes Tai Tham shows up as a
    ///         red build asking for a line to be deleted, rather than as nothing at all.
    ///     </para>
    ///     <para>
    ///         Dropping these cases from the port instead would have been the easy thing and the
    ///         wrong one: the count would then be silent, and "413 cases pass" would mean whatever
    ///         the generator's filter happened to allow through.
    ///     </para>
    /// </remarks>
    static readonly (string Prefix, string Reason)[] Quarantine = [];

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
    static string? Check(FontFace font, string text, IReadOnlyList<(string Name, double X, double Y)> expected) {
        var shaped = TextShaper.Shape(font, text);
        var actual = new List<(string Name, double X, double Y)>();

        foreach (var placement in shaped.Placements()) {
            actual.Add((font.GlyphName(placement.GlyphId), placement.X, placement.Y));
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
        foreach (var (prefix, reason) in Quarantine) {
            if (id.StartsWith(prefix, StringComparison.Ordinal)) {
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
