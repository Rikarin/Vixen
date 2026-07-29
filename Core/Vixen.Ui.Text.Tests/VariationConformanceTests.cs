// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Reflection;
using System.Text;
using Vixen.Ui.Text.Outlines;
using Xunit;

namespace Vixen.Ui.Text.Tests;

/// <summary>The Consortium's variable-font cases, run against Vixen's <c>gvar</c> reader.</summary>
/// <remarks>
///     <para>
///         <b>This one is not about HarfBuzz.</b> The shaping suite next door has to argue for itself
///         — it compares a shaper Vixen does not own — but nothing shapes a <c>gvar</c> delta.
///         HarfBuzzSharp exposes no outline API at all, so every contour here is read, varied and
///         interpolated by code in this repository, and these expectations were written by hand from
///         the OpenType specification and the fonts' own tables. That makes them an oracle in the
///         strongest sense available: an independent implementation's answer to the same question.
///     </para>
///     <para>
///         The cases are chosen to break specific mistakes rather than to cover an API. <c>GVAR-1</c>
///         is called <i>Sharing All Points</i> and exists because a point set of length zero means
///         every point; <c>GVAR-2</c> and <c>GVAR-3</c> vary that with private and shared sets;
///         <c>GVAR-7</c> composites; <c>GVAR-8</c> and <c>GVAR-9</c> are entirely about which
///         untouched points get inferred deltas; <c>AVAR-1</c> warps the axis before any of it
///         happens. A reader that gets the packing right and the inference wrong passes some and
///         fails others.
///     </para>
///     <para>
///         ⚠ <b>It found two real bugs on its first run, and neither was visible from the code.</b>
///         Thirty-two cases drew the same glyph nine times because a four-byte axis tag is padded and
///         nothing here was: Zycon's axes are <c>M1&#160;&#160;</c> and every caller writes <c>M1</c>.
///         Twelve more got the interpolation of untouched points wrong in the one place the
///         specification is not the obvious thing — two references at the same coordinate pulling
///         different ways infer <i>nothing</i>, and <c>GVAR-9</c> makes them differ by one unit in a
///         hundred so that taking either of them looks almost right. Both are pinned by their own
///         tests now, in <see cref="FontVariationTests" /> and in the reader's own remarks.
///     </para>
/// </remarks>
public class VariationConformanceTests {
    /// <summary>The suite's tolerance, in its own units.</summary>
    /// <remarks>
    ///     ⚠ <b>One unit of it is the harness's, not Vixen's.</b> The expectations are FreeType's
    ///     26.6 coordinates divided by 64 with C's truncating division, so a 2048-unit font's
    ///     expectation is already up to a whole unit below the real value before anything here runs.
    ///     The remaining half unit covers this reader working in <c>float</c> where FreeType works in
    ///     16.16 fixed point, which can disagree by a rounding step on a delta that lands on a half.
    ///     It is not slack for a wrong delta: the deltas these fonts carry are tens of units, and
    ///     <see cref="Reading_the_font_without_its_deltas_fails_the_suite" /> is what says so.
    /// </remarks>
    const double MaximumDelta = 1.5;

    /// <summary>The em the expectations are written in, whatever the font's own is.</summary>
    const double SuiteUnitsPerEm = 1000.0;

    [Fact]
    public void The_variable_font_conformance_suite_is_green() {
        var failures = Run(varied: true, out var total, out var glyphs);

        Assert.True(total > 90, $"only {total} cases were read — the conformance data is not being embedded");
        Assert.True(glyphs >= total, $"{glyphs} outlines for {total} cases — the expectations are not being read");

        Assert.True(
            failures.Count == 0,
            $"{failures.Count} of {total} cases differ:\n{string.Join("\n", failures.Take(8))}"
        );
    }

    /// <summary>The sabotage, kept as a test: the same suite read at the font's default instance.</summary>
    /// <remarks>
    ///     ⚠ <b>Without this the tolerance above is an unbacked claim.</b> A suite that compares
    ///     outlines within a unit and a half would stay green against a reader that ignored
    ///     <c>gvar</c> entirely if the fonts' deltas happened to be small — so this asks for the same
    ///     hundred cases with no instance set and requires that most of them fail. 82 of them do. The
    ///     18 that do not are the cases whose axis value <i>is</i> the default, and the suite walks
    ///     each axis through its range, so there are a handful by construction.
    /// </remarks>
    [Fact]
    public void Reading_the_font_without_its_deltas_fails_the_suite() {
        var failures = Run(varied: false, out var total, out _);

        Assert.True(
            failures.Count > total / 2,
            $"only {failures.Count} of {total} cases noticed that no deltas were applied — the tolerance is too loose"
        );
    }

