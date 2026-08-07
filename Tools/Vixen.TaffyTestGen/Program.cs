// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Vixen.TaffyTestGen;

var taffy = args.Length > 0 ? args[0] : Path.Combine("references", "taffy");
var output = args.Length > 1 ? args[1] : Path.Combine("Core", "Vixen.Ui.Layout.Tests", "Taffy", "Corpus");

var fixtures = Path.Combine(taffy, "tests", "xml");
if (!Directory.Exists(fixtures)) {
    Console.Error.WriteLine(
        $"No fixtures at '{fixtures}'. Clone the reference — references/README.md has the command — "
        + "or pass the path to a Taffy checkout as the first argument."
    );

    return 1;
}

var version = ReadVersion(taffy);
Directory.CreateDirectory(output);
foreach (var stale in Directory.GetFiles(output, "*.xml")) {
    File.Delete(stale);
}

var refused = new List<RefusedFixture>();
var attributes = new SortedSet<string>(StringComparer.Ordinal);
var totals = new List<(string Category, int Fixtures, int Nodes)>();

foreach (var directory in Directory.GetDirectories(fixtures).OrderBy(path => path, StringComparer.Ordinal)) {
    var category = Path.GetFileName(directory);

    var vetted = Directory
        .GetFiles(directory, "*.xml")
        .OrderBy(path => path, StringComparer.Ordinal)
        .Select(path => CorpusVetter.Vet(path, category, refused))
        .OfType<VettedFixture>()
        .ToList();

    if (vetted.Count == 0) {
        continue;
    }

    foreach (var attribute in vetted.SelectMany(fixture => fixture.Attributes)) {
        attributes.Add(attribute);
    }

    File.WriteAllText(Path.Combine(output, category + ".xml"), Consolidate(category, version, vetted));
    totals.Add((category, vetted.Count, vetted.Sum(fixture => fixture.NodeCount)));
}

Console.WriteLine($"Taffy {version}");
foreach (var (category, count, nodes) in totals) {
    Console.WriteLine($"  {category,-10} {count,5} fixtures, {nodes,6} nodes");
}

Console.WriteLine($"  {"total",-10} {totals.Sum(total => total.Fixtures),5} fixtures, {totals.Sum(total => total.Nodes),6} nodes");
Console.WriteLine();
Console.WriteLine($"{attributes.Count} distinct style attributes: {string.Join(" ", attributes)}");

if (refused.Count > 0) {
    Console.WriteLine();
    Console.WriteLine($"{refused.Count} fixture(s) refused:");
    foreach (var fixture in refused) {
        Console.WriteLine($"  {fixture.Category}/{fixture.Name}: {fixture.Reason}");
    }
}

return 0;

static string ReadVersion(string taffy) {
    var manifest = Path.Combine(taffy, "Cargo.toml");
    if (!File.Exists(manifest)) {
        return "unknown";
    }

    // The first `version = "…"` in the manifest is the package's own.
    var line = File.ReadLines(manifest).FirstOrDefault(line => line.StartsWith("version = ", StringComparison.Ordinal));
    return line?.Split('"').ElementAtOrDefault(1) ?? "unknown";
}

/// <summary>
///     One file per category, each fixture's XML embedded verbatim.
/// </summary>
/// <remarks>
///     ⚠ <b>Verbatim, and consolidated, are both deliberate.</b> Verbatim because the fixture text
///     <i>is</i> the artefact — Yoga's fixtures had to be translated because they were C++, and every
///     translation step is a place for a bug that reads as a layout bug. Taffy's are already
///     language-neutral, so the honest move is to carry them unchanged and diff them against upstream
///     byte for byte. Consolidated because 5 500 loose files is a real cost to every clone, checkout
///     and IDE index, and a category is the smallest unit anyone reasons about anyway.
/// </remarks>
static string Consolidate(string category, string version, List<VettedFixture> fixtures) {
    var text = new StringBuilder();
    text.AppendLine("<!--");
    text.AppendLine($"    Taffy's {category} conformance fixtures, vetted and consolidated by Tools/Vixen.TaffyTestGen.");
    text.AppendLine();
    text.AppendLine($"    Source: taffy {version}, tests/xml/{category}, MIT licensed — see NOTICE.");
    text.AppendLine("    Each <test> below is one of Taffy's files, embedded verbatim. Taffy generates them from");
    text.AppendLine("    an HTML fixture laid out by Chrome-for-Testing, so every expected number is a real");
    text.AppendLine("    browser's answer. That is what makes this a conformance corpus rather than a recording");
    text.AppendLine("    of what some implementation happens to do.");
    text.AppendLine();
    text.AppendLine("    Do not edit: re-run the tool.");
    text.AppendLine("-->");
    text.AppendLine($"<corpus category=\"{category}\" count=\"{fixtures.Count}\" source=\"taffy {version}\">");

    foreach (var fixture in fixtures) {
        foreach (var line in fixture.Text.ReplaceLineEndings("\n").Split('\n')) {
            text.AppendLine(line.Length == 0 ? string.Empty : "  " + line);
        }
    }

    text.AppendLine("</corpus>");
    return text.ToString().ReplaceLineEndings("\n");
}
