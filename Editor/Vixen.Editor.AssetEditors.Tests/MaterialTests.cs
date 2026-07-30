// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Core.Yaml;
using Vixen.Editor.AssetEditors.Materials;
using Vixen.Rendering.Materials;
using Xunit;

namespace Vixen.Editor.AssetEditors.Tests;

/// <summary>What a <c>.vxmat</c> holds and what an editor does to it.</summary>
public class MaterialTests {
    /// <summary>An empty file is a new material rather than an error.</summary>
    [Fact]
    public void AnEmptyFileIsANewMaterial() {
        var material = MaterialAsset.FromYaml(string.Empty);

        Assert.Equal(MaterialAsset.Current, material.Version);
        Assert.Empty(material.Parameters);
    }

    /// <summary>⚠ A file from a newer editor is refused rather than half-bound.</summary>
    [Fact]
    public void ANewerFileIsRefused() =>
        Assert.Throws<NotSupportedException>(() => MaterialAsset.FromYaml("version: 99\nshader: Standard\n"));

    /// <summary>Every parameter kind round-trips through its own tag.</summary>
    [Fact]
    public void ParametersRoundTripByTag() {
        var material = new MaterialAsset {
            Shader = "ForwardPlus",
            Parameters = [
                new ScalarParameter { Name = "roughness", Value = 0.25f },
                new ColourParameter { Name = "tint", Value = new(1f, 0.5f, 0.25f, 1f) },
                new VectorParameter { Name = "tiling", Value = new(2f, 2f, 0f, 0f) },
                new TextureParameter { Name = "albedo", Value = AssetId.New() },
                new FlagParameter { Name = "useFoam", Value = true }
            ]
        };

        var read = MaterialAsset.FromYaml(material.ToYaml());

        Assert.Equal(5, read.Parameters.Count);
        Assert.IsType<ScalarParameter>(read.Find("roughness"));
        Assert.IsType<ColourParameter>(read.Find("tint"));
        Assert.IsType<VectorParameter>(read.Find("tiling"));
        Assert.IsType<TextureParameter>(read.Find("albedo"));
        Assert.IsType<FlagParameter>(read.Find("useFoam"));
    }

    /// <summary>A colour keeps its channels through a round trip.</summary>
    [Fact]
    public void AColourKeepsItsChannels() {
        var material = new MaterialAsset {
            Parameters = [new ColourParameter { Name = "tint", Value = new(0.25f, 0.5f, 0.75f, 1f) }]
        };

        var read = (ColourParameter) MaterialAsset.FromYaml(material.ToYaml()).Find("tint")!;

        Assert.Equal(0.25f, read.Value.R, 5);
        Assert.Equal(0.75f, read.Value.B, 5);
    }

    /// <summary>A texture reference is written as the prefixed scalar the reference scan finds.</summary>
    [Fact]
    public void ATextureIsWrittenAsAReference() {
        var texture = AssetId.New();

        var material = new MaterialAsset {
            Parameters = [new TextureParameter { Name = "albedo", Value = texture }]
        };

        Assert.Contains(texture.ToString(), material.ToYaml(), StringComparison.Ordinal);
    }

    /// <summary>Opening and saving is a no-op in the diff.</summary>
    [Fact]
    public void OpeningAndSavingChangesNothing() {
        using var fixture = new EditorFixture();

        var original = new MaterialAsset {
            Shader = "ForwardPlus",
            Shading = "StandardShading",
            Parameters = [new ScalarParameter { Name = "roughness", Value = 0.4f }]
        }.ToYaml();

        var path = fixture.Write("Assets/hero.vxmat", original);
        var document = new MaterialDocument(fixture.Project, AssetId.New(), path);

        Assert.Equal(original, document.ToYaml());
    }

    /// <summary>Adding a parameter is one undo entry, and undoing it takes the parameter away.</summary>
    [Fact]
    public void AddingAParameterIsUndoable() {
        using var fixture = new EditorFixture();
        var path = fixture.Write("Assets/hero.vxmat", string.Empty);

        var document = new MaterialDocument(fixture.Project, AssetId.New(), path);
        document.Add(new ScalarParameter { Name = "roughness" });

        Assert.Single(document.Material.Parameters);

        document.Stack.Undo();
        Assert.Empty(document.Material.Parameters);

        document.Stack.Redo();
        Assert.Single(document.Material.Parameters);
    }

    /// <summary>Removing one puts it back where it was.</summary>
    [Fact]
    public void RemovingKeepsThePosition() {
        using var fixture = new EditorFixture();
        var path = fixture.Write("Assets/hero.vxmat", string.Empty);

        var document = new MaterialDocument(fixture.Project, AssetId.New(), path);

        document.Add(new ScalarParameter { Name = "a" });
        var middle = document.Add(new ScalarParameter { Name = "b" });
        document.Add(new ScalarParameter { Name = "c" });

        document.Remove(middle);
        document.Stack.Undo();

        Assert.Equal("b", document.Material.Parameters[1].Name);
    }

