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
        var set = baker.Bake(Barrel, "Barrel", Sheet(), Sheet(), Settings());

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
        var set = new ProjectMeshMapBaker(project).Bake(Barrel, "Barrel", Sheet(), Sheet(), Settings());

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
        var set = new ProjectMeshMapBaker(project).Bake(Barrel, "Barrel", Sheet(), Sheet(), Settings());

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
        var set = new ProjectMeshMapBaker(project).Bake(Barrel, "Barrel", Raised(0.1f), Sheet(), Settings());

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
        var set = new ProjectMeshMapBaker(project).Bake(Barrel, "Barrel", Raised(0.1f), Sheet(), Settings());
        var height = set.Files.Single(file => file.EndsWith("_height.png", StringComparison.Ordinal));
        var meta = AssetMetaFile.ReadFile(AssetMetaFile.PathFor(height));

        Assert.True(meta.Extensions.TryGetValue(MeshMapNaming.ScaleKey, out var written), "no scale was written.");
        Assert.Equal(0.1f, float.Parse(written, CultureInfo.InvariantCulture), 0.01f);
    }

    /// <summary>Baking the same model's same mesh twice overwrites the set and keeps every GUID.</summary>
    /// <remarks>
    ///     ⚠ <b>The failure this stops is a project full of <c>Barrel_ao_3</c> while every generator
    ///     goes on reading the first one.</b> Re-baking is an artist raising the ray count; the maps
    ///     they are already using have to be the maps that change.
    /// </remarks>
    [Fact]
    public void Re_baking_overwrites_and_keeps_the_guids() {
        var project = Project();
        var baker = new ProjectMeshMapBaker(project);

        var first = baker.Bake(Barrel, "Cube", Sheet(), Sheet(), Settings());
        var second = baker.Bake(Barrel, "Cube", Sheet(), Sheet(), Settings() with { OcclusionSamples = 8 });

        Assert.Equal(first.Files, second.Files);
        Assert.Equal(first.Maps, second.Maps);
        Assert.Equal("Cube", second.Mesh);
        Assert.False(Renamed(second), "a re-bake was reported as a collision.");

        // And no second set beside the first: the folder holds one file and one sidecar per usage.
        var written = Directory.GetFiles(Path.Combine(project.Paths.Assets, MeshMapNaming.DefaultFolder));

        Assert.Equal(MeshMapNaming.Every.Count * 2, written.Length);
    }

    /// <summary>Two models whose meshes share a name do not overwrite each other, and are told so.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The same nine file names as the test above, and the opposite required
    ///         behaviour.</b> <c>Cube</c> is Blender's default object name and every exporter's
    ///         fallback, so two unrelated models producing one set of names is the ordinary case
    ///         rather than a contrived one. Before the set was keyed on the model, the second bake
    ///         overwrote the first's pixels, the scan handed back <i>the first's GUIDs</i>, and every
    ///         material bound to the first went on resolving and started sampling the second — with
    ///         no message anywhere.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Distinct GUIDs is the assertion that matters</b>, not distinct names. A name is
    ///         what an artist reads; a GUID is what a generator binds through, and inheriting one is
    ///         what makes the failure silent.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Two_models_with_one_mesh_name_do_not_overwrite_each_other() {
        var project = Project();
        var baker = new ProjectMeshMapBaker(project);

        var barrel = baker.Bake(Barrel, "Cube", Sheet(), Sheet(), Settings());
        var crate = baker.Bake(Crate, "Cube", Sheet(), Sheet(), Settings());

        Assert.NotEqual(barrel.Mesh, crate.Mesh);
        Assert.Empty(barrel.Files.Intersect(crate.Files, StringComparer.Ordinal));

        foreach (var usage in MeshMapNaming.Every) {
            Assert.NotEqual(barrel.Maps[usage], crate.Maps[usage]);
        }

        // Both sets are on disk, whole: two files and two sidecars per usage.
        var written = Directory.GetFiles(Path.Combine(project.Paths.Assets, MeshMapNaming.DefaultFolder));

        Assert.Equal(MeshMapNaming.Every.Count * 4, written.Length);

        // ⚠ And the artist is told, which is the other half. A bake that quietly renamed its output
        // is a bake whose files nobody can find under the name they asked for.
        Assert.True(Renamed(crate), "the collision was not reported.");
        Assert.Contains(crate.Warnings, warning => warning.Contains(crate.Mesh, StringComparison.Ordinal));

        // Re-baking the second model finds its own set again rather than making a third.
        var again = baker.Bake(Crate, "Cube", Sheet(), Sheet(), Settings());

        Assert.Equal(crate.Files, again.Files);
        Assert.Equal(crate.Maps, again.Maps);
    }

    /// <summary>A mesh named by a person cannot escape the folder it is baked into.</summary>
    /// <remarks>
    ///     <para>
    ///         "Wall / 2" is a perfectly good name for a mesh and a path traversal in a file system.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Through <see cref="IMeshMapBaker.Write" />, because that is the path the editor
    ///         takes and the one that was unguarded.</b> This test called <c>Bake</c> — the overload
    ///         that sanitised — so it stayed green whatever <c>Write</c> did with the name, while
    ///         <c>EditorParity.BakeSelectedMeshMaps</c> handed Assimp's own string to
    ///         <c>ContentTasks</c>, which encoded and wrote it directly. A guard on a path nothing
    ///         takes is not a guard.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_name_that_is_a_path_is_sanitised() {
        var project = Project();
        var folder = Path.Combine(project.Paths.Assets, MeshMapNaming.DefaultFolder);

        var set = new ProjectMeshMapBaker(project).Write(
            Barrel,
            "../Wall / 2",
            MeshMapBake.Run(Sheet(), Sheet(), Settings()),
            []
        );

        Assert.NotEmpty(set.Files);

        foreach (var file in set.Files) {
            Assert.Equal(folder, Path.GetDirectoryName(file));

            // ⚠ Not merely "inside the folder": `Path.Combine` with a name carrying a separator
            // produces a path *under* it, and `Assets/MeshMaps/Wall/2_normal.png` is inside the
            // folder by every prefix test and is still a directory that does not exist.
            Assert.Equal(Path.GetFileName(file), file[(folder.Length + 1)..]);
        }

        // And the sidecar agrees with the file, which is the half that made the two disagree.
        foreach (var file in set.Files) {
            Assert.True(MeshMapNaming.TryParseFileName(file, out var mesh, out _), file);

            var meta = AssetMetaFile.ReadFile(AssetMetaFile.PathFor(file));

            Assert.Equal(mesh, meta.Extensions[MeshMapNaming.MeshKey]);
            Assert.Equal(set.Mesh, mesh);
        }
    }

    /// <summary>The sidecar records which model the set was baked from, which is its identity.</summary>
    /// <remarks>
    ///     ⚠ <b>Read back out of the file rather than asserted on the record.</b> The key is what the
    ///     <i>next</i> bake reads to decide whether it is looking at its own set, so "the writer
    ///     meant to record it" and "the file records it" are two claims and only the second one is
    ///     load-bearing.
    /// </remarks>
    [Fact]
    public void The_sidecar_records_the_model_the_set_came_from() {
        var project = Project();
        var set = new ProjectMeshMapBaker(project).Bake(Barrel, "Cube", Sheet(), Sheet(), Settings());

        foreach (var file in set.Files) {
            var meta = AssetMetaFile.ReadFile(AssetMetaFile.PathFor(file));

            Assert.True(meta.Extensions.TryGetValue(MeshMapNaming.ModelKey, out var written), file);
            Assert.Equal(Barrel, AssetId.Parse(written));
        }
    }

    /// <summary>A set written before the key existed is adopted by the model that re-bakes it.</summary>
    /// <remarks>
    ///     ⚠ <b>The migration, and the alternative was worse than doing nothing.</b> A set with no
    ///     model recorded is what every bake before this key wrote; treating it as somebody else's
    ///     would leave it stranded under its name while the re-bake landed beside it as
    ///     <c>Cube_2</c> — a project that gains a duplicate set the first time it is opened by a
    ///     newer editor, with the generators still pointed at the old one.
    /// </remarks>
    [Fact]
    public void An_unkeyed_set_is_adopted_rather_than_avoided() {
        var project = Project();
        var baker = new ProjectMeshMapBaker(project);

        var before = baker.Bake(AssetId.Empty, "Cube", Sheet(), Sheet(), Settings());
        var after = baker.Bake(Barrel, "Cube", Sheet(), Sheet(), Settings());

        Assert.Equal(before.Files, after.Files);
        Assert.Equal(before.Maps, after.Maps);
        Assert.False(Renamed(after), "an unkeyed set was treated as another model's.");
    }

    /// <summary>The one-at-a-time guard is still held while the bake's files are being written.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The guard was released before a single byte was written, and its own remark gave
    ///         a reason that was therefore false.</b> <c>ContentTasks.BakeMeshMaps</c> said it took
    ///         the guard "because it writes into <c>Assets/</c> and an import running over the same
    ///         folder would read the files half-written" — and released it in the pool task's
    ///         <c>finally</c>, which runs when the <i>arithmetic</i> ends. The write happens
    ///         afterwards, on the frame thread, from <c>Pump</c>. So the one thing the guard existed
    ///         to be exclusive against was the one thing it did not cover.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Asserted from inside the write rather than around it</b>, because "busy after the
    ///         bake returned" and "busy while the bytes are going down" are different claims and only
    ///         the second one is what an import must not interleave with.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_one_at_a_time_guard_covers_the_write() {
        using var session = EditorSession.Start();
        var tasks = new ContentTasks(session.Project, session.Shell);
        var baker = new RecordingBaker();

        baker.Busy = () => tasks.IsBusy;

        tasks.BakeMeshMaps(baker, AssetId.Empty, "Cube", Sheet(), Sheet(), Settings());

        // ⚠ A hang check and not a budget. The bake is 8² texels with four rays; what this waits for
        // is the pool getting to it at all, and a machine on which that takes ten seconds has a
        // problem this test cannot describe.
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (!baker.Wrote && DateTime.UtcNow < deadline) {
            tasks.Pump();
            Thread.Sleep(5);
        }

        Assert.True(baker.Wrote, "the bake never reached the write.");
        Assert.True(baker.BusyDuringWrite, "the one-at-a-time guard had been released before the write.");

        // And released once it is over, or the editor never imports again.
        tasks.Pump();

        Assert.False(tasks.IsBusy);
    }

    /// <summary>A baker that says whether the editor still thought it was busy while it wrote.</summary>
    sealed class RecordingBaker : IMeshMapBaker {
        /// <summary>What to ask, at the moment of the write.</summary>
        public Func<bool>? Busy { get; set; }

        /// <summary>What it answered, or null where the write never happened.</summary>
        public bool? BusyDuringWrite { get; private set; }

        /// <summary>Whether the write happened at all.</summary>
        public bool Wrote { get; private set; }

        /// <inheritdoc />
        public MeshMapSet Bake(AssetId model, string mesh, EditMesh source, EditMesh target, BakeSettings settings) =>
            throw new NotSupportedException("ContentTasks bakes and writes in two halves; it never calls this.");

        /// <inheritdoc />
        public MeshMapSet Write(
            AssetId model,
            string mesh,
            IReadOnlyList<MeshMapImage> images,
            IReadOnlyList<string> warnings
        ) {
            BusyDuringWrite = Busy?.Invoke();
            Wrote = true;

            return new(mesh, new Dictionary<MeshMapUsage, AssetReference>(), [], warnings);
        }
    }

    /// <summary>Whether a set's own warnings say the name it asked for was already taken.</summary>
    /// <remarks>
    ///     ⚠ <b>A phrase rather than a count, because a bake has warnings of its own.</b> The sheet
    ///     these tests bake produces one about face groups every time; asserting an empty list would
    ///     be asserting something about <c>MapBaker</c> and would go green the day it stopped saying
    ///     it, whichever way this writer behaved.
    /// </remarks>
    static bool Renamed(MeshMapSet set) =>
        set.Warnings.Any(warning => warning.Contains("was written as", StringComparison.Ordinal));

    /// <summary>Writing a set that was baked elsewhere is the same set, which is what lets it be off-thread.</summary>
    /// <remarks>
    ///     The editor bakes on a pool thread and writes on the frame thread, because a write means
    ///     <c>AssetDatabase.Scan</c> and every panel is reading that index. What this asserts is that
    ///     the split is a split and not a second code path.
    /// </remarks>
    [Fact]
    public void A_bake_and_a_separate_write_agree() {
        var whole = new ProjectMeshMapBaker(Project()).Bake(Barrel, "Barrel", Sheet(), Sheet(), Settings());

        var split = new ProjectMeshMapBaker(Project()).Write(
            Barrel,
            "Barrel",
            MeshMapBake.Run(Sheet(), Sheet(), Settings()),
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

    /// <summary>Two models, as fixed ids.</summary>
    /// <remarks>
    ///     ⚠ <b>Fixed rather than <c>AssetId.New()</c>, and it is not tidiness.</b> What separates a
    ///     re-bake from a collision is that the id is <i>the same one</i> on the second bake; a fresh
    ///     id per call would make every re-bake look like a collision and the suite would go green on
    ///     a writer that had no concept of a re-bake at all.
    /// </remarks>
    static readonly AssetId Barrel = AssetId.Parse("0f8c1d2e3a4b5c6d7e8f90a1b2c3d4e5");

    /// <inheritdoc cref="Barrel" />
    static readonly AssetId Crate = AssetId.Parse("1a2b3c4d5e6f708192a3b4c5d6e7f809");

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
