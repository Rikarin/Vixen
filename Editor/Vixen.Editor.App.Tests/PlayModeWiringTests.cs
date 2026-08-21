// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Editor.Testing;
using Vixen.Engine.Behaviors;
using Vixen.Engine.Transforms;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>Pressing Play makes the editor's frame advance the game.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>This is the test whose absence was the defect.</b> <c>PlayModeController</c>'s state
///         machine was covered five ways — pause, step, step-from-stopped, resume, a second press —
///         and <c>ShouldTick</c>, the method all five were about, had no caller in the product for as
///         long as it existed. Every one of those assertions passed against an editor in which Play
///         stepped nothing.
///     </para>
///     <para>
///         So this asserts through the <i>commands</i>, over a real session, and about the world
///         rather than about a flag: the thing that can only be true if
///         <c>EditorApplication.Update</c> is the caller.
///     </para>
/// </remarks>
public class PlayModeWiringTests {
    /// <inheritdoc cref="BehaviorAuthoringTests" path="/remarks/para[1]" />
    static PlayModeWiringTests() => SceneBehaviorRegistry.Register<Drifter>();

    [Fact]
    public void Playing_advances_the_scene_and_stopping_puts_it_back() {
        using var editor = EditorSession.Start();

        var scene = editor.Scene;
        var entity = scene.Add("Drifting", LocalTransform.Identity);

        scene.Behaviors.Add(entity, new Drifter());
        editor.Frames(2);

        // Nothing moves while the editor is editing, which is the other half of the contract: a
        // behaviour somebody authored holds values and has not run.
        Assert.Equal(0f, scene.World.Read<LocalTransform>(entity).Position.X);

        editor.Run("play.play");
        editor.Frames(8);

        var moved = Moved(scene.World);

        Assert.True(moved > 0f, "the behaviour never ran, so Play is still not stepping anything");

        editor.Run("play.stop");
        editor.Frames(2);

        // ⚠ Back where it was authored — and the behaviour is back too, with the values somebody
        // typed rather than the ones a session left in it.
        var restored = Assert.Single(Carriers(scene.World));

        Assert.Equal(0f, scene.World.Read<LocalTransform>(restored).Position.X);
        Assert.NotNull(scene.Behaviors.Get<Drifter>(restored));
    }

    /// <summary>Pause holds the frame, and Step Frame gives back exactly one.</summary>
    [Fact]
    public void Pausing_holds_the_frame_and_a_step_gives_back_one() {
        using var editor = EditorSession.Start();

        var scene = editor.Scene;

        scene.Behaviors.Add(scene.Add("Drifting", LocalTransform.Identity), new Drifter());

        editor.Run("play.play");
        editor.Frames(8);

        editor.Run("play.pause");
        editor.Frames(4);

        var held = Moved(scene.World);

        editor.Frames(4);
        Assert.Equal(held, Moved(scene.World));

        editor.Run("play.step");
        editor.Frames(4);

        // One frame's worth, however many editor frames went by — which is what makes Step Frame a
        // simulation step rather than a UI one.
        Assert.Equal(held + 1f, Moved(scene.World));

        editor.Run("play.stop");
    }

    /// <summary>Every entity carrying a behaviour, whichever store attached it.</summary>
    static List<Entity> Carriers(World world) {
        List<Entity> carriers = [];

        foreach (var chunk in world.Chunks(new QueryDescription().WithAll<BehaviorRef>())) {
            foreach (var entity in chunk.Entities[..chunk.Count]) {
                carriers.Add(entity);
            }
        }

        return carriers;
    }

    /// <summary>How far the drifting entity has got, in whole frames.</summary>
    static float Moved(World world) {
        var query = new QueryDescription().WithAll<LocalTransform, BehaviorRef>();
        var furthest = 0f;

        foreach (var chunk in world.Chunks(query)) {
            foreach (var transform in chunk.ReadValues<LocalTransform>()[..chunk.Count]) {
                furthest = MathF.Max(furthest, transform.Position.X);
            }
        }

        return furthest;
    }
}

/// <summary>A behaviour that moves its entity one metre per frame it is given.</summary>
/// <remarks>
///     ⚠ <b>One metre per <i>frame</i> rather than per second.</b> A rate would make the assertion
///     depend on what the harness uses as a frame delta, and "did the game advance" is a question
///     about whether a frame happened at all.
/// </remarks>
[DataContract("Drifter")]
public sealed class Drifter : Behavior {
    /// <inheritdoc />
    protected override void Update() {
        ref var local = ref World.Get<LocalTransform>(Entity);

        local.Position = local.Position + new Vector3(1f, 0f, 0f);
    }
}