    /// <summary>Runs the suite, either at each case's instance or at the font as it is stored.</summary>
    static List<string> Run(bool varied, out int total, out int glyphs) {
        var fonts = new Dictionary<string, FontFace>(StringComparer.Ordinal);
        var failures = new List<string>();

        total = 0;
        glyphs = 0;

        try {
            foreach (var (id, font, axes, text, expected) in Cases()) {
                total++;
                glyphs += expected.Count;

                if (!fonts.TryGetValue(font, out var face)) {
                    face = FontFace.Load(FontBytes(font), name: font);
                    fonts[font] = face;
                }

                if (Check(face, varied ? face.Variation(axes) : null, text, expected) is { } failure) {
                    failures.Add($"  {id} [{font} {Describe(axes)}] {failure}");
                }
            }
        } finally {
            foreach (var face in fonts.Values) {
                face.Dispose();
            }
        }

        return failures;
    }

    /// <summary>Draws one case and says how it differs, or null if it does not.</summary>
    /// <remarks>
    ///     The glyphs come from shaping the case's own string rather than from the symbol names in
    ///     the expectation, so that a case whose text maps to the wrong glyph fails here rather than
    ///     comparing the right outline to itself.
    /// </remarks>
    static string? Check(FontFace font, FontVariation? variation, string text, List<string> expected) {
        var shaped = TextShaper.Shape(font, text);
        var placements = shaped.Placements().ToList();

        if (placements.Count != expected.Count) {
            return $"expected {expected.Count} glyphs, got {placements.Count}";
        }

        var scale = SuiteUnitsPerEm / font.UnitsPerEm;

        for (var i = 0; i < placements.Count; i++) {
            var drawn = Commands(font.GetOutline(placements[i].GlyphId, variation), scale);
            var wanted = Parse(expected[i]);

            if (drawn.Count != wanted.Count) {
                return $"glyph {i} ({font.GlyphName(placements[i].GlyphId)}): "
                    + $"{drawn.Count} path commands, expected {wanted.Count}";
            }

            for (var command = 0; command < drawn.Count; command++) {
                if (drawn[command].Verb != wanted[command].Verb) {
                    return $"glyph {i} command {command}: {drawn[command].Verb}, expected {wanted[command].Verb}";
                }

                for (var point = 0; point < wanted[command].Points.Length; point++) {
                    var difference = Math.Abs(drawn[command].Points[point] - wanted[command].Points[point]);

                    if (difference > MaximumDelta) {
                        return string.Create(
                            CultureInfo.InvariantCulture,
                            $"glyph {i} command {command} ({wanted[command].Verb}) coordinate {point}: "
                            + $"{drawn[command].Points[point]:0.##}, expected {wanted[command].Points[point]:0.##}"
                        );
                    }
                }
            }
        }

        return null;
    }

    /// <summary>One path command: the verb and its coordinates, x then y, in the suite's em.</summary>
    readonly record struct Command(char Verb, double[] Points);

    /// <summary>Turns an outline into the commands the harness would have written for it.</summary>
    /// <remarks>
    ///     ⚠ <b>A closing line is dropped, because the harness writes one as <c>Z</c>.</b> FreeType
    ///     ends every contour with an explicit line back to its first point, and the converter that
    ///     produced these expectations turns a line landing on the start into a close and emits
    ///     nothing else for it. Keeping ours would make every single contour differ by one command,
    ///     which reads as a variation bug and is a serialisation convention.
    /// </remarks>
    static List<Command> Commands(GlyphOutline outline, double scale) {
        var commands = new List<Command>();
        double startX = 0, startY = 0;
        Command? deferred = null;

        void Flush() {
            if (deferred is { } line) {
                commands.Add(line);
                deferred = null;
            }
        }

        foreach (var segment in outline.Segments) {
            switch (segment.Verb) {
                case OutlineVerb.Move:
                    Flush();
                    startX = segment.X0 * scale;
                    startY = segment.Y0 * scale;
                    commands.Add(new Command('M', [startX, startY]));
                    break;

                case OutlineVerb.Line:
                    Flush();
                    deferred = new Command('L', [segment.X0 * scale, segment.Y0 * scale]);
                    break;

                case OutlineVerb.Quadratic:
                    Flush();
                    commands.Add(new Command('Q', [
                        segment.X0 * scale, segment.Y0 * scale, segment.X1 * scale, segment.Y1 * scale
                    ]));

                    break;

                case OutlineVerb.Cubic:
                    Flush();
                    commands.Add(new Command('C', [
                        segment.X0 * scale, segment.Y0 * scale, segment.X1 * scale, segment.Y1 * scale,
                        segment.X2 * scale, segment.Y2 * scale
                    ]));

                    break;

                case OutlineVerb.Close:
                    if (deferred is { } closing && !Lands(closing, startX, startY)) {
                        commands.Add(closing);
                    }

                    deferred = null;
                    commands.Add(new Command('Z', []));
                    break;

                default:
                    break;
            }
        }

        Flush();
        return commands;
    }

