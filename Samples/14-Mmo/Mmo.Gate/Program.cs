// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Vixen.Live.Gate;
using Vixen.Samples.Mmo.Contracts;

namespace Vixen.Samples.Mmo.Gates;

/// <summary>The service plane: login, the character list, the realm list, and the ticket.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Everything a player does before they are in the world, and nothing they do after.</b>
///         Doc 27's three planes: this is HTTPS and WSS, the control plane is Orleans, and the data
///         plane is UDP straight from the client to its realm. A gate that stayed in the path after
///         admission would be a proxy in front of every packet in the game.
///     </para>
///     <para>
///         <b>How little there is here is the point.</b> <c>Vixen.Live.Gate</c> already implements
///         the endpoints, the token, the subscription and the retry contract. What a game supplies is
///         its version, its content URL, its region and its map list — and, in a real deployment, an
///         <c>IAccountAuthority</c> that is not the development one.
///     </para>
/// </remarks>
public static class Program {
    /// <summary>Runs it.</summary>
    /// <param name="args">Whatever the deployment passes.</param>
    /// <returns>The exit code.</returns>
    public static async Task<int> Main(string[] args) {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddVixenGate(gate => {
            gate.Region = "eu";
            foreach (var map in MmoMaps.Public) {
                gate.Maps.Add(map);
            }

            // ⚠ Barrowdeep and Ravensford are deliberately absent. They are `Instance` and `Match`
            // shards, which are allocated by a placement or a queue rather than walked into — a
            // player who could ask the gate for a dungeon could ask for one they are locked out of.
        });

        var app = builder.Build();

        app.UseWebSockets();
        app.MapVixenGate();

        await app.RunAsync();

        return 0;
    }
}
