// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Editor.Core;
using Vixen.Editor.SceneView;
using Vixen.Engine.Transforms;
using Vixen.Geometry;
using Xunit;

namespace Vixen.Editor.Blockout.Tests;

/// <summary>Doc 24's Part 4: a random sequence of verbs, undone to empty and redone to the end.</summary>
/// <remarks>
///     ⚠ <b>"This is what catches a command that stored a reference where it needed a copy."</b> Every
///     verb has a test that says it did the right thing once; none of them says the <i>history</i> is
///     right, and a history that is wrong is wrong three steps later in a mesh somebody has spent an
///     hour on. So this records the whole mesh after every step, walks all the way back asserting each
///     one, and walks forward again asserting the same.
/// </remarks>
public class BlockoutHistoryTests : IDisposable {
    readonly string root = Path.Combine(Path.GetTempPath(), "vixen-history-" + Guid.NewGuid().ToString("N"));
    readonly EditorProject project;
    readonly World world = new("Test");
    readonly SceneDocument scene;
    readonly MeshEdit editing;
    readonly TransformSystem transforms = new();

    public BlockoutHistoryTests() {
        Directory.CreateDirectory(root);

        project = new(new ProjectPaths(root));
        scene = new(project, world, AssetId.Empty, "Untitled");
        editing = new(scene);
    }

