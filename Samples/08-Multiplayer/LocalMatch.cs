// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core.Mathematics;
using Vixen.Net.Motion;
using Vixen.Net.Transport;
using Vixen.Net.Transport.Local;

namespace Vixen.Samples.Multiplayer;

/// <summary>A whole match in one process, one thread, and no sockets.</summary>
/// <remarks>
///     <para>
///         The default mode, and the one that is worth running in CI. Time is a parameter everywhere
///         in <c>Vixen.Net</c> — <c>Poll(elapsed, …)</c>, <c>TickManager.Advance</c>,
///         <c>RpcRouter.Advance</c> — so a match driven by a fixed step is exactly reproducible, and
///         so is a match driven by a fixed step through a seeded packet-loss simulation. There is no
///         clock to be flaky about and no thread to interleave differently.
///     </para>
///     <para>
///         It ends by asserting the thing a networking layer exists to provide: that after the noise
///         stops, every client's copy agrees with the server's. The settle phase is not a fudge —
///         while fighters are moving, a client is <i>meant</i> to disagree, by its interpolation
///         delay. What must not survive the quiet is a disagreement that nothing corrects.
///     </para>
/// </remarks>
internal static class LocalMatch {
    /// <summary>How far apart the two copies may be and still count as the same.</summary>
    /// <remarks>
    ///     Twice the quantizer's half-level, which is the error a position is <i>supposed</i> to have:
    ///     three centimetres over a two-kilometre range, spent deliberately in
    ///     <c>NetworkTransformReplicator</c>. Anything larger is not rounding.
    /// </remarks>
    public static float Tolerance => NetworkTransformReplicator.PositionRange.MaxError * 2f;

    /// <summary>Runs a match.</summary>
    /// <param name="settings">What to run.</param>
    /// <returns>Zero if every client converged, one if any did not.</returns>
    public static int Run(in MatchSettings settings) {
        var network = new LocalNetwork();
        var damage = new List<NetworkSimulation>();

        // Both directions, and that is not a detail. The simulation injects on the way out, so
        // wrapping only the clients would lose their input and never lose a snapshot — which is the
        // direction all the delta and acknowledgement machinery lives in, and the one a "tested
        // under packet loss" claim is about.
        using var server = new GameServer(Wrap(new LocalTransport(network), settings, 0, damage));
        server.StartServer();

        var clients = new List<GameClient>();

        try {
            for (var index = 0; index < settings.Clients; index++) {
                clients.Add(Connect(network, settings, index, damage));
            }

            Write(
                $"{settings.Clients} clients, {settings.Ticks} frames at "
                + $"{MatchSettings.FrameStep.TotalMilliseconds:N0} ms, "
                + $"{settings.Loss:P0} loss, {settings.Latency.TotalMilliseconds:N0} ms latency, seed {settings.Seed}"
            );

            Pump(server, clients, settings.Ticks);

            // Stop asking for anything and let the last snapshots land. Everything below is about
            // what survives the quiet.
            foreach (var client in clients) {
                client.Idle = true;
            }

            Pump(server, clients, settings.SettleTicks);

            Report(server, clients, damage);

            return Converged(server, clients) ? 0 : 1;
        } finally {
            foreach (var client in clients) {
                client.Dispose();
            }
        }
    }

    static GameClient Connect(
        LocalNetwork network,
        in MatchSettings settings,
        int index,
        List<NetworkSimulation> damage
    ) {
        var client = new GameClient(Wrap(new LocalTransport(network), settings, index + 1, damage));
        client.StartClient();

        return client;
    }

    static ITransport Wrap(
        ITransport transport,
        in MatchSettings settings,
        int index,
        List<NetworkSimulation> damage
    ) {
        if (settings.Loss <= 0 && settings.Latency <= TimeSpan.Zero) {
            return transport;
        }

        // A seed per participant, derived from the match's: eight clients losing the same packets in
        // the same order would be one run repeated eight times.
        var simulation = new NetworkSimulation(
            transport,
            new() {
                LossChance = settings.Loss,
                Latency = settings.Latency,
                Jitter = settings.Latency / 4,
                DuplicateChance = settings.Loss / 4
            },
            settings.Seed + (ulong)index
        );

        damage.Add(simulation);

        return simulation;
    }

    static void Pump(GameServer server, List<GameClient> clients, int frames) {
        for (var frame = 0; frame < frames; frame++) {
            server.Update(MatchSettings.FrameStep);

            foreach (var client in clients) {
                client.Update(MatchSettings.FrameStep);
            }
        }
    }

