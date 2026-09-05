// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Imaging;
using Vixen.Core.Yaml;
using Vixen.Core.Yaml.Meta;
using Vixen.Editor.Assets.Materials;
using Vixen.Editor.Core;
using Vixen.Rendering.Materials;
using Xunit;

namespace Vixen.Editor.Assets.Tests;

/// <summary>Doc 48 § D11's write: the maps, the material, the GUIDs and the provenance.</summary>
/// <remarks>
///     <para>
///         Every test here goes through <see cref="ProjectMaterialBaker" />, because what M5 claims is
///         that a graph's outputs become <i>files the rest of the engine already understands</i> —
///         and the assertions that separate that from a cache are the ones about identity: the file
///         is under <c>Assets/</c>, it has a sidecar, the database knows it by a GUID, and re-baking
///         keeps that GUID rather than minting a second one.
///     </para>
///     <para>
///         ⚠ <b>The painted-over check is the one that has to be proved in both directions.</b> A
///         guard that never fires and a guard that always fires look the same from a green suite, so
///         there is a test that a re-bake of untouched files is allowed and a test that a re-bake over
///         an edited file is refused.
///     </para>
/// </remarks>
public sealed class MaterialBakeAssetTests : IDisposable {
    readonly string root = Path.Combine(Path.GetTempPath(), "vixen-matbake-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() {
        try {
            if (Directory.Exists(root)) {
                Directory.Delete(root, recursive: true);
            }
        } catch (IOException) {
            // A file the test wrote and the OS has not let go of. Not what is under test.
        }
    }

    /// <summary>A bake writes files, and the database knows every one of them by a GUID.</summary>
    [Fact]
    public void A_bake_lands_as_ordinary_project_assets() {
        var project = Project();
        var set = new ProjectMaterialBaker(project).Write("ShipHull", Images(), Record());

        Assert.Equal("ShipHull", set.Name);
        Assert.NotEqual(AssetReference.Null, set.Material);

        foreach (var file in set.Files) {
            Assert.True(File.Exists(file), file);
            Assert.True(File.Exists(AssetMetaFile.PathFor(file)), file + " has no sidecar.");

            // Under Assets/ and not under Library/, which is the whole of "not a cache".
            Assert.StartsWith(project.Paths.Assets, file, StringComparison.Ordinal);
        }

        foreach (var (target, reference) in set.Maps) {
            Assert.NotEqual(AssetReference.Null, reference);
            Assert.True(project.Assets.TryGetByGuid(reference.Asset, out _), $"{target} is not in the database.");
        }

        Assert.True(project.Assets.TryGetByGuid(set.Material.Asset, out _));
    }

    /// <summary>The material names its maps by the ids the scan minted, and compiles.</summary>
    /// <remarks>
    ///     ⚠ <b>Two scans, and this is what the second one buys.</b> A <c>.vxmat</c> names its maps by
    ///     <c>AssetId</c>, and those ids do not exist until the maps have been scanned — so a
    ///     material written in the same pass as its pixels would name nothing at all.
    /// </remarks>
    [Fact]
    public void The_material_names_every_map_it_wrote() {
        var project = Project();
        var set = new ProjectMaterialBaker(project).Write("ShipHull", Images(), Record());
        var material = YamlSerializer.Parse<MaterialContent>(File.ReadAllText(Vxmat(project, "ShipHull")));

        Assert.Equal(3, material.Textures.Length);

        foreach (var texture in material.Textures) {
            Assert.NotEqual(AssetReference.Null, texture.Texture);
            Assert.True(project.Assets.TryGetByGuid(texture.Texture.Asset, out _), texture.Parameter);
        }

        Assert.Contains(material.Textures, texture => texture.Texture == set.Maps[MaterialMapTarget.Orm]);
        Assert.True(MaterialShading.TryResolve(material.Shading, out var shading));
        Assert.False(MaterialCompiler.Compile(material.ToDescriptor(shading)).Failed);
    }

    /// <summary>Re-baking overwrites and keeps every GUID, so what points at it picks up the maps.</summary>
    [Fact]
    public void Re_baking_overwrites_and_keeps_the_guids() {
        var project = Project();
        var baker = new ProjectMaterialBaker(project);
        var first = baker.Write("ShipHull", Images(), Record());
        var second = baker.Write("ShipHull", Images(20), Record());

        Assert.Equal(first.Name, second.Name);
        Assert.Equal(first.Material, second.Material);
        Assert.Equal(first.Files.Count, second.Files.Count);

        foreach (var (target, reference) in first.Maps) {
            Assert.Equal(reference, second.Maps[target]);
        }

        // And the pixels are the second bake's, which is the half that makes the shared GUID useful
        // rather than merely tidy.
        Assert.Equal(20, PngCodec.Decode(File.ReadAllBytes(Map(project, "ShipHull", MaterialMapTarget.Orm))).Pixels[1]);
    }