    public void Dispose() {
        world.Dispose();

        if (Directory.Exists(root)) {
            Directory.Delete(root, true);
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>Everything about a mesh that an undo has to put back.</summary>
    /// <remarks>
    ///     ⚠ <b>The corners and the groups as well as the positions.</b> A command that restored the
    ///     positions and left the face table renumbered would pass every "is it still solid" check and
    ///     would have moved every selection, every material assignment and every smoothing group onto
    ///     different faces.
    /// </remarks>
    static string State(EditMesh? mesh) {
        if (mesh is null) {
            return "none";
        }

        var text = new System.Text.StringBuilder();

        foreach (var position in mesh.Positions) {
            text.Append(position.X.ToString("F4", System.Globalization.CultureInfo.InvariantCulture))
                .Append(',')
                .Append(position.Y.ToString("F4", System.Globalization.CultureInfo.InvariantCulture))
                .Append(',')
                .Append(position.Z.ToString("F4", System.Globalization.CultureInfo.InvariantCulture))
                .Append(' ');
        }

        text.Append('|');

        foreach (var corner in mesh.Corners) {
            text.Append(corner).Append(',');
        }

        text.Append('|');

        foreach (var face in mesh.Faces) {
            text.Append(face.Count).Append(':').Append(face.Group).Append(':').Append(face.Smoothing).Append(' ');
        }

        return text.ToString();
    }

    string Snapshot(Entity entity) =>
        State(scene.MeshOf(entity))
        + "|shape:" + (scene.ShapeOf(entity)?.ToString() ?? "none")
        + "|derived:" + scene.IsDerived(entity);

    void Settle() {
        transforms.Resolve(world);
        world.AdvanceVersion();
    }

    /// <summary>The verbs a random walk chooses from, each named so a failure says which one.</summary>
    static (string Name, Func<MeshEdit, bool> Run)[] Verbs => [
        ("extrude", editing => Face(editing) && BlockoutGeometry.Extrude(editing, 0.7f)),
        ("extrude-individual", editing => Face(editing) && BlockoutGeometry.Extrude(editing, 0.4f, individually: true)),
        ("inset", editing => Face(editing) && BlockoutGeometry.Inset(editing, 0.2f)),
        ("bevel", editing => Edge(editing) && BlockoutGeometry.Bevel(editing, 0.1f, 1, out _)),
        ("loop-cut", editing => Edge(editing) && BlockoutGeometry.LoopCut(editing)),
        ("subdivide", editing => Face(editing) && BlockoutGeometry.Subdivide(editing)),
        ("flip", editing => Face(editing) && BlockoutGeometry.Flip(editing)),
        ("delete", editing => Face(editing) && BlockoutGeometry.Delete(editing)),
        ("dissolve", editing => Edge(editing) && BlockoutGeometry.Dissolve(editing)),
        ("weld", editing => Vertex(editing) && BlockoutGeometry.Weld(editing)),
        ("project", editing => Face(editing) && BlockoutSurfaces.Project(editing)),
        ("auto-smooth", editing => Face(editing) && BlockoutSurfaces.AutoSmooth(editing)),
        ("regroup", editing => Face(editing) && BlockoutSurfaces.Regroup(editing)),
        ("plane-cut", editing => Cut(editing))
    ];

    static bool Face(MeshEdit editing) => Pick(editing, MeshElementKind.Face, 1);

    static bool Edge(MeshEdit editing) => Pick(editing, MeshElementKind.Edge, 1);

    static bool Vertex(MeshEdit editing) => Pick(editing, MeshElementKind.Vertex, 2);

    static bool Cut(MeshEdit editing) {
        var document = editing.Document;

        document.Selection.Set(editing.Target);

        return BlockoutBoolean.PlaneCut(document, new Plane(Vector3.UnitY, -0.6f)) > 0;
    }

    /// <summary>Chooses a few elements of a kind, deterministically from the mesh's own size.</summary>
    static bool Pick(MeshEdit editing, MeshElementKind kind, int count) {
        if (editing.Mesh is not { } mesh) {
            return false;
        }

        editing.Element = kind;

        var total = MeshSelection.Total(mesh, kind);

        if (total < count) {
            return false;
        }

        List<int> chosen = [];

        for (var index = 0; index < count; index++) {
            chosen.Add((total / (count + 1) * (index + 1)) % total);
        }

        editing.Selection.Set(chosen.Distinct().ToArray());

        return !editing.Selection.IsEmpty;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(13)]
    [InlineData(29)]
    [InlineData(101)]
    [InlineData(1009)]
    public void A_random_sequence_of_verbs_undoes_and_redoes_exactly(int seed) {
        var entity = BlockoutCreate.Shape(
            scene,
            new ShapeParameters { Kind = ShapeKind.Box, Size = new(2f, 2f, 2f) }
        );

        Settle();
        scene.Selection.Set(entity);
        editing.Enter(MeshElementKind.Face);

        // ⚠ The one-way door opened up front, so the walk below exercises the verbs rather than the
        // demotion. The first edit to a parametric shape is legitimately *two* entries — the door and
        // the edit — and that is asserted on its own below; mixing it into the walk would make every
        // sequence start with the one step that is allowed to break the rule.
        Assert.True(editing.Demote());

        var random = new Random(seed);
        var verbs = Verbs;

        List<(string Name, string State, int History)> steps = [(("start"), Snapshot(entity), scene.Stack.History.Count)];

        for (var step = 0; step < 24; step++) {
            var verb = verbs[random.Next(verbs.Length)];
            var before = scene.Stack.History.Count;

            if (!verb.Run(editing) || scene.MeshOf(entity) is null) {
                continue;
            }

            Settle();

            // ⚠ Every verb is one entry. A verb that pushed two — a demotion and an edit, say — would
            // undo halfway and leave the mesh in a state the designer never saw, which is exactly the
            // "bugged undo" that is invisible until somebody presses Ctrl+Z twice.
            var after = scene.Stack.History.Count;

            Assert.True(
                after == before + 1,
                $"seed {seed} step {step} '{verb.Name}' pushed {after - before} entries"
            );

            steps.Add((verb.Name, Snapshot(entity), after));
        }

        Assert.True(steps.Count > 4, $"seed {seed} did almost nothing: {steps.Count} steps");

        // All the way back, asserting the state after each undo is the one that step started from.
        for (var step = steps.Count - 1; step > 0; step--) {
            Assert.True(scene.Stack.Undo(), $"seed {seed}: undo {step} refused");
            Settle();

            Assert.Equal(steps[step - 1].State, Snapshot(entity));
        }

        // And forward again.
        for (var step = 1; step < steps.Count; step++) {
            Assert.True(scene.Stack.Redo(), $"seed {seed}: redo {step} refused");
            Settle();

            Assert.Equal(steps[step].State, Snapshot(entity));
        }
    }

    [Fact]
    public void A_verb_that_changes_the_element_mode_is_still_one_undo_entry() {
        var entity = BlockoutCreate.Shape(scene, ShapeKind.Box);

        Settle();
        scene.Selection.Set(entity);
        editing.Enter(MeshElementKind.Face);

        // The demotion and the extrude are two acts and two entries, which is the one case where two
        // is right — a designer is entitled to step back over the door on its own.
        editing.Selection.Set(0);

        var before = scene.Stack.History.Count;

        Assert.True(BlockoutGeometry.Extrude(editing, 1f));
        Assert.Equal(before + 2, scene.Stack.History.Count);

        // Every verb after it is one, because there is no door left to open.
        for (var step = 0; step < 3; step++) {
            var count = scene.Stack.History.Count;

            editing.Element = MeshElementKind.Face;
            editing.Selection.Set(0);

            Assert.True(BlockoutGeometry.Inset(editing, 0.1f));
            Assert.Equal(count + 1, scene.Stack.History.Count);
        }
    }
}
