// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Threading;
using Vixen.Ecs;
using Vixen.Ecs.Systems;
using Vixen.Editor.Core;
using Vixen.Engine.Transforms;
using Xunit;

namespace Vixen.Editor.SceneView.Tests;

/// <summary>Doc 50 § P2a's exit: play mode entered and left through the service, with no editor and no window.</summary>
/// <remarks>
///     ⚠ <b>The shell-driven play tests in <c>Vixen.Editor.App.Tests</c> stay</b> — they prove the
///     wiring (F5 reaches <c>PlayModule</c>, <c>PlayModule</c> reaches this). These prove the verb,
///     which until #1289 could not be called without opening a whole <c>EditorSession</c>.
/// </remarks>
public class EditorPlaySessionTests {
    static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(16);

    [Fact]
    public void Entering_and_leaving_play_restores_the_world_and_translates_the_selection() {
        using var world = new World("Scene");
        var entity = Hierarchy.CreateTransform(world, LocalTransform.Identity);
        var selection = new Selection<Entity>();

        selection.Set(entity);

        using var controller = new PlayModeController(world);
        IPlaySession play = new EditorPlaySession(controller, () => selection);

        var entered = 0;
        var left = 0;

        play.Entered += _ => entered++;
        play.Left += _ => left++;

        Assert.True(play.Enter());
        Assert.Equal(PlayState.Playing, play.State);
        Assert.NotNull(play.Current);
        Assert.Equal(1, entered);

        // A second press resumes rather than restarting, and is not a second entry.
        Assert.False(play.Enter());
        Assert.Equal(1, entered);

        var restored = play.Leave();

        Assert.Equal(PlayState.Editing, play.State);
        Assert.Null(play.Current);
        Assert.Equal(1, left);

        // ⚠ The selection now names the restored world's handle rather than the one the snapshot
        // threw away — which is what `LeavePlay` did by hand, and what a plugin leaving play through
        // the service has to get without knowing it.
        var now = Assert.Single(selection);

        Assert.Equal(Assert.Single(restored), now);
        Assert.True(world.IsAlive(now));
    }

    [Fact]
    public void Pause_and_step_advance_exactly_one_frame() {
        using var world = new World("Scene");
        using var controller = new PlayModeController(world);
        var play = new EditorPlaySession(controller, () => new Selection<Entity>());

        play.Enter();
        play.TogglePause();

        Assert.Equal(PlayState.Paused, play.State);

        // Paused: no frame.
        Assert.False(play.Tick(Frame));

        play.StepFrame();

        Assert.True(play.Tick(Frame));
        Assert.False(play.Tick(Frame));

        play.TogglePause();

        Assert.Equal(PlayState.Playing, play.State);
        Assert.True(play.Tick(Frame));

        play.Leave();
    }

    /// <summary>
    ///     The report names a project's own <c>ISystem</c> that is not marked <c>[GameSystem]</c>, by
    ///     type name — and says nothing about the project when there is none.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Not "names nothing" with no project.</b> This assembly declares <c>[GameSystem]</c>
    ///     fixtures for other suites whose services a bare session lacks, and those are reported
    ///     honestly — the half that depends on the project is the one asserted.
    /// </remarks>
    [Fact]
    public void The_report_names_a_project_system_that_is_not_marked() {
        using var world = new World("Scene");
        using var controller = new PlayModeController(world);
        var play = new EditorPlaySession(controller, () => new Selection<Entity>());

        play.Enter();

        Assert.StartsWith("Play mode runs behaviours, coroutines and transforms", play.DescribeRunning());
        Assert.DoesNotContain("does not mark [GameSystem]", play.DescribeMissing(project: null) ?? string.Empty);

        var said = play.DescribeMissing(typeof(UnmarkedSystem).Assembly);

        Assert.NotNull(said);
        Assert.StartsWith("Not running — ", said);
        Assert.Contains(nameof(UnmarkedSystem), said);

        play.Leave();
    }

    /// <summary>A system a project declares and does not opt into the editor's session.</summary>
    sealed class UnmarkedSystem : SystemBase {
        public override JobHandle Update(in SystemContext context, JobHandle dependency) => dependency;
    }
}
