// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Core.Yaml.Meta;
using Vixen.Editor.Assets.MeshMaps;
using Vixen.Editor.Assets.Textures;
using Vixen.Editor.Core;
using Vixen.Editor.Testing;
using Vixen.Editor.Ui;
using Vixen.Geometry;
using Vixen.Geometry.Remeshing;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>Doc 48 § D12's maps landing in the project as ordinary assets.</summary>
/// <remarks>
///     <para>
///         ⚠ <b><c>MapBaker.Bake</c> had no caller in the repository.</b> The seven measurements were
///         built, tested against closed-form oracles, and reachable from nothing — not an importer,
///         not a content build, not the editor. Every test here goes through
///         <see cref="ProjectMeshMapBaker" />, so what they assert is that a person can now cause a
///         mesh map to exist.
///     </para>
///     <para>
///         ⚠ <b>Ordinary assets, and § D12 is explicit about why it matters: an artist wants to look
///         at the curvature map when a generator misbehaves.</b> So the assertions are the ones that
///         separate a file in <c>Assets/</c> from a cache in <c>Library/</c> — it is on disk under a
///         name, it has a sidecar, the database knows it by a GUID, and re-baking keeps that GUID
///         rather than making a second one.
///     </para>
/// </remarks>
public sealed class MeshMapAssetTests : IDisposable {
    readonly string root = Path.Combine(Path.GetTempPath(), "vixen-meshmaps-" + Guid.NewGuid().ToString("N")[..12]);

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
        var baker = new ProjectMeshMapBaker(project);
        var set = baker.Bake("Barrel", Sheet(), Sheet(), Settings());

        Assert.Equal("Barrel", set.Mesh);
        Assert.Equal(MeshMapNaming.Every.Count, set.Files.Count);

        foreach (var file in set.Files) {
            Assert.True(File.Exists(file), file);
            Assert.True(File.Exists(AssetMetaFile.PathFor(file)), file + " has no sidecar.");

            // The folder is under Assets/ and not under Library/, which is the whole of "not a cache".
            Assert.StartsWith(project.Paths.Assets, file, StringComparison.Ordinal);
        }

