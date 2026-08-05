// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Live;
using Vixen.Live.Client;
using Vixen.Samples.Mmo.Contracts;

namespace Vixen.Samples.Mmo.Clients;

/// <summary>A headless client: sign in, pick a character, get a ticket, connect.</summary>
/// <remarks>
///     <para>
///         <b>Two connections, and briefly three during a transfer.</b> That is the whole of the
///         client's topology, and this program is the whole of proving it: HTTPS and WSS to the gate,
///         UDP straight to the realm, and nothing at all to the cluster. ADR-017 makes that
///         mechanical rather than remembered — <c>Mmo.Cluster</c> is not in this project's reference
///         list, so a grain is not a type this binary has.
///     </para>
///     <para>
///         ⚠ <b><c>PlayStatus.Starting</c> is a wait and not a failure</b>, and getting that wrong is
///         the classic launcher bug: the fleet is starting a shard for you, and a client that treated
///         it as an error would ask for a different map and start a second one.
///     </para>
///     <para>
///         ⚠ <b>Headless is #38's boundary rather than a shortcut.</b> This proves the protocol and,
///         with <c>Mmo.Shared</c>, the prediction; drawing any of it is a separate deliverable that
///         sits on top and is copyable on its own.
///     </para>
/// </remarks>
public static class Program {
    /// <summary>Runs it.</summary>
    /// <param name="args">The gate's base address, then a credential.</param>
    /// <returns>The exit code.</returns>
    public static async Task<int> Main(string[] args) {
        if (args is not [var gateAddress, var credential, ..]) {
            await Console.Error.WriteLineAsync("usage: Mmo.Client <gate-url> <credential> [map]");

            return 2;
        }

        var map = args.Length > 2 ? args[2] : MmoMaps.Greenmarch;

        using var http = new HttpClient { BaseAddress = new(gateAddress) };
        var gate = new GateClient(http);
        var cancellation = CancellationToken.None;

        // Before signing in, because a launcher wants to know whether it needs to patch and a
        // patcher has nobody to sign in as.
        var catalog = await gate.CatalogAsync(cancellation);

        if (catalog.Value is not { } version) {
            await Console.Error.WriteLineAsync($"the gate would not say what version it is: {catalog.Status}");

            return 1;
        }

        // ⚠ The token is held in memory and never written to disk. A credential that outlives the
        // process is a credential somebody else's process can read.
        await gate.SignInAsync("development", credential, cancellation);

        var characters = await gate.CharactersAsync(cancellation);

        if (characters.Value is not { Characters.Count: > 0 } roster) {
            await Console.Error.WriteLineAsync("this account has no characters.");

            return 1;
        }

        // `attempts` rather than a loop here: EnterAsync is what knows the difference between
        // Starting (wait, the fleet is bringing a shard up) and Refused (do not retry, and here is
        // the map's own sentence about why).
        var play = await gate.EnterAsync(
            new PlayRequest(roster.Characters[0].Character, map, version.Version, "en-GB", default, default),
            attempts: 5,
            cancellation
        );

        if (play.Value is not { Status: PlayStatus.Placed } placed) {
            await Console.Error.WriteLineAsync($"not placed: {play.Value?.Status.ToString() ?? play.Status.ToString()}");

            return 1;
        }

        Console.WriteLine($"placed on {placed.Endpoint} for {map}");

        // What follows is doc 16's handshake, carrying the ticket, over Vixen.Net.Transport.Udp —
        // and then the prediction loop, which runs MmoRules.Step against the same code the realm
        // does. The soak (#37) is what drives it in anger.
        return 0;
    }
}
