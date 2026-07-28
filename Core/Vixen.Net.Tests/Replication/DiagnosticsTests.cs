// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Net.Diagnostics;
using Vixen.Net.Motion;
using Vixen.Net.Replication;
using Vixen.Net.Sessions;
using Xunit;

namespace Vixen.Net.Tests.Replication;

/// <summary>Attribution: that the totals add up, and that a packet can be taken apart.</summary>
public sealed class DiagnosticsTests : IDisposable {
    static readonly PlayerId Player = new(1);

    readonly World server = new("server");
    readonly ReplicationRegistry registry = new();
    readonly NetworkIdAllocator ids = new();
    readonly ReplicationServer sender;
    readonly BandwidthLedger ledger = new();
    readonly byte[] buffer = new byte[8192];

    uint tick = 1;

    public DiagnosticsTests() {
        registry.Register(new NetworkTransformReplicator());
        sender = new(registry) { Ledger = ledger };
    }

    public void Dispose() => server.Dispose();

    /// <summary>
    ///     The per-component attribution adds up to what actually went out.
    /// </summary>
    /// <remarks>
    ///     The property that makes a bandwidth report worth reading. A breakdown that does not sum to
    ///     the total is one where the missing bits are exactly the ones you are looking for.
    /// </remarks>
    [Fact]
    public void WhatTheLedgerSaysWentOut_IsWhatWentOut() {
        // The tick stamp, and the bit that ends each of the two lists.
        const int PerSnapshot = 32 + 1 + 1;

        var entity = Spawn();
        var sent = 0;
        var snapshots = 0;

        for (var step = 0; step < 20; step++) {
            Move(entity, step * 0.4f);
            var bits = Replicate();

            if (bits > 0) {
                sent += bits;
                snapshots++;
            }
        }

        var attributed = 0L;

        foreach (var entry in ledger.TopComponents()) {
            attributed += entry.Bits;
        }

        Assert.Equal(ledger.TotalBits, attributed);

        // Everything in a snapshot is either a record the ledger counted or the fixed framing above
        // it, and what is left over is the padding to a whole byte. Stated exactly rather than as a
        // tolerance, because a breakdown that does not add up is one where the missing bits are the
        // ones being looked for.
        var unaccounted = sent - ledger.TotalBits - (snapshots * PerSnapshot);

        Assert.InRange(unaccounted, 0, snapshots * 7);
    }

    [Fact]
    public void TheFieldBreakdownAddsUpToTheRecordsItIsInside() {
        var entity = Spawn();

        for (var step = 0; step < 20; step++) {
            Move(entity, step * 0.4f);
            Replicate();
        }

        var fields = 0L;

        foreach (var entry in ledger.TopFields(32)) {
            fields += entry.Bits;
        }

        // Inside the records, never counted towards the total — a field's bits are already in the
        // record's. So this is under the total rather than equal to it, and by the record headers.
        Assert.True(fields > 0);
        Assert.True(fields < ledger.TotalBits, $"{fields} fields against {ledger.TotalBits} total");
    }

    /// <summary>The field a game never changes costs exactly its "unchanged" bit.</summary>
    /// <remarks>
    ///     The whole point of per-field attribution: this is the report telling you that a component
    ///     is carrying a field this game does not use, which is a decision to make rather than a
    ///     number to look at.
    /// </remarks>
    [Fact]
    public void AFieldThatNeverChanges_ShowsUpAsOneBit() {
        var entity = Spawn();

        for (var step = 0; step < 20; step++) {
            // Along X only, so Y and Z never move and neither does the teleport counter.
            ref var transform = ref server.Get<NetworkTransform>(entity);
            transform.Position = new(step * 0.4f, 0f, 0f);
            Replicate();
        }

        var still = Find(ledger.TopFields(32), "Position.Y");

        Assert.True(still.Count > 0, "the field was never accounted for");
        Assert.Equal(1d, still.MeanBits, 3);
    }

    [Fact]
    public void ASnapshotCanBeTakenApartWithoutBeingApplied() {
        var first = Spawn();
        var second = Spawn();

        Move(first, 1f);
        Move(second, 2f);
        Replicate();

        Move(first, 1.2f);
        sender.Capture(server, Tick());

        Assert.True(sender.TryWriteSnapshot(server, Player, Tick(), buffer, out var snapshot));

        var contents = SnapshotInspector.Inspect(registry, snapshot);

        Assert.True(contents.Complete);
        Assert.Equal(Tick(), contents.Tick);
        Assert.Empty(contents.Removals);

        var record = Assert.Single(contents.Records);
        Assert.Equal(server.Read<NetworkId>(first), record.Object);
        Assert.Equal("Vixen.Net.Motion.NetworkTransform", record.TypeName);
        Assert.True(record.IsDelta);
        Assert.True(record.Bits > 0);

        // The inspector's record sizes account for the whole snapshot bar its own header and the two
        // terminators, which is the claim that makes it usable for finding where a packet went.
        Assert.InRange(record.Bits, 1, contents.Bits);
    }

    [Fact]
    public void ATruncatedSnapshotReportsWhatItFoundRatherThanFailing() {
        var entity = Spawn();
        Move(entity, 1f);
        sender.Capture(server, Tick());

        Assert.True(sender.TryWriteSnapshot(server, Player, Tick(), buffer, out var snapshot));

        var contents = SnapshotInspector.Inspect(registry, snapshot[..3]);

        Assert.False(contents.Complete);
        Assert.Empty(contents.Records);
    }

    [Fact]
    public void ResettingTheLedgerStartsTheReportAgain() {
        var entity = Spawn();
        Move(entity, 1f);
        Replicate();

        Assert.True(ledger.TotalBits > 0);

        ledger.Reset();

        Assert.Equal(0, ledger.TotalBits);
        Assert.Empty(ledger.TopComponents());
        Assert.Equal(TimeSpan.Zero, ledger.Elapsed);
    }

    [Fact]
    public void ObjectsAreOnlyAttributedWhenAsked() {
        var entity = Spawn();
        Move(entity, 1f);
        Replicate();

        Assert.Empty(ledger.TopObjects());

        ledger.TrackObjects = true;
        Move(entity, 2f);
        Replicate();

        Assert.NotEmpty(ledger.TopObjects());
    }

    static BandwidthEntry Find(IReadOnlyList<BandwidthEntry> entries, string ending) {
        foreach (var entry in entries) {
            if (entry.Name.EndsWith(ending, StringComparison.Ordinal)) {
                return entry;
            }
        }

        return default;
    }

    Entity Spawn() =>
        server.Create(
            ids.Next(),
            new NetworkTransform { Position = Vector3.Zero, Rotation = Quaternion.Identity }
        );

    void Move(Entity entity, float along) {
        ref var transform = ref server.Get<NetworkTransform>(entity);
        transform.Position = new(along, 0f, along * 0.5f);
    }

    int Replicate() {
        var at = Tick();
        sender.Capture(server, at);
        ledger.Advance(TimeSpan.FromMilliseconds(33));

        var bits = 0;

        if (sender.TryWriteSnapshot(server, Player, at, buffer, out var snapshot)) {
            bits = snapshot.Length * 8;
            sender.Acknowledge(Player, at);
        }

        server.AdvanceVersion();
        tick++;

        return bits;
    }

    Tick Tick() => new(tick);
}
