// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Ecs;
using Vixen.Net;
using Vixen.Net.Messaging;
using Vixen.Net.Replication;
using Xunit;

namespace Vixen.Live.Realm.Tests;

/// <summary>
///     The payload, round-tripped between two worlds — which is what "realm to realm" means when
///     there is only one process.
/// </summary>
public class HandoffCodecTests {
    static readonly PlayerKey Bruna = new(Guid.NewGuid(), Guid.NewGuid());

    [Fact]
    public void A_player_arrives_with_the_state_they_left_with() {
        var source = new World();
        var target = new World();

        var here = source.Create();

        source.Add(here, new Position { X = 12.5f, Y = -3.25f });
        source.Add(here, new Cooldown { Remaining = 4.75f });

        Span<byte> buffer = stackalloc byte[512];
        var payload = HandoffCodec.Write(source, here, Replicators, buffer);

        Assert.NotEmpty(payload);

        var there = target.Create();

        Assert.True(HandoffCodec.Apply(target, there, payload.AsSpan(), Replicators, out var applied));
        Assert.Equal(2, applied);

        Assert.Equal(12.5f, target.Get<Position>(there).X);
        Assert.Equal(-3.25f, target.Get<Position>(there).Y);
        Assert.Equal(4.75f, target.Get<Cooldown>(there).Remaining);
    }

    /// <summary>
    ///     A fixed slot per replicator would make the payload grow with the game's component
    ///     vocabulary rather than with what this player actually is.
    /// </summary>
    [Fact]
    public void A_component_the_player_does_not_have_is_not_written() {
        var source = new World();
        var target = new World();

        var here = source.Create();

        source.Add(here, new Position { X = 1, Y = 2 });

        Span<byte> buffer = stackalloc byte[512];
        var payload = HandoffCodec.Write(source, here, Replicators, buffer);

        var there = target.Create();

        Assert.True(HandoffCodec.Apply(target, there, payload.AsSpan(), Replicators, out var applied));
        Assert.Equal(1, applied);
        Assert.True(target.Has<Position>(there));
        Assert.False(target.Has<Cooldown>(there));
    }

    [Fact]
    public void A_player_with_nothing_on_them_still_round_trips() {
        var source = new World();
        var target = new World();

        Span<byte> buffer = stackalloc byte[64];
        var payload = HandoffCodec.Write(source, source.Create(), Replicators, buffer);

        Assert.True(HandoffCodec.Apply(target, target.Create(), payload.AsSpan(), Replicators, out var applied));
        Assert.Equal(0, applied);
    }

    /// <summary>
    ///     ⚠ Refusing is what leaves the transfer to fail rather than half-applying. The source has
    ///     not committed — it is waiting for an acknowledgement this cannot now send — so the player
    ///     is still being simulated where they were.
    /// </summary>
    [Fact]
    public void A_truncated_payload_is_refused_rather_than_half_applied() {
        var source = new World();
        var target = new World();

        var here = source.Create();

        source.Add(here, new Position { X = 12.5f, Y = -3.25f });
        source.Add(here, new Cooldown { Remaining = 4.75f });

        Span<byte> buffer = stackalloc byte[512];
        var payload = HandoffCodec.Write(source, here, Replicators, buffer);

        var there = target.Create();

        Assert.False(HandoffCodec.Apply(target, there, payload.AsSpan()[..^2], Replicators, out var applied));

        // ⚠ Nothing, and not "fewer than two". The first record reads cleanly and the second runs
        // out, so a codec that applies as it goes leaves a Position on an entity whose transfer was
        // refused — a player who is somewhere with the wrong body, which is the one outcome this
        // design is built to make impossible. `applied < 2` was true of that codec too.
        Assert.Equal(0, applied);
        Assert.False(target.Has<Position>(there));
        Assert.False(target.Has<Cooldown>(there));
    }

    /// <summary>
    ///     A record this build cannot read is refused with the ones before it left off, for the same
    ///     reason: an unknown type id cannot be skipped, so everything after it is unreadable and
    ///     everything before it must not stand.
    /// </summary>
    [Fact]
    public void A_payload_whose_last_record_is_unknown_applies_none_of_it() {
        var source = new World();
        var target = new World();

        var here = source.Create();

        source.Add(here, new Position { X = 1, Y = 2 });
        source.Add(here, new Cooldown { Remaining = 3 });

        Span<byte> buffer = stackalloc byte[512];
        var payload = HandoffCodec.Write(source, here, Replicators, buffer);

        var there = target.Create();

        // The receiving build knows Position and has never heard of Cooldown, which is written second.
        Assert.False(HandoffCodec.Apply(target, there, payload.AsSpan(), [new PositionReplicator()], out var applied));

        Assert.Equal(0, applied);
        Assert.False(target.Has<Position>(there));
    }

    /// <summary>The rehearsal does not leave anything of its own behind.</summary>
    /// <remarks>
    ///     Applying a payload twice — once onto a scratch entity to find out whether all of it reads,
    ///     then onto the real one — is the only way to be atomic over records that are not
    ///     length-prefixed. The scratch entity is this codec's business and nobody else's, so a world
    ///     it was used on has to end up holding exactly the entity the caller asked about.
    /// </remarks>
    [Fact]
    public void The_rehearsal_leaves_no_entity_of_its_own() {
        var source = new World();
        var target = new World();

        var here = source.Create();

        source.Add(here, new Position { X = 1, Y = 2 });

        Span<byte> buffer = stackalloc byte[512];
        var payload = HandoffCodec.Write(source, here, Replicators, buffer);

        var there = target.Create();
        var before = target.EntityCount;

        Assert.True(HandoffCodec.Apply(target, there, payload.AsSpan(), Replicators, out _));
        Assert.Equal(before, target.EntityCount);

        Assert.False(HandoffCodec.Apply(target, there, payload.AsSpan()[..^2], Replicators, out _));
        Assert.Equal(before, target.EntityCount);
    }

