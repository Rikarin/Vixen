// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Core.Threading;
using Vixen.Ecs;
using Vixen.Ecs.Systems;
using Vixen.Editor.Core;
using Vixen.Engine.Transforms;
using Xunit;

namespace Vixen.Editor.SceneView.Tests;

/// <summary>A contribution's systems run inside a session and are gone outside one.</summary>
/// <remarks>
///     <para>
///         <b>The general mechanism doc 11 said was missing.</b> An <c>EngineLoop</c>'s default graph
///         is behaviours, coroutines and transforms; everything else a game runs takes a service the
///         loop cannot invent. <c>IPlaySystems</c> is how whoever owns such a service adds the
///         systems that need it — and the assertions here are about *lifetime*, because that is the
///         half a registration call gets wrong: a physics world that outlives the session is one
///         simulating under a gizmo drag.
///     </para>
///     <para>
///         ⚠ <b>Each of these asserts a frame happened, not that a method was called.</b> That is
///         <c>PlayGraphTests</c>' own lesson one layer up: <c>ShouldTick</c> was covered five ways
///         while having no caller, so a test that only proved <c>Attach</c> ran would prove exactly
///         as much.
///     </para>
/// </remarks>
public class PlaySystemsTests {
    static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(16);

    [Fact]
    public void A_contributed_system_runs_in_the_session_and_not_before_it() {
        using var world = new World("Scene");
        var entity = Hierarchy.CreateTransform(world, LocalTransform.Identity);

        var extensions = new EditorRegistry();
        var contribution = new Nudge();

        using var registration = extensions.Add<IPlaySystems>(contribution);
        using var play = new PlayModeController(world, extensions: extensions);

        // Nothing is running, so nothing has attached — the editing half of "physics belongs to
        // play, not to editing", asserted about the world rather than about a flag.
        Assert.Null(play.Session);
        Assert.Equal(0f, world.Read<LocalTransform>(entity).Position.X);

        Assert.True(play.Play());
        Assert.NotNull(play.Session);
        Assert.Equal(["nudging"], play.Session.Running);
        Assert.Empty(play.Refused);

        Assert.True(play.Tick(Frame));
        Assert.True(play.Tick(Frame));

        Assert.Equal(2f, world.Read<LocalTransform>(entity).Position.X);
    }

    /// <summary>Stopping runs the teardown, and it runs before the world is put back.</summary>
    /// <remarks>
    ///     ⚠ <b>The entity count is the assertion that matters.</b> A contribution's bodies live on
    ///     entities in the world being edited; if the release ran <em>after</em> the restore it would
    ///     be asked to clean up handles that had already stopped existing, and if it never ran the
    ///     native world would outlive every session in the process.
    /// </remarks>
    [Fact]
    public void Stopping_releases_what_the_contribution_owns_while_the_world_is_still_there() {
        using var world = new World("Scene");
        Hierarchy.CreateTransform(world, LocalTransform.Identity);

        var extensions = new EditorRegistry();
        var contribution = new Nudge();

        using var registration = extensions.Add<IPlaySystems>(contribution);
        using var play = new PlayModeController(world, extensions: extensions);

        play.Play();
        play.Tick(Frame);

        Assert.NotNull(contribution.Owned);
        Assert.False(contribution.Owned.IsDisposed);

        play.Stop();

        Assert.True(contribution.Owned.IsDisposed);
        Assert.True(contribution.SawWorldAtStop, "the release ran after the restore had cleared the world");
        Assert.Null(play.Session);

        // And the graph is gone with it: a tick after a stop advances nothing.
        Assert.False(play.Tick(Frame));
    }

    /// <summary>A second session gets a second set, rather than the first one's leftovers.</summary>
    [Fact]
    public void Playing_again_attaches_again() {
        using var world = new World("Scene");
        var entity = Hierarchy.CreateTransform(world, LocalTransform.Identity);

        var extensions = new EditorRegistry();
        var contribution = new Nudge();

        using var registration = extensions.Add<IPlaySystems>(contribution);
        using var play = new PlayModeController(world, extensions: extensions);

        play.Play();
        play.Tick(Frame);
        play.Stop();

        Assert.Equal(1, contribution.Attaches);

        play.Play();
        play.Tick(Frame);

        Assert.Equal(2, contribution.Attaches);

        // ⚠ The restore put the entity back at the origin under a *new* handle, so this reads the
        // world rather than the handle it started with — and one frame of the second session is one
        // metre, not two.
        Assert.Equal(1f, Furthest(world));
    }

