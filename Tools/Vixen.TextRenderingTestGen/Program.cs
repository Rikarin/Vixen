// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace Vixen.TextRenderingTestGen;

/// <summary>Turns the Consortium's text-rendering-tests into a shaping conformance suite.</summary>
/// <remarks>
///     <para>
///         Shaping is the one part of text where Vixen writes no algorithm — HarfBuzz does the work
///         — and that makes the obvious gate a worthless one. "Compare Vixen's glyphs against
///         <c>hb_shape</c>'s glyphs" is comparing HarfBuzz to itself, and it would stay green
///         through any itemisation bug that happened to hand the shaper the same wrong arguments
///         twice. This repository has already been bitten once by an oracle that shared an
///         implementation with its subject.
///     </para>
///     <para>
///         <b>text-rendering-tests is a real oracle.</b> Its expectations are written by hand from
///         the OpenType specification and the fonts' own tables, by people who were not running
///         HarfBuzz when they wrote them. And it is sensitive to exactly the part Vixen <i>does</i>
///         own: a shaper's output depends on the script, direction and language the buffer is
///         given, so a run itemiser that calls Kannada "Latin" produces wrong glyphs from a correct
///         shaper. Passing these cases is evidence about Vixen's code, not only about HarfBuzz's.
///     </para>
///     <para>
///         Each case gives a font, a string, and the glyphs a conforming engine draws — by name, at
///         a position. That is precisely a shaping golden test, and it is the gate docs/plan/12
///         asks for, sourced rather than invented.
///     </para>
/// </remarks>
static class Program {
    /// <summary>The test groups this port covers, and the reason it is these.</summary>
    /// <remarks>
    ///     <para>
    ///         <c>SH*</c> are the shaping-engine cases — Arabic in Nastaliq, Balinese, Kannada, Tai
    ///         Tham — and are the whole point. <c>GSUB</c>, <c>GPOS</c> and <c>KERN</c> are the
    ///         OpenType layout tables shaping is made of, and <c>CMAP</c> is the character-to-glyph
    ///         mapping every one of them starts from.
    ///     </para>
    ///     <para>
    ///         Deliberately left out, and said out loud rather than quietly dropped: <c>MORX</c>
    ///         (166 cases) is Apple's AAT layout, a font technology rather than a shaping question;
    ///         <c>CFF</c>, <c>GLYF</c> and <c>SFNT</c> test outline and container parsing, which is
    ///         a rasteriser's job and not this project's yet; and every case carrying an
    ///         <c>ft:var</c> attribute needs variable-font axis setting, which Vixen does not do yet
    ///         and which would be a claim rather than a test. The run prints how many it dropped for
    ///         each reason, so neither number can quietly grow.
    ///     </para>
    /// </remarks>
    static readonly string[] Groups = ["CMAP", "GPOS", "GSUB", "KERN", "SHARAN", "SHBALI", "SHKNDA", "SHLANA"];

    const string FontTest = "{https://github.com/OpenType/fonttest}";
    const string XLink = "{http://www.w3.org/1999/xlink}";

    static int Main(string[] args) {
        if (args.Length != 3) {
            Console.Error.WriteLine(
                "usage: Vixen.TextRenderingTestGen <text-rendering-tests-directory> <test-output-directory> <font-output-directory>"
            );

            return 1;
        }

        var source = args[0];
        var tests = args[1];
        var fontDirectory = args[2];

        var testcases = Path.Combine(source, "testcases");
        if (!Directory.Exists(testcases)) {
            Console.Error.WriteLine($"'{testcases}' does not exist — see references/README.md");
            return 1;
        }

        Directory.CreateDirectory(tests);
        Directory.CreateDirectory(fontDirectory);

        var cases = new List<Case>();
        var skippedVariable = 0;
        var skippedGroups = 0;

        foreach (var file in Directory.EnumerateFiles(testcases, "*.html").Order(StringComparer.Ordinal)) {
            var document = XDocument.Load(file);

            foreach (var element in document.Descendants()) {
                if ((string?)element.Attribute("class") != "expected") {
                    continue;
                }

                var id = (string?)element.Attribute(FontTest + "id");
                var font = (string?)element.Attribute(FontTest + "font");
                var render = (string?)element.Attribute(FontTest + "render");

                if (id is null || font is null || render is null) {
                    continue;
                }

                if (!Groups.Contains(id[..id.IndexOf('-', StringComparison.Ordinal)], StringComparer.Ordinal)) {
                    skippedGroups++;
                    continue;
                }

                // A variable-font case is a different feature, not a harder version of this one.
                if (element.Attribute(FontTest + "var") is not null) {
                    skippedVariable++;
                    continue;
                }

                cases.Add(new Case(id, font, render, ReadGlyphs(element, id)));
            }
        }

        cases.Sort(static (left, right) => CompareIds(left.Id, right.Id));

        var fonts = cases.Select(entry => entry.Font).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var bytes = 0L;

        foreach (var font in fonts) {
            var from = Path.Combine(source, "fonts", font);
            File.Copy(from, Path.Combine(fontDirectory, font), overwrite: true);
            bytes += new FileInfo(from).Length;
        }

        WriteData(Path.Combine(tests, "ShapingConformance.data"), cases);

        Console.WriteLine($"{cases.Count} cases, {fonts.Count} fonts ({bytes / 1024} KiB)");
        Console.WriteLine($"skipped: {skippedGroups} out-of-scope, {skippedVariable} variable-font");
        return 0;
    }

    /// <summary>One case: a font, a string, and the glyphs a conforming engine draws.</summary>
    sealed record Case(string Id, string Font, string Text, List<Glyph> Glyphs);

