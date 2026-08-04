// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace Vixen.Live;

/// <summary>What makes two shards interchangeable: a map, a region and a version pair.</summary>
/// <remarks>
///     <para>
///         Every hard filter in doc 27 § Placement is a comparison against this one value —
///         <c>shard.Version == request.BuildVersion</c>, <c>shard.Content == request.ContentHash</c>,
///         <c>shard.Region == request.Region</c> — so naming the tuple makes "the set of shards a
///         player could be sent to" a dictionary lookup rather than a scan with four predicates.
///         Everything the score then weighs (who is already there, how full it is, how old it is) is
///         a property of a shard <em>within</em> one key.
///     </para>
///     <para>
///         <see cref="Region" /> is an opaque string and the engine never interprets it (M-Q5). Every
///         game has latency zones and no two of them mean the same thing by "EU"; what the engine can
///         usefully promise is that it will never place a player across one.
///     </para>
/// </remarks>
/// <param name="Map">An addressable address — <c>maps/queensdale</c>. ADR-013: there is no other kind.</param>
/// <param name="Region">The latency zone. Opaque, compared verbatim.</param>
/// <param name="Version">The build and content pair a client must match (ADR-022).</param>
public readonly record struct ShardKey(string Map, string Region, RealmVersion Version) {
    /// <summary>The map's address. Null only on <c>default</c>; see <see cref="RealmInstanceId" />.</summary>
    public string Map { get; } = Map ?? "";

    /// <summary>The latency zone. Null only on <c>default</c>.</summary>
    public string Region { get; } = Region ?? "";

    /// <summary>Whether this names a placeable set at all.</summary>
    public bool IsValid => !string.IsNullOrEmpty(Map);

    /// <summary>
    ///     The map's leaf name — what <c>NetworkSceneId</c> hashes and what a loaded scene is called.
    /// </summary>
    /// <remarks>
    ///     Doc 27 § The scene-management join: the wire says a scene by the hash of its <em>name</em>,
    ///     and the name is the last segment of the address the content build published it under. So
    ///     a client that has loaded <c>maps/queensdale</c> already agrees with the realm about what
    ///     the props are, without a message having been exchanged to say so.
    /// </remarks>
    public string SceneName {
        get {
            if (!IsValid) {
                return "";
            }

            var separator = Map.LastIndexOf('/');

            return separator < 0 ? Map : Map[(separator + 1)..];
        }
    }

    /// <inheritdoc />
    public override string ToString() =>
        IsValid
            ? string.Create(CultureInfo.InvariantCulture, $"{Map} [{Region}] {Version}")
            : "no map";
}

/// <summary>One mechanism, four configurations. Doc 27 § Shard kinds.</summary>
/// <remarks>
///     All four are the same shard with the same lifecycle; they differ in who is admitted and when
///     the shard is allowed to stop. Modelling them as one type with a discriminator rather than as
///     four subsystems is what makes a dungeon a placement decision instead of a second server.
/// </remarks>
public enum ShardKind : byte {
    /// <summary>Anyone may be placed here, and it lives while it is populated. The megaserver case.</summary>
    Public = 0,

    /// <summary>An access list — a party, a guild, a raid roster — and a lifetime tied to the group.</summary>
    Instance = 1,

    /// <summary>A matchmaker's roster, for exactly one match.</summary>
    Match = 2,

    /// <summary>
    ///     An owner and their permissions: housing, a guild hall. Hibernated when empty and
    ///     rehydrated on entry, which is nearly free because its authored state is durable rather
    ///     than volatile (ADR-021).
    /// </summary>
    Persistent = 3
}

/// <summary>Where a shard is in its life. Doc 27 § Grains, and the spine of the whole design.</summary>
/// <remarks>
///     <code>
///     Requested → Starting → Ready → Draining → Stopping → Stopped
///                      ↓        ↓        ↓
///                    Failed ← Lost ← (missed heartbeats)
///     </code>
///     ⚠ <b><see cref="Ready" /> is the only state that is a placement candidate</b>, and that single
///     rule is what makes both elastic scaling and rolling upgrades work with no further mechanism:
///     a shard stops taking arrivals the instant it starts draining, and one that has not finished
///     loading its map never takes any.
/// </remarks>
public enum ShardState : byte {
    /// <summary>A decision to exist, with no process behind it yet.</summary>
    Requested = 0,

