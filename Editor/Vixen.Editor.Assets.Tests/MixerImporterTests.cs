// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio;
using Vixen.Audio.Assets;
using Vixen.Audio.Devices;
using Vixen.Core;
using Vixen.Core.IO;
using Vixen.Core.Serialization;
using Vixen.Editor.Assets;
using Vixen.Editor.Assets.Audio;
using Xunit;

namespace Tests;

/// <summary>The join #473 says is the only missing piece for a <c>.vxmixer</c>.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Both halves existed and did not meet.</b> <c>AudioEngine.LoadMixer(MixerAsset)</c>
///         has been tested since the mixing layer was written and <c>AudioMixerDocument</c> authors
///         the YAML, but nothing claimed the extension — so the file fell to <c>RawImporter</c> and
///         shipped as a chunk called <c>Blob</c>. A sound designer's mix could not be loaded by a
///         game, and the only trace was the note the fallback started writing in #318.
///     </para>
///     <para>
///         ⚠ <b>What is asserted is the chunk a game reads, not that the import succeeded.</b> The
///         bytes go back through <c>Serializer.Read&lt;MixerAsset&gt;</c> — which is what
///         <c>AssetManager</c> does — and then into a real <c>AudioEngine.LoadMixer</c>. An importer
///         that wrote the authored YAML under the right type name would pass a test that only read
///         the artefact's <c>Type</c>, and would ship a document no runtime links a parser for.
///     </para>
/// </remarks>
public sealed class MixerImporterTests {
    const string Authored =
        """
        buses:
          - name: Music
            parent: Master
            gainDb: -6
          - name: SFX
            parent: Master
        snapshots:
          - name: Underwater
            buses:
              - name: Music
                gainDb: -12
        """;

    [Fact]
    public async Task AMixerIsCompiledIntoTheChunkTheRuntimeReads() {
        var result = await Import(Authored);

        Assert.True(result.Succeeded);

        var artifact = Assert.Single(result.Artifacts);

        // ⚠ The `[DataContract]` alias, because `ImportPipeline.TypeIdOf` resolves this through the
        // type registry and stamps `ImportedArtifact` on anything it cannot — which is a chunk no
        // reader in a game claims, about content the build declared good.
        Assert.Equal("MixerAsset", artifact.Type);

        var asset = Serializer.Read<MixerAsset>(artifact.Content.ToArray());

        Assert.Equal(["Music", "SFX"], asset.Buses.Select(bus => bus.Name));
        Assert.Equal(-6f, asset.Buses[0].GainDb);
        Assert.Equal("Underwater", Assert.Single(asset.Snapshots).Name);
    }

    /// <summary>
    ///     ⚠ <b>And the chunk is one <c>AudioEngine.LoadMixer</c> accepts, which is the half a
    ///     serialization round trip cannot see.</b> The two ends of #473 are an importer and a loader
    ///     that were written years apart; a test that stopped at the record would not notice that
    ///     what came out was not what the loader takes.
    /// </summary>
    [Fact]
    public async Task TheCompiledChunkLoadsIntoARealAudioEngine() {
        var result = await Import(Authored);
        var asset = Serializer.Read<MixerAsset>(Assert.Single(result.Artifacts).Content.ToArray());

        using var backend = new NullAudioBackend();

        using var engine = new AudioEngine(
            backend.OpenDevice(new AudioDeviceOptions()),
            new AudioEngineOptions { StreamOnOwnThread = false }
        );

        // Empty is the good case: every send, effect and snapshot resolved against a real mixer.
        Assert.Empty(engine.LoadMixer(asset));
        Assert.NotNull(engine.Snapshots);
    }

    /// <summary>A document that is not a mixer fails the import rather than shipping as a mixer.</summary>
    /// <remarks>
    ///     ⚠ <b>The predicate's other half.</b> An importer that wrote whatever bound would report
    ///     success on a text file, and the artefact would be an empty <c>MixerAsset</c> — a mix with
    ///     no buses, which is silence a game cannot tell from a mix nobody authored.
    /// </remarks>
    [Fact]
    public async Task ADocumentThatIsNotAMappingIsRefused() {
        var result = await Import("- not\n- a\n- mixer\n");

        Assert.False(result.Succeeded);
        Assert.Empty(result.Artifacts);
        Assert.Contains(result.Diagnostics, entry => entry.Severity == ImportSeverity.Error);
    }

    /// <summary>And a misspelled field is said out loud, like every other text-binding importer's.</summary>
    [Fact]
    public async Task AMisspelledFieldIsWarnedAbout() {
        var result = await Import("buses:\n  - name: Music\n    gaindB: -6\n    gian: -6\n");

        Assert.Contains(
            result.Diagnostics,
            entry => entry.Severity == ImportSeverity.Warning
                && entry.Message.Contains("gian", StringComparison.Ordinal)
                && entry.Message.Contains("a mixer", StringComparison.Ordinal)
        );
    }

    static async Task<ImportResult> Import(string text) {
        var importer = new MixerImporter();
        var source = new VirtualPath("/Assets/Game.vxmixer");
        var files = new MemoryFileProvider();

        files.Seed(source, text);

        var context = new ImportContext(
            AssetId.New(),
            source,
            importer.CreateSettings(),
            files,
            importer.Name,
            "Windows"
        );

        return await importer.ImportAsync(context, TestContext.Current.CancellationToken);
    }
}
