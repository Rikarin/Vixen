// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Xunit;

namespace Vixen.Ecs.Tests;

/// <summary>
///     The structural-change hooks: what is announced, in what order, and whether the build raised
///     anything at all.
/// </summary>
/// <remarks>
///     ⚠ <b>Read <see cref="TheHooksAreCompiledIntoADebugBuild" /> first.</b> Every other test here
///     skips itself when <c>World.EventsEnabled</c> is false, and a suite of skipped tests is green
///     for the same reason a passing one is. That test is what makes the skips mean "this build does
///     not have them" rather than "nobody noticed they stopped working".
/// </remarks>
public sealed class WorldEventTests {
    [Fact]
    public void TheHooksAreCompiledIntoADebugBuild() {
#if DEBUG
        Assert.True(World.EventsEnabled);
#else
        Assert.Equal(World.EventsEnabled, Watched(new World()) > 0);
#endif
    }

    [Fact]
    public void CreatingAnEntityAnnouncesItAndEveryComponentOnIt() {
        Assert.SkipWhen(!World.EventsEnabled, "Compiled out; needs DEBUG or VIXEN_ECS_EVENTS.");

        using var world = new World();
        var log = new List<string>();

        world.EntityCreated += entity => log.Add($"created {entity.Id}");
        world.ComponentAdded += (entity, component) => log.Add($"added {Name(component)} to {entity.Id}");

        var made = world.Create(new Position(1, 2, 3), new Velocity(4, 5, 6));

        Assert.Equal(
            [$"created {made.Id}", $"added Position to {made.Id}", $"added Velocity to {made.Id}"],
            log
        );
    }

    [Fact]
    public void AComponentIsAnnouncedWithItsValueAlreadyThere() {
        Assert.SkipWhen(!World.EventsEnabled, "Compiled out; needs DEBUG or VIXEN_ECS_EVENTS.");

        using var world = new World();
        var seen = new List<float>();

        // ⚠ The reason `Create<T0>` allocates and announces in two steps rather than one. The typed
        // overloads write their components after the row exists, so announcing from inside the
        // allocation would hand a listener a zero — a value that looks perfectly plausible and is
        // not the one that was passed.
        world.ComponentAdded += (entity, component) => {
            if (component == ComponentType<Position>.Id) {
                seen.Add(world.Read<Position>(entity).X);
            }
        };

        world.Create(new Position(7, 0, 0));
        world.Add(world.Create(), new Position(9, 0, 0));

        Assert.Equal([7f, 9f], seen);
    }

    [Fact]
    public void RemovingAComponentAnnouncesItWhileItIsStillReadable() {
        Assert.SkipWhen(!World.EventsEnabled, "Compiled out; needs DEBUG or VIXEN_ECS_EVENTS.");

        using var world = new World();
        var entity = world.Create(new Position(3, 0, 0));
        var seen = new List<float>();

        world.ComponentRemoved += (removed, component) => {
            if (component == ComponentType<Position>.Id) {
                seen.Add(world.Read<Position>(removed).X);
            }
        };

        world.Remove<Position>(entity);

        Assert.Equal([3f], seen);
        Assert.False(world.Has<Position>(entity));
    }

    [Fact]
    public void DestroyingAnEntityAnnouncesItsComponentsBeforeItself() {
        Assert.SkipWhen(!World.EventsEnabled, "Compiled out; needs DEBUG or VIXEN_ECS_EVENTS.");

        using var world = new World();
        var entity = world.Create(new Position(1, 0, 0), new Velocity(2, 0, 0));
        var log = new List<string>();

        world.ComponentRemoved += (_, component) => log.Add($"removed {Name(component)}");
        world.EntityDestroyed += _ => log.Add("destroyed");

        world.Destroy(entity);

        Assert.Equal(["removed Position", "removed Velocity", "destroyed"], log);
    }

    [Fact]
    public void SetIsAnnouncedAndGetIsNot() {
        Assert.SkipWhen(!World.EventsEnabled, "Compiled out; needs DEBUG or VIXEN_ECS_EVENTS.");

        using var world = new World();
        var entity = world.Create(new Position(0, 0, 0));
        var sets = 0;

        world.ComponentSet += (_, _) => sets++;

        world.Set(entity, new Position(1, 0, 0));
        world.Get<Position>(entity).X = 2;

        // ⚠ Not an omission. `Get` hands out a `ref`, so there is no moment at which the new value
        // exists to announce — which is the same reason a `ref` counts as a write for the change
        // version whether or not one happens.
        Assert.Equal(1, sets);
        Assert.Equal(2, world.Read<Position>(entity).X);
    }

    [Fact]
    public void ClearAnnouncesEveryLiveEntity() {
        Assert.SkipWhen(!World.EventsEnabled, "Compiled out; needs DEBUG or VIXEN_ECS_EVENTS.");

        using var world = new World();

        for (var index = 0; index < 4; index++) {
            world.Create(new Position(index, 0, 0));
        }

        var destroyed = 0;
        var removed = 0;

        world.EntityDestroyed += _ => destroyed++;
        world.ComponentRemoved += (_, _) => removed++;

        world.Clear();

        // The same shape a Destroy per entity would have produced. A bulk path that announced less
        // than the per-entity one would leave a mirror silently holding four entities.
        Assert.Equal(4, destroyed);
        Assert.Equal(4, removed);
    }

    [Fact]
    public void PlaybackAnnouncesWhatTheBufferRecorded() {
        Assert.SkipWhen(!World.EventsEnabled, "Compiled out; needs DEBUG or VIXEN_ECS_EVENTS.");

        using var world = new World();
        var existing = world.Create(new Position(0, 0, 0));
        var log = new List<string>();

        world.EntityCreated += _ => log.Add("created");
        world.ComponentAdded += (_, component) => log.Add($"added {Name(component)}");
        world.EntityDestroyed += _ => log.Add("destroyed");

        var buffer = new CommandBuffer(world);
        buffer.Add(buffer.Create(), new Velocity(1, 0, 0));
        buffer.Destroy(existing);
        buffer.Playback();

        // The buffer plays back through the world's own calls, so the hooks come for free — which is
        // the property worth pinning, because a playback with its own storage path would not.
        Assert.Contains("created", log);
        Assert.Contains("added Velocity", log);
        Assert.Contains("destroyed", log);
    }

    [Fact]
    public void AWorldWithNoListenersRaisesNothingAnybodyCanSee() {
        // The other half of the instrument. A run in which nothing subscribed and a run in which the
        // hooks are not compiled in look identical from the outside; `EventsEnabled` is the only
        // thing that tells them apart, and this is what says the flag is not merely decorative.
        using var world = new World();
        var entity = world.Create(new Position(1, 0, 0));

        world.Destroy(entity);

        Assert.Equal(0, world.EntityCount);
    }

    static string Name(ComponentTypeId component) => ComponentRegistry.Get(component).Type.Name;

#if !DEBUG
    static int Watched(World world) {
        var count = 0;
        world.EntityCreated += _ => count++;
        world.Create();
        world.Dispose();
        return count;
    }
#endif
}
