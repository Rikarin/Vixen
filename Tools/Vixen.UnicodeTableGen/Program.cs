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
        if (args.Length is < 3 or > 4) {
            Console.Error.WriteLine(
                "usage: Vixen.UnicodeTableGen <ucd-directory> <table-output-directory> <test-output-directory> [only]"
            );

            return 1;
        }

        var ucd = args[0];
        var tables = args[1];
        var tests = args[2];

        // ⚠ <b>An opt-in name, and it writes that one artefact and says so — it never falls back to
        // writing the rest.</b> The UCD is fetched file by file (see references/README.md) and one
        // file arriving before the others is ordinary; what is not acceptable is a generator that
        // skips what it cannot read and exits 0, which is the shape of every instrument in this
        // repository that reported success on the day it did not run. So a partial database is
        // named explicitly on the command line or the run fails on the missing file.
        var only = args.Length == 4 ? args[3] : null;

        if (!Directory.Exists(ucd)) {
            Console.Error.WriteLine($"the UCD directory '{ucd}' does not exist — see references/README.md");
            return 1;
        }

        Directory.CreateDirectory(tables);
        Directory.CreateDirectory(tests);

        if (only is not null) {
            if (!string.Equals(only, "SpecialCasing", StringComparison.Ordinal)) {
                Console.Error.WriteLine($"'{only}' is not an artefact this generator knows — the only name is SpecialCasing");
                return 1;
            }

            WriteSpecialCasingTable(
                Path.Combine(tables, "SpecialCasingTable.g.cs"),
                Path.Combine(ucd, "SpecialCasing.txt")
            );

            return 0;
        }

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
        WriteTable(Path.Combine(tables, "LineBreakTable.g.cs"), "LineBreakClass", ReadProperties(Path.Combine(ucd, "LineBreak.txt")), version);

        WriteTable(
            Path.Combine(tables, "BidiClassTable.g.cs"),
            "BidiClass",
            ReadBidiClasses(Path.Combine(ucd, "DerivedBidiClass.txt")),
            version
        );

        WriteBracketTable(Path.Combine(tables, "BidiBracketTable.g.cs"), Path.Combine(ucd, "BidiBrackets.txt"), version);

        // ⚠ The one table whose version is read from its <i>own</i> file rather than from
        // GraphemeBreakTest.txt, because it is also the one that can be regenerated alone — see the
        // `only` argument above. Two headers disagreeing about the Unicode version is the point:
        // it is visible in a diff, where a silently stale table is not.
        WriteSpecialCasingTable(
            Path.Combine(tables, "SpecialCasingTable.g.cs"),
            Path.Combine(ucd, "SpecialCasing.txt")
        );

        // UAX#24. Shaping is per script, so itemisation needs this before a shaper can be handed
        // anything at all.
        WriteScriptTable(
            Path.Combine(tables, "ScriptTable.g.cs"),
            Path.Combine(ucd, "Scripts.txt"),
            Path.Combine(ucd, "PropertyValueAliases.txt"),
            version
        );

        WriteBidiConformance(
            Path.Combine(ucd, "BidiCharacterTest.txt"),
            Path.Combine(tests, "BidiCharacterConformance.data"),
            version
        );

        // LB30 asks whether a bracket is East Asian, which is a property in a file of its own again.
        WriteTable(
            Path.Combine(tables, "EastAsianWidthTable.g.cs"),
            "EastAsianWidthClass",
            ReadProperties(Path.Combine(ucd, "EastAsianWidth.txt")),
            version
        );

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

        // ⚠ Line breaking gets a data file rather than a method per case, and the reason is only
        // size: there are 19 338 of them, and a C# file with that many [Fact]s is tens of megabytes
        // and minutes of test discovery. The cases are still the Consortium's and still one test
        // each — they arrive through [Theory] from an embedded resource instead of through codegen.
        WriteConformanceData(
            Path.Combine(ucd, "LineBreakTest.txt"),
            Path.Combine(tests, "LineBreakConformance.data"),
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

    /// <summary>Reads the bidi classes, honouring the defaults for code points nobody has assigned.</summary>
    /// <remarks>
    ///     ⚠ <b>The default is not <c>L</c> everywhere.</b> DerivedBidiClass.txt carries a set of
    ///     <c>@missing</c> lines in its comments saying that unassigned code points in the Hebrew
    ///     block are <c>R</c>, in the Arabic blocks <c>AL</c>, and in the currency block <c>ET</c> —
    ///     so that a character added to those blocks tomorrow behaves correctly today. Reading only
    ///     the explicit ranges and defaulting the rest to <c>L</c> would silently make every
    ///     unassigned Arabic code point left-to-right.
    /// </remarks>
    static SortedDictionary<string, List<(int First, int Last)>> ReadBidiClasses(string path) {
        var defaults = new List<(int First, int Last, string Class)>();

        foreach (var line in File.ReadLines(path)) {
            var marker = line.IndexOf("@missing:", StringComparison.Ordinal);
            if (marker < 0) {
                continue;
            }

            var body = line[(marker + "@missing:".Length)..];
            var semicolon = body.IndexOf(';', StringComparison.Ordinal);
            var range = body[..semicolon].Trim();
            var dots = range.IndexOf("..", StringComparison.Ordinal);

            defaults.Add((
                int.Parse(range[..dots], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                int.Parse(range[(dots + 2)..], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                Abbreviate(body[(semicolon + 1)..].Trim())
            ));
        }

        // Later @missing lines are narrower and override the wide one that opens the file.
        var explicitRanges = ReadProperties(path);
        var occupied = new List<(int First, int Last)>();

        foreach (var ranges in explicitRanges.Values) {
            occupied.AddRange(ranges);
        }

        occupied.Sort(static (left, right) => left.First.CompareTo(right.First));

        // Every gap between explicit ranges takes the narrowest @missing default covering it.
        var cursor = 0;
        foreach (var (first, last) in occupied) {
            if (first > cursor) {
                AddDefaults(explicitRanges, defaults, cursor, first - 1);
            }

            cursor = Math.Max(cursor, last + 1);
        }

        AddDefaults(explicitRanges, defaults, cursor, 0x10FFFF);
        return explicitRanges;
    }

    static void AddDefaults(
        SortedDictionary<string, List<(int First, int Last)>> into,
        List<(int First, int Last, string Class)> defaults,
        int from,
        int to
    ) {
        for (var codePoint = from; codePoint <= to;) {
            var name = "L";
            var end = to;

            foreach (var (first, last, value) in defaults) {
                if (codePoint < first || codePoint > last) {
                    continue;
                }

                name = value;
                end = Math.Min(end, last);
            }

            foreach (var (first, _, _) in defaults) {
                if (first > codePoint) {
                    end = Math.Min(end, first - 1);
                }
            }

            if (!into.TryGetValue(name, out var ranges)) {
                ranges = [];
                into[name] = ranges;
            }

            ranges.Add((codePoint, end));
            codePoint = end + 1;
        }
    }

    /// <summary>The short names the algorithm is written in, from the long ones the file uses.</summary>
    static string Abbreviate(string name) => name switch {
        "Left_To_Right" => "L",
        "Right_To_Left" => "R",
        "Arabic_Letter" => "AL",
        "European_Terminator" => "ET",
        _ => name
    };

    /// <summary>Writes the paired-bracket table N0 needs.</summary>
    static void WriteBracketTable(string path, string source, string version) {
        var entries = new List<(int Code, int Paired, char Kind)>();

        foreach (var raw in File.ReadLines(source)) {
            var line = raw;
            var hash = line.IndexOf('#', StringComparison.Ordinal);
            if (hash >= 0) {
                line = line[..hash];
            }

            var fields = line.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != 3) {
                continue;
            }

            entries.Add((
                int.Parse(fields[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                int.Parse(fields[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                fields[2][0]
            ));
        }

        entries.Sort(static (left, right) => left.Code.CompareTo(right.Code));

        var builder = new StringBuilder();
        builder.Append("// SPDX-FileCopyrightText: Copyright (c) Rikarin\n");
        builder.Append("// SPDX-License-Identifier: Apache-2.0\n//\n// <auto-generated>\n");
        builder.Append("//     Generated by Tools/Vixen.UnicodeTableGen from the Unicode Character Database,\n");
        builder.Append(CultureInfo.InvariantCulture, $"//     version {version}. Do not edit — re-run the generator.\n");
        builder.Append("// </auto-generated>\n\nnamespace Vixen.Ui.Text;\n\n");
        builder.Append("/// <summary>The brackets rule N0 pairs up, and what each one pairs with.</summary>\n");
        builder.Append("static class BidiBracketTable {\n");
        builder.Append("    static readonly int[] Codes = [\n");
        AppendNumbers(builder, entries.Select(entry => entry.Code));
        builder.Append("    ];\n\n    static readonly int[] Paired = [\n");
        AppendNumbers(builder, entries.Select(entry => entry.Paired));
        builder.Append("    ];\n\n    static readonly bool[] Opens = [\n        ");

        var count = 0;
        foreach (var entry in entries) {
            builder.Append(entry.Kind == 'o' ? "true, " : "false, ");
            if (++count % 12 == 0) {
                builder.Append("\n        ");
            }
        }

        builder.Append("\n    ];\n\n");
        builder.Append("    /// <summary>Looks a bracket up.</summary>\n");
        builder.Append("    /// <param name=\"codePoint\">The code point.</param>\n");
        builder.Append("    /// <param name=\"paired\">Receives the bracket that closes or opens it.</param>\n");
        builder.Append("    /// <param name=\"opens\">Whether this one is the opening half.</param>\n");
        builder.Append("    /// <returns>Whether it is a paired bracket at all.</returns>\n");
        builder.Append("    public static bool TryGet(int codePoint, out int paired, out bool opens) {\n");
        builder.Append("        var index = Array.BinarySearch(Codes, codePoint);\n\n");
        builder.Append("        if (index < 0) {\n            paired = 0;\n            opens = false;\n            return false;\n        }\n\n");
        builder.Append("        paired = Paired[index];\n        opens = Opens[index];\n        return true;\n    }\n}\n");

        File.WriteAllText(path, builder.ToString());
        Console.WriteLine($"{Path.GetFileName(path)}: {entries.Count} brackets");
    }

    /// <summary>Writes the UAX#24 script table, keyed by the ISO 15924 tag.</summary>
    /// <remarks>
    ///     <para>
    ///         The enum's <i>value</i> is the four-letter ISO 15924 tag packed big-endian, which is
    ///         exactly what a shaper is handed — HarfBuzz's <c>hb_script_t</c> is that same packed
    ///         tag. So the table produces the shaper's argument directly and there is no second
    ///         mapping to keep in step with this one. That is the whole reason
    ///         PropertyValueAliases.txt is read here: Scripts.txt is written in long names
    ///         (<c>Latin</c>) and every shaping API in existence wants the short one (<c>Latn</c>).
    ///     </para>
    ///     <para>
    ///         Unlike the segmentation properties the default is not <c>Other</c> but <c>Unknown</c>
    ///         — <c>Zzzz</c> — which is a real script code with a real meaning, and one the
    ///         itemiser treats differently from a script it recognises.
    ///     </para>
    /// </remarks>
    static void WriteScriptTable(string path, string source, string aliases, string version) {
        var tags = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var raw in File.ReadLines(aliases)) {
            var line = raw;
            var hash = line.IndexOf('#', StringComparison.Ordinal);
            if (hash >= 0) {
                line = line[..hash];
            }

            // `sc ; Latn ; Latin`, among a file of every other property's aliases.
            var fields = line.Split(';', StringSplitOptions.TrimEntries);
            if (fields.Length < 3 || fields[0] != "sc") {
                continue;
            }

            tags[fields[2]] = fields[1];
        }

        var properties = ReadProperties(source);
        var all = new List<(int First, int Last, string Script)>();

        foreach (var (name, ranges) in properties) {
            if (!tags.ContainsKey(name)) {
                Console.Error.WriteLine($"no ISO 15924 tag for script '{name}' — PropertyValueAliases.txt is stale");
                return;
            }

            foreach (var (first, last) in ranges) {
                all.Add((first, last, name));
            }
        }

        all.Sort(static (left, right) => left.First.CompareTo(right.First));

        var merged = new List<(int First, int Last, string Script)>();
        foreach (var entry in all) {
            if (merged.Count > 0 && merged[^1].Script == entry.Script && merged[^1].Last + 1 >= entry.First) {
                merged[^1] = (merged[^1].First, Math.Max(merged[^1].Last, entry.Last), entry.Script);
                continue;
            }

            merged.Add(entry);
        }

        var builder = new StringBuilder();

        builder.Append("// SPDX-FileCopyrightText: Copyright (c) Rikarin\n");
        builder.Append("// SPDX-License-Identifier: Apache-2.0\n//\n// <auto-generated>\n");
        builder.Append("//     Generated by Tools/Vixen.UnicodeTableGen from the Unicode Character Database,\n");
        builder.Append(CultureInfo.InvariantCulture, $"//     version {version}. Do not edit — re-run the generator.\n//\n");
        builder.Append("//     Derived from Unicode data files, which carry the Unicode terms of use:\n");
        builder.Append("//     https://www.unicode.org/terms_of_use.html\n");
        builder.Append("// </auto-generated>\n\nnamespace Vixen.Ui.Text;\n\n");

        builder.Append("/// <summary>The UAX#24 Script property of a code point, as its ISO 15924 tag.</summary>\n");
        builder.Append("/// <remarks>\n");
        builder.Append("///     Each value is the four-letter tag packed big-endian, so casting one to <c>uint</c>\n");
        builder.Append("///     produces the tag a shaper expects with nothing in between to get wrong.\n");
        builder.Append("/// </remarks>\n");
        builder.Append("public enum Script : uint {\n");

        // ⚠ `Unknown` is never a *range* in Scripts.txt — it is the file's `@missing` default, so
        // reading only the ranges leaves the enum without the one member every unassigned code point
        // resolves to. The lookup below returns it, so it has to exist whether or not the data
        // mentions it.
        var names = new SortedSet<string>(properties.Keys, StringComparer.Ordinal) { "Unknown" };

        foreach (var name in names) {
            var tag = tags[name];
            builder.Append(CultureInfo.InvariantCulture, $"    /// <summary><c>{tag}</c> — {name.Replace('_', ' ')}.</summary>\n");
            builder.Append(CultureInfo.InvariantCulture, $"    {Identifier(name)} = 0x{Pack(tag):X8}u,\n\n");
        }

        builder.Length -= 1;
        builder.Append("}\n\n");

        builder.Append("/// <summary>Looks the script of a code point up.</summary>\n");
        builder.Append("/// <remarks>\n");
        builder.Append(CultureInfo.InvariantCulture, $"///     {merged.Count} ranges, sorted and merged, in one flat array — the same layout the\n");
        builder.Append("///     segmentation tables use and for the same reason.\n");
        builder.Append("/// </remarks>\n");
        builder.Append("static class ScriptTable {\n");
        builder.Append("    /// <summary>The Unicode version these ranges came from.</summary>\n");
        builder.Append(CultureInfo.InvariantCulture, $"    public const string UnicodeVersion = \"{version}\";\n\n");
        builder.Append("    static readonly int[] Starts = [\n");
        AppendNumbers(builder, merged.Select(entry => entry.First));
        builder.Append("    ];\n\n    static readonly int[] Ends = [\n");
        AppendNumbers(builder, merged.Select(entry => entry.Last));
        builder.Append("    ];\n\n    static readonly Script[] Scripts = [\n");

        for (var i = 0; i < merged.Count; i += 8) {
            builder.Append("        ");
            for (var j = i; j < Math.Min(i + 8, merged.Count); j++) {
                builder.Append(CultureInfo.InvariantCulture, $"Script.{Identifier(merged[j].Script)}, ");
            }

            builder.Length -= 1;
            builder.Append('\n');
        }

        builder.Append("    ];\n\n");
        builder.Append("    /// <summary>The script of a code point.</summary>\n");
        builder.Append("    /// <param name=\"codePoint\">The code point.</param>\n");
        builder.Append("    /// <returns>Its script, or <see cref=\"Script.Unknown\" /> if it has none.</returns>\n");
        builder.Append("    public static Script Of(int codePoint) {\n");
        builder.Append("        var low = 0;\n        var high = Starts.Length - 1;\n\n");
        builder.Append("        while (low <= high) {\n            var middle = (low + high) >> 1;\n\n");
        builder.Append("            if (codePoint < Starts[middle]) {\n                high = middle - 1;\n");
        builder.Append("            } else if (codePoint > Ends[middle]) {\n                low = middle + 1;\n");
        builder.Append("            } else {\n                return Scripts[middle];\n            }\n        }\n\n");
        builder.Append("        return Script.Unknown;\n    }\n}\n");

        File.WriteAllText(path, builder.ToString());
        Console.WriteLine($"{Path.GetFileName(path)}: {merged.Count} ranges, {names.Count} scripts");
    }

    /// <summary>Packs a four-letter ISO 15924 tag into the integer a shaper wants.</summary>
    static uint Pack(string tag) =>
        ((uint)tag[0] << 24) | ((uint)tag[1] << 16) | ((uint)tag[2] << 8) | tag[3];

    /// <summary>Writes BidiCharacterTest as compact data.</summary>
    static void WriteBidiConformance(string source, string path, string version) {
        var builder = new StringBuilder();
        var cases = 0;

        builder.Append(CultureInfo.InvariantCulture, $"# Unicode {version}. Generated by Tools/Vixen.UnicodeTableGen — do not edit.\n");
        builder.Append("# codePoints;paragraphDirection;resolvedParagraphLevel;levels;visualOrder\n");

        foreach (var raw in File.ReadLines(source)) {
            if (raw.Length == 0 || raw[0] == '#') {
                continue;
            }

            builder.Append(raw).Append('\n');
            cases++;
        }

        File.WriteAllText(path, builder.ToString());
        Console.WriteLine($"{Path.GetFileName(path)}: {cases} cases");
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

    /// <summary>Writes the full case mappings that are not one code point to one code point.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>This is the table .NET does not have.</b> <c>string.ToUpperInvariant</c> — and
    ///         <c>ToUpper</c> in every culture, and <c>Rune.ToUpperInvariant</c> over all 1 112 064
    ///         scalars — implements the <i>simple</i> case mappings of UnicodeData.txt, which are
    ///         one code point to one code point by definition. So <c>straße</c> uppercases to
    ///         <c>STRAßE</c> there and to <c>STRASSE</c> in every browser, because CSS Text 3 § 2.1
    ///         specifies the <i>full</i> mappings, and the difference between the two is exactly
    ///         this file.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Only the unconditional entries.</b> SpecialCasing.txt's remaining rows carry a
    ///         condition list — <c>Final_Sigma</c>, <c>After_Soft_Dotted</c>, <c>tr</c>, <c>az</c>,
    ///         <c>lt</c> — and every one of them needs either surrounding context or a language.
    ///         <c>TextShaper</c> leaves HarfBuzz's language unset on purpose so that shaping does
    ///         not depend on the machine's locale, and the same reasoning applies here: a case
    ///         mapping that changed with the operating system's region would make a golden image
    ///         machine-dependent. The conditional rows are counted and reported so that dropping
    ///         them is a number somebody can see rather than a silence.
    ///     </para>
    ///     <para>
    ///         The version is read from this file's own header rather than from GraphemeBreakTest.txt,
    ///         because this is the one artefact that can be regenerated on its own.
    ///     </para>
    /// </remarks>
    static void WriteSpecialCasingTable(string path, string source) {
        var version = "unknown";
        var upper = new List<(int Code, string Mapping)>();
        var lower = new List<(int Code, string Mapping)>();
        var title = new List<(int Code, string Mapping)>();
        var conditional = 0;

        foreach (var raw in File.ReadLines(source)) {
            if (version == "unknown" && raw.StartsWith("# SpecialCasing-", StringComparison.Ordinal)) {
                version = raw["# SpecialCasing-".Length..].Replace(".txt", string.Empty, StringComparison.Ordinal).Trim();
            }

            var line = raw;
            var hash = line.IndexOf('#', StringComparison.Ordinal);
            if (hash >= 0) {
                line = line[..hash];
            }

            // `<code>; <lower>; <title>; <upper>; (<condition_list>;)?` — the trailing semicolon
            // makes an unconditional row four fields and a conditional one five.
            var fields = line.Split(';', StringSplitOptions.TrimEntries);
            if (fields.Length < 4 || fields[0].Length == 0) {
                continue;
            }

            if (fields.Length > 4 && fields[4].Length > 0) {
                conditional++;
                continue;
            }

            var code = int.Parse(fields[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture);

            Collect(lower, code, fields[1], 'l');
            Collect(title, code, fields[2], 't');
            Collect(upper, code, fields[3], 'u');
        }

        upper.Sort(static (left, right) => left.Code.CompareTo(right.Code));
        lower.Sort(static (left, right) => left.Code.CompareTo(right.Code));
        title.Sort(static (left, right) => left.Code.CompareTo(right.Code));

        var builder = new StringBuilder();
        builder.Append("// SPDX-FileCopyrightText: Copyright (c) Rikarin\n");
        builder.Append("// SPDX-License-Identifier: Apache-2.0\n//\n// <auto-generated>\n");
        builder.Append("//     Generated by Tools/Vixen.UnicodeTableGen from the Unicode Character Database,\n");
        builder.Append(CultureInfo.InvariantCulture, $"//     version {version}. Do not edit — re-run the generator.\n");
        builder.Append("//\n");
        builder.Append("//     Derived from Unicode data files, which carry the Unicode terms of use:\n");
        builder.Append("//     https://www.unicode.org/terms_of_use.html\n");
        builder.Append("// </auto-generated>\n\nnamespace Vixen.Ui.Text;\n\n");
        builder.Append("/// <summary>The full case mappings that are not one code point to one.</summary>\n");
        builder.Append("/// <remarks>\n");
        builder.Append("///     SpecialCasing.txt's unconditional rows, and only those. A code point absent from a\n");
        builder.Append("///     table here has a full mapping equal to its simple one, which is what\n");
        builder.Append("///     <c>Rune.ToUpperInvariant</c> and its siblings already answer.\n");
        builder.Append(CultureInfo.InvariantCulture, $"///     The {conditional} conditional rows are deliberately not here — see the generator.\n");
        builder.Append("/// </remarks>\n");
        builder.Append("static class SpecialCasingTable {\n");

        AppendCasing(builder, "Upper", upper);
        builder.Append('\n');
        AppendCasing(builder, "Lower", lower);
        builder.Append('\n');
        AppendCasing(builder, "Title", title);
        builder.Append("}\n");

        File.WriteAllText(path, builder.ToString());

        Console.WriteLine(
            $"{Path.GetFileName(path)}: Unicode {version}, {upper.Count} upper, {lower.Count} lower, "
            + $"{title.Count} title, {conditional} conditional rows dropped"
        );

        static void Collect(List<(int Code, string Mapping)> into, int code, string field, char kind) {
            var scalars = field.Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var text = new StringBuilder();

            foreach (var scalar in scalars) {
                text.Append(
                    char.ConvertFromUtf32(int.Parse(scalar, NumberStyles.HexNumber, CultureInfo.InvariantCulture))
                );
            }

            var rune = new System.Text.Rune(code);

            // ⚠ Only the rows where the full mapping differs from the simple one reach the table.
            // Every other row would cost a binary search to answer what `Rune.ToUpperInvariant` was
            // going to answer anyway — and, worse, would stop the table's own count being the
            // number of places the two disagree, which is the number worth reading.
            //
            // ⚠ Titlecase is compared against the *simple titlecase* mapping, which .NET does not
            // expose at all; `Rune`'s uppercase is the nearest thing and is wrong for the two dozen
            // digraphs whose titlecase is a third form (`ǅ` is neither `ǄDŽ` nor `ǆdž`). So the title column
            // keeps the row whenever it differs from the code point itself, which over-collects by
            // the handful of rows where title equals simple-title and costs nothing but entries.
            var simple = kind switch {
                'u' => System.Text.Rune.ToUpperInvariant(rune).ToString(),
                'l' => System.Text.Rune.ToLowerInvariant(rune).ToString(),
                _ => rune.ToString()
            };

            var mapped = text.ToString();

            if (mapped == simple) {
                return;
            }

            into.Add((code, mapped));
        }
    }

    static void AppendCasing(StringBuilder builder, string name, List<(int Code, string Mapping)> entries) {
        builder.Append(CultureInfo.InvariantCulture, $"    static readonly int[] {name}Codes = [\n");
        AppendNumbers(builder, entries.Select(entry => entry.Code));
        builder.Append(CultureInfo.InvariantCulture, $"    ];\n\n    static readonly string[] {name}Mappings = [\n");

        foreach (var (_, mapping) in entries) {
            builder.Append("        \"");

            foreach (var unit in mapping) {
                builder.Append(CultureInfo.InvariantCulture, $"\\u{(int) unit:X4}");
            }

            builder.Append("\",\n");
        }

        builder.Append("    ];\n\n");
        builder.Append(CultureInfo.InvariantCulture, $"    /// <summary>The full {name.ToLowerInvariant()}case mapping, when it is not the simple one.</summary>\n");
        builder.Append("    /// <param name=\"codePoint\">The code point.</param>\n");
        builder.Append("    /// <param name=\"mapping\">Receives the replacement, which may be several code units.</param>\n");
        builder.Append("    /// <returns>Whether this code point has one at all.</returns>\n");
        builder.Append(CultureInfo.InvariantCulture, $"    public static bool Try{name}(int codePoint, out string mapping) {{\n");
        builder.Append(CultureInfo.InvariantCulture, $"        var index = Array.BinarySearch({name}Codes, codePoint);\n\n");
        builder.Append("        if (index < 0) {\n            mapping = string.Empty;\n            return false;\n        }\n\n");
        builder.Append(CultureInfo.InvariantCulture, $"        mapping = {name}Mappings[index];\n        return true;\n    }}\n");
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

    /// <summary>Writes a conformance suite as compact data rather than as generated methods.</summary>
    /// <remarks>
    ///     One line per case: the code points in hex, a tab, the break offsets, a tab, and the
    ///     Consortium's own comment so a failure can be read against the rule it names. Committed
    ///     and embedded, for the same reason the generated files are — CI has no copy of the UCD.
    /// </remarks>
    static void WriteConformanceData(string source, string path, string version) {
        var builder = new StringBuilder();
        var cases = 0;

        builder.Append(CultureInfo.InvariantCulture, $"# Unicode {version}. Generated by Tools/Vixen.UnicodeTableGen — do not edit.\n");
        builder.Append("# codePoints<TAB>breakOffsets<TAB>the Unicode Consortium's own description of the case.\n");

        foreach (var raw in File.ReadLines(source)) {
            var line = raw;
            var hash = line.IndexOf('#', StringComparison.Ordinal);
            var comment = hash >= 0 ? line[(hash + 1)..].Trim() : string.Empty;

            if (hash >= 0) {
                line = line[..hash];
            }

            line = line.Trim();
            if (line.Length == 0 || !TryParseCase(line, out var codePoints, out var breaks)) {
                continue;
            }

            cases++;
            builder.Append(CultureInfo.InvariantCulture, $"{string.Join(' ', codePoints.Select(c => c.ToString("X", CultureInfo.InvariantCulture)))}\t");
            builder.Append(CultureInfo.InvariantCulture, $"{string.Join(' ', breaks)}\t{comment}\n");
        }

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