    /// <summary>A re-bake of the same outputs on the same machine writes the same bytes.</summary>
    /// <remarks>
    ///     ⚠ <b>The sidecar is deliberately excluded, and § D4 is why.</b> The provenance block
    ///     carries the time the bake ran, so two runs differ there by construction — what the exit
    ///     criterion is about is the outputs and the material, which is what a build consumes and
    ///     what a reviewer diffs.
    /// </remarks>
    [Fact]
    public void A_re_bake_is_byte_identical() {
        var project = Project();
        var baker = new ProjectMaterialBaker(project);

        baker.Write("ShipHull", Images(), Record());

        var before = baker.Written.ToDictionary(file => file, File.ReadAllBytes, StringComparer.Ordinal);

        baker.Write("ShipHull", Images(), Record());

        Assert.Equal(before.Count, baker.Written.Count);

        foreach (var file in baker.Written) {
            Assert.True(before.ContainsKey(file), file + " was not written by the first bake.");
            Assert.Equal(before[file], File.ReadAllBytes(file));
        }
    }

    /// <summary>An output somebody painted over stops the bake instead of being replaced.</summary>
    [Fact]
    public void A_painted_over_map_is_refused() {
        var project = Project();
        var baker = new ProjectMaterialBaker(project);

        baker.Write("ShipHull", Images(), Record());

        var painted = Map(project, "ShipHull", MaterialMapTarget.BaseColor);

        File.WriteAllBytes(painted, PngCodec.Encode(Flat(4, 99)));

        var kept = File.ReadAllBytes(painted);
        var failure = Assert.Throws<IOException>(() => baker.Write("ShipHull", Images(20), Record()));

        Assert.Contains("baseColor", failure.Message, StringComparison.Ordinal);
        Assert.Contains("painted", failure.Message, StringComparison.Ordinal);

        // ⚠ And nothing was written before the refusal. A guard that refuses after overwriting the
        // first three of seven maps is a guard that destroys the work it exists to protect.
        Assert.Equal(kept, File.ReadAllBytes(painted));
        Assert.NotEqual(
            20,
            PngCodec.Decode(File.ReadAllBytes(Map(project, "ShipHull", MaterialMapTarget.Orm))).Pixels[1]
        );
    }

    /// <summary>And is overwritten when a person says they meant it.</summary>
    [Fact]
    public void A_painted_over_map_is_overwritten_when_forced() {
        var project = Project();
        var baker = new ProjectMaterialBaker(project);

        baker.Write("ShipHull", Images(), Record());
        File.WriteAllBytes(Map(project, "ShipHull", MaterialMapTarget.BaseColor), PngCodec.Encode(Flat(4, 99)));

        var set = baker.Write("ShipHull", Images(20), Record(), force: true);

        Assert.Contains(set.Warnings, warning => warning.Contains("painted", StringComparison.Ordinal));
        Assert.Equal(
            20,
            PngCodec.Decode(File.ReadAllBytes(Map(project, "ShipHull", MaterialMapTarget.BaseColor))).Pixels[0]
        );
    }

    /// <summary>An untouched set re-bakes without complaint, which is the guard's other direction.</summary>
    /// <remarks>
    ///     ⚠ <b>A predicate that cannot be false is worse than the flake it replaced.</b> Without
    ///     this, a <see cref="MaterialProvenance.Painted" /> that returned every output would leave
    ///     the refusal test green and make every re-bake in the editor impossible.
    /// </remarks>
    [Fact]
    public void An_untouched_set_re_bakes_without_being_called_painted() {
        var project = Project();
        var baker = new ProjectMaterialBaker(project);

        baker.Write("ShipHull", Images(), Record());

        var set = baker.Write("ShipHull", Images(20), Record());

        Assert.Empty(set.Warnings);
    }

