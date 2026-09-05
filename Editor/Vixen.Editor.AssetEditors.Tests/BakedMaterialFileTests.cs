// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Yaml;
using Vixen.Editor.AssetEditors.Materials;
using Vixen.Editor.Assets.Materials;
using Vixen.Rendering.Materials;
using Xunit;

namespace Vixen.Editor.AssetEditors.Tests;

/// <summary>
///     The <c>.vxmat</c> a bake writes, read by the other reader of that file.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>One declaration, two readers, and <see cref="MaterialContent" />'s own remarks name
///         the hazard.</b> <c>MaterialImporter</c> binds a <c>.vxmat</c> as a
///         <see cref="MaterialContent" /> and the editor binds the same file as a
///         <see cref="MaterialAsset" /> to draw it in an inspector; a binder skips a key it does not
///         know, so the failure is not an error — it is a baked material opened and saved by the
///         editor with its features quietly gone.
///     </para>
///     <para>
///         Doc 48 § M5 writes through the first of those, because the bake lives in
///         <c>Vixen.Editor.Assets</c> and the authoring type is an assembly above it. So this is the
///         test that says the choice costs nothing: what the bake writes, the editor reads.
///     </para>
/// </remarks>
public sealed class BakedMaterialFileTests {
    /// <summary>What a bake writes, the editor's own reader binds whole.</summary>
    [Fact]
    public void The_editor_reads_every_feature_and_texture_a_bake_wrote() {
        var material = MaterialAsset.FromYaml(YamlSerializer.ToYaml(Baked()));

        Assert.Equal(MaterialAsset.Current, material.Version);
        Assert.Equal(3, material.Features.Count);
        Assert.Equal(3, material.Textures.Count);

        Assert.Contains(material.Features, feature => feature is TexturedMetalRoughnessFeature);
        Assert.Contains(material.Features, feature => feature is TexturedNormalMapFeature);
        Assert.Contains(material.Features, feature => feature is TexturedOrmFeature);

        Assert.Contains(material.Textures, texture => texture.Parameter == new TexturedOrmFeature().OrmMap);

        foreach (var texture in material.Textures) {
            Assert.NotEqual(AssetReference.Null, texture.Texture);
        }
    }

    /// <summary>And opening it in the editor and saving it back does not drop them.</summary>
    /// <remarks>
    ///     ⚠ <b>This is the failure the hazard actually produces</b>: not a file that fails to load,
    ///     but a file that loads, looks right in the inspector, and comes back out with no features
    ///     in it — after which the material draws as a white dielectric and nothing says why.
    /// </remarks>
    [Fact]
    public void A_round_trip_through_the_editor_keeps_what_the_bake_put_there() {
        var written = YamlSerializer.ToYaml(Baked());
        var again = YamlSerializer.Parse<MaterialContent>(MaterialAsset.FromYaml(written).ToYaml());

        Assert.Equal(3, again.Features.Length);
        Assert.Equal(3, again.Textures.Length);

        Assert.Contains(again.Textures, texture => texture.Parameter == new TexturedNormalMapFeature().NormalMap);
        Assert.True(MaterialShading.TryResolve(again.Shading, out var shading));
        Assert.False(MaterialCompiler.Compile(again.ToDescriptor(shading)).Failed);
    }

    static MaterialContent Baked() =>
        MaterialBake.Material(
            new Dictionary<MaterialMapTarget, AssetReference> {
                [MaterialMapTarget.BaseColor] = Reference(1),
                [MaterialMapTarget.Normal] = Reference(2),
                [MaterialMapTarget.Orm] = Reference(3)
            }
        );

    static AssetReference Reference(int seed) =>
        new(new AssetId(Guid.Parse($"{seed:D8}-0000-0000-0000-000000000000")));
}
