// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Samples.Mmo.Contracts;

/// <summary>The maps, by address. Strings, because that is all four processes agree about.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Here rather than in <c>Mmo.Shared</c>, and finding out why was worth the compile
///         error.</b> The gate needs the map list — it answers "which realms are there" and it
///         validates an <c>EnterWorldRequest</c> — and the orchestrator needs it to configure a
///         placement. Neither simulates anything, so neither should link twenty gameplay libraries;
///         a login service that loaded a threat table would be exactly the thing doc 27's assembly
///         split exists to prevent.
///     </para>
///     <para>
///         So the <em>names</em> are wire vocabulary and live here, and the <em>ids</em> are the
///         simulation's and live in <c>MmoAddresses</c>. A <c>DefId</c> would drag
///         <c>Vixen.Gameplay</c> in behind it, and nothing on this side of the split needs one.
///     </para>
/// </remarks>
public static class MmoMaps {
    /// <summary>The starter valley. Public, and where a new character begins.</summary>
    public const string Greenmarch = "maps/greenmarch";

    /// <summary>The higher-level map, and the transfer target.</summary>
    public const string Thornwood = "maps/thornwood";

    /// <summary>The five-player instance's scene.</summary>
    /// <remarks>⚠ Not offered by the gate: an instance is allocated, never walked into.</remarks>
    public const string Barrowdeep = "maps/barrowdeep";

    /// <summary>The battleground's scene.</summary>
    /// <remarks>⚠ Not offered by the gate either: a match is what a queue hands out.</remarks>
    public const string Ravensford = "maps/ravensford";

    /// <summary>The two a player may ask for by name.</summary>
    public static IEnumerable<string> Public { get; } = [Greenmarch, Thornwood];
}
