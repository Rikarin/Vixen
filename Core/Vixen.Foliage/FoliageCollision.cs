// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Foliage;

/// <summary>
///     Which instances are near enough to something to be worth a physics body.
/// </summary>
/// <remarks>
///     <para>
///         <b>[docs/plan/31 § D10]: a foliage type declares a collision shape and an activation
///         radius, and instances within that radius of a physics-relevant entity get a body.</b> Ten
///         thousand static bodies is not a scene, it is a broadphase problem.
///     </para>
///     <para>
///         ⚠ <b>This is a visible behavioural difference and it is stated rather than hidden</b>: a
///         projectile fired at a tree four hundred metres away passes through it. The alternative is a
///         broadphase that degrades for the whole game, and the mitigation available to a project that
///         needs it is to raise <see cref="FoliageType.ActivationRadius" /> for that type.
///     </para>
///     <para>
///         ⚠ <b>What comes out is a <em>difference</em>, not a set.</b> The caller pools bodies, and a
///         set would make it diff two collections of ten thousand addresses every frame to find the
///         four that changed. <see cref="Update" /> hands back exactly the four.
///     </para>
///     <para>
///         Grass never collides: a derived type is never asked, because its instances do not exist
///         between one frame and the next.
///     </para>
/// </remarks>
public sealed class FoliageCollision {
    readonly HashSet<FoliageAddress> active = [];
    readonly List<FoliageAddress> activated = [];
    readonly List<FoliageAddress> deactivated = [];
    readonly HashSet<FoliageAddress> wanted = [];

    /// <summary>Every instance that currently has a body.</summary>
    public IReadOnlySet<FoliageAddress> Active => active;

    /// <summary>What gained one on the last <see cref="Update" />.</summary>
    public IReadOnlyList<FoliageAddress> Activated => activated;

    /// <summary>And what lost one.</summary>
    public IReadOnlyList<FoliageAddress> Deactivated => deactivated;

    /// <summary>How many bodies are wanted right now.</summary>
    public int Count => active.Count;

    /// <summary>Works out which instances should have a body, and says what changed.</summary>
    /// <param name="volume">The instances.</param>
    /// <param name="sources">
    ///     Where the physics-relevant entities are, in world space. Usually one: the player.
    /// </param>
    /// <returns>How many bodies are wanted afterwards.</returns>
    /// <remarks>
    ///     ⚠ <b>The radius is the <em>type's</em>, so one source produces a different set per
    ///     type.</b> A boulder wants a body from further away than a fern does, and a single global
    ///     radius would make one of them wrong — which is the setting a project reaches for when a
    ///     vehicle drives through a rock it should have hit.
    /// </remarks>
    public int Update(FoliageVolume volume, IReadOnlyList<Vector3> sources) {
        ArgumentNullException.ThrowIfNull(volume);
        ArgumentNullException.ThrowIfNull(sources);

        wanted.Clear();
        activated.Clear();
        deactivated.Clear();

        for (var type = 0; type < volume.Palette.Count; type++) {
            var settings = volume.Palette[type];

            if (!settings.Collides || settings.Storage == FoliageStorage.Derived) {
                continue;
            }

            var only = new HashSet<int> { type };

            foreach (var source in sources) {
                foreach (var address in volume.Within(
                             new(source.X, source.Z),
                             settings.ActivationRadius,
                             only
                         )) {
                    wanted.Add(address);
                }
            }
        }

        foreach (var address in wanted) {
            if (active.Add(address)) {
                activated.Add(address);
            }
        }

        foreach (var address in active) {
            if (!wanted.Contains(address)) {
                deactivated.Add(address);
            }
        }

        foreach (var address in deactivated) {
            active.Remove(address);
        }

        return active.Count;
    }

    /// <summary>Drops every body, which is what leaving a level does.</summary>
    /// <remarks>
    ///     ⚠ <b>The whole active set comes out as <see cref="Deactivated" />, so a pool can return
    ///     it.</b> Clearing silently would leak every body the caller had allocated — and it would
    ///     leak them into a physics world that is about to be handed a different volume's instances
    ///     at the same addresses.
    /// </remarks>
    public void Clear() {
        activated.Clear();
        deactivated.Clear();
        deactivated.AddRange(active);

        active.Clear();
        wanted.Clear();
    }

    /// <summary>Forgets an address without reporting it, for an instance that no longer exists.</summary>
    /// <param name="address">The address.</param>
    /// <returns>Whether it had a body.</returns>
    /// <remarks>
    ///     ⚠ <b>What an erase stroke needs, and it is not the same as letting the next
    ///     <see cref="Update" /> notice.</b> An erased instance's address now belongs to whichever
    ///     instance shifted down into it, so the next update would find that one already active and
    ///     never give it a body of its own — a tree with a hole where its collision should be, for as
    ///     long as the level runs.
    /// </remarks>
    public bool Forget(FoliageAddress address) => active.Remove(address);

    /// <summary>Forgets every body in a cell, for a cell an edit rewrote.</summary>
    /// <param name="type">Which type.</param>
    /// <param name="cell">Which cell.</param>
    /// <returns>How many were forgotten.</returns>
    public int ForgetCell(int type, FoliageCellKey cell) {
        var stale = active.Where(address => address.Type == type && address.Cell == cell).ToArray();

        foreach (var address in stale) {
            active.Remove(address);
        }

        return stale.Length;
    }
}
