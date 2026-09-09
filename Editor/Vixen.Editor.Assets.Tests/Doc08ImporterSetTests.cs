// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using Xunit;

namespace Vixen.Editor.Assets.Tests;

/// <summary>
///     Doc 08's "Importer set for 1.0" table and the code agree about which of its importers exist.
/// </summary>
/// <remarks>
///     <para>
///         <b>What this is the instrument for.</b> Five of the fourteen rows name an importer this
///         tree does not have, and for a long time the only consequence was that a <c>.ttf</c> or a
///         <c>.cs</c> under <c>Assets/</c> fell to <see cref="RawImporter" /> and became an anonymous
///         byte blob with no diagnostic anywhere. <c>UnimportedFormats</c> closed that half; this
///         closes the half nothing could see, which is the table drifting from the list. A promise in
///         a plan document that nobody can check is how five of them survived a 1.0 table for months.
///     </para>
///     <para>
///         ⚠ <b>The decision this pins is recorded rather than invented here.</b> Doc 08 now says
///         which of the five are MSBuild's (shaders, <c>.vxml</c>, <c>.vcss</c>) and which are still
///         owed to the asset database (a font's *bake*, script metadata). What this test asserts is
///         only that the two documents cannot disagree: a row whose importer does not exist must have
///         a sentence for every extension it claims, and an importer somebody writes tomorrow has to
///         take its row's sentence out in the same change — which is what
///         <c>UnimportedFormatTests.EveryExtensionTheTableNamesStillFallsThroughToTheFallback</c>
///         says from the other side.
///     </para>
///     <para>
///         ⚠ <b>The row count is asserted, because a loop that checks rows proves nothing about a
///         table it failed to find.</b> A renamed heading, a reformatted table or a moved file would
///         otherwise leave this passing on an empty list for ever — which is this repository's
///         standing example of an instrument that reports success on the day it does not run.
///     </para>
///     <para>
///         ⚠ <b>The document is found by walking up from the test binary</b>, never down from a
///         repository root: <c>.claude/worktrees</c> holds a whole checkout per parallel agent, and a
///         search from above reads another branch's copy. Walking up also survives CI, where
///         <c>DeterministicSourcePaths</c> rewrites every <c>[CallerFilePath]</c> to <c>/_/</c>.
///     </para>
/// </remarks>
public sealed class Doc08ImporterSetTests {
    /// <summary>How many rows the table has, so a table that was not found cannot pass.</summary>
    const int Rows = 14;

    [Fact]
    public void EveryImporterTheTableNamesEitherExistsOrHasASentenceForEveryExtensionItClaims() {
        var rows = Table();

        Assert.Equal(Rows, rows.Count);

        List<string> missing = [];

        foreach (var (importer, extensions) in rows) {
            if (Exists(importer)) {
                continue;
            }

            missing.Add(importer);

            foreach (var extension in extensions) {
                Assert.True(
                    UnimportedFormats.Extensions.Contains("." + extension, StringComparer.OrdinalIgnoreCase),
                    $"docs/plan/08 promises a {importer} for '.{extension}' and there is none, and "
                    + "UnimportedFormats says nothing about it — so a file with that extension imports as an "
                    + "anonymous blob. Add a row to UnimportedFormats, or write the importer and delete the "
                    + "table's row."
                );
            }
        }

        // ⚠ Exact, not "at least": an importer that lands without its row and sentence being removed
        // leaves a plan document promising work that is done and a fallback explaining a file it no
        // longer claims. Both are worse than the gap they describe.
        Assert.Equal(
            ["FontImporter", "ShaderImporter", "MarkupImporter", "StyleImporter", "ScriptImporter"],
            missing
        );
    }

    /// <summary>Whether a type of that name is an importer this build has.</summary>
    /// <remarks>
    ///     Matched on the name before the arity tick, so the table's <c>AssetImporter</c> row finds
    ///     <see cref="AssetImporter{TSettings}" />.
    /// </remarks>
    static bool Exists(string importer) {
        foreach (var type in typeof(RawImporter).Assembly.GetTypes()) {
            var name = type.Name;
            var tick = name.IndexOf('`', StringComparison.Ordinal);

            if (string.Equals(tick < 0 ? name : name[..tick], importer, StringComparison.Ordinal)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>The table's rows, as an importer name and the extensions its second column names.</summary>
    /// <remarks>
    ///     The extension column is prose in three rows — <c>blend¹</c>, a tick, a parenthesis — so a
    ///     token is kept only when it is plainly an extension. That is deliberate leniency in the
    ///     half that does not decide anything: every row whose importer exists is skipped before the
    ///     extensions are read at all, and the five that are read name nothing but extensions.
    /// </remarks>
    static List<(string Importer, string[] Extensions)> Table() {
        var lines = File.ReadAllLines(Document());
        List<(string, string[])> rows = [];
        var inside = false;

        foreach (var line in lines) {
            if (line.StartsWith("| Importer | Extensions |", StringComparison.Ordinal)) {
                inside = true;
                continue;
            }

            if (!inside) {
                continue;
            }

            if (!line.StartsWith('|')) {
                break;
            }

            var cells = line.Split('|');

            if (cells.Length < 4 || cells[1].Trim().Trim('-').Length == 0) {
                continue;
            }

            var importer = cells[1].Trim().Trim('`');

            rows.Add((
                importer,
                [
                    .. cells[2]
                        .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Where(token => Regex.IsMatch(token, "^[a-z0-9]+$"))
                ]
            ));
        }

        return rows;
    }

    /// <summary>Where doc 08 is, found by walking up from the test binary.</summary>
    static string Document() {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent) {
            var candidate = Path.Combine(
                directory.FullName,
                "docs",
                "plan",
                "08-asset-pipeline-and-addressables.md"
            );

            if (File.Exists(candidate)) {
                return candidate;
            }
        }

        Assert.Fail($"docs/plan/08-asset-pipeline-and-addressables.md was not found above '{AppContext.BaseDirectory}'.");
        return string.Empty;
    }
}