    static void Report(GameServer server, List<GameClient> clients, List<NetworkSimulation> damage) {
        var arena = server.Arena;
        var seconds = server.StepCount * server.Session.Options.TickRate.Duration.TotalSeconds;

        Write("");

        if (damage.Count != 0) {
            long sent = 0;
            long dropped = 0;
            long duplicated = 0;

            foreach (var simulation in damage) {
                sent += simulation.SentPayloadCount;
                dropped += simulation.DroppedPayloadCount;
                duplicated += simulation.DuplicatedPayloadCount;
            }

            // Measured, not claimed. A reliable channel's payloads are never among the dropped —
            // the simulation only injects what a channel's own contract already permits — so the
            // proportion here is below the figure asked for, and that is correct rather than a bug.
            Write(
                $"wire     {sent + dropped:N0} payloads offered, {dropped:N0} thrown away "
                + $"({(sent + dropped == 0 ? 0 : dropped / (double)(sent + dropped)):P1}), "
                + $"{duplicated:N0} duplicated"
            );
        }

        Write(
            $"server   {arena.Fighters.Count} fighters, {server.StepCount:N0} ticks, "
            + $"{arena.ShotsFired:N0} shots ({arena.ShotsHit:N0} hit, {arena.Deaths:N0} deaths)"
        );

        Write(
            $"         {server.SnapshotCount:N0} snapshots, {server.SnapshotBytes / 1024d:N1} KiB, "
            + $"mean {Mean(server.SnapshotBytes, server.SnapshotCount):N0} B, "
            + $"{Rate(server.SnapshotBytes, seconds, clients.Count):N1} kbit/s per client"
        );

        Write("");
        Write("client  entities  applied  rejected     rtt      received   hits  interp  extrap  snap  starved");

        for (var index = 0; index < clients.Count; index++) {
            var client = clients[index];
            var motion = client.Motion;

            Write(
                $"{index + 1,6}  {client.EntityCount,8}  {client.SnapshotsApplied,7:N0}  "
                + $"{client.Replication.RejectedSnapshotCount,8:N0}  "
                + $"{client.Session.Clock.RoundTrip.RoundTrip.TotalMilliseconds,5:N1}ms  "
                + $"{client.BytesReceived / 1024d,9:N1} KiB  {client.HitsSeen,5:N0}  "
                + $"{motion.Interpolated,6:N0}  {motion.Extrapolated,6:N0}  {motion.Snapped,4:N0}  {motion.Starved,7:N0}"
            );
        }

        Write("");
    }

    static bool Converged(GameServer server, List<GameClient> clients) {
        var world = server.World;
        var failures = 0;

        for (var index = 0; index < clients.Count; index++) {
            var client = clients[index];
            var name = (index + 1).ToString(CultureInfo.InvariantCulture);

            if (client.EntityCount != server.Arena.Fighters.Count) {
                Write(
                    $"client {name}: holds {client.EntityCount} entities, the server has "
                    + $"{server.Arena.Fighters.Count}"
                );

                failures++;

                continue;
            }

            foreach (var fighter in server.Arena.Fighters) {
                var truth = world.Read<NetworkTransform>(fighter.Entity);
                var vitals = world.Read<Vitals>(fighter.Entity);

                if (!client.TryLatest(fighter.Id, out var held)) {
                    Write($"client {name}: never heard about {fighter.Id}");
                    failures++;

                    continue;
                }

                var apart = Vector3.Distance(truth.Position, held.Position);

                if (apart > Tolerance) {
                    Write(
                        $"client {name}: {fighter.Id} is {apart:N3} m out, "
                        + $"which is more than the {Tolerance:N3} m the quantizer costs"
                    );

                    failures++;
                }

                if (!client.TryVitals(fighter.Id, out var seen) || !Same(seen, vitals)) {
                    Write(
                        $"client {name}: {fighter.Id} vitals disagree — "
                        + $"{seen.Health}/{seen.Score}/{seen.Deaths} against "
                        + $"{vitals.Health}/{vitals.Score}/{vitals.Deaths}"
                    );

                    failures++;
                }
            }
        }

        Write(
            failures == 0
                ? $"converged: {clients.Count} clients agree with the server about "
                + $"{server.Arena.Fighters.Count} fighters, to within {Tolerance:N3} m"
                : $"NOT converged: {failures} disagreements"
        );

        return failures == 0;
    }

    static bool Same(in Vitals left, in Vitals right) =>
        left.Health == right.Health && left.Score == right.Score && left.Deaths == right.Deaths;

    static double Mean(long total, long count) => count == 0 ? 0 : total / (double)count;

    static double Rate(long bytes, double seconds, int clients) =>
        seconds <= 0 || clients == 0 ? 0 : bytes * 8d / seconds / clients / 1000d;

    static void Write(string line) => Console.Out.WriteLine(line);
}

/// <summary>What a local match is.</summary>
internal readonly record struct MatchSettings {
    /// <summary>How long a frame is. The engine's fixed step; the session's tick rate divides it.</summary>
    public static TimeSpan FrameStep => TimeSpan.FromMilliseconds(16);

    /// <summary>How many players.</summary>
    public int Clients { get; init; }

    /// <summary>How many frames of play.</summary>
    public int Ticks { get; init; }

    /// <summary>How many frames of quiet afterwards, for the last snapshots to land.</summary>
    public int SettleTicks { get; init; }

    /// <summary>What fraction of payloads to throw away, from 0 to 1.</summary>
    public double Loss { get; init; }

    /// <summary>How long to hold payloads for. Jitter is a quarter of it.</summary>
    public TimeSpan Latency { get; init; }

    /// <summary>The seed every random decision comes from.</summary>
    public ulong Seed { get; init; }
}