        foreach (var usage in MeshMapNaming.Every) {
            Assert.True(set.Maps.TryGetValue(usage, out var reference), $"{usage} produced no reference.");
            Assert.NotEqual(AssetReference.Null, reference);
            Assert.True(project.Assets.TryGetByGuid(reference.Asset, out _), $"{usage} is not in the database.");
        }
    }

    /// <summary>The sidecar says what the map measures, which is what a generator binds through.</summary>
    /// <remarks>
    ///     ⚠ <b>The file name is the artist's answer and this is the authoritative one.</b> § 4.8's
    ///     Mesh Map Input binds by usage; a rename that unbound every generator would be a rename
    ///     that looks harmless.
    /// </remarks>
    [Fact]
    public void The_sidecar_says_what_each_map_measures() {
        var project = Project();
        var set = new ProjectMeshMapBaker(project).Bake("Barrel", Sheet(), Sheet(), Settings());

        foreach (var file in set.Files) {
            Assert.True(MeshMapNaming.TryParseFileName(file, out var mesh, out var usage), file);

            var meta = AssetMetaFile.ReadFile(AssetMetaFile.PathFor(file));

            Assert.Equal(MeshMapNaming.Suffix(usage), meta.Extensions[MeshMapNaming.UsageKey]);
            Assert.Equal(mesh, meta.Extensions[MeshMapNaming.MeshKey]);
        }
    }

    /// <summary>The settings a mesh map needs survive the round trip through the sidecar.</summary>
    /// <remarks>
    ///     ⚠ <b>Read back rather than asserted on the record that was written.</b> The settings are
    ///     polymorphic in the file — <c>importer: !TextureImporter</c> — so "the record said
    ///     <c>None</c>" and "the file says <c>None</c>" are two different claims, and only the second
    ///     one is what the importer will act on.
    /// </remarks>
    [Fact]
    public void The_sidecar_carries_the_settings_a_mesh_map_needs() {
        var project = Project();
        var set = new ProjectMeshMapBaker(project).Bake("Barrel", Sheet(), Sheet(), Settings());

        foreach (var file in set.Files) {
            var meta = AssetMetaFile.ReadFile(AssetMetaFile.PathFor(file));
            var settings = Assert.IsType<TextureImportSettings>(meta.Importer);

            Assert.Equal(TextureCompression.None, settings.Compression);
            Assert.False(settings.GenerateMips, file + " would be mipped.");
        }
    }

    /// <summary>Only a map that is stored quantized carries a scale.</summary>
    /// <remarks>
    ///     ⚠ <b>An occlusion or a position map with a scale beside it would be a reader's invitation
    ///     to multiply by it.</b> Those two are already fractions of something; only the displacement
    ///     and the curvature are measurements in the model's own units squeezed into eight bits.
    /// </remarks>
    [Fact]
    public void Only_a_quantized_map_can_carry_a_scale() {
        var project = Project();
        var set = new ProjectMeshMapBaker(project).Bake("Barrel", Raised(0.1f), Sheet(), Settings());

        foreach (var file in set.Files) {
            Assert.True(MeshMapNaming.TryParseFileName(file, out _, out var usage), file);

            var meta = AssetMetaFile.ReadFile(AssetMetaFile.PathFor(file));

            if (usage is MeshMapUsage.Displacement or MeshMapUsage.Curvature) {
                continue;
            }

            Assert.False(meta.Extensions.ContainsKey(MeshMapNaming.ScaleKey), file + " carries a scale.");
        }
    }

    /// <summary>A bake that measured a range writes it beside the pixels that were quantized by it.</summary>
    /// <remarks>
    ///     ⚠ <b>The scale and the pixels have to be written by the same code, and this is why.</b>
    ///     Displacement comes back in the model's own units and deliberately un-normalised —
    ///     <c>BakedMaps.DisplacementRange</c> says a caller quantizes with it — so a map whose scale
    ///     lives anywhere else is a map that means nothing. A source lifted a tenth of a unit off the
    ///     target must therefore produce a scale of about a tenth.
    /// </remarks>
    [Fact]
    public void A_displaced_bake_writes_the_scale_that_decodes_it() {
        var project = Project();
        var set = new ProjectMeshMapBaker(project).Bake("Barrel", Raised(0.1f), Sheet(), Settings());
        var height = set.Files.Single(file => file.EndsWith("_height.png", StringComparison.Ordinal));
        var meta = AssetMetaFile.ReadFile(AssetMetaFile.PathFor(height));

        Assert.True(meta.Extensions.TryGetValue(MeshMapNaming.ScaleKey, out var written), "no scale was written.");
        Assert.Equal(0.1f, float.Parse(written, CultureInfo.InvariantCulture), 0.01f);
    }

    /// <summary>Baking the same mesh twice overwrites the set and keeps every GUID.</summary>
    /// <remarks>
    ///     ⚠ <b>The failure this stops is a project full of <c>Barrel_ao_3</c> while every generator
    ///     goes on reading the first one.</b> Re-baking is an artist raising the ray count; the maps
    ///     they are already using have to be the maps that change.
    /// </remarks>
    [Fact]
    public void Re_baking_overwrites_and_keeps_the_guids() {
        var project = Project();
        var baker = new ProjectMeshMapBaker(project);

        var first = baker.Bake("Barrel", Sheet(), Sheet(), Settings());
        var second = baker.Bake("Barrel", Sheet(), Sheet(), Settings() with { OcclusionSamples = 8 });

        Assert.Equal(first.Files, second.Files);
        Assert.Equal(first.Maps, second.Maps);

        // And no second set beside the first: the folder holds one file and one sidecar per usage.
        var written = Directory.GetFiles(Path.Combine(project.Paths.Assets, MeshMapNaming.DefaultFolder));

        Assert.Equal(MeshMapNaming.Every.Count * 2, written.Length);
    }

    /// <summary>A mesh named by a person cannot escape the folder it is baked into.</summary>
    /// <remarks>"Wall / 2" is a perfectly good name for a mesh and a path traversal in a file system.</remarks>
    [Fact]
    public void A_name_that_is_a_path_is_sanitised() {
        var project = Project();
        var set = new ProjectMeshMapBaker(project).Bake("../Wall / 2", Sheet(), Sheet(), Settings());
        var folder = Path.Combine(project.Paths.Assets, MeshMapNaming.DefaultFolder);

        foreach (var file in set.Files) {
            Assert.Equal(folder, Path.GetDirectoryName(file));
        }
    }

    /// <summary>Writing a set that was baked elsewhere is the same set, which is what lets it be off-thread.</summary>
    /// <remarks>
    ///     The editor bakes on a pool thread and writes on the frame thread, because a write means
    ///     <c>AssetDatabase.Scan</c> and every panel is reading that index. What this asserts is that
    ///     the split is a split and not a second code path.
    /// </remarks>
    [Fact]
    public void A_bake_and_a_separate_write_agree() {
        var whole = new ProjectMeshMapBaker(Project()).Bake("Barrel", Sheet(), Sheet(), Settings());

        var split = new ProjectMeshMapBaker(Project()).Write(
            "Barrel",
            MeshMapBake.Run("Barrel", Sheet(), Sheet(), Settings()),
            []
        );

        Assert.Equal(
            whole.Files.Select(Path.GetFileName),
            split.Files.Select(Path.GetFileName)
        );

        Assert.Equal(whole.Maps.Keys.Order(), split.Maps.Keys.Order());
    }

    /// <summary>The verb exists in a real editor, is implemented, and is on both menus.</summary>
    /// <remarks>
    ///     ⚠ <b>This is "grep for callers" turned into an assertion, and it is the point of the whole
    ///     slice.</b> Everything above proves that a bake, once invoked, lands correctly; none of it
    ///     proves anybody can invoke one. A registered command that no menu names is reachable only
    ///     from the palette; a menu line naming a command nobody registered is skipped silently by
    ///     the builder. Both are ways for a finished bake to have no caller, which is exactly the
    ///     state § D12's maps were already in.
    /// </remarks>
    [Fact]
    public void The_bake_verb_is_registered_and_reachable() {
        using var fixture = EditorSession.Start();

        var command = fixture.Shell.Commands["assets.bake-mesh-maps"];

        Assert.NotNull(command);
        Assert.False(command.IsUnavailable, "the bake verb is registered as not yet built.");

        var named = new List<string>();

        foreach (var menu in fixture.Shell.Menus.Menus) {
            Walk(menu, named);
        }

        Assert.Contains("assets.bake-mesh-maps", named);
    }

    static void Walk(MenuGroup group, List<string> named) {
        foreach (var entry in group.Entries) {
            switch (entry) {
                case MenuCommand(var id):
                    named.Add(id);
                    break;

                case MenuSubmenu(var child):
                    Walk(child, named);
                    break;

                default:
                    break;
            }
        }
    }

    EditorProject Project() {
        var directory = Path.Combine(root, Guid.NewGuid().ToString("N")[..8]);

        Directory.CreateDirectory(Path.Combine(directory, "Assets"));

        return new(new ProjectPaths(directory));
    }

    /// <summary>Everything § D12 lists, at a size a test can afford.</summary>
    static BakeSettings Settings() =>
        new() {
            Resolution = 8,
            Gutter = 1,
            SearchRadius = 0.5f,
            Maps = Vixen.Geometry.Remeshing.MeshMaps.All,
            OcclusionSamples = 4
        };

    /// <summary>A unit square with an atlas over the whole of it, which is enough to bake into.</summary>
    static EditMesh Sheet() {
        var mesh = new EditMesh();
        var corners = new int[3, 3];

        for (var i = 0; i < 3; i++) {
            for (var j = 0; j < 3; j++) {
                corners[i, j] = mesh.AddPosition(new(i / 2f, 0f, j / 2f));
            }
        }

        for (var i = 0; i < 2; i++) {
            for (var j = 0; j < 2; j++) {
                Span<int> loop = [corners[i, j], corners[i + 1, j], corners[i + 1, j + 1], corners[i, j + 1]];

                mesh.AddFace(loop, 0);
            }
        }

        var coordinates = new Vector2[mesh.CornerCount];

        for (var face = 0; face < mesh.FaceCount; face++) {
            var entry = mesh.Faces[face];
            var loop = mesh.CornersOf(face);

            for (var corner = 0; corner < loop.Length; corner++) {
                var position = mesh.Positions[loop[corner]];

                coordinates[entry.Start + corner] = new(position.X, position.Z);
            }
        }

        mesh.SetTexCoords(coordinates);

        return mesh;
    }

    /// <summary>The same sheet, lifted, so a bake against it measures a displacement it can report.</summary>
    static EditMesh Raised(float height) {
        var mesh = Sheet();

        for (var vertex = 0; vertex < mesh.PositionCount; vertex++) {
            mesh.MovePosition(vertex, mesh.Positions[vertex] + new Vector3(0f, height, 0f));
        }

        return mesh;
    }
}
