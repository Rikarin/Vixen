// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Editor.Core;
using Vixen.Editor.Core.Scenes;
using Vixen.Editor.SceneView;
using Vixen.Editor.Ui;
using Vixen.Engine.Transforms;
using Vixen.Geometry;
using Xunit;

namespace Vixen.Editor.Blockout.Tests;

/// <summary>#1307: the mode's verbs as a service — reachable without a window, and reached by every command.</summary>
/// <remarks>
///     Two different claims and two tests for them, which is doc 50 § D2's own split: the verb works
///     (called on <see cref="BlockoutVerbs" /> with no shell), and the wiring holds (every command
///     the mode registers reaches its method). The second is the "finished thing nothing calls" rule
///     applied to the new surface — an <see cref="IBlockout" /> method no command runs fails it, by
///     reflection over the interface, so adding a verb without a command cannot pass silently.
/// </remarks>
public class BlockoutVerbTests : IDisposable {
    readonly string root = Path.Combine(Path.GetTempPath(), "vixen-verbs-" + Guid.NewGuid().ToString("N"));
    readonly EditorProject project;
    readonly World world = new("Test");
    readonly SceneDocument scene;
    readonly TransformSystem transforms = new();

    public BlockoutVerbTests() {
        Directory.CreateDirectory(root);

        project = new(new ProjectPaths(root));
        scene = new(project, world, AssetId.Empty, "Untitled");
    }

