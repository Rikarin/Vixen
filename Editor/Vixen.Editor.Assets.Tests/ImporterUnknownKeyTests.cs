// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation.Moves;
using Vixen.Core;
using Vixen.Core.IO;
using Vixen.Core.Serialization;
using Vixen.Editor.Assets;
using Vixen.Editor.Assets.Animation;
using Vixen.Editor.Assets.Compositors;
using Vixen.Editor.Assets.Materials;
using Vixen.Rendering.Materials;
using Xunit;

namespace Tests;

/// <summary>A key an importer did not read is said out loud, whichever importer it was.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The defect these hold is one line repeated eleven times, which is why the fix is one
///         method.</b> <c>YamlSerializer.Parse</c> drops a key that matched no member unless the
///         caller hands it <c>OnUnknownKey</c>, and only <c>NetworkRulesImporter</c> ever did. So
///         <c>shadr: GBuffer</c> in a <c>.vxmat</c> bound to nothing, the material imported clean,
///         and the mesh drew with <c>ForwardPlus</c> — a wrong picture with no diagnostic anywhere.
///         <see cref="ImportContext.BindYaml{T}" /> is the seam every text-binding importer now goes
///         through.
///     </para>
///     <para>
///         <b>Each format is asserted twice on purpose.</b> The typo half says the warning fires; the
///         well-spelled half says it fires for a reason. Without the second, a warning that fired on
///         every field of every correct file would pass the first — and a warning authors learn to
///         scroll past is worth less than the silence it replaced.
///     </para>
///     <para>
///         ⚠ <b>And the consequence is asserted, not only the message.</b> A test that matched the
///         sentence would pass against an importer that warned and then bound the typo anyway; what
///         makes the warning true is that the field really is on its default.
///     </para>
/// </remarks>
public sealed class ImporterUnknownKeyTests {
    [Fact]
    public async Task AMisspelledMaterialFieldIsWarnedAboutRatherThanDroppedInSilence() {
        var result = await ImportMaterial("shadr: GBuffer\nshading: StandardShading\n");

        Assert.True(result.Succeeded);

        var content = Serializer.Read<MaterialContent>(Assert.Single(result.Artifacts).Content.ToArray());

        Assert.Equal("ForwardPlus", content.Shader);

        Assert.Contains(
            result.Diagnostics,
            entry => entry.Severity == ImportSeverity.Warning
                && entry.Message.Contains("shadr", StringComparison.Ordinal)
                && entry.Message.Contains("a material", StringComparison.Ordinal)
        );
    }

    /// <summary>A material that spells every field right says nothing, so the warning means something.</summary>
    [Fact]
    public async Task AMaterialThatSpellsEveryFieldRightWarnsAboutNoField() {
        var result = await ImportMaterial(
            """
            version: 1
            shader: ForwardPlus
            shading: StandardShading
            features:
              - !MetalRoughness
                baseColor: 0.8 0.2 0.1
                metalness: 1
                roughness: 0.25
              - !NormalMap
                strength: 0.5
            """
        );

        Assert.True(result.Succeeded);
        Assert.DoesNotContain(result.Diagnostics, entry => entry.Severity == ImportSeverity.Warning);
    }

    /// <summary>
    ///     ⚠ A misspelled knob in a frame document is the sharpest case of all: every one of them has
    ///     a legitimate-looking default.
    /// </summary>
    /// <remarks>
    ///     <c>resources: []</c> and <c>resources</c> misspelled produce the same document to a binder
    ///     that says nothing, and the difference between them is a frame whose passes write into
    ///     targets nobody declared.
    /// </remarks>
    [Fact]
    public async Task AMisspelledCompositorFieldIsWarnedAbout() {
        var result = await ImportCompositor("version: 2\nresorces: []\n");

        Assert.Contains(
            result.Diagnostics,
            entry => entry.Severity == ImportSeverity.Warning
                && entry.Message.Contains("resorces", StringComparison.Ordinal)
                && entry.Message.Contains("a compositor", StringComparison.Ordinal)
        );
    }

    [Fact]
    public async Task ACompositorThatSpellsEveryFieldRightWarnsAboutNoField() {
        var result = await ImportCompositor(
            """
            version: 2
            stages: []
            resources: []
            buffers: []
            """
        );

        Assert.DoesNotContain(result.Diagnostics, entry => entry.Severity == ImportSeverity.Warning);
    }

    /// <summary>
    ///     And the same through <c>ShapeYaml.ReadAsync</c>, which five importers share.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>That helper is why the fix could not be "the two sharpest importers".</b> Move sets,
    ///     proxy shape sets, shape vocabularies, priority ladders, constraint templates and harness
    ///     plans all read their document through one generic method — so one call site there is six
    ///     formats, and a fix applied importer by importer would have missed five of them without
    ///     anybody noticing which.
    /// </remarks>
    [Fact]
    public async Task AMisspelledMoveSetFieldIsWarnedAboutThroughTheSharedReader() {
        var result = await ImportMoveSet(
            """
            name: locomotion
            entries:
              - name: walk
                clip: Assets/Anim/Walk.vxanim
                speeed: 1.4
            """
        );

        Assert.True(result.Succeeded);

        var content = Serializer.Read<MoveSetContent>(Assert.Single(result.Artifacts).Content.ToArray());
        var moves = content.Preview();

        Assert.Equal(1, moves.Count);
        Assert.Equal(0f, moves[0].Traits.Speed, 3);

        Assert.Contains(
            result.Diagnostics,
            entry => entry.Severity == ImportSeverity.Warning
                && entry.Message.Contains("speeed", StringComparison.Ordinal)
                && entry.Message.Contains("a move set", StringComparison.Ordinal)
        );
    }

    [Fact]
    public async Task AMoveSetThatSpellsEveryFieldRightWarnsAboutNoField() {
        var result = await ImportMoveSet(
            """
            name: locomotion
            entries:
              - name: walk
                clip: Assets/Anim/Walk.vxanim
                speed: 1.4
                minRate: 0.85
                maxRate: 1.15
                footPhase: 0.12
                facets:
                  - key: role
                    value: loop
            """
        );

        Assert.DoesNotContain(result.Diagnostics, entry => entry.Severity == ImportSeverity.Warning);
    }

    static Task<ImportResult> ImportMaterial(string text) =>
        Import(new MaterialImporter(), "/Assets/hero.vxmat", text);

    static Task<ImportResult> ImportCompositor(string text) =>
        Import(new CompositorImporter(), "/Assets/Frame.vxcompositor", text);

    static Task<ImportResult> ImportMoveSet(string text) =>
        Import(new MoveSetImporter(), "/Assets/locomotion.vxmoveset", text);

    static async Task<ImportResult> Import(IAssetImporter importer, string path, string text) {
        var source = new VirtualPath(path);
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
