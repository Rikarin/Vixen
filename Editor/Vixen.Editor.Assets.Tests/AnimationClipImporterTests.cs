// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation;
using Vixen.Core;
using Vixen.Core.IO;
using Vixen.Core.Reflection;
using Vixen.Core.Serialization;
using Vixen.Editor.Assets;
using Vixen.Editor.Assets.Animation;
using Xunit;

namespace Tests;

/// <summary>A <c>.vxanim</c> becomes something a game can play.</summary>
/// <remarks>
///     <para>
///         <b>What this closes.</b> An authored clip was carried forward as text, and the runtime
///         links no YAML parser — so nothing on the far side could read one and no game could load a
///         clip by address. The curves are sampled here now, once, at build time, and what ships is
///         <see cref="AnimationClipContent" />.
///     </para>
///     <para>
///         The assertions are about the <em>artefact</em> rather than about the parse, for
///         <c>MaterialImporterTests</c>' reason: that the bytes read back as a clip is the whole
///         claim, and a test of the binder is <c>Vixen.Core.Yaml</c>'s to have.
///     </para>
/// </remarks>
public sealed class AnimationClipImporterTests {
    const string Walk = """
        name: Walk
        duration: 1.0
        wrap: Loop
        targets:
          - target: LegLeft
            curves:
              - property: RotationX
                keys:
                  - time: 0.0
                    value: 0.0
                    mode: Linear
                  - time: 1.0
                    value: 0.5
                    mode: Linear
        events:
          - name: FootstepLeft
            time: 0.25
            int: 1
        """;

    [Fact]
    public void ItClaimsTheClipExtensionAndNothingElse() {
        var importer = new AnimationClipImporter();

        Assert.Equal("AnimationClipImporter", importer.Name);
        Assert.Equal([".vxanim"], importer.Extensions);
    }

    /// <summary>The artefact's type string names the type actually written.</summary>
    /// <remarks>
    ///     ⚠ <b>The assertion that would have caught a clip no runtime could load.</b> These bytes
    ///     were written under <c>"AnimationClip"</c> — the name of the <i>runtime</i> class, which is
    ///     not what the bytes are and which nothing could resolve. It never surfaced because no
    ///     project had ever loaded one. Asserted against the registry rather than a literal, because
    ///     a literal would have been copied from the same mistaken place.
    /// </remarks>
    [Fact]
    public void TheArtifactTypeIsTheContractOfWhatIsWritten() {
        Assert.True(TypeRegistry.TryGetByAlias(AnimationClipImporter.ClipType, out var descriptor));
        Assert.Equal(typeof(AnimationClipContent), descriptor.Type);
    }

    /// <summary>The join itself: curves in, sampled channels and events out.</summary>
    [Fact]
    public async Task AClipCompilesToAChunkThatReadsBack() {
        var result = await Import(Walk);

        var artifact = Assert.Single(result.Artifacts);
        Assert.Equal(AnimationClipImporter.ClipType, artifact.Type);
        Assert.Equal(SubAssetId.Main, artifact.SubAsset);

        var content = Serializer.Read<AnimationClipContent>(artifact.Content.ToArray());

        Assert.Equal("Walk", content.Name);
        Assert.Equal(WrapMode.Loop, content.Wrap);
        Assert.Equal(1f, content.Data.Duration);

        var channel = Assert.Single(content.Data.Channels);
        Assert.Equal("LegLeft", channel.Target);
        Assert.Equal(2, channel.RotationTimes.Length);

        var fired = Assert.Single(content.Events);
        Assert.Equal("FootstepLeft", fired.Name);
        Assert.Equal(0.25f, fired.Time);
        Assert.Equal(1, fired.Int);
    }

    /// <summary>The compiled clip poses a rig, which is the only thing any of this is for.</summary>
    [Fact]
    public async Task TheCompiledClipBakesAgainstASkeleton() {
        var content = Serializer.Read<AnimationClipContent>(
            Assert.Single((await Import(Walk)).Artifacts).Content.ToArray()
        );

        Assert.True(
            Skeleton.TryCreate(
                new() {
                    Name = "Rig",
                    Joints = [
                        new() { Name = "Root", Parent = -1 },
                        new() { Name = "LegLeft", Parent = 0 }
                    ]
                },
                out var skeleton,
                out var error
            ),
            error
        );

        var clip = content.Bake(skeleton!);

        Assert.Equal(1f, clip.Duration);
        Assert.Equal(0, clip.UnresolvedChannels);
    }

    /// <summary>A clip with no skeleton is still samplable, which is half of what the format is for.</summary>
    [Fact]
    public async Task ANamedTargetSamplesWithoutARig() {
        var content = Serializer.Read<AnimationClipContent>(
            Assert.Single((await Import(Walk)).Artifacts).Content.ToArray()
        );

        Assert.True(content.TrySample("LegLeft", 0f, out var start));
        Assert.True(content.TrySample("LegLeft", 1f, out var end));
        Assert.False(content.TrySample("ArmLeft", 0f, out _));

        // The authored keys are linear from 0 to 0.5 on X, and a quaternion built from four
        // independently sampled components is normalised on the way out — so the ends are what the
        // author wrote and the assertion is that they differ, not what they are.
        Assert.NotEqual(start.Rotation, end.Rotation);
    }

    /// <summary>A duration of zero is refused rather than shipped.</summary>
    /// <remarks>
    ///     It divides by zero the first time anything maps a phase onto it, and the artefact that
    ///     would have been written is one every consumer has to defend against.
    /// </remarks>
    [Fact]
    public async Task AClipOfNoLengthIsAnError() {
        var result = await Import("name: Broken\nduration: 0\n");

        Assert.Empty(result.Artifacts);
        Assert.Contains(result.Diagnostics, entry => entry.Severity == ImportSeverity.Error);
    }