    /// <summary>The provenance block says what produced the bytes.</summary>
    [Fact]
    public void The_sidecar_carries_the_provenance_block() {
        var project = Project();
        var baker = new ProjectMaterialBaker(project);
        var record = Record() with {
            Parameters = new Dictionary<string, string>(StringComparer.Ordinal) { ["rust"] = "0.6" },
            Adapter = "AMD Radeon RX 7900 XT"
        };

        baker.Write("ShipHull", Images(), record);

        var meta = AssetMetaFile.ReadFile(AssetMetaFile.PathFor(Vxmat(project, "ShipHull")));

        Assert.Equal(record.Source, meta.Extensions[MaterialProvenance.SourceKey]);
        Assert.Equal(record.SourceAsset.ToString(), meta.Extensions[MaterialProvenance.SourceAssetKey]);
        Assert.Equal("baseColor, orm, opacity", meta.Extensions[MaterialProvenance.OutputsKey]);
        Assert.Equal("4", meta.Extensions[MaterialProvenance.ResolutionKey]);
        Assert.Equal("AMD Radeon RX 7900 XT", meta.Extensions[MaterialProvenance.AdapterKey]);
        Assert.Equal("0.6", meta.Extensions[MaterialProvenance.ParameterPrefix + "rust"]);
        Assert.StartsWith("sha256:", meta.Extensions[MaterialProvenance.WrittenDigestKey], StringComparison.Ordinal);
        Assert.True(meta.Extensions.ContainsKey(MaterialProvenance.AtKey));
    }

    /// <summary>The adapter is recorded and never compared.</summary>
    /// <remarks>
    ///     ⚠ <b>§ D4 states this as a decision.</b> A re-bake on a different card is not byte-identical
    ///     and is not supposed to be refused for it — asserting the adapter would make the first artist
    ///     with a different GPU a bug report.
    /// </remarks>
    [Fact]
    public void A_re_bake_on_another_adapter_is_not_refused() {
        var project = Project();
        var baker = new ProjectMaterialBaker(project);

        baker.Write("ShipHull", Images(), Record() with { Adapter = "AMD Radeon RX 7900 XT" });

        var set = baker.Write("ShipHull", Images(), Record() with { Adapter = "Apple M4 Max" });

        Assert.Empty(set.Warnings);
        Assert.Equal(
            "Apple M4 Max",
            AssetMetaFile.ReadFile(AssetMetaFile.PathFor(Vxmat(project, "ShipHull")))
                .Extensions[MaterialProvenance.AdapterKey]
        );
    }

    /// <summary>Two sources whose materials are both called the same thing get a set each.</summary>
    /// <remarks>
    ///     ⚠ <b>The mesh-map baker's <a href="https://github.com/Rikarin/Vixen/issues/681">#681</a>,
    ///     one asset type over.</b> Overwriting is right for a re-bake and catastrophic for a
    ///     collision, and only the source tells them apart — a name-keyed writer silently swapped one
    ///     model's pixels for another's <i>and</i> handed back its GUIDs.
    /// </remarks>
    [Fact]
    public void Two_sources_with_one_name_do_not_share_a_set() {
        var project = Project();
        var baker = new ProjectMaterialBaker(project);
        var first = baker.Write("Material", Images(), Record());
        var second = baker.Write("Material", Images(20), Record() with { SourceAsset = Asset(7) });

        Assert.Equal("Material", first.Name);
        Assert.Equal("Material_2", second.Name);
        Assert.NotEqual(first.Material, second.Material);
        Assert.Contains(second.Warnings, warning => warning.Contains("rename", StringComparison.Ordinal));

        foreach (var (target, reference) in first.Maps) {
            Assert.NotEqual(reference, second.Maps[target]);
        }

        // And the first source's pixels are still its own.
        Assert.Equal(
            10,
            PngCodec.Decode(File.ReadAllBytes(Map(project, "Material", MaterialMapTarget.Orm))).Pixels[1]
        );
    }

    /// <summary>A name that is a path is made safe on the path a caller actually takes.</summary>
    /// <remarks>
    ///     ⚠ <b><a href="https://github.com/Rikarin/Vixen/issues/680">#680</a>'s lesson, which is
    ///     about where a guard lives rather than whether one exists.</b> The mesh-map baker sanitised
    ///     in <c>Bake</c> and trusted in <c>Write</c>, and the editor only ever called <c>Write</c> —
    ///     so this baker has one entry point and it is the one that sanitises.
    /// </remarks>
    [Fact]
    public void A_name_that_is_a_path_is_sanitised() {
        var project = Project();
        var set = new ProjectMaterialBaker(project).Write("../Ship / Hull", Images(), Record());

        // ⚠ The separator is what is removed, and `..` is not. `Safe` replaces the characters a file
        // name may not contain, so `../Ship` becomes the single, harmless name `.._Ship` rather than
        // a directory that does not exist — and the property that matters is the one below: every
        // file the bake wrote is inside the folder it was told to write into.
        Assert.DoesNotContain(Path.DirectorySeparatorChar, set.Name);
        Assert.DoesNotContain(Path.AltDirectorySeparatorChar, set.Name);

        foreach (var file in set.Files) {
            Assert.StartsWith(
                Path.Combine(project.Paths.Assets, MaterialMapNaming.DefaultFolder),
                Path.GetFullPath(file),
                StringComparison.Ordinal
            );
        }
    }