    /// <summary>⚠ Two parameters of one name mean the shader gets whichever was reached last.</summary>
    [Fact]
    public void ANameCannotBeUsedTwice() {
        using var fixture = new EditorFixture();
        var path = fixture.Write("Assets/hero.vxmat", string.Empty);

        var document = new MaterialDocument(fixture.Project, AssetId.New(), path);
        document.Add(new ScalarParameter { Name = "roughness" });

        Assert.Throws<InvalidOperationException>(() => document.Add(new ColourParameter { Name = "roughness" }));
    }

    /// <summary>The header is copied back into the asset on the way out.</summary>
    [Fact]
    public void TheHeaderIsWrittenBack() {
        using var fixture = new EditorFixture();
        var path = fixture.Write("Assets/hero.vxmat", string.Empty);

        var document = new MaterialDocument(fixture.Project, AssetId.New(), path);
        document.Header.Shader = "Unlit";
        document.Save();

        Assert.Contains("shader: Unlit", EditorFixture.Read(path), StringComparison.Ordinal);
    }

    /// <summary>⚠ A file that will not parse opens empty and says why.</summary>
    [Fact]
    public void ABrokenFileOpensAndExplains() {
        using var fixture = new EditorFixture();
        var path = fixture.Write("Assets/hero.vxmat", "version: 99\n");

        var document = new MaterialDocument(fixture.Project, AssetId.New(), path);

        Assert.NotNull(document.LoadError);
        Assert.Empty(document.Material.Parameters);
    }

    /// <summary>The picker's names produce the parameter kinds they say they do.</summary>
    [Fact]
    public void TheKindsMatchTheirNames() {
        Assert.IsType<ColourParameter>(MaterialView.Create("Colour", "a"));
        Assert.IsType<VectorParameter>(MaterialView.Create("Vector", "a"));
        Assert.IsType<TextureParameter>(MaterialView.Create("Texture", "a"));
        Assert.IsType<FlagParameter>(MaterialView.Create("Flag", "a"));
        Assert.IsType<ScalarParameter>(MaterialView.Create("Scalar", "a"));

        // Anything unrecognised is a scalar rather than nothing, so a picker that gained an option
        // the factory has not learned about still produces a parameter.
        Assert.IsType<ScalarParameter>(MaterialView.Create("Nonsense", "a"));
    }

    /// <summary>A parameter's label says which kind it is, because the name alone does not.</summary>
    [Fact]
    public void TheAliasIsTheTag() {
        Assert.Equal("Colour", MaterialView.Alias(new ColourParameter()));
        Assert.Equal("Scalar", MaterialView.Alias(new ScalarParameter()));
    }

    /// <summary>
    ///     A material saved by the editor is a material the pipeline reads, features and all.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Two readers of one file, and this is the seam between them.</b> The editor binds a
    ///         <see cref="MaterialAsset" /> to draw it in an inspector; the build binds a
    ///         <see cref="MaterialContent" /> to compile it. Neither is wrong to exist — one carries a
    ///         parameter list and an undo stack's worth of editing affordances, and the other carries
    ///         what the renderer needs — but they read the same bytes, so nothing except this says they
    ///         agree.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The failure it exists for is silent.</b> The binder skips a key it does not know, so
    ///         an editor that had never heard of <c>features</c> would open a material, drop them, and
    ///         write the file back without them — no error, no diff anybody reads, and a material that
    ///         is a white dielectric from then on.
    ///     </para>
    /// </remarks>
    [Fact]
    public void AMaterialSavedByTheEditorIsOneThePipelineReads() {
        var authored = new MaterialAsset {
            Shader = "ForwardPlus",
            Shading = "CelShading",
            Features = [
                new MetalRoughnessFeature { BaseColor = new(0.2f, 0.4f, 0.6f), Metalness = 1f, Roughness = 0.3f }
            ],
            Textures = [new("baseColorMap", new(AssetId.New(), SubAssetId.Main))],
            Parameters = [new ScalarParameter { Name = "tiling", Value = 2f }]
        };

        var yaml = authored.ToYaml();

        // The editor's own round trip: what it wrote, it reads. Without this the assertion below
        // could pass against a file the editor can no longer open.
        var reopened = MaterialAsset.FromYaml(yaml);

        Assert.Single(reopened.Features);
        Assert.Single(reopened.Textures);
        Assert.Single(reopened.Parameters);

        // And the pipeline's, which is the one that matters: the same bytes, bound by the type the
        // importer compiles and the runtime loads.
        var content = YamlSerializer.Parse<MaterialContent>(yaml);

        Assert.Equal("ForwardPlus", content.Shader);
        Assert.Equal("CelShading", content.Shading);
        Assert.Equal("baseColorMap", Assert.Single(content.Textures).Parameter);

        var workflow = Assert.IsType<MetalRoughnessFeature>(Assert.Single(content.Features));

        Assert.Equal(1f, workflow.Metalness);
        Assert.Equal(0.3f, workflow.Roughness);
        Assert.Equal(0.6f, workflow.BaseColor.Z, 1e-6f);

        // The editor's own list is not the pipeline's business and is skipped rather than refused,
        // which is what makes the two schemas able to differ at all.
        Assert.True(MaterialShading.TryResolve(content.Shading, out var shading));
        Assert.IsType<CelShading>(shading);
    }
}