    /// <summary>
    ///     ⚠ Metadata this build does not understand survives the round trip instead of being deleted.
    /// </summary>
    /// <remarks>
    ///     <b>The contract the open half of the format rests on.</b> The binder ignores an unknown key
    ///     — deliberately, so an older editor can open a newer project — which means markup somebody
    ///     spent a day authoring is silently dropped the next time the file is saved. The reserved
    ///     block is what makes "unrecognised" mean "carried" rather than "lost", and this asserts it
    ///     at both ends: the authored document round-trips, and the compiled artefact still carries
    ///     the block for a runtime that does know the kind.
    /// </remarks>
    [Fact]
    public async Task AnUnknownExtensionSurvivesBothEnds() {
        const string Authored = """
            name: Tagged
            duration: 1.0
            extensions:
              somethingThisBuildHasNeverHeardOf:
                weight: 0.5
                chain:
                  - upperarm_r
                  - hand_r
            """;

        var clip = AnimationClipAsset.FromYaml(Authored);
        var block = Assert.Single(clip.Extensions);

        Assert.Equal("somethingThisBuildHasNeverHeardOf", block.Key);

        // Re-read rather than compared as text: the writer is entitled to its own indentation, and
        // what has to survive is the data, not the whitespace.
        var reopened = AnimationClipAsset.FromYaml(clip.ToYaml());
        var carried = Assert.Single(reopened.Extensions);

        Assert.Equal(block.Key, carried.Key);
        Assert.Contains("upperarm_r", YamlWriterText(carried.Value));

        var content = Serializer.Read<AnimationClipContent>(
            Assert.Single((await Import(Authored)).Artifacts).Content.ToArray()
        );

        Assert.Contains("upperarm_r", Assert.Single(content.Extensions).Value);
    }

    /// <summary>A face driving many shapes off one node is a correct file, not a duplicated curve.</summary>
    /// <remarks>
    ///     ⚠ <b>The check used to group by the property alone, and would have greeted the first
    ///     facial clip anybody wrote with a warning about it.</b> Every weight curve on a node is
    ///     <c>Weight</c>, so twenty shapes read as nineteen duplicates and "the first of each is the
    ///     one that is sampled" — a sentence describing a correct file, and the one thing worse than
    ///     no diagnostic. The pair <c>(Property, Shape)</c> is what identifies a curve.
    /// </remarks>
    [Fact]
    public async Task ManyWeightCurvesOnOneNodeAreNotDuplicates() {
        var result = await Import(Face);

        Assert.Single(result.Artifacts);
        Assert.DoesNotContain(result.Diagnostics, entry => entry.Message.Contains("more than one curve", StringComparison.Ordinal));

        var content = Serializer.Read<AnimationClipContent>(result.Artifacts[0].Content.ToArray());
        var weighted = content.Data.Channels.Where(channel => channel.WeightTimes.Length > 0).ToArray();

        Assert.Equal(2, weighted.Length);
        Assert.Equal(["jawOpen", "browRaise"], weighted.Select(channel => channel.Shape));

        // And the same file with a shape genuinely written twice still is one.
        var twice = await Import(Face.Replace("shape: browRaise", "shape: jawOpen", StringComparison.Ordinal));

        Assert.Contains(twice.Diagnostics, entry => entry.Message.Contains("Weight 'jawOpen'", StringComparison.Ordinal));
    }

    /// <summary>
    ///     ⚠ A weight curve that names no shape is an error, because it is the one mistake this
    ///     format makes easy and the one that says nothing.
    /// </summary>
    /// <remarks>
    ///     A weight is bound by the shape's name — the ordinal a source file used is not the one the
    ///     mesh ended up with — so a curve with no name binds to nothing. It would import, ship, play,
    ///     and hold a face perfectly still: the exact failure a name-bound channel exists to make
    ///     impossible, arrived at by leaving the name out.
    /// </remarks>
    [Fact]
    public async Task AWeightCurveThatNamesNoShapeIsAnError() {
        var result = await Import(Face.Replace("        shape: jawOpen\n", string.Empty, StringComparison.Ordinal));

        Assert.Empty(result.Artifacts);

        Assert.Contains(
            result.Diagnostics,
            entry => entry.Severity == ImportSeverity.Error
                && entry.Message.Contains("names no blend shape", StringComparison.Ordinal)
        );
    }

    /// <summary>A clip that drives two blend shapes off one mesh node and nothing else.</summary>
    const string Face = """
        version: 2
        name: Face
        duration: 2.0
        targets:
          - target: Head
            curves:
              - property: Weight
                shape: jawOpen
                keys:
                  - { time: 0.0, value: 0.0, mode: Linear }
                  - { time: 2.0, value: 1.0, mode: Linear }
              - property: Weight
                shape: browRaise
                keys:
                  - { time: 0.0, value: 0.25, mode: Linear }
        """;

    static string YamlWriterText(Vixen.Core.Yaml.YamlNode node) => Vixen.Core.Yaml.YamlWriter.Write(node);

    static async Task<ImportResult> Import(string text) {
        var path = new VirtualPath("/Assets/walk.vxanim");
        var files = new MemoryFileProvider();
        files.Seed(path, text);

        var importer = new AnimationClipImporter();
        var context = new ImportContext(AssetId.New(), path, importer.CreateSettings(), files, importer.Name, "Windows");

        return await importer.ImportAsync(context, TestContext.Current.CancellationToken);
    }
}
