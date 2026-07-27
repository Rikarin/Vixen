// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;

namespace Vixen.UnicodeTableGen;

/// <summary>Turns the Unicode Character Database into committed C#.</summary>
/// <remarks>
///     <para>
///         Two artefacts, and they are different in kind. The <b>property tables</b> are data the
///         implementation cannot work without — a sorted range table per segmentation property,
///         binary-searched at runtime. The <b>conformance suites</b> are the reason to trust the
///         implementation at all: two thousand seven hundred cases written by the people who wrote
///         the specification, which is the strongest kind of oracle there is and the same bet the
///         Yoga fixtures were.
///     </para>
///     <para>
///         Both are generated once and committed, because CI has no copy of the database. Re-run by
///         hand when the UCD version moves; the generated files say which version they came from, so
///         a mismatch is visible in a diff rather than at runtime.
///     </para>
/// </remarks>
static class Program {
    static int Main(string[] args) {
        if (args.Length != 3) {
            Console.Error.WriteLine(
                "usage: Vixen.UnicodeTableGen <ucd-directory> <table-output-directory> <test-output-directory>"
            );

            return 1;
        }

        var ucd = args[0];
        var tables = args[1];
        var tests = args[2];

        if (!Directory.Exists(ucd)) {
            Console.Error.WriteLine($"the UCD directory '{ucd}' does not exist — see references/README.md");
            return 1;
        }

        Directory.CreateDirectory(tables);
        Directory.CreateDirectory(tests);

        var version = ReadVersion(Path.Combine(ucd, "GraphemeBreakTest.txt"));
        Console.WriteLine($"Unicode {version}");

        var grapheme = ReadProperties(Path.Combine(ucd, "GraphemeBreakProperty.txt"));
        var word = ReadProperties(Path.Combine(ucd, "WordBreakProperty.txt"));
        var pictographic = ReadProperties(Path.Combine(ucd, "emoji-data.txt"), "Extended_Pictographic");

        // GB9c — the indic conjunct rule — needs a property that lives in neither of the files
        // above. `InCB` is in DerivedCoreProperties.txt and is written `; InCB; Consonant`, a second
        // semicolon the range reader has to be told about.
        var conjunct = ReadProperties(Path.Combine(ucd, "DerivedCoreProperties.txt"), prefix: "InCB");

        // ⚠ Extended_Pictographic gets a table of its own and is *not* folded into the other two.
        // It comes from a different UCD file and overlaps them: U+24C2 CIRCLED LATIN CAPITAL LETTER M
        // is Word_Break=ALetter and Extended_Pictographic=Yes at the same time. Merging the ranges
        // into one class table makes one of the two silently shadow the other — which one depends on
        // sort order — and the word suite says exactly that, in forty-four cases all containing
        // U+24C2. A code point has one Word_Break property and separately may or may not be
        // pictographic; the tables have to say so too.
        WriteTable(
            Path.Combine(tables, "ExtendedPictographicTable.g.cs"),
            "ExtendedPictographicClass",
            pictographic,
            version
        );

        WriteTable(Path.Combine(tables, "GraphemeBreakTable.g.cs"), "GraphemeBreakClass", grapheme, version);
        WriteTable(Path.Combine(tables, "IndicConjunctTable.g.cs"), "IndicConjunctClass", conjunct, version);
        WriteTable(Path.Combine(tables, "WordBreakTable.g.cs"), "WordBreakClass", word, version);

        WriteConformance(
            Path.Combine(ucd, "GraphemeBreakTest.txt"),
            Path.Combine(tests, "GraphemeBreakConformance.g.cs"),
            "GraphemeBreakConformance",
            "Graphemes",
            version
        );

        WriteConformance(
            Path.Combine(ucd, "WordBreakTest.txt"),
            Path.Combine(tests, "WordBreakConformance.g.cs"),
            "WordBreakConformance",
            "Words",
            version
        );

        return 0;
    }