    [Fact]
    public void A_payload_from_another_framing_version_is_refused() {
        var target = new World();
        Span<byte> payload = [HandoffCodec.Version + 1, 0, 0, 0];

        Assert.False(HandoffCodec.Apply(target, target.Create(), payload, Replicators, out _));
    }

    [Fact]
    public void An_empty_payload_is_refused() {
        var target = new World();

        Assert.False(HandoffCodec.Apply(target, target.Create(), [], Replicators, out _));
    }

    /// <summary>
    ///     Unreachable in a fleet whose versions are filtered (ADR-022), and checked anyway: the
    ///     records are not length-prefixed, so an unknown id cannot be skipped past.
    /// </summary>
    [Fact]
    public void A_component_this_build_does_not_know_is_refused() {
        var source = new World();
        var target = new World();

        var here = source.Create();

        source.Add(here, new Cooldown { Remaining = 1 });

        Span<byte> buffer = stackalloc byte[512];
        var payload = HandoffCodec.Write(source, here, Replicators, buffer);

        // The receiving build knows Position and has never heard of Cooldown.
        Assert.False(HandoffCodec.Apply(target, target.Create(), payload.AsSpan(), [new PositionReplicator()], out _));
    }

    [Fact]
    public void A_payload_that_does_not_fit_is_empty_rather_than_truncated() {
        var source = new World();
        var here = source.Create();

        source.Add(here, new Position { X = 1, Y = 2 });
        source.Add(here, new Cooldown { Remaining = 3 });

        Span<byte> tiny = stackalloc byte[4];

        Assert.Empty(HandoffCodec.Write(source, here, Replicators, tiny));
    }

    [Fact]
    public void The_handoff_message_carries_the_epoch_and_the_tick() {
        var source = new World();
        var here = source.Create();

        source.Add(here, new Position { X = 1, Y = 2 });

        Span<byte> buffer = stackalloc byte[512];
        var handoff = HandoffCodec.Handoff(Bruna, 7, 4_200, source, here, Replicators, buffer);

        Assert.Equal(Bruna, handoff.Player);
        Assert.Equal(7, handoff.LeaseEpoch);
        Assert.Equal(4_200, handoff.AtTick);
        Assert.NotEmpty(handoff.Components);
    }

    static IReadOnlyList<IComponentReplicator> Replicators => [new PositionReplicator(), new CooldownReplicator()];

    /// <summary>
    ///     ⚠ <c>Apply</c> is contracted to add the component if it is not there, and a handoff is the
    ///     case that always hits it: the entity a player arrives on is bare. A replicator that only
    ///     <c>Set</c>s works for every snapshot — where the client already spawned the entity from a
    ///     prefab — and throws on the one path this codec exists for.
    /// </summary>
    static void Put<T>(World world, Entity entity, T value) where T : unmanaged {
        if (world.Has<T>(entity)) {
            world.Set(entity, value);
        } else {
            world.Add(entity, value);
        }
    }

    // ── Two components and hand-written replicators ─────────────────────────────────────────────
    // Hand-written rather than generated, which IComponentReplicator explicitly allows: the codec
    // under test is the framing around a replicator, not the replicator, and a test that needed the
    // generator would be testing two things at once.

    [Component]
    struct Position {
        public float X;
        public float Y;
    }

    [Component]
    struct Cooldown {
        public float Remaining;
    }

    sealed class PositionReplicator : IComponentReplicator {
        public ComponentTypeId ComponentType => ComponentType<Position>.Id;

        public uint TypeId => 0x50_53_49_00;

        public string TypeName => nameof(Position);

        public Channel Channel => Channel.Unreliable;

        public int Priority => 0;

        public QueryDescription ChangedQuery => new QueryDescription().WithAll<Position>();

        public bool Has(World world, Entity entity) => world.Has<Position>(entity);

        public void Write(World world, Entity entity, ref BitWriter writer) {
            var value = world.Get<Position>(entity);

            writer.WriteSingle(value.X);
            writer.WriteSingle(value.Y);
        }

        public bool Apply(World world, Entity entity, ref BitReader reader) {
            if (!reader.TryReadSingle(out var x) || !reader.TryReadSingle(out var y)) {
                return false;
            }

            Put(world, entity, new Position { X = x, Y = y });

            return true;
        }
    }

    sealed class CooldownReplicator : IComponentReplicator {
        public ComponentTypeId ComponentType => ComponentType<Cooldown>.Id;

        public uint TypeId => 0x43_44_00_00;

        public string TypeName => nameof(Cooldown);

        public Channel Channel => Channel.Reliable;

        public int Priority => 1;

        public QueryDescription ChangedQuery => new QueryDescription().WithAll<Cooldown>();

        public bool Has(World world, Entity entity) => world.Has<Cooldown>(entity);

        public void Write(World world, Entity entity, ref BitWriter writer) =>
            writer.WriteSingle(world.Get<Cooldown>(entity).Remaining);

        public bool Apply(World world, Entity entity, ref BitReader reader) {
            if (!reader.TryReadSingle(out var remaining)) {
                return false;
            }

            Put(world, entity, new Cooldown { Remaining = remaining });

            return true;
        }
    }
}
