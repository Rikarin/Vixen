// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Threading;
using Vixen.Ecs;
using Vixen.Ecs.Systems;
using Vixen.Editor.Core;
using Vixen.Engine.Frames;
using Xunit;

namespace Vixen.Editor.SceneView.Tests;

/// <summary>A project's own declared systems run inside an in-editor session.</summary>
/// <remarks>
///     <para>
///         <b>The half of doc 11's seam <c>IPlaySystems</c> did not close.</b> That interface lets
///         whoever owns a service — the editor — add the systems that need it. This is the other
///         direction: the <em>project</em> declares a system with <c>[GameSystem]</c>, the generator
///         writes the constructor call, and a session resolves its parameters against the services
///         the contributions provided. Neither side was told about the other; they agree because a
///         service is keyed on its static type in both.
///     </para>
///     <para>
///         ⚠ <b><see cref="ProjectSystem" /> asks for a service nothing else in this assembly
///         provides, deliberately.</b> The registry is process-wide and every session reads all of
///         it, so a declared system that could be satisfied by accident would join the frame of every
///         other test in here — and <c>PlaySystemsTests</c> asserts on <c>Session.Running</c>
///         exactly.
///     </para>
/// </remarks>
public class PlayDeclaredSystemsTests {
    static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(16);

    /// <summary>
    ///     ⚠ <b>A frame, not a call.</b> Proving <c>AddDeclaredSystems</c> returned a name would
    ///     prove exactly what <c>ShouldTick</c>'s five tests proved while it had no caller.
    /// </summary>
    [Fact]
    public void A_declared_system_runs_when_a_contribution_provides_what_it_asks_for() {
        using var world = new World("Scene");

        var extensions = new EditorRegistry();

        using var provider = extensions.Add<IPlaySystems>(new SuppliesAWarehouse());
        using var play = new PlayModeController(world, extensions: extensions);

        ProjectSystem.Ran = 0;

        play.Play();

        Assert.Contains(nameof(ProjectSystem), play.Declared.Running);
        Assert.DoesNotContain(play.Declared.Missing, line => line.Contains(nameof(ProjectSystem), StringComparison.Ordinal));

        play.Tick(Frame);

        Assert.Equal(1, ProjectSystem.Ran);

        // And it is gone with the session: the loop it was added to is this session's.
        play.Stop();
        play.Tick(Frame);

        Assert.Equal(1, ProjectSystem.Ran);
    }

    /// <summary>
    ///     ⚠ <b>The rule the feature rests on.</b> A declared system whose service nobody provided is
    ///     named. Doc 11's standard for play mode is that a thing which does not happen must be
    ///     visibly not happening, and a frame quietly missing a system is exactly the failure that
    ///     gets read as a broken script.
    /// </summary>
    [Fact]
    public void A_declared_system_whose_service_is_absent_is_named() {
        using var world = new World("Scene");
        using var play = new PlayModeController(world);

        ProjectSystem.Ran = 0;

        play.Play();

        Assert.DoesNotContain(nameof(ProjectSystem), play.Declared.Running);

        var absent = Assert.Single(
            play.Declared.Missing,
            line => line.Contains(nameof(ProjectSystem), StringComparison.Ordinal)
        );

        Assert.Contains(nameof(Warehouse), absent, StringComparison.Ordinal);

        play.Tick(Frame);

        Assert.Equal(0, ProjectSystem.Ran);
    }

    /// <summary>
    ///     The session's own loop and world are askable, so a declared system can take either — which
    ///     a game's <c>ServiceRegistry</c> also offers, and a frame that differed on that point would
    ///     differ for a reason that has nothing to do with the editor.
    /// </summary>
    [Fact]
    public void A_session_offers_its_loop_and_its_world_as_services() {
        using var world = new World("Scene");
        using var loop = new EngineLoop(world);

        var session = new PlaySession(loop, world);
        var services = (IServiceProvider) session;

        Assert.Same(loop, services.GetService(typeof(EngineLoop)));
        Assert.Same(world, services.GetService(typeof(World)));
        Assert.Null(services.GetService(typeof(Warehouse)));
    }

    /// <summary>A contribution that owns a service the project's declared system asks for.</summary>
    sealed class SuppliesAWarehouse : IPlaySystems {
        public void Attach(PlaySession session) {
            session.Provide(session.Owns(new Warehouse()));
            session.Runs("the warehouse");
        }
    }
}

/// <summary>A service only this file's contribution provides.</summary>
public sealed class Warehouse : IDisposable {
    /// <inheritdoc />
    public void Dispose() { }
}

/// <summary>What a project would write: a system, marked, taking the service it needs.</summary>
[GameSystem]
public sealed class ProjectSystem(Warehouse warehouse) : SystemBase {
    /// <summary>How many frames have reached it.</summary>
    public static int Ran { get; set; }

    /// <summary>What it was handed.</summary>
    public Warehouse Warehouse { get; } = warehouse;

    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) {
        Ran++;

        return dependency;
    }
}