    /// <summary>A set that crosses the container limit reports the old file and does not delete it.</summary>
    /// <remarks>
    ///     <para>
    ///         The copy that stays behind is a project asset holding the previous bake's pixels under
    ///         a name that says it is this one's, which is what a generator or a second material picks
    ///         up by accident. It is still a hazard and it is still named in a warning.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It is no longer deleted, and this test asserted the deletion.</b> Nothing here
    ///         establishes that the file being removed was written by a previous run of this bake —
    ///         the name comes from the material's name alone — so the first bake of a material called
    ///         <c>Rock</c> deleted a hand-authored <c>Rock_basecolor.png</c> <em>and its
    ///         <c>.meta</c></em>, destroying the id every scene resolved that texture through. See
    ///         <see href="https://github.com/Rikarin/Vixen/issues/715" />. An orphan an artist can see
    ///         is a strictly better failure than one this code deletes for them, so the assertion is
    ///         inverted rather than removed: the file survives, and the warning has to say so.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_map_that_grew_past_the_limit_reports_the_old_file_and_keeps_it() {
        var project = Project();
        var baker = new ProjectMaterialBaker(project);

        baker.Write("Strip", Images(), Record());

        var before = Map(project, "Strip", MaterialMapTarget.BaseColor);

        Assert.True(File.Exists(before));

        var set = baker.Write(
            "Strip",
            MaterialBake.Encode(
                new Dictionary<MaterialMapUsage, Bitmap> { [MaterialMapUsage.BaseColor] = Wide(30) }
            ),
            Record()
        );

        Assert.True(File.Exists(before), "the PNG under the old extension was deleted");
        Assert.Contains(set.Warnings, warning => warning.Contains(".ktx2", StringComparison.Ordinal));
        Assert.Contains(set.Warnings, warning => warning.Contains("LEFT IN PLACE", StringComparison.Ordinal));
    }

    static string Vxmat(EditorProject project, string name) =>
        Path.Combine(project.Paths.Assets, MaterialMapNaming.DefaultFolder, name + MaterialImporter.Extension);

    static string Map(EditorProject project, string name, MaterialMapTarget target) =>
        Path.Combine(
            project.Paths.Assets,
            MaterialMapNaming.DefaultFolder,
            MaterialMapNaming.FileName(name, target, MaterialMapNaming.PortableExtension)
        );

    EditorProject Project() {
        var directory = Path.Combine(root, Guid.NewGuid().ToString("N")[..8]);

        Directory.CreateDirectory(Path.Combine(directory, "Assets"));

        return new(new ProjectPaths(directory));
    }

    /// <summary>A base colour, a packed map and a mask, which is three of the seven files.</summary>
    static IReadOnlyList<MaterialMapImage> Images(byte value = 10) =>
        MaterialBake.Encode(
            new Dictionary<MaterialMapUsage, Bitmap> {
                [MaterialMapUsage.BaseColor] = Flat(4, value),
                [MaterialMapUsage.Roughness] = Flat(4, value),
                [MaterialMapUsage.Opacity] = Flat(4, value)
            }
        );

    static MaterialBakeRecord Record() =>
        new() { Source = "Assets/Materials/ship-hull.vxtexgraph", SourceAsset = Asset(3) };

    static AssetId Asset(int seed) => new(Guid.Parse($"{seed:D8}-0000-0000-0000-000000000000"));

    static Bitmap Flat(int side, byte value) => Filled(side, side, value);

    static Bitmap Wide(byte value) => Filled(MaterialMapNaming.PortableLimit + 16, 16, value);

    static Bitmap Filled(int width, int height, byte value) {
        var pixels = new byte[width * height * 4];

        Array.Fill(pixels, value);

        return new(width, height, pixels);
    }
}
