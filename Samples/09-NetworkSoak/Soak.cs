// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Globalization;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Net;
using Vixen.Net.Diagnostics;
using Vixen.Net.Motion;
using Vixen.Net.Replication;
using Vixen.Net.Sessions;
using Vixen.Net.Time;

namespace Vixen.Samples.NetworkSoak;

/// <summary>The run itself.</summary>
internal sealed class Soak(SoakSettings settings) : IDisposable {
    static readonly TickRate Rate = TickRate.Default;

    readonly World world = new("soak");
    readonly ReplicationRegistry registry = new();
    readonly NetworkIdAllocator ids = new();
    readonly BandwidthLedger ledger = new();
    readonly byte[] buffer = new byte[2048];
    readonly List<Entity> entities = [];
    readonly List<PlayerId> clients = [];
    readonly Queue<(int Tick, PlayerId Player)> acknowledging = new();

    ReplicationServer? server;

    /// <summary>Runs it.</summary>
    /// <returns>Zero if every budget held.</returns>
    public int Run() {
        registry.Register(new NetworkTransformReplicator());

        server = new(registry, new SliceResolver(settings.Observed, settings.SeesEverything)) { Ledger = ledger };

        Write(
            $"{settings.Entities:N0} entities, {settings.Clients:N0} connections, "
            + $"{(settings.SeesEverything ? "everybody sees everything" : $"{settings.Observed:N0} observed each")}, "
            + $"{settings.MovingPercent}% moving, {settings.Ticks:N0} ticks at {Rate.TicksPerSecond} Hz "
            + $"({Rate.ToTime(settings.Ticks).TotalMinutes:N1} minutes of match)"
        );

        Build();

        // Everything below is steady state. Measuring the build would measure the build.
        var settled = GC.GetTotalMemory(forceFullCollection: true);
        var beforeAllocated = GC.GetAllocatedBytesForCurrentThread();
        var beforeGen0 = GC.CollectionCount(0);
        var beforeGen1 = GC.CollectionCount(1);
        var beforeGen2 = GC.CollectionCount(2);

        var clock = Stopwatch.StartNew();
        var worst = TimeSpan.Zero;
        var total = TimeSpan.Zero;

        for (var tick = 1; tick <= settings.Ticks; tick++) {
            var started = clock.Elapsed;
            Step(tick);
            var took = clock.Elapsed - started;

            total += took;

            if (took > worst) {
                worst = took;
            }

            if (tick % 600 == 0) {
                Write(
                    $"  tick {tick,7:N0}  mean {total.TotalMicroseconds / tick,7:N0} us  "
                    + $"worst {worst.TotalMicroseconds,8:N0} us  "
                    + $"{ledger.KilobitsPerSecond / settings.Clients,6:N1} kbit/s per client  "
                    + $"heap {GC.GetTotalMemory(false) / 1024d / 1024d,6:N1} MiB"
                );
            }
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - beforeAllocated;
        var live = GC.GetTotalMemory(forceFullCollection: true);

        return Report(
            new Measurements {
                Settled = settled,
                Live = live,
                Allocated = allocated,
                Gen0 = GC.CollectionCount(0) - beforeGen0,
                Gen1 = GC.CollectionCount(1) - beforeGen1,
                Gen2 = GC.CollectionCount(2) - beforeGen2,
                Mean = total / settings.Ticks,
                Worst = worst
            }
        );
    }

    void Build() {
        var clock = Stopwatch.StartNew();

        for (var i = 0; i < settings.Entities; i++) {
            var angle = i * 0.01f;

            entities.Add(
                world.Create(
                    ids.Next(),
                    new NetworkTransform {
                        Position = new(MathF.Cos(angle) * 200f, 0f, MathF.Sin(angle) * 200f),
                        Rotation = Quaternion.Identity
                    }
                )
            );
        }

        for (var i = 1; i <= settings.Clients; i++) {
            clients.Add(new((uint)i));
        }

        Write($"built in {clock.Elapsed.TotalMilliseconds:N0} ms");
    }

    void Step(int tick) {
        var at = new Tick((uint)tick);

        world.AdvanceVersion();
        Move(tick);

        server!.Capture(world, at);
        ledger.Advance(Rate.Duration);

        foreach (var player in clients) {
            if (server.TryWriteSnapshot(world, player, at, buffer, out _)) {
                // A connection acknowledges some ticks later, which is what a round trip looks like
                // to the baseline and what decides whether a difference can be measured at all.
                acknowledging.Enqueue((tick + settings.AcknowledgeLag, player));
            }
        }

        while (acknowledging.TryPeek(out var due) && due.Tick <= tick) {
            acknowledging.Dequeue();
            server.Acknowledge(due.Player, new((uint)Math.Max(1, due.Tick - settings.AcknowledgeLag)));
        }
    }

    void Move(int tick) {
        if (settings.MovingPercent <= 0) {
            return;
        }

        // A rotating slice rather than the same ones every tick, so every entity is eventually
        // captured and every baseline eventually has something to be measured from.
        var moving = Math.Max(1, entities.Count * settings.MovingPercent / 100);
        var start = tick * moving % entities.Count;
        var step = (float)Rate.Duration.TotalSeconds;

        for (var i = 0; i < moving; i++) {
            var entity = entities[(start + i) % entities.Count];
            ref var transform = ref world.Get<NetworkTransform>(entity);
            transform.Position += new Vector3(step * 4f, 0f, step * 2f);
        }
    }

    int Report(in Measurements measured) {
        var seconds = Rate.ToTime(settings.Ticks).TotalSeconds;
        var perClient = ledger.KilobitsPerSecond / settings.Clients;
        var perTick = measured.Allocated / (double)settings.Ticks;
        var records = ledger.DeltaCount + ledger.WholeCount;

        Write("");
        Write($"records   {records:N0}, {ledger.DeltaCount:N0} as a difference ({(records == 0 ? 0 : ledger.DeltaCount / (double)records):P0})");
        Write($"bandwidth {ledger.TotalBits / 8d / 1024d / 1024d:N1} MiB over {seconds:N0} s — {perClient:N1} kbit/s per client");
        Write($"tick      mean {measured.Mean.TotalMicroseconds:N0} us, worst {measured.Worst.TotalMicroseconds:N0} us, budget {Rate.Duration.TotalMicroseconds:N0} us");
        Write($"memory    {measured.Settled / 1024d / 1024d:N1} MiB after the build, {measured.Live / 1024d / 1024d:N1} MiB at the end");
        Write($"alloc     {measured.Allocated / 1024d / 1024d:N1} MiB over the run — {perTick:N0} B a tick");
        Write($"gc        {measured.Gen0:N0} gen0, {measured.Gen1:N0} gen1, {measured.Gen2:N0} gen2");
        Write("");

        var failures = 0;

        failures += Budget("bandwidth", perClient <= settings.BandwidthBudget, $"{perClient:N1} kbit/s a client against a budget of {settings.BandwidthBudget}");
        failures += Budget("tick time", measured.Worst < Rate.Duration, $"worst tick {measured.Worst.TotalMicroseconds:N0} us against a {Rate.Duration.TotalMicroseconds:N0} us tick");
        failures += Budget("allocation", perTick <= settings.AllocationBudget, $"{perTick:N0} B a tick against a budget of {settings.AllocationBudget:N0}");

        // Growth, not size. A steady state that keeps a hundred megabytes is a design decision; one
        // that keeps growing is a leak, and the difference is whether the end is larger than the
        // settled figure by more than the rings can account for.
        var growth = (measured.Live - measured.Settled) / 1024d / 1024d;
        failures += Budget("memory", growth < 64, $"the heap grew {growth:N1} MiB after settling");

        Write(failures == 0 ? "every budget held" : $"BUDGETS MISSED: {failures}");

        return failures == 0 ? 0 : 1;
    }

    static int Budget(string name, bool held, string detail) {
        Write($"  {(held ? "ok  " : "FAIL")}  {name,-11} {detail}");

        return held ? 0 : 1;
    }

    /// <summary>Lets go of the world.</summary>
    public void Dispose() => world.Dispose();

    static void Write(string line) => Console.Out.WriteLine(line);

    readonly record struct Measurements {
        public long Settled { get; init; }
        public long Live { get; init; }
        public long Allocated { get; init; }
        public int Gen0 { get; init; }
        public int Gen1 { get; init; }
        public int Gen2 { get; init; }
        public TimeSpan Mean { get; init; }
        public TimeSpan Worst { get; init; }
    }
}
