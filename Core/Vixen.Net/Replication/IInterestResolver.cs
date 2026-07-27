// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Ecs;
using Vixen.Net.Sessions;

namespace Vixen.Net.Replication;

/// <summary>Decides which entities one player is told about.</summary>
/// <remarks>
///     <para>
///         Composable by design: the shipped resolvers are meant to be joined and added to — scene
///         scope, then explicit overrides, then a distance grid, then rate falloff — and a game with
///         a notion of rooms, teams or fog of war writes its own and puts it in the chain. The
///         interface is deliberately about <i>which entities</i> and not about how they are found.
///     </para>
///     <para>
///         The default is everything, which is a deliberate ergonomics choice rather than a placeholder:
///         a prototype works before anybody has thought about interest management, and the first time
///         it matters is the first time the profiler says so.
///     </para>
/// </remarks>
public interface IInterestResolver {
    /// <summary>Fills <paramref name="observed" /> with what this player should be told about.</summary>
    /// <param name="world">The server's world.</param>
    /// <param name="player">Who is being told.</param>
    /// <param name="observed">Where to put the entities. Cleared by the caller before this runs.</param>
    void Resolve(World world, PlayerId player, List<Entity> observed);
}

/// <summary>Tells everybody about everything that has a <see cref="NetworkId" />.</summary>
/// <remarks>
///     What a new project gets. It is correct, it is what a prototype wants, and it is the thing to
///     replace first when the bandwidth graph stops being flat.
/// </remarks>
public sealed class ReplicateEverythingResolver : IInterestResolver {
    static readonly QueryDescription Networked = new QueryDescription().RequireAll([ComponentType<NetworkId>.Id]);

    /// <inheritdoc />
    public void Resolve(World world, PlayerId player, List<Entity> observed) {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(observed);

        foreach (var chunk in world.Chunks(Networked)) {
            observed.AddRange(chunk.Entities);
        }
    }
}
