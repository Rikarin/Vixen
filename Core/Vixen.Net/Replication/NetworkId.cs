// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core;

namespace Vixen.Net.Replication;

/// <summary>The component that makes an entity one the network knows about.</summary>
/// <remarks>
///     <para>
///         <b>The server allocates these and clients never invent one.</b> A client that could name a
///         new entity could name one the server did not create, which is the shape of half the
///         cheating in a multiplayer game and all of the desyncs.
///     </para>
///     <para>
///         It is not an <c>Entity</c>. An entity handle is an index and a generation into one world's
///         arrays, and two machines' worlds have no reason to agree about either — the same tank is
///         entity 41 here and entity 900 there. The <see cref="NetworkId" /> is what they agree on,
///         and each side keeps its own map from it to its own handle.
///     </para>
/// </remarks>
/// <param name="Value">The number the server gave it. Zero is <see cref="None" />.</param>
[Component]
public readonly record struct NetworkId(uint Value) {
    /// <summary>Not a networked entity.</summary>
    public static NetworkId None => default;

    /// <summary>Where the band of ids that are computed rather than handed out begins.</summary>
    /// <remarks>
    ///     <para>
    ///         An object placed in a scene by a designer exists on every peer before anybody connects,
    ///         so nothing can have allocated it an id — and asking the server to number them at load
    ///         time would mean a client cannot touch its own scene until it has been told about it.
    ///         Instead those ids are <i>derived</i> from the scene and the object's place in it, which
    ///         is deterministic and therefore already agreed.
    ///     </para>
    ///     <para>
    ///         The two schemes have to share one number space, so they are given half each. The
    ///         allocator counts up from one and stops here; derived ids live from here up. The
    ///         alternative — one counter and a flag — puts the two on a collision course that only
    ///         shows up in a session long enough to reach the crossover.
    ///     </para>
    /// </remarks>
    public const uint FirstBaked = 0x8000_0000;

    /// <summary>Whether this names a networked entity at all.</summary>
    public bool IsValid => Value != 0;

    /// <summary>Whether this id was derived from a scene rather than handed out by a server.</summary>
    public bool IsBaked => Value >= FirstBaked;

    /// <inheritdoc />
    public override string ToString() =>
        Value == 0 ? "not networked" : string.Create(CultureInfo.InvariantCulture, $"net {Value}");
}

/// <summary>Hands out <see cref="NetworkId" />s. Server-side, and the only thing that makes them.</summary>
public sealed class NetworkIdAllocator {
    uint next = 1;

    /// <summary>How many have been handed out.</summary>
    public uint Count => next - 1;

    /// <summary>Takes the next id.</summary>
    /// <returns>An id nothing else has had.</returns>
    /// <remarks>
    ///     Monotonic and never reused within a session. Reuse would let a stale packet about a
    ///     destroyed entity be applied to the entity that took its number, which is a bug that only
    ///     happens under packet loss and is therefore a bug nobody reproduces.
    /// </remarks>
    public NetworkId Next() => Reserve(1);

    /// <summary>Takes a run of consecutive ids.</summary>
    /// <param name="count">How many. Must be at least one.</param>
    /// <returns>The first. The run is that id and the <paramref name="count" /> − 1 ids after it.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count" /> is less than one.</exception>
    /// <exception cref="InvalidOperationException">
    ///     The run would reach <see cref="NetworkId.FirstBaked" />, where the ids scenes derive begin.
    /// </exception>
    /// <remarks>
    ///     <para>
    ///         <b>What makes a spawn message a fixed size.</b> A prefab is a subtree, and the parts of
    ///         it that carry replicated state each need an id. Listing them would make the message grow
    ///         with the prefab — a two-hundred entity set piece would spend most of its spawn on a
    ///         table the receiver could have worked out — so instead the root's id is sent and the rest
    ///         are the ids after it, handed to the template's networked nodes in capture order.
    ///     </para>
    ///     <para>
    ///         <b>That is only sound because the capture order is deterministic.</b> It is a
    ///         depth-first walk of the source subtree, recorded once when the prefab was built, so both
    ///         ends number the same entity the same way. If prefab capture ever became order-dependent
    ///         on something that varies between processes, this is what would break, and it would break
    ///         as components arriving on the wrong entity rather than as an error.
    ///     </para>
    /// </remarks>
    public NetworkId Reserve(int count) {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);

        var first = next;

        // Two billion spawns in one session is not a number anybody reaches, so this is a guard
        // rather than a limit — but the failure it guards against is an allocated id colliding with a
        // scene's derived one, which would be two different objects answering to the same number.
        if (first > NetworkId.FirstBaked - (uint)count) {
            throw new InvalidOperationException(
                $"This session has handed out {Count} ids and {count} more would reach the band scene-placed objects "
                + "derive theirs from. Something is spawning without despawning."
            );
        }

        next += (uint)count;

        return new(first);
    }
}