    /// <summary>A contribution that throws is named and the session runs without it.</summary>
    /// <remarks>
    ///     ⚠ <b>Not swallowed and not fatal.</b> Standing systems up takes native libraries and
    ///     devices, any of which can be missing on one machine; a Play button that refused to work
    ///     because audio could not open a device would be worse than one that plays without sound and
    ///     says so. What makes that honest is that the failure is named to the person.
    /// </remarks>
    [Fact]
    public void A_contribution_that_throws_is_named_and_the_rest_of_the_session_runs() {
        using var world = new World("Scene");
        Hierarchy.CreateTransform(world, LocalTransform.Identity);

        var extensions = new EditorRegistry();

        using var broken = extensions.Add<IPlaySystems>(new Refuses());
        using var working = extensions.Add<IPlaySystems>(new Nudge());
        using var play = new PlayModeController(world, extensions: extensions);

        Assert.True(play.Play());

        var named = Assert.Single(play.Refused);

        Assert.Contains(nameof(Refuses), named, StringComparison.Ordinal);
        Assert.Contains("no device", named, StringComparison.Ordinal);

        Assert.Equal(["nudging"], play.Session!.Running);
        Assert.True(play.Tick(Frame));
        Assert.Equal(1f, Furthest(world));
    }

    /// <summary>A later contribution finds what an earlier one provided.</summary>
    /// <remarks>
    ///     ⚠ <b>The ordering is the registration's, and it is the mechanism the editor's own physics
    ///     uses.</b> The application provides one <c>PhysicsScene</c>; the terrain module's collider
    ///     contribution asks for it rather than building a second, because two simulations over one
    ///     scene is a world in which nothing collides with anything and nothing is raised.
    /// </remarks>
    [Fact]
    public void A_later_contribution_asks_an_earlier_one_for_its_service() {
        using var world = new World("Scene");

        var extensions = new EditorRegistry();
        var consumer = new Consumer();

        using var first = extensions.Add<IPlaySystems>(new Provider());
        using var second = extensions.Add<IPlaySystems>(consumer);
        using var play = new PlayModeController(world, extensions: extensions);

        play.Play();

        Assert.NotNull(consumer.Found);
        Assert.Equal(["a shared thing", "something over it"], play.Session!.Running);
    }

    /// <summary>How far the one moving entity has got, whatever handle it has now.</summary>
    static float Furthest(World world) {
        var furthest = 0f;

        foreach (var chunk in world.Chunks(new QueryDescription().WithAll<LocalTransform>())) {
            foreach (var transform in chunk.ReadValues<LocalTransform>()[..chunk.Count]) {
                furthest = MathF.Max(furthest, transform.Position.X);
            }
        }

        return furthest;
    }

    /// <summary>A contribution that adds a system and owns something the session must dispose.</summary>
    sealed class Nudge : IPlaySystems {
        public int Attaches { get; private set; }

        public Native? Owned { get; private set; }

        public bool SawWorldAtStop { get; private set; }

        public void Attach(PlaySession session) {
            Attaches++;
            Owned = session.Owns(new Native());

            var world = session.World;

            session.Loop.Add(new NudgeSystem());
            session.OnStop(() => SawWorldAtStop = Alive(world));
            session.Runs("nudging");
        }

        /// <summary>Whether the world still has the entities the session was running over.</summary>
        static bool Alive(World world) {
            foreach (var chunk in world.Chunks(new QueryDescription().WithAll<LocalTransform>())) {
                if (chunk.Count > 0) {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>Stands in for a native world: something whose disposal is the whole point.</summary>
    sealed class Native : IDisposable {
        public bool IsDisposed { get; private set; }

        public void Dispose() => IsDisposed = true;
    }

    /// <summary>Moves everything one metre per frame, so "a frame happened" is readable off the world.</summary>
    [UpdateInGroup(SystemPhase.EarlyUpdate)]
    sealed class NudgeSystem : SystemBase, IDeclaredAccess {
        public SystemAccess Access { get; } = SystemAccess.Declare().Write<LocalTransform>().Build();

        public override JobHandle Update(in SystemContext context, JobHandle dependency) {
            foreach (var chunk in context.World.Chunks(new QueryDescription().WithAll<LocalTransform>())) {
                var transforms = chunk.Values<LocalTransform>();

                for (var index = 0; index < chunk.Count; index++) {
                    transforms[index].Position += new Vector3(1f, 0f, 0f);
                }
            }

            return dependency;
        }
    }

    /// <summary>A contribution whose service is missing on this machine.</summary>
    sealed class Refuses : IPlaySystems {
        public void Attach(PlaySession session) => throw new InvalidOperationException("no device");
    }

    /// <summary>Provides a service for whatever attaches after it.</summary>
    sealed class Provider : IPlaySystems {
        public void Attach(PlaySession session) {
            session.Provide(session.Owns(new Native()));
            session.Runs("a shared thing");
        }
    }

    /// <summary>And asks for one.</summary>
    sealed class Consumer : IPlaySystems {
        public Native? Found { get; private set; }

        public void Attach(PlaySession session) {
            if (session.TryGet<Native>(out var shared)) {
                Found = shared;
                session.Runs("something over it");
            }
        }
    }
}