    /// <summary>A drawn glyph, by name, at a position in the suite's 1000-unit em.</summary>
    sealed record Glyph(string Name, double X, double Y);

    /// <summary>Reads the drawn glyphs out of a case's expected SVG.</summary>
    /// <remarks>
    ///     <para>
    ///         The SVG defines one <c>&lt;symbol&gt;</c> per distinct glyph and then one
    ///         <c>&lt;use&gt;</c> per drawn glyph, in the order the engine produced them — which for
    ///         a right-to-left run is visual order, so a mark comes out before the base it sits on.
    ///         The symbols carry outlines, which are a rasteriser's problem; the <c>&lt;use&gt;</c>
    ///         elements carry the shaping result, which is this one's.
    ///     </para>
    ///     <para>
    ///         ⚠ The name is the part of the href after the case id and a dot, and it has to be
    ///         taken by <i>length</i> rather than by looking for the last dot: real glyph names
    ///         contain dots. <c>#GSUB-1/1.a.alt</c> is the glyph <c>a.alt</c>, not <c>alt</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ The y here is <i>not</i> negated, although these are SVG coordinates and SVG's y
    ///         axis points down. The harness writes the shaper's own y-up offsets into the
    ///         attribute and its report stylesheet flips the whole drawing back with
    ///         <c>transform: scaleY(-1)</c>. Assuming the usual convention costs nothing on Latin,
    ///         where every offset is zero, and turns every Nastaliq mark upside down.
    ///     </para>
    ///     <para>
    ///         ⚠ The positions are in a <b>1000-unit em</b>, not in the font's own units. The
    ///         harness sets FreeType to a 1000-pixel size, so a 2048-unit font's expectations come
    ///         out scaled by 1000/2048. Nine of these fourteen fonts are 2048, and every case with
    ///         more than one glyph in it says so.
    ///     </para>
    /// </remarks>
    static List<Glyph> ReadGlyphs(XElement element, string id) {
        var glyphs = new List<Glyph>();
        var prefix = "#" + id + ".";

        foreach (var use in element.Descendants().Where(static node => node.Name.LocalName == "use")) {
            var href = (string?)use.Attribute(XLink + "href") ?? (string?)use.Attribute("href");
            if (href is null || !href.StartsWith(prefix, StringComparison.Ordinal)) {
                continue;
            }

            glyphs.Add(new Glyph(href[prefix.Length..], Coordinate(use, "x"), Coordinate(use, "y")));
        }

        return glyphs;
    }

    static double Coordinate(XElement element, string name) =>
        double.TryParse((string?)element.Attribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;

    /// <summary>Orders <c>SHKNDA-2/10</c> after <c>SHKNDA-2/9</c> rather than before it.</summary>
    static int CompareIds(string left, string right) {
        var leftParts = left.Split('/', '-');
        var rightParts = right.Split('/', '-');

        for (var i = 0; i < Math.Min(leftParts.Length, rightParts.Length); i++) {
            var leftNumeric = int.TryParse(leftParts[i], CultureInfo.InvariantCulture, out var leftNumber);
            var rightNumeric = int.TryParse(rightParts[i], CultureInfo.InvariantCulture, out var rightNumber);

            var comparison = leftNumeric && rightNumeric
                ? leftNumber.CompareTo(rightNumber)
                : string.CompareOrdinal(leftParts[i], rightParts[i]);

            if (comparison != 0) {
                return comparison;
            }
        }

        return leftParts.Length.CompareTo(rightParts.Length);
    }

    /// <summary>Writes the suite as data rather than as a method per case.</summary>
    /// <remarks>
    ///     The same decision the line-break suite made, for the same reason: a test needs a font
    ///     loaded once and reused, which a file of four hundred independent <c>[Fact]</c>s makes
    ///     awkward and slow. The text is written as code points so that the file is pure ASCII and a
    ///     diff of it is readable — an Arabic case written literally is unreviewable in a terminal.
    /// </remarks>
    static void WriteData(string path, List<Case> cases) {
        var builder = new StringBuilder();

        builder.Append("# Generated by Tools/Vixen.TextRenderingTestGen from unicode-org/text-rendering-tests.\n");
        builder.Append("# Do not edit — re-run the generator.\n");
        builder.Append("#\n");
        builder.Append("# The expectations are the Unicode Consortium's, written by hand from the OpenType\n");
        builder.Append("# specification rather than produced by any shaping engine. That is what makes them\n");
        builder.Append("# an oracle instead of a recording.\n");
        builder.Append("#\n");
        builder.Append("# id<TAB>font<TAB>text as code points<TAB>glyphName:x:y ...\n");
        builder.Append("# Positions are in a 1000-unit em, y up, pen origin at zero: the harness renders at a\n");
        builder.Append("# 1000-pixel size, so the expectations for a 2048-unit font are scaled by 1000/2048.\n");

        foreach (var entry in cases) {
            builder.Append(entry.Id).Append('\t').Append(entry.Font).Append('\t');

            var first = true;
            foreach (var rune in entry.Text.EnumerateRunes()) {
                if (!first) {
                    builder.Append(' ');
                }

                builder.Append(CultureInfo.InvariantCulture, $"{rune.Value:X4}");
                first = false;
            }

            builder.Append('\t');

            first = true;
            foreach (var glyph in entry.Glyphs) {
                if (!first) {
                    builder.Append(' ');
                }

                builder.Append(CultureInfo.InvariantCulture, $"{glyph.Name}:{glyph.X:0.###}:{glyph.Y:0.###}");
                first = false;
            }

            builder.Append('\n');
        }

        File.WriteAllText(path, builder.ToString());
        Console.WriteLine($"{Path.GetFileName(path)}: {cases.Count} cases");
    }
}