    /// <summary>The backend created something. It is not a placement candidate.</summary>
    Starting = 1,

    /// <summary>Connected, map loaded, endpoint reported. The only placeable state.</summary>
    Ready = 2,

    /// <summary>No new placements; the players already here are moved at safe moments (§ Drain).</summary>
    Draining = 3,

    /// <summary>Empty, and shutting down.</summary>
    Stopping = 4,

    /// <summary>Gone, on purpose.</summary>
    Stopped = 5,

    /// <summary>It never came up.</summary>
    Failed = 6,

    /// <summary>
    ///     Three heartbeats missed. Gone, not on purpose, and its volatile state with it — recovery
    ///     is a placement rather than a resurrection.
    /// </summary>
    Lost = 7
}

/// <summary>How full a shard is allowed to get, and where placement stops preferring it.</summary>
/// <remarks>
///     <para>
///         Two numbers rather than one, because they answer different questions.
///         <see cref="HardCap" /> is a hard filter — a candidate at it is not scored at all — and
///         <see cref="SoftCap" /> is where doc 27 § Placement's fill term turns negative and a spawn
///         starts being considered. The gap between them is what a party arriving together fits
///         into: reserving the last slots is the difference between "join your friend" working and
///         it working except at peak.
///     </para>
///     <para>
///         ⚠ <b>Neither is the engine's to guess.</b> The right numbers depend on what a map's
///         entities cost to replicate, which doc 27 answers by measurement —
///         <c>Samples/09-NetworkSoak</c> — rather than by a default that reads as advice.
///     </para>
/// </remarks>
/// <param name="SoftCap">The population placement starts steering away from.</param>
/// <param name="HardCap">The population above which a shard admits nobody.</param>
public readonly record struct ShardCapacity(int SoftCap, int HardCap) {
    /// <summary>Whether the two numbers make sense together.</summary>
    public bool IsValid => SoftCap > 0 && HardCap >= SoftCap;

    /// <summary>Whether a shard at this population may admit one more.</summary>
    /// <param name="population">How many are on it now.</param>
    /// <returns>Whether there is room under the hard cap.</returns>
    public bool Admits(int population) => population < HardCap;

    /// <summary>How full a shard is, as a fraction of its soft cap.</summary>
    /// <param name="population">How many are on it now.</param>
    /// <returns>Zero to one and beyond — a shard over its soft cap reports more than one.</returns>
    public double FillAt(int population) => SoftCap <= 0 ? 0 : (double)population / SoftCap;

    /// <inheritdoc />
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"soft {SoftCap}, hard {HardCap}");
}

/// <summary>Whether a player can be moved right now. Doc 27 § Drain.</summary>
/// <remarks>
///     <para>
///         The whole quality of draining a shard is in <em>when</em>, and the engine deliberately
///         does not pretend to know: it ships a default that answers <see cref="Ready" /> for
///         everyone and the game replaces it, because "in a scripted encounter" is a sentence only
///         the game can finish.
///     </para>
///     <para>
///         ⚠ <b>Nothing is force-disconnected by drain.</b> <see cref="Blocked" /> escalates to a
///         live-ops alert at the hard deadline, and the escalation path ends in a human or a
///         maintenance window rather than in a kick.
///     </para>
/// </remarks>
public enum TransferReadiness : byte {
    /// <summary>Idle, standing, walking. Move them now.</summary>
    Ready = 0,

    /// <summary>Mid-interaction, or in combat under the grace period. Ask again shortly.</summary>
    Soon = 1,

    /// <summary>In a boss fight, a story step, a match. Not until it ends, or the hard deadline.</summary>
    Blocked = 2
}