    /// <summary>Whether a line ends where its contour began, as the harness's converter asks.</summary>
    /// <remarks>
    ///     Compared after scaling and at the tolerance, not exactly: the harness compares 26.6
    ///     integers, and a line that lands on the start there can be a fraction of a unit away here.
    /// </remarks>
    static bool Lands(Command line, double x, double y) =>
        Math.Abs(line.Points[0] - x) <= MaximumDelta && Math.Abs(line.Points[1] - y) <= MaximumDelta;

    /// <summary>Reads the harness's path syntax: <c>M</c>, <c>L</c>, <c>Q</c>, <c>C</c> and <c>Z</c>.</summary>
    static List<Command> Parse(string data) {
        var commands = new List<Command>();
        var tokens = data.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i < tokens.Length;) {
            var token = tokens[i++];
            var verb = token[0];

            if (verb == 'Z') {
                commands.Add(new Command('Z', []));
                continue;
            }

            var pairs = verb switch { 'Q' => 2, 'C' => 3, _ => 1 };
            var points = new double[pairs * 2];
            (points[0], points[1]) = Pair(token[1..]);

            // ⚠ The later pairs are separate tokens with no letter on them, which is why this cannot
            // be a per-token parse: `Q914,539 914,-27` is one command, not two.
            for (var pair = 1; pair < pairs && i < tokens.Length; pair++) {
                (points[pair * 2], points[(pair * 2) + 1]) = Pair(tokens[i++]);
            }

            commands.Add(new Command(verb, points));
        }

        return commands;
    }

    static (double X, double Y) Pair(string token) {
        var comma = token.IndexOf(',', StringComparison.Ordinal);

        return comma < 0
            ? (0, 0)
            : (double.Parse(token[..comma], CultureInfo.InvariantCulture),
                double.Parse(token[(comma + 1)..], CultureInfo.InvariantCulture));
    }

    static string Describe(Dictionary<string, float> axes) =>
        string.Join(
            ";",
            axes.Select(axis => string.Create(CultureInfo.InvariantCulture, $"{axis.Key}:{axis.Value:0.###}"))
        );

    static byte[] FontBytes(string name) {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream($"Vixen.Ui.Text.Tests.Fonts.{name}")
            ?? throw new InvalidOperationException($"the font '{name}' is not embedded");

        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    static IEnumerable<(string Id, string Font, Dictionary<string, float> Axes, string Text, List<string> Expected)>
        Cases() {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Vixen.Ui.Text.Tests.Generated.VariationConformance.data")
            ?? throw new InvalidOperationException("the variation conformance data is not embedded");

        using var reader = new StreamReader(stream);

        while (reader.ReadLine() is { } line) {
            if (line.Length == 0 || line[0] == '#') {
                continue;
            }

            var fields = line.Split('\t');
            if (fields.Length < 5) {
                continue;
            }

            var axes = new Dictionary<string, float>(StringComparer.Ordinal);
            foreach (var setting in fields[2].Split(';', StringSplitOptions.RemoveEmptyEntries)) {
                var colon = setting.IndexOf(':', StringComparison.Ordinal);
                if (colon > 0) {
                    axes[setting[..colon]] = float.Parse(setting[(colon + 1)..], CultureInfo.InvariantCulture);
                }
            }

            var text = new StringBuilder();
            foreach (var codePoint in fields[3].Split(' ', StringSplitOptions.RemoveEmptyEntries)) {
                text.Append(char.ConvertFromUtf32(int.Parse(codePoint, NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
            }

            yield return (
                fields[0],
                fields[1],
                axes,
                text.ToString(),
                [..fields[4].Split('|', StringSplitOptions.RemoveEmptyEntries)]
            );
        }
    }
}
