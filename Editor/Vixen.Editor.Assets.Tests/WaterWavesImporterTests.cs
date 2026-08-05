// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.IO;
using Vixen.Core.Serialization;
using Vixen.Editor.Assets.Water;
using Vixen.Water;
using Xunit;

namespace Vixen.Editor.Assets.Tests;

/// <summary>The one asset kind water adds — [docs/plan/35 § D6], and its importer.</summary>
/// <remarks>
///     ⚠ <b>What is asserted throughout is the artefact read back the way the <em>runtime</em> reads
///     it</b>, rather than that the import succeeded. A sea state that imports and produces a chunk
///     nothing can deserialise is a zone that falls back to its inline spectrum and draws convincing
///     water somebody never authored — the failure mode <c>WaterZoneSystem.UnresolvedWaves</c> exists
///     to make visible, and the one this suite exists to prevent shipping.
/// </remarks>
public sealed class WaterWavesImporterTests {
    [Fact]
    public void ItClaimsTheExtensionAndNamesTheTypeItWrites() {
        var importer = new WaterWavesImporter();

        Assert.Equal("WaterWavesImporter", importer.Name);
        Assert.Contains(".vxwaves", importer.Extensions);
        Assert.Equal("WaterWavesAsset", WaterWavesImporter.WavesType);
    }

    /// <summary>And that the build's own registry hands a <c>.vxwaves</c> to it.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The gap every other test in this file stepped over.</b> They construct the importer
    ///         and drive it, which asserts that it works and nothing about whether anything reaches it —
    ///         and <c>[Importer]</c> is a declaration the registry does not scan for.
    ///         <see cref="BuiltInImporters.Create()" /> is a hand-written list, water was not in it, and
    ///         a <c>.vxwaves</c> in a project fell through to <c>RawImporter</c>: a byte blob under a
    ///         name no runtime reader resolves, with no error anywhere, which is
    ///         <c>WaterZoneSystem.UnresolvedWaves</c> and a zone drawing its inline spectrum.
    ///     </para>
    ///     <para>
    ///         The importer that claimed it is what is asserted, and not that an artefact appeared —
    ///         <c>RawImporter</c> produces one of those too.
    ///     </para>
    /// </remarks>
    [Fact]
    public void TheBuildsOwnRegistryHandsAWavesFileToIt() {
        // ⚠ A contribution set of its own, not ImporterContributions.Default: the default is
        // process-wide and ImporterContributionTests mutates it, so reading it here would race.
        var registry = BuiltInImporters.Create(new ImporterContributions());

        Assert.True(registry.TryGetForFile("Assets/Seas/northsea.vxwaves", out var importer));
        Assert.Equal("WaterWavesImporter", importer.Name);
        Assert.IsType<WaterWavesImporter>(importer);
    }

    /// <summary>A sea state is compiled to the record <c>AssetWaterSource</c> opens.</summary>
    /// <remarks>
    ///     ⚠ <b>The trap the grass and foliage chunks closed, one asset kind over.</b> A game does not
    ///     carry the YAML dialect — that is the editor's format — so a text chunk here is a sea state
    ///     that quietly never arrives. Asserted by reading the artefact back the way the runtime does.
    /// </remarks>
    [Fact]
    public async Task AWavesChunkIsTheSerializedRecord() {
        var (_, result) = await Import(
            "northsea.vxwaves",
            """
            name: North Sea
            spectrum:
              windDirection: 1.2
              windSpeed: 18
              directionalSpread: 0.7
              minimumWavelength: 6
              maximumWavelength: 120
              wavelengthFalloff: 1
              amplitudeScale: 1
              steepness: 0.6
              seed: 7
              count: ThirtyTwo
            """
        );

        Assert.True(result.Succeeded);

        var artefact = Assert.Single(result.Artifacts);
        var written = Serializer.Read<WaterWavesAsset>(artefact.Content.Span);

        Assert.Equal("WaterWavesAsset", artefact.Type);
        Assert.Equal("North Sea", written.Name);
        Assert.Equal(18f, written.Spectrum.WindSpeed);
        Assert.Equal(120f, written.Spectrum.MaximumWavelength);
        Assert.Equal(WaterWaveCount.ThirtyTwo, written.Spectrum.Count);
        Assert.Equal(7u, written.Spectrum.Seed);
    }

