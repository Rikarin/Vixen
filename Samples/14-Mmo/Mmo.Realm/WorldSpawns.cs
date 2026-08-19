// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Gameplay.Ai;

namespace Vixen.Samples.Mmo.Realms;

/// <summary>Every camp this shard keeps populated, ticked on the realm's clock.</summary>
/// <remarks>
///     <para>
///         <b>The reason this class exists is that a shard with no players still has work to do</b>,
///         and until now <c>MmoRealm.OnRealmUpdate</c> did none of it: it purged lockouts, which is
///         housekeeping over a view, and touched no gameplay library at all. A camp coming back on
///         its own timer is the smallest thing a world owes that is unambiguously the AI library's
///         and unambiguously the realm's to drive.
///     </para>
///     <para>
///         ⚠ <b>It says what to spawn and never where.</b> That is <see cref="Spawner" />'s boundary
///         and not this class' choice — placing something needs the scene and a navigation mesh, and
///         a realm that wanted those would ask <c>Services.Scenes</c> for them. What lands here is
///         the order, which is the half a content build decides.
///     </para>
///     <para>
///         ⚠ <b>Every table in the build, because the content does not say which map a camp is on.</b>
///         That is a real gap in the sample's content model rather than a simplification: a
///         <c>SpawnTableDefinition</c> names its entries, its cap and its leash, and nothing anywhere
///         joins a camp to a map. A shard therefore cannot scope its camps, and this drives the lot.
///         The fix is a field on the map or on the table, and it is content work rather than engine
///         work — see the README.
///     </para>
///     <para>
///         ⚠ <b>Seeded from the shard, so a replay matches.</b> <see cref="Spawner" /> takes a seed
///         and derives its stream per table id; two shards of one map with the same seed populate
///         identically, which is what makes a soak's numbers reproducible rather than anecdotal.
///     </para>
/// </remarks>
public sealed class WorldSpawns {
    readonly List<Spawner> spawners = [];
    readonly List<SpawnOrder> orders = [];

    /// <summary>Stands the camps up from a compiled library.</summary>
    /// <param name="library">The build's spawn tables.</param>
    /// <param name="seed">What the shard's streams are seeded from.</param>
    public WorldSpawns(SpawnLibrary library, ulong seed) {
        ArgumentNullException.ThrowIfNull(library);

        // Address order, which SpawnLibrary.Tables already guarantees — so two processes given the
        // same content build stand the same camps up in the same order and the same slots.
        foreach (var table in library.Tables) {
            spawners.Add(new(table, seed));
        }
    }

    /// <summary>How many camps this shard is keeping.</summary>
    public int Camps => spawners.Count;

    /// <summary>How many things are alive across all of them.</summary>
    public int Alive => spawners.Sum(spawner => spawner.Alive);

    /// <summary>How many spawn orders have been issued since the shard started.</summary>
    /// <remarks>
    ///     <b>The counter that proves the library ran in this process.</b> It is a function of the
    ///     content and the clock and nothing else, so a shard started twice on one build reports the
    ///     same number at the same tick.
    /// </remarks>
    public long Issued { get; private set; }

    /// <summary>What the last tick asked for.</summary>
    public IReadOnlyList<SpawnOrder> Last => orders;

    /// <summary>Puts back whatever is due.</summary>
    /// <param name="now">The realm's clock, in seconds since it started.</param>
    /// <returns>How many orders this tick made.</returns>
    public int Tick(float now) {
        orders.Clear();

        var made = 0;

        foreach (var spawner in spawners) {
            made += spawner.Tick(now, orders);
        }

        Issued += made;

        return made;
    }

    /// <summary>Says something died, so its slot comes back on the table's timer.</summary>
    /// <param name="camp">Which camp, by index into <see cref="Camps" />.</param>
    /// <param name="slot">Which slot it filled.</param>
    /// <param name="now">When, on the realm's clock.</param>
    /// <returns>Whether that slot was occupied.</returns>
    /// <remarks>
    ///     ⚠ <b>The timer starts here and not on the tick that notices.</b> A server that fell behind
    ///     would otherwise repopulate faster than one that did not — a difference players feel and
    ///     nobody can explain. <see cref="Spawner.Died" /> is where that is actually enforced; this
    ///     only forwards.
    /// </remarks>
    public bool Died(int camp, int slot, float now) =>
        (uint)camp < (uint)spawners.Count && spawners[camp].Died(slot, now);
}
