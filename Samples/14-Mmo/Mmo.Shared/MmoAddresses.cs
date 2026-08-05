// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Gameplay;
using Vixen.Samples.Mmo.Contracts;

namespace Vixen.Samples.Mmo.Rules;

/// <summary>The handful of addresses code names, as constants rather than as literals.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Only the ones code actually names, which is a much shorter list than the content.</b>
///         There are 112 definitions and this holds a dozen: everything else is reached by walking a
///         library or by following a reference out of a definition that already resolved. A constant
///         per address would be a second copy of the content tree, maintained by hand, and out of
///         date the first time somebody renames a file.
///     </para>
///     <para>
///         <b>A real game generates this.</b> <c>vixen import --addresses Addresses.cs</c> writes the
///         whole tree as nested classes with a <c>DefId</c> apiece, from the addresses the content
///         build actually planned — which is the version that cannot drift. This file is hand-written
///         because the sample's content build is not wired into MSBuild yet, and the two are the same
///         shape on purpose so that swapping them is deleting a file.
///     </para>
///     <para>
///         ⚠ <b>A misspelt address here is not a compile error and never will be.</b>
///         <c>DefId.From</c> hashes whatever it is given, so <c>"maps/thornwod"</c> is a perfectly
///         good id for nothing at all. That is why the content test asserts every one of these
///         resolves — see <c>AddressTests</c>.
///     </para>
/// </remarks>
public static class MmoAddresses {
    /// <summary>The starter valley. The name is <c>Mmo.Contracts</c>'s — see <see cref="MmoMaps" />.</summary>
    public const string GreenmarchAddress = MmoMaps.Greenmarch;

    /// <summary>The higher-level map, and the transfer target.</summary>
    public const string ThornwoodAddress = MmoMaps.Thornwood;

    /// <summary>The five-player instance.</summary>
    public const string BarrowdeepAddress = "instances/barrowdeep";

    /// <summary>The battleground.</summary>
    public const string RavensfordAddress = "pvp/ravensford";

    /// <summary>The guild charter every guild in the sample is founded on.</summary>
    public const string FreeholdCharterAddress = "social/freehold-charter";

    /// <summary>The party policy.</summary>
    public const string PartyAddress = "social/party";

    /// <summary>The three specialisations, which decide what a class spends.</summary>
    public const string VanguardAddress = "progression/specialisations/vanguard";

    /// <summary>The caster.</summary>
    public const string EmberwrightAddress = "progression/specialisations/emberwright";

    /// <summary>The one with the rifle.</summary>
    public const string MarksmanAddress = "progression/specialisations/marksman";

    /// <summary>The level curve.</summary>
    public const string CurveAddress = "progression/curve";

    /// <summary>The world boss.</summary>
    public const string ColossusAddress = "events/rootbound-colossus";

    /// <summary>What a hearthstone does.</summary>
    public const string HearthstoneAddress = "travel/hearthstone";

    /// <summary>The primary currency, which the conservation oracle counts.</summary>
    public const string GoldAddress = "currencies/gold";

    /// <summary>The account-scoped token.</summary>
    public const string MarchmarksAddress = "currencies/marchmarks";

    /// <summary>The starter valley.</summary>
    public static DefId Greenmarch { get; } = DefId.From(GreenmarchAddress);

    /// <summary>The higher-level map.</summary>
    public static DefId Thornwood { get; } = DefId.From(ThornwoodAddress);

    /// <summary>The instance.</summary>
    public static DefId Barrowdeep { get; } = DefId.From(BarrowdeepAddress);

    /// <summary>The battleground.</summary>
    public static DefId Ravensford { get; } = DefId.From(RavensfordAddress);

    /// <summary>The charter.</summary>
    public static DefId FreeholdCharter { get; } = DefId.From(FreeholdCharterAddress);

    /// <summary>The party policy.</summary>
    public static DefId Party { get; } = DefId.From(PartyAddress);

    /// <summary>The tank.</summary>
    public static DefId Vanguard { get; } = DefId.From(VanguardAddress);

    /// <summary>The caster.</summary>
    public static DefId Emberwright { get; } = DefId.From(EmberwrightAddress);

    /// <summary>The one with the rifle.</summary>
    public static DefId Marksman { get; } = DefId.From(MarksmanAddress);

    /// <summary>The curve.</summary>
    public static DefId Curve { get; } = DefId.From(CurveAddress);

    /// <summary>The world boss.</summary>
    public static DefId Colossus { get; } = DefId.From(ColossusAddress);

    /// <summary>The hearthstone.</summary>
    public static DefId Hearthstone { get; } = DefId.From(HearthstoneAddress);

    /// <summary>Gold.</summary>
    public static DefId Gold { get; } = DefId.From(GoldAddress);

    /// <summary>Marchmarks.</summary>
    public static DefId Marchmarks { get; } = DefId.From(MarchmarksAddress);

    /// <summary>Every address this class names, so a test can check they all resolve.</summary>
    public static IEnumerable<string> All { get; } = [
        GreenmarchAddress,
        ThornwoodAddress,
        BarrowdeepAddress,
        RavensfordAddress,
        FreeholdCharterAddress,
        PartyAddress,
        VanguardAddress,
        EmberwrightAddress,
        MarksmanAddress,
        CurveAddress,
        ColossusAddress,
        HearthstoneAddress,
        GoldAddress,
        MarchmarksAddress
    ];
}