    /// <summary>An unnamed sea state takes the file's own stem, because a zone names it by name.</summary>
    /// <remarks>
    ///     ⚠ <b>Without this an author who left the field blank gets an asset nothing can refer to.</b>
    ///     A zone naming the empty string never reaches the source at all — see
    ///     <c>WaterZoneSystem</c> — so the symptom is not "asset not found", it is water that looks
    ///     right and is the inline spectrum, with no diagnostic anywhere.
    /// </remarks>
    [Fact]
    public async Task AnUnnamedSeaStateTakesTheFilesOwnName() {
        var (_, result) = await Import("harbour.vxwaves", "spectrum:\n  windSpeed: 4\n  minimumWavelength: 2\n  maximumWavelength: 20\n  count: Eight\n");

        Assert.True(result.Succeeded);

        var written = Serializer.Read<WaterWavesAsset>(Assert.Single(result.Artifacts).Content.Span);

        Assert.Equal("harbour", written.Name);
    }

    /// <summary>
    ///     ⚠ A spectrum the evaluator refuses is an error, where its terrain siblings would warn.
    /// </summary>
    /// <remarks>
    ///     A foliage type with no mesh is an author part-way through and warns. A wavelength range
    ///     that runs backwards has no defensible partial state: <see cref="WaterWaveSpectrum.Generate" />
    ///     throws on it, so a build that accepted it produces a zone rendering no waves at all — which
    ///     reads as the water being broken rather than as the file being wrong.
    /// </remarks>
    [Fact]
    public async Task ASpectrumThatCannotBeSummedFailsTheImport() {
        var (_, result) = await Import(
            "backwards.vxwaves",
            """
            name: Backwards
            spectrum:
              minimumWavelength: 60
              maximumWavelength: 4
              count: Sixteen
            """
        );

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            message => message.Severity == ImportSeverity.Error
                && message.Message.Contains("runs backwards", StringComparison.Ordinal)
        );
    }

    /// <summary>A wave count that is not one of the three permutations is refused with the reason.</summary>
    [Fact]
    public async Task AWaveCountThatIsNotAPermutationIsRefused() {
        var (_, result) = await Import(
            "twenty.vxwaves",
            """
            name: Twenty
            spectrum:
              minimumWavelength: 4
              maximumWavelength: 60
              count: 20
            """
        );

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            message => message.Message.Contains("quantised wave counts", StringComparison.Ordinal)
        );
    }

    /// <summary>The two seas that are legal, load, and are almost never what anybody meant.</summary>
    /// <remarks>
    ///     ⚠ <b>Warnings and not errors, because both are things somebody may want</b> — a dead calm
    ///     is a legitimate lake and a perfectly ordered swell is a legitimate stylisation. What they
    ///     have in common is that they are also what a half-filled file looks like, and the symptom
    ///     ("the water is flat", "the water looks like corrugated iron") sends people to the renderer.
    /// </remarks>
    [Fact]
    public async Task AFlatSeaAndAnUnspreadOneAreWarnedAbout() {
        var (_, calm) = await Import(
            "mirror.vxwaves",
            """
            name: Mirror
            spectrum:
              windSpeed: 0
              directionalSpread: 0.6
              minimumWavelength: 4
              maximumWavelength: 60
              count: Eight
            """
        );

        Assert.True(calm.Succeeded);
        Assert.Contains(calm.Diagnostics, message => message.Message.Contains("mirror", StringComparison.Ordinal));
        Assert.DoesNotContain(calm.Diagnostics, message => message.Severity == ImportSeverity.Error);

        var (_, iron) = await Import(
            "swell.vxwaves",
            """
            name: Swell
            spectrum:
              windSpeed: 8
              directionalSpread: 0
              minimumWavelength: 4
              maximumWavelength: 60
              count: Eight
            """
        );

        Assert.True(iron.Succeeded);
        Assert.Contains(iron.Diagnostics, message => message.Message.Contains("corrugated iron", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BrokenYamlIsReportedRatherThanThrown() {
        var (_, result) = await Import("broken.vxwaves", "name: [unclosed\n");

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, message => message.Severity == ImportSeverity.Error);
    }

    static async Task<(ImportContext Context, ImportResult Result)> Import(string name, string text) {
        var path = new VirtualPath("/Assets/" + name);
        var files = new MemoryFileProvider();

        files.Seed(path, text);

        var importer = new WaterWavesImporter();
        var context = new ImportContext(
            AssetId.New(),
            path,
            importer.CreateSettings(),
            files,
            importer.Name,
            "Windows"
        );

        return (context, await importer.ImportAsync(context, TestContext.Current.CancellationToken));
    }
}
