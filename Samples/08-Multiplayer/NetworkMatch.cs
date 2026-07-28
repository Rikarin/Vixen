// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Net;
using Vixen.Net.Transport.Udp;

namespace Vixen.Samples.Multiplayer;

/// <summary>The same match, over real sockets, in two processes.</summary>
/// <remarks>
///     <para>
///         Nothing above the transport changes, and that is the claim being made: the server and the
///         client below are the same <see cref="GameServer" /> and <see cref="GameClient" /> the
///         local match runs, handed a different <c>ITransport</c>. <c>TransportConformance</c> is why
///         that works — both transports are held to the same executable contract, so the session,
///         replication and RPC layers have nothing to tell apart.
///     </para>
///     <para>
///         What does change is time. Here it comes from a <see cref="Stopwatch" /> rather than from a
///         constant, so this mode is the one that proves the loop tolerates a real frame length, and
///         the local one is where anything is asserted.
///     </para>
/// </remarks>
internal static class NetworkMatch {
    static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(8);
    static readonly TimeSpan ReportEvery = TimeSpan.FromSeconds(2);

    /// <summary>Hosts a match.</summary>
    /// <param name="listen">Where to listen.</param>
    /// <param name="duration">How long to run, or <see cref="TimeSpan.Zero" /> for until interrupted.</param>
    /// <returns>Zero.</returns>
    public static int RunServer(IPEndPoint listen, TimeSpan duration) {
        var transport = new UdpTransport(
            new UdpDatagramSocketFactory(),
            new() { ListenEndPoint = listen, MaxConnections = 8 }
        );

        using var server = new GameServer(transport);
        server.StartServer();

        Write($"listening on {transport.ListeningOn}");

        Loop(
            duration,
            server.Update,
            () => Write(
                $"tick {server.Tick.Value,8}  {server.Session.Players.Count} players  "
                + $"{server.SnapshotCount:N0} snapshots  {server.SnapshotBytes / 1024d:N0} KiB  "
                + $"{server.Arena.ShotsHit:N0} hits  {server.Arena.Deaths:N0} deaths"
            )
        );

        return 0;
    }

    /// <summary>Joins a match.</summary>
    /// <param name="connect">Where the server is.</param>
    /// <param name="duration">How long to run, or <see cref="TimeSpan.Zero" /> for until interrupted.</param>
    /// <returns>Zero if it got in, one if it never connected.</returns>
    public static int RunClient(IPEndPoint connect, TimeSpan duration) {
        var transport = new UdpTransport(
            new UdpDatagramSocketFactory(),
            new() { ListenEndPoint = new(IPAddress.Any, 0), RemoteEndPoint = connect }
        );

        using var client = new GameClient(transport);
        client.StartClient();

        Write($"connecting to {connect}");

        Loop(
            duration,
            client.Update,
            () => Write(
                client.Session.LocalPlayer is null
                    ? "not connected yet"
                    : $"tick {client.Session.Tick.Value,8}  {client.EntityCount} entities  "
                    + $"{client.SnapshotsApplied:N0} applied ({client.Replication.RejectedSnapshotCount:N0} rejected)  "
                    + $"rtt {client.Session.Clock.RoundTrip.RoundTrip.TotalMilliseconds:N1} ms  "
                    + $"{client.BytesReceived / 1024d:N0} KiB  {client.HitsSeen} hits"
            )
        );

        if (client.Session.LocalPlayer is null) {
            Write("never connected");

            return 1;
        }

        return 0;
    }

    static void Loop(TimeSpan duration, Func<TimeSpan, int> update, Action report) {
        using var stopping = new ManualResetEventSlim();

        void Interrupt(object? sender, ConsoleCancelEventArgs e) {
            e.Cancel = true;
            stopping.Set();
        }

        Console.CancelKeyPress += Interrupt;

        try {
            var clock = Stopwatch.StartNew();
            var last = clock.Elapsed;
            var nextReport = ReportEvery;

            while (!stopping.IsSet && (duration == TimeSpan.Zero || clock.Elapsed < duration)) {
                var now = clock.Elapsed;
                update(now - last);
                last = now;

                if (now >= nextReport) {
                    report();
                    nextReport = now + ReportEvery;
                }

                // A real frame, because this mode is about a real socket. The local match is where
                // the step is a constant and the run is reproducible.
                stopping.Wait(Frame);
            }
        } finally {
            Console.CancelKeyPress -= Interrupt;
        }

        report();
    }

    static void Write(string line) => Console.Out.WriteLine(line);
}
