// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Hosting;
using Vixen.Live.Orchestration;

namespace Vixen.Samples.Mmo.Orchestration;

/// <summary>The silo. Every grain doc 27 ships, plus this game's own.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>No packet a player is waiting on passes through any of it.</b> That is the whole
///         justification for putting Orleans here and nowhere else: thousands of small,
///         independently-addressable, single-threaded-by-construction pieces of coordination state —
///         and coordination is not a frame path.
///     </para>
///     <para>
///         <b>The game's own grains need no registration.</b> A silo hosts whatever grain classes are
///         in assemblies it loads, so <c>Mmo.Cluster</c>'s interface and its implementation being
///         referenced is the whole of the wiring. That is also why this project references
///         <c>Mmo.Cluster</c> and never <c>Mmo.Shared</c>: a grain that needed the gameplay libraries
///         would be a grain doing simulation.
///     </para>
/// </remarks>
public static class Program {
    /// <summary>Runs it.</summary>
    /// <param name="args">Whatever the deployment passes.</param>
    /// <returns>The exit code.</returns>
    public static async Task<int> Main(string[] args) {
        var builder = Host.CreateApplicationBuilder(args);

        // ⚠ A development cluster: localhost clustering, in-memory storage, and a placement that
        // starts realms as child processes. A deployment swaps the three and changes nothing else,
        // which is the point of them being options rather than code.
        builder.UseDevelopmentCluster(
            new OrchestratorOptions(
                ClusterId: "mmo-dev",
                ServiceId: "mmo",
                Maps: new Dictionary<string, MapOptions>(StringComparer.Ordinal),
                Default: null
            )
        );

        await builder.Build().RunAsync();

        return 0;
    }
}
