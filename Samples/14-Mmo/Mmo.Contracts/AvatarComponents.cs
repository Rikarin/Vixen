// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Net.Replication;

namespace Vixen.Samples.Mmo.Contracts;

// What crosses the wire, and the only assembly all four processes agree about.
//
// ⚠ Replication is a delta PER COMPONENT: an entity's component is compared with what a connection
// has acknowledged, and either the whole of it is sent or none of it. So what changes together
// belongs together, and what never changes belongs on its own. That is why identity, pose, vitals
// and appearance are four structs and not one — a character standing still and chatting sends
// nothing, where one struct would send their whole state because their facing twitched.

/// <summary>Who an avatar belongs to, and what they are. Sent once.</summary>
[Replicated(Priority = 40)]
public struct Avatar {
    /// <summary>The session's number for the player, not their account. <c>PlayerKey</c> is that.</summary>
    public uint Owner;

    /// <summary>Their specialisation, as a <c>DefId</c> value — Vanguard, Emberwright, Marksman.</summary>
    public uint Specialisation;

    /// <summary>Their level, which changes rarely enough to belong with identity rather than vitals.</summary>
    public ushort Level;
}

/// <summary>Where something is. Sent every tick, which is why nothing else is in it.</summary>
[Replicated(Priority = 20)]
public struct Pose {
    /// <summary>Metres.</summary>
    public float X;

    /// <summary>Metres.</summary>
    public float Y;

    /// <summary>Metres.</summary>
    public float Z;

    /// <summary>Radians.</summary>
    public float Facing;
}

/// <summary>Health and the class resource. Sent when they move, which is often but not every tick.</summary>
/// <remarks>
///     ⚠ <b>One resource field and not three.</b> Rage, mana and focus are the same number to the
///     wire; which one it is follows from <see cref="Avatar.Specialisation" />, which the receiver
///     already has. Three fields would send two zeroes to every client for every character in view.
/// </remarks>
[Replicated(Priority = 30)]
public struct Vitals {
    /// <summary>What they have.</summary>
    public int Health;

    /// <summary>What they could have.</summary>
    public int MaximumHealth;

    /// <summary>Rage, mana or focus, depending on the specialisation.</summary>
    public int Resource;

    /// <summary>The same, at full.</summary>
    public int MaximumResource;
}

/// <summary>What to draw them as. Sent when it changes, which is rarely and never in combat.</summary>
/// <remarks>
///     The wardrobe's answer rather than the equipment's: <c>Wardrobe.Resolve</c> has already decided
///     whether a slot is hidden, overridden or worn, so the client is told what to draw and never has
///     to know what a transmog is.
/// </remarks>
[Replicated(Priority = 60)]
public struct Appearance {
    /// <summary>What is on their head, as a <c>DefId</c> value, or zero for nothing.</summary>
    public uint Head;

    /// <summary>Their chest.</summary>
    public uint Chest;

    /// <summary>Their main hand.</summary>
    public uint MainHand;

    /// <summary>The title after their name, or zero.</summary>
    public uint Title;
}

/// <summary>What a mount or a waggon is carrying. Absent when nobody is riding.</summary>
/// <remarks>
///     ⚠ <b>The passenger's <em>seat</em> and not their position.</b> A passenger replicating world
///     coordinates fights the vehicle's own; a seat index is the one fact that does not. Doc 16's
///     parent-relative replication has since landed — <c>Vixen.Net.Motion.NetworkParent</c> — and
///     this sample has not adopted it, which is issue 434 rather than an omission here.
/// </remarks>
[Replicated(Priority = 50)]
public struct Riding {
    /// <summary>The vehicle's network id, or zero when they are on foot.</summary>
    public uint Vehicle;

    /// <summary>Which seat.</summary>
    public byte Seat;
}
