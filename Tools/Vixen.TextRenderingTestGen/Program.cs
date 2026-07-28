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
    ///         <c>ft:var</c> attribute is a variable-font case, which goes to the second suite below
    ///         when its outlines can be checked and is dropped when they cannot. The run prints how
    ///         many it dropped for each reason, so neither number can quietly grow.
    ///     </para>
    /// </remarks>
    static readonly string[] Groups = ["CMAP", "GPOS", "GSUB", "KERN", "SHARAN", "SHBALI", "SHKNDA", "SHLANA"];

    /// <summary>The variable-font groups, which are an outline suite rather than a shaping one.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>These cases carry the drawn contours, not just glyph positions.</b> Every one of them
    ///         renders a single string at one point along a font's axes and writes out the path an
    ///         engine that applied <c>gvar</c> correctly produces — which makes them a real oracle for
    ///         delta application, written from the tables rather than recorded from an implementation.
    ///         The shaping suite reads the <c>&lt;use&gt;</c> elements and throws the
    ///         <c>&lt;symbol&gt;</c>s away; this one is the other half.
    ///     </para>
    ///     <para>
    ///         Left out, and for reasons rather than by omission: <c>CVAR</c> varies the hinting
    ///         control values, so its expectations differ from the unhinted outline and cannot be
    ///         checked without an interpreter; <c>CFF2</c> varies charstrings, which is a different
    ///         table from <c>glyf</c>; <c>HVAR</c> varies advances rather than contours, and this
    ///         suite compares no advances; and <c>GPOS-5</c> is a shaping case that happens to set an
    ///         axis, whose expectation is positions.
    ///     </para>
    /// </remarks>
    static readonly string[] VariationGroups = ["AVAR", "GVAR"];

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
        var variable = new List<VariationCase>();
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

                var group = id[..id.IndexOf('-', StringComparison.Ordinal)];

                // A variable-font case is a different suite, not a harder version of this one: its
                // expectation is a contour rather than a position.
                if ((string?)element.Attribute(FontTest + "var") is { } axes) {
                    if (VariationGroups.Contains(group, StringComparer.Ordinal)) {
                        variable.Add(new VariationCase(id, font, axes, render, ReadOutlines(element)));
                    } else {
                        skippedVariable++;
                    }

                    continue;
                }

                if (!Groups.Contains(group, StringComparer.Ordinal)) {
                    skippedGroups++;
                    continue;
                }

                cases.Add(new Case(id, font, render, ReadGlyphs(element, id)));
            }
        }

        cases.Sort(static (left, right) => CompareIds(left.Id, right.Id));
        variable.Sort(static (left, right) => CompareIds(left.Id, right.Id));

        var fonts = cases.Select(entry => entry.Font)
            .Concat(variable.Select(entry => entry.Font))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        var bytes = 0L;

        foreach (var font in fonts) {
            var from = Path.Combine(source, "fonts", font);
            File.Copy(from, Path.Combine(fontDirectory, font), overwrite: true);
            bytes += new FileInfo(from).Length;
        }

        WriteData(Path.Combine(tests, "ShapingConformance.data"), cases);
        WriteVariationData(Path.Combine(tests, "VariationConformance.data"), variable);

        Console.WriteLine($"{cases.Count} shaping cases, {variable.Count} variation cases, {fonts.Count} fonts ({bytes / 1024} KiB)");
        Console.WriteLine($"skipped: {skippedGroups} out-of-scope, {skippedVariable} variable-font without an outline oracle");
        return 0;
    }

    /// <summary>One case: a font, a string, and the glyphs a conforming engine draws.</summary>
    sealed record Case(string Id, string Font, string Text, List<Glyph> Glyphs);

    /// <summary>A drawn glyph, by name, at a position in the suite's 1000-unit em.</summary>
    sealed record Glyph(string Name, double X, double Y);

    /// <summary>One variable-font case: a font at a set of axis values, and the contours it draws.</summary>
    /// <param name="Id">The Consortium's case id.</param>
    /// <param name="Font">The font file.</param>
    /// <param name="Axes">The axis settings verbatim — <c>wght:300</c>, or <c>M1:-1.0;T1:0.0</c>.</param>
    /// <param name="Text">What is rendered.</param>
    /// <param name="Outlines">One SVG path per drawn glyph, in the order the engine produced them.</param>
    sealed record VariationCase(string Id, string Font, string Axes, string Text, List<string> Outlines);

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

    /// <summary>Reads the drawn contours out of a case's expected SVG, one per drawn glyph.</summary>
    /// <remarks>
    ///     <para>
    ///         The same <c>&lt;use&gt;</c> elements the shaping port reads, resolved to the
    ///         <c>&lt;symbol&gt;</c>s they name rather than to their positions. A glyph drawn twice
    ///         appears twice: the list is per drawn glyph so that it lines up index for index with
    ///         what a shaper produces, and a suite that de-duplicated it would have to re-derive that
    ///         mapping to compare anything.
    ///     </para>
    ///     <para>
    ///         ⚠ The path is in <b>FreeType's scaled output space</b>, not in the font's design units:
    ///         the harness renders at a 1000-pixel size and then prints each coordinate as an integer
    ///         division of the 26.6 value by 64. For a 1000-unit font that is the identity and the
    ///         numbers are design units exactly; for a 2048-unit one it is a scale by 1000/2048 and a
    ///         truncation, which is why the suite that reads this needs a tolerance at all.
    ///     </para>
    /// </remarks>
    static List<string> ReadOutlines(XElement element) {
        var symbols = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var symbol in element.Descendants().Where(static node => node.Name.LocalName == "symbol")) {
            var id = (string?)symbol.Attribute("id");
            var path = symbol.Descendants().FirstOrDefault(static node => node.Name.LocalName == "path");

            if (id is not null && path is not null) {
                symbols[id] = ((string?)path.Attribute("d") ?? "").Trim();
            }
        }

        var outlines = new List<string>();

        foreach (var use in element.Descendants().Where(static node => node.Name.LocalName == "use")) {
            var href = (string?)use.Attribute(XLink + "href") ?? (string?)use.Attribute("href");

            if (href is not null && href.StartsWith('#')) {
                // A glyph the case draws but gives no symbol for draws nothing, and an empty path is
                // the honest expectation for it rather than a dropped entry that shortens the list.
                outlines.Add(symbols.TryGetValue(href[1..], out var path) ? path : "");
            }
        }

        return outlines;
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

    /// <summary>Writes the variable-font suite: axis settings in, contours out.</summary>
    /// <remarks>
    ///     One line per case and one path per drawn glyph, tab separated the same way the shaping
    ///     file is. The paths are long — a CJK glyph is two kilobytes of them — and they are kept
    ///     verbatim rather than re-encoded, so that a line of this file can be pasted into an SVG and
    ///     looked at when a case fails.
    /// </remarks>
    static void WriteVariationData(string path, List<VariationCase> cases) {
        var builder = new StringBuilder();

        builder.Append("# Generated by Tools/Vixen.TextRenderingTestGen from unicode-org/text-rendering-tests.\n");
        builder.Append("# Do not edit — re-run the generator.\n");
        builder.Append("#\n");
        builder.Append("# The Consortium's variable-font cases. Each renders one string at one point along a\n");
        builder.Append("# font's axes and gives the contours a conforming engine draws, which makes this an\n");
        builder.Append("# oracle for 'gvar' and 'avar' delta application rather than a recording of one.\n");
        builder.Append("#\n");
        builder.Append("# id<TAB>font<TAB>axis:value;axis:value<TAB>text as code points<TAB>path|path\n");
        builder.Append("# Axis values are in user units, before normalisation and before 'avar'.\n");
        builder.Append("# Paths are FreeType's output at a 1000-pixel size, each coordinate the 26.6 value\n");
        builder.Append("# divided by 64 and truncated: design units exactly for a 1000-unit font, scaled by\n");
        builder.Append("# 1000/2048 and truncated for a 2048-unit one.\n");

        foreach (var entry in cases) {
            builder.Append(entry.Id).Append('\t').Append(entry.Font).Append('\t').Append(entry.Axes).Append('\t');

            var first = true;
            foreach (var rune in entry.Text.EnumerateRunes()) {
                if (!first) {
                    builder.Append(' ');
                }

                builder.Append(CultureInfo.InvariantCulture, $"{rune.Value:X4}");
                first = false;
            }

            builder.Append('\t').AppendJoin('|', entry.Outlines).Append('\n');
        }

        File.WriteAllText(path, builder.ToString());
        Console.WriteLine($"{Path.GetFileName(path)}: {cases.Count} cases");
    }
}