    static string ReadVersion(string path) {
        foreach (var line in File.ReadLines(path)) {
            // `# GraphemeBreakTest-17.0.0.txt`
            var dash = line.IndexOf('-', StringComparison.Ordinal);
            if (line.StartsWith("# GraphemeBreakTest-", StringComparison.Ordinal) && dash > 0) {
                return line[(dash + 1)..].Replace(".txt", string.Empty, StringComparison.Ordinal).Trim();
            }
        }

        return "unknown";
    }

    /// <summary>Reads a <c>range ; Property</c> file into ranges per property.</summary>
    static SortedDictionary<string, List<(int First, int Last)>> ReadProperties(
        string path,
        string? only = null,
        string? prefix = null
    ) {
        var properties = new SortedDictionary<string, List<(int, int)>>(StringComparer.Ordinal);

        foreach (var raw in File.ReadLines(path)) {
            var line = raw;
            var hash = line.IndexOf('#', StringComparison.Ordinal);
            if (hash >= 0) {
                line = line[..hash];
            }

            var semicolon = line.IndexOf(';', StringComparison.Ordinal);
            if (semicolon < 0) {
                continue;
            }

            var name = line[(semicolon + 1)..].Trim();

            if (prefix is not null) {
                // `0300 ; InCB; Extend` — the property is two fields, and every other line in the
                // file is a different property entirely.
                var second = name.IndexOf(';', StringComparison.Ordinal);
                if (second < 0 || name[..second].Trim() != prefix) {
                    continue;
                }

                name = name[(second + 1)..].Trim();
            }

            if (name.Length == 0 || (only is not null && name != only)) {
                continue;
            }

            var range = line[..semicolon].Trim();
            var dots = range.IndexOf("..", StringComparison.Ordinal);

            var first = int.Parse(dots < 0 ? range : range[..dots], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var last = dots < 0
                ? first
                : int.Parse(range[(dots + 2)..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);

            if (!properties.TryGetValue(name, out var ranges)) {
                ranges = [];
                properties[name] = ranges;
            }

            ranges.Add((first, last));
        }

        return properties;
    }

    static void WriteTable(
        string path,
        string enumName,
        SortedDictionary<string, List<(int First, int Last)>> properties,
        string version
    ) {
        // One flat array of (first, last, class) sorted by `first`, so a lookup is one binary
        // search over a contiguous span rather than a probe per property. Adjacent ranges of the
        // same class are merged, which the UCD does not always do for us.
        var all = new List<(int First, int Last, string Class)>();
        foreach (var (name, ranges) in properties) {
            foreach (var (first, last) in ranges) {
                all.Add((first, last, name));
            }
        }

        all.Sort(static (left, right) => left.First.CompareTo(right.First));

        var merged = new List<(int First, int Last, string Class)>();
        foreach (var entry in all) {
            if (merged.Count > 0
                && merged[^1].Class == entry.Class
                && merged[^1].Last + 1 >= entry.First) {
                merged[^1] = (merged[^1].First, Math.Max(merged[^1].Last, entry.Last), entry.Class);
                continue;
            }

            merged.Add(entry);
        }

        var names = new SortedSet<string>(properties.Keys, StringComparer.Ordinal);
        var builder = new StringBuilder();

        builder.Append("// SPDX-FileCopyrightText: Copyright (c) Rikarin\n");
        builder.Append("// SPDX-License-Identifier: Apache-2.0\n");
        builder.Append("//\n");
        builder.Append("// <auto-generated>\n");
        builder.Append("//     Generated by Tools/Vixen.UnicodeTableGen from the Unicode Character Database,\n");
        builder.Append(CultureInfo.InvariantCulture, $"//     version {version}. Do not edit — re-run the generator.\n");
        builder.Append("//\n");
        builder.Append("//     Derived from Unicode data files, which carry the Unicode terms of use:\n");
        builder.Append("//     https://www.unicode.org/terms_of_use.html\n");
        builder.Append("// </auto-generated>\n\n");
        builder.Append("namespace Vixen.Ui.Text;\n\n");

        builder.Append(CultureInfo.InvariantCulture, $"/// <summary>The {enumName.Replace("Class", string.Empty, StringComparison.Ordinal)} property of a code point.</summary>\n");
        builder.Append("/// <remarks>\n");
        builder.Append("///     <c>Other</c> is zero and is the default, which is what the specification calls\n");
        builder.Append("///     <c>Any</c> — every code point the tables do not mention has it.\n");
        builder.Append("/// </remarks>\n");
        builder.Append(CultureInfo.InvariantCulture, $"public enum {enumName} : byte {{\n");
        builder.Append("    /// <summary>Any code point no rule gives a class to.</summary>\n");
        builder.Append("    Other,\n");

        foreach (var name in names) {
            builder.Append(CultureInfo.InvariantCulture, $"\n    /// <summary>The <c>{name}</c> class.</summary>\n");
            builder.Append(CultureInfo.InvariantCulture, $"    {Identifier(name)},\n");
        }

        builder.Length -= 2;
        builder.Append("\n}\n\n");

        builder.Append(CultureInfo.InvariantCulture, $"/// <summary>Looks the {enumName} of a code point up.</summary>\n");
        builder.Append("/// <remarks>\n");
        builder.Append(CultureInfo.InvariantCulture, $"///     {merged.Count} ranges, sorted and merged, in one flat array — a lookup is a binary search\n");
        builder.Append("///     over a contiguous span rather than a probe per property. Segmentation asks this once per\n");
        builder.Append("///     code point of every string it measures, so the layout matters more than the count does.\n");
        builder.Append("/// </remarks>\n");
        builder.Append(CultureInfo.InvariantCulture, $"static class {enumName}Table {{\n");
        builder.Append(CultureInfo.InvariantCulture, $"    /// <summary>The Unicode version these ranges came from.</summary>\n");
        builder.Append(CultureInfo.InvariantCulture, $"    public const string UnicodeVersion = \"{version}\";\n\n");
        builder.Append("    static readonly int[] Starts = [\n");
        AppendNumbers(builder, merged.Select(entry => entry.First));
        builder.Append("    ];\n\n");
        builder.Append("    static readonly int[] Ends = [\n");
        AppendNumbers(builder, merged.Select(entry => entry.Last));
        builder.Append("    ];\n\n");
        builder.Append(CultureInfo.InvariantCulture, $"    static readonly {enumName}[] Classes = [\n");

        for (var i = 0; i < merged.Count; i += 12) {
            builder.Append("        ");
            for (var j = i; j < Math.Min(i + 12, merged.Count); j++) {
                builder.Append(CultureInfo.InvariantCulture, $"{enumName}.{Identifier(merged[j].Class)}, ");
            }

            builder.Length -= 1;
            builder.Append('\n');
        }

        builder.Append("    ];\n\n");
        builder.Append("    /// <summary>The class of a code point.</summary>\n");
        builder.Append("    /// <param name=\"codePoint\">The code point.</param>\n");
        builder.Append("    /// <returns>Its class.</returns>\n");
        builder.Append(CultureInfo.InvariantCulture, $"    public static {enumName} Of(int codePoint) {{\n");
        builder.Append("        var low = 0;\n");
        builder.Append("        var high = Starts.Length - 1;\n\n");
        builder.Append("        while (low <= high) {\n");
        builder.Append("            var middle = (low + high) >> 1;\n\n");
        builder.Append("            if (codePoint < Starts[middle]) {\n");
        builder.Append("                high = middle - 1;\n");
        builder.Append("            } else if (codePoint > Ends[middle]) {\n");
        builder.Append("                low = middle + 1;\n");
        builder.Append("            } else {\n");
        builder.Append("                return Classes[middle];\n");
        builder.Append("            }\n");
        builder.Append("        }\n\n");
        builder.Append(CultureInfo.InvariantCulture, $"        return {enumName}.Other;\n");
        builder.Append("    }\n");
        builder.Append("}\n");

        File.WriteAllText(path, builder.ToString());
        Console.WriteLine($"{Path.GetFileName(path)}: {merged.Count} ranges, {names.Count} classes");
    }

    static void AppendNumbers(StringBuilder builder, IEnumerable<int> values) {
        var count = 0;
        builder.Append("        ");

        foreach (var value in values) {
            builder.Append(CultureInfo.InvariantCulture, $"0x{value:X}, ");

            if (++count % 12 != 0) {
                continue;
            }

            builder.Length -= 1;
            builder.Append("\n        ");
        }

        builder.Length -= count % 12 == 0 ? 8 : 1;
        builder.Append('\n');
    }

    /// <summary>Turns a UCD property name into a C# identifier.</summary>
    static string Identifier(string name) {
        var builder = new StringBuilder(name.Length);
        var upper = true;

        foreach (var c in name) {
            if (c == '_') {
                upper = true;
                continue;
            }

            builder.Append(upper ? char.ToUpperInvariant(c) : c);
            upper = false;
        }

        return builder.ToString();
    }

    static void WriteConformance(string source, string path, string className, string what, string version) {
        var builder = new StringBuilder();
        var cases = 0;

        builder.Append("// SPDX-FileCopyrightText: Copyright (c) Rikarin\n");
        builder.Append("// SPDX-License-Identifier: Apache-2.0\n");
        builder.Append("//\n");
        builder.Append("// <auto-generated>\n");
        builder.Append("//     Generated by Tools/Vixen.UnicodeTableGen from the Unicode Character Database,\n");
        builder.Append(CultureInfo.InvariantCulture, $"//     version {version}. Do not edit — re-run the generator.\n");
        builder.Append("//\n");
        builder.Append("//     Every case is the Unicode Consortium's, from the conformance suite published\n");
        builder.Append("//     alongside UAX#29. Nothing here is a Vixen expectation about what the algorithm\n");
        builder.Append("//     should do; that is the entire point of it.\n");
        builder.Append("// </auto-generated>\n\n");
        builder.Append("using Xunit;\n\n");
        builder.Append("namespace Vixen.Ui.Text.Tests;\n\n");
        builder.Append(CultureInfo.InvariantCulture, $"/// <summary>The UAX#29 {what.ToLowerInvariant()} conformance suite, Unicode {version}.</summary>\n");
        builder.Append(CultureInfo.InvariantCulture, $"public class {className} {{\n");

        foreach (var raw in File.ReadLines(source)) {
            var line = raw;
            var hash = line.IndexOf('#', StringComparison.Ordinal);
            var comment = hash >= 0 ? line[(hash + 1)..].Trim() : string.Empty;

            if (hash >= 0) {
                line = line[..hash];
            }

            line = line.Trim();
            if (line.Length == 0) {
                continue;
            }

            if (!TryParseCase(line, out var codePoints, out var breaks)) {
                Console.Error.WriteLine($"skipped: {raw}");
                continue;
            }

            cases++;
            builder.Append(CultureInfo.InvariantCulture, $"\n    /// <summary>{Escape(comment)}</summary>\n");
            builder.Append("    [Fact]\n");
            builder.Append(CultureInfo.InvariantCulture, $"    public void Case{cases:0000}() => UnicodeConformance.Assert{what}(\n");
            builder.Append(CultureInfo.InvariantCulture, $"        [{string.Join(", ", codePoints.Select(c => $"0x{c:X}"))}],\n");
            builder.Append(CultureInfo.InvariantCulture, $"        [{string.Join(", ", breaks)}]\n");
            builder.Append("    );\n");
        }

        builder.Append("}\n");
        File.WriteAllText(path, builder.ToString());
        Console.WriteLine($"{Path.GetFileName(path)}: {cases} cases");
    }

    /// <summary>Reads one <c>÷ 0020 × 0308 ÷</c> line.</summary>
    /// <remarks>
    ///     The breaks come back as <i>code point offsets</i>, not as positions in the token stream:
    ///     the suite is written per code point and the implementation works on UTF-16, so the
    ///     translation happens in the test helper where it can be done once.
    /// </remarks>
    static bool TryParseCase(string line, out List<int> codePoints, out List<int> breaks) {
        codePoints = [];
        breaks = [];

        foreach (var token in line.Split(' ', StringSplitOptions.RemoveEmptyEntries)) {
            switch (token) {
                case "÷":
                    breaks.Add(codePoints.Count);
                    break;

                case "×":
                    break;

                default: {
                    if (!int.TryParse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)) {
                        return false;
                    }

                    codePoints.Add(value);
                    break;
                }
            }
        }

        return codePoints.Count > 0;
    }

    static string Escape(string text) =>
        text.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);
}
