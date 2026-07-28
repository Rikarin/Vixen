// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Net;

namespace Vixen.Samples.Multiplayer;

/// <summary>The whole of Phase 9 at once: eight players, one authority, and no trust.</summary>
/// <remarks>
///     <para>
///         A console program with no window, because Phase 9 draws nothing. What it shows is what
///         crosses the wire and what the other end makes of it: a session handshake, a tick clock
///         being corrected towards the server's, a snapshot per connection built out of one capture,
///         remote calls that are checked before they are run, and smoothing that turns thirty
///         positions a second into motion.
///     </para>
///     <para>
///         The default mode runs the whole match in one process against a deterministic transport and
///         ends by checking that every client agrees with the server. That is the mode worth putting
///         in CI, and it exits non-zero when they do not.
///     </para>
/// </remarks>
public static class Program {
    /// <summary>Runs the sample.</summary>
    /// <param name="arguments">See <c>--help</c>, or the README beside this file.</param>
    /// <returns>Zero on success.</returns>
    public static int Main(string[] arguments) {
        ArgumentNullException.ThrowIfNull(arguments);

        if (Array.IndexOf(arguments, "--help") >= 0) {
            Help();

            return 0;
        }

        var mode = Text(arguments, "--mode", "local");

        return mode switch {
            "local" => LocalMatch.Run(
                new MatchSettings {
                    Clients = Math.Clamp(Number(arguments, "--clients", 8), 1, 8),
                    Ticks = Number(arguments, "--ticks", 1800),
                    SettleTicks = Number(arguments, "--settle", 120),
                    Loss = Math.Clamp(Number(arguments, "--loss", 0) / 100d, 0d, 0.9d),
                    Latency = TimeSpan.FromMilliseconds(Number(arguments, "--latency", 0)),
                    Seed = (ulong)Number(arguments, "--seed", 20260728)
                }
            ),
            "server" => NetworkMatch.RunServer(
                new(IPAddress.Any, Number(arguments, "--port", 7777)),
                TimeSpan.FromSeconds(Number(arguments, "--seconds", 0))
            ),
            "client" => Join(arguments),
            _ => Unknown(mode)
        };
    }

    static int Join(string[] arguments) {
        var address = Text(arguments, "--connect", "127.0.0.1:7777");

        if (!IPEndPoint.TryParse(address, out var endPoint)) {
            Console.Error.WriteLine($"'{address}' is not an address and a port.");

            return 2;
        }

        return NetworkMatch.RunClient(endPoint, TimeSpan.FromSeconds(Number(arguments, "--seconds", 0)));
    }

    static int Unknown(string mode) {
        Console.Error.WriteLine($"'{mode}' is not a mode. Try --help.");

        return 2;
    }

    static void Help() {
        Console.Out.WriteLine(
            """
            08-Multiplayer — server-authoritative, eight players, movement and shooting.

              --mode local    everybody in one process, deterministic, checked at the end (default)
                --clients N   how many players, 1 to 8            (8)
                --ticks N     frames of play                      (1800, thirty seconds)
                --settle N    frames of quiet before checking      (120)
                --loss N      percent of payloads to throw away     (0)
                --latency N   milliseconds each way, jitter a quarter of it  (0)
                --seed N      what every random decision comes from  (20260728)

              --mode server   host over UDP
                --port N      what to listen on                  (7777)
                --seconds N   how long to run, 0 for forever        (0)

              --mode client   join over UDP
                --connect A:P where the server is         (127.0.0.1:7777)
                --seconds N   how long to run, 0 for forever        (0)

            The exit criterion for the phase, run two ways:

              dotnet run -c Release --project Samples/08-Multiplayer
              dotnet run -c Release --project Samples/08-Multiplayer -- --loss 20 --latency 60
            """
        );
    }

    static int Number(string[] arguments, string name, int fallback) {
        for (var index = 0; index < arguments.Length - 1; index++) {
            if (arguments[index] == name
                && int.TryParse(arguments[index + 1], CultureInfo.InvariantCulture, out var value)) {
                return value;
            }
        }

        return fallback;
    }

    static string Text(string[] arguments, string name, string fallback) {
        for (var index = 0; index < arguments.Length - 1; index++) {
            if (arguments[index] == name) {
                return arguments[index + 1];
            }
        }

        return fallback;
    }
}
