// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Vixen.Core;
using Vixen.Core.IO;
using Xunit;

namespace Vixen.Editor.Assets.Tests;

/// <summary>
///     What a <c>.vxplacement</c> does today, pinned — because today it does the thing
///     <see cref="EveryAttributedImporterTests" /> exists to prevent, and that gate cannot see it.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>A tripwire, not an endorsement.</b> Every assertion below states a defect. It is here
///         because the defect is otherwise <em>completely invisible</em>: the import succeeds, no
///         diagnostic is raised anywhere, and the only trace is a chunk labelled <c>"Blob"</c> that no
///         runtime reader resolves. That is the failure <c>.vxwaves</c>, <c>.cube</c>, <c>.vxbt</c>,
///         <c>.vxgoap</c>, <c>.vxquery</c>, <c>.vxutility</c> and <c>.dds</c> each shipped with, and
///         it is the one thing about this format nothing in the suite said out loud.
///     </para>
///     <para>
///         ⚠ <b>Why the existing gate cannot catch it.</b>
///         <c>EveryAttributedImporterTests.EveryImporterInThisAssemblyIsRegistered</c> walks the
///         <c>[Importer]</c> types in this assembly and fails on one absent from
///         <see cref="BuiltInImporters.Create()" />. It catches an importer that <em>exists</em> and is
///         unlisted. There is no <c>.vxplacement</c> importer type at all, so the gate has nothing to
///         walk — the extension is simply unclaimed, and <see cref="RawImporter" /> takes it the way it
///         takes a CSV.
///     </para>
///     <para>
///         ⚠ <b>Why there is no importer, which is a blocker and not an oversight.</b> The extension is
///         declared by <c>PlacementWeights.Extension</c> in <c>Live/Vixen.Live.Orchestrator</c>, and
///         <c>CheckArchitecture</c> refuses <c>Editor/</c> → <c>Live/</c> — <c>Live/</c> is shipped and
///         operated, <c>Editor/</c> is a developer tool. No shipped project in the tree may reference
///         both this assembly and <c>Vixen.Live.Orchestrator</c>, so an importer of the shape
///         <c>UtilitySetImporter</c> has (which names its runtime record and parses straight into it)
///         cannot be written here. See <c>docs/overview.md</c> § 1.13 for the two options and why
///         neither has been picked.
///     </para>
///     <para>
///         <b>Delete this file when one of them is.</b> It will fail the moment an importer claims the
///         extension, which is the point: whoever adds it is told that the recorded behaviour changed
///         rather than discovering later that the recording went stale.
///     </para>
/// </remarks>
public sealed class UnclaimedPlacementExtensionTests {
    /// <summary>The extension, spelled here rather than referenced.</summary>
    /// <remarks>
    ///     ⚠ <b>Not <c>PlacementWeights.Extension</c>, and the duplication is the subject.</b> This
    ///     assembly cannot name that constant — that is the whole finding — so the literal here is the
    ///     honest expression of the gap, and the one place in <c>Editor/</c> that spells it.
    /// </remarks>
    const string Extension = ".vxplacement";

    /// <summary>What a game authors, per <c>Live/Vixen.Live.Orchestrator/README.md</c>.</summary>
    const string Sample = """
                          party: 12000
                          locale: 900
                          maxAge: 04:00:00
                          """;

    /// <summary>Nothing claims it, so the fallback does.</summary>
    /// <remarks>
    ///     ⚠ <b>Asserted as "the fallback claimed it", not as "nothing claimed it".</b>
    ///     <c>TryGetForFile</c> returns true either way — <see cref="RawImporter" /> takes anything —
    ///     so a test that only checked the boolean would pass on a healthy registry and on this one
    ///     alike, which is exactly how the six previous instances of this defect went unnoticed.
    /// </remarks>
    [Fact]
    public void TheFallbackClaimsIt() {
        // ⚠ A contribution set of its own, not ImporterContributions.Default: the default is
        // process-wide and ImporterContributionTests mutates it, so reading it here would race.
        var registry = BuiltInImporters.Create(new ImporterContributions());

        Assert.True(registry.TryGetForFile("Assets/Maps/queensdale" + Extension, out var importer));
        Assert.IsType<RawImporter>(importer);
    }

    /// <summary>And the import succeeds as a byte blob — no longer silently.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The assertions together are the failure, and one of them has been fixed.</b>
    ///         Succeeded, so no build stops; and the type is <c>"Blob"</c>, which no typed reader
    ///         resolves — so an address bound to this deserialises into nothing whatever asks for it.
    ///         A green build and an address that binds to nothing.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What changed: <c>Assert.Empty(result.Diagnostics)</c> used to be the third.</b>
    ///         The extension is now in <c>UnimportedFormats</c>, so the fallback says out loud that
    ///         nothing imports it — see <see cref="UnimportedFormatTests" />. The gap is unchanged and
    ///         the invisibility is not, which is why this file stays and its middle assertion is
    ///         inverted rather than deleted.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And it is one step worse here than it was for <c>.vxwaves</c>.</b> There, a runtime
    ///         reader existed and would have resolved the right type had the importer been listed; the
    ///         zone fell back to its inline spectrum. Here nothing anywhere loads a
    ///         <c>.vxplacement</c> through the asset system at all, so the blob is not merely
    ///         unreadable — it is unasked for.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task ItImportsAsAByteBlobAndSaysSo() {
        var path = new VirtualPath("/Assets/Maps/queensdale" + Extension);
        var files = new MemoryFileProvider();

        files.Seed(path, Sample);

        var registry = BuiltInImporters.Create(new ImporterContributions());

        Assert.True(registry.TryGetForFile(path.ToString(), out var importer));

        var context = new ImportContext(
            AssetId.New(),
            path,
            importer.CreateSettings(),
            files,
            importer.Name,
            "Windows"
        );

        var result = await importer.ImportAsync(context, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);

        var said = Assert.Single(result.Diagnostics);

        Assert.Equal(ImportSeverity.Warning, said.Severity);
        Assert.Contains(Extension, said.Message, StringComparison.Ordinal);

        var artefact = Assert.Single(result.Artifacts);

        Assert.Equal("Blob", artefact.Type);

        // The bytes are the author's YAML, verbatim and uninterpreted — nothing checked that the
        // document is even a mapping, let alone that `party` is a term this format has.
        Assert.Equal(Sample, Encoding.UTF8.GetString(artefact.Content.Span));
    }
}