    public void Dispose() {
        world.Dispose();

        if (Directory.Exists(root)) {
            Directory.Delete(root, true);
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>Records which <see cref="IBlockout" /> methods were called and answers each with its default.</summary>
    public class Recorder : DispatchProxy {
        /// <summary>The method names, in call order.</summary>
        public List<string> Called { get; } = [];

        /// <inheritdoc />
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) {
            Called.Add(targetMethod!.Name);

            return targetMethod.ReturnType == typeof(void) || !targetMethod.ReturnType.IsValueType
                ? null
                : Activator.CreateInstance(targetMethod.ReturnType);
        }
    }

    Entity Box(Vector3 size, Vector3 at = default) {
        var entity = BlockoutCreate.Shape(scene, new ShapeParameters { Kind = ShapeKind.Box, Size = size }, at);

        transforms.Resolve(world);
        world.AdvanceVersion();

        return entity;
    }

    [Fact]
    public void A_verb_runs_with_no_shell_and_no_window() {
        var mode = new BlockoutMode { Editing = new MeshEdit(scene) };
        var wall = Box(new(4f, 3f, 0.5f));
        var hole = Box(new(1f, 2f, 2f));

        scene.Selection.Set([wall, hole]);

        var result = mode.Verbs.Subtract();

        Assert.NotEqual(Entity.Null, result);
        Assert.True(scene.IsDerived(result));

        // One undo, through the document's own stack: the service is the same verb the menu runs.
        Assert.True(scene.Stack.Undo());
        Assert.False(world.IsAlive(result));
    }

    [Fact]
    public void A_verb_with_nothing_to_act_on_does_nothing_rather_than_throwing() {
        var verbs = new BlockoutMode().Verbs;

        Assert.Equal(Entity.Null, verbs.Union());
        Assert.Equal(Entity.Null, verbs.CreateShape());
        Assert.False(verbs.Extrude());
        Assert.Equal(0, verbs.Duplicate());
        Assert.False(verbs.Export(".obj"));
    }

    [Fact]
    public void The_mode_reads_its_dials_into_the_verbs_at_the_moment_they_run() {
        var mode = new BlockoutMode { Editing = new MeshEdit(scene) };
        var cube = Box(new(1f, 1f, 1f));

        scene.Selection.Set(cube);
        mode.Copies = 4;

        Assert.Equal(4, mode.Verbs.Repeat(radial: false));
    }

    /// <summary>The loop cut the verb makes is the one the hover drew, at the mode's count and slide — #1485.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The preview read <c>LoopCuts</c> and <c>LoopSlide</c>; the verb ran with the
    ///         defaults.</b> So with three loops on the dial the viewport drew three and Ctrl+Shift+R cut
    ///         one, latent only because nothing yet sets either dial.
    ///     </para>
    ///     <para>
    ///         <b>Closed form, on a unit box.</b> Every ring edge is one unit long, so each new
    ///         position's distance to the nearest original corner is <c>min(t, 1 - t)</c> for the
    ///         parameter it was cut at: <c>slide</c> for one cut, <c>k / (n + 1)</c> for <c>n</c>. Every
    ///         row differs from the default's four halves, so a verb that ignores the dials is red on
    ///         each — and the previewed points must be among the positions made, which is the
    ///         agreement the remark on <c>BlockoutGeometry.LoopCut</c> promises.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData(1, 0.25f, new[] { 0.25f, 0.25f, 0.25f, 0.25f })]
    [InlineData(2, 0.5f, new[] { 1f / 3f, 1f / 3f, 1f / 3f, 1f / 3f, 1f / 3f, 1f / 3f, 1f / 3f, 1f / 3f })]
    [InlineData(3, 0.5f, new[] { 0.25f, 0.25f, 0.25f, 0.25f, 0.25f, 0.25f, 0.25f, 0.25f, 0.5f, 0.5f, 0.5f, 0.5f })]
    public void The_loop_cut_verb_cuts_at_the_count_and_slide_the_preview_draws(int cuts, float slide, float[] expected) {
        var editing = new MeshEdit(scene);
        var mode = new BlockoutMode { Editing = editing, LoopCuts = cuts, LoopSlide = slide };
        var box = Box(new(1f, 1f, 1f));

        scene.Selection.Set(box);
        editing.Enter(MeshElementKind.Edge);
        editing.Selection.Set([0]);

        var before = scene.MeshOf(box)!;
        var corners = before.Positions.ToArray();
        var faces = before.FaceCount;
        List<(Vector3 A, Vector3 B)> preview = [];

        Assert.True(BlockoutHover.LoopCut(before, 0, mode.LoopCuts, mode.LoopSlide, preview));
        Assert.True(mode.Verbs.LoopCut());

        var after = scene.MeshOf(box)!;
        var made = after.Positions.ToArray()[corners.Length..];

        Assert.Equal(faces + 4 * cuts, after.FaceCount);

        var fractions = made
            .Select(position => corners.Min(corner => Vector3.Distance(corner, position)))
            .Order()
            .ToArray();

        Assert.Equal(expected.Length, fractions.Length);

        for (var index = 0; index < expected.Length; index++) {
            Assert.Equal(expected[index], fractions[index], 4);
        }

        foreach (var (a, b) in preview) {
            Assert.Contains(made, position => Vector3.Distance(position, a) < 1e-4f);
            Assert.Contains(made, position => Vector3.Distance(position, b) < 1e-4f);
        }
    }

    [Fact]
    public void Every_verb_is_reached_by_a_command() {
        using var shell = new EditorShell(1280f, 800f);
        var mode = new BlockoutMode();
        var recorder = DispatchProxy.Create<IBlockout, Recorder>();

        mode.Verbs = recorder;

        shell.Modes.Add(new SelectMode());
        shell.Modes.Add(mode);
        shell.Modes.Changed += modes => shell.Context = modes.Context ?? "scene";

        Assert.True(shell.Modes.Activate(BlockoutMode.ModeId));

        // Into an element mode, or every element verb is disabled and runs nothing.
        Assert.True(shell.Commands.Execute(BlockoutMode.ElementCommand(BlockoutElement.Face)));

        foreach (var id in BlockoutMode.SelectionCommands
                     .Concat(BlockoutMode.GeometryCommands)
                     .Concat(BlockoutMode.CreationCommands)
                     .Concat(BlockoutMode.SurfaceCommands)
                     .Concat(BlockoutMode.BooleanCommands)
                     .Concat(BlockoutMode.HandoffCommands)) {
            shell.Commands.Execute(id);
        }

        var called = ((Recorder)(object)recorder).Called.ToHashSet(StringComparer.Ordinal);
        var declared = typeof(IBlockout).GetMethods().Select(static method => method.Name).ToHashSet(StringComparer.Ordinal);

        Assert.Empty(declared.Except(called).Order());
    }
}
