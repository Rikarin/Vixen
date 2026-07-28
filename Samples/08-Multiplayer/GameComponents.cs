// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Net.Replication;

namespace Vixen.Samples.Multiplayer;

/// <summary>Who a fighter belongs to, and which side they are on.</summary>
/// <remarks>
///     <para>
///         Split from <see cref="Vitals" /> deliberately, and the split is the lesson. Replication is
///         a delta <b>per component</b>: an entity's component is compared with what a connection has
///         acknowledged, and either the whole of it is sent or none of it is. Owner and team never
///         change after the spawn; health changes every time somebody is shot. Put them in one struct
///         and every hit re-sends the owner id to everybody, for the rest of the match.
///     </para>
///     <para>
///         Priority 40 puts it ahead of everything else when the bandwidth budget runs out, which is
///         the right way round for a value that is sent once: a fighter whose identity has not
///         arrived cannot be drawn at all, and one whose health is a tick stale looks fine.
///     </para>
/// </remarks>
[Replicated(Priority = 40)]
internal struct Combatant {
    /// <summary>The <c>PlayerId</c> whose fighter this is.</summary>
    public uint Owner;

    /// <summary>Which side. Two of them, alternating on join.</summary>
    public byte Team;
}

/// <summary>What changes when somebody is shot.</summary>
/// <remarks>
///     Three bytes on the wire and no quantization to declare, because these are already integers of
///     the size they mean. A <c>[Quantize]</c> on a byte would be a mistake the generator refuses —
///     see <c>VXNET1002</c>.
/// </remarks>
[Replicated(Priority = 30)]
internal struct Vitals {
    /// <summary>Nought to a hundred. Zero means dead and waiting to respawn.</summary>
    public byte Health;

    /// <summary>How many other fighters this one has finished off.</summary>
    public byte Score;

    /// <summary>How many times it has been finished off.</summary>
    public byte Deaths;
}
