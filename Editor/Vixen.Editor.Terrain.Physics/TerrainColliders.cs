// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Terrain;
using Vixen.Terrain.Physics;
using TerrainMap = Vixen.Terrain.Terrain;

namespace Vixen.Editor.Terrain.Physics;

/// <summary>
///     Rebuilds a terrain's Jolt collision when a sculpt stroke moves the ground: the editor's
///     <see cref="ITerrainColliders" /> seam, fed by <see cref="TerrainColliderSystem" />.
/// </summary>
/// <remarks>
///     <para>
///         <b>The adapter <c>docs/plan/31</c> § D10 said nobody had written.</b>
///         <see cref="ITerrainColliders" /> is what <c>TerrainEdit.Commit</c> calls after every
///         stroke and its only implementation in the tree was a test double;
///         <see cref="TerrainColliderSystem" /> is what turns a tile's samples into a height-field
///         body. Neither may reference the other — the layer rules fail a <c>Core/</c> project that
///         references an <c>Editor/</c> one, and the toolset deliberately links no physics — so the
///         join is a third assembly, which is <c>Vixen.Terrain.Physics</c>' own arrangement one layer
///         up.
///     </para>
///     <para>
///         ⚠ <b>The claim it was "three lines" was nearly right and the missing line is the whole
///         point.</b> The two <c>Rebuild</c> methods return <see langword="bool" /> where the
///         interface returns <see langword="void" />, and a forwarding wrapper that discarded that
///         value would be the exact failure this engine keeps producing: <see langword="false" />
///         means <em>this system has never heard of this terrain</em>, so every stroke would return
///         successfully having rebuilt nothing. A terrain becomes known through
///         <see cref="TerrainColliderSystem.Sync" /> over its <c>ITerrainPlacements</c>, so that is
///         what a refusal is answered with.
///     </para>
///     <para>
///         ⚠ <b>Push and poll do not fight, and it is worth knowing why rather than assuming.</b>
///         <see cref="TerrainColliderSystem" /> stamps each tile with
///         <c>Terrain.RevisionOf</c> as it builds it, and its per-frame pass skips a tile whose stamp
///         still matches — so a stroke pushed through here is not rebuilt a second time on the next
///         frame. What this adds over the poll is only <em>when</em>: the frame the artist let go of
///         the mouse rather than the one after it.
///     </para>
///     <para>
///         ⚠ <b><see cref="Missed" /> is the number that says the wiring is wrong.</b> A stroke whose
///         terrain is in no placement list rebuilds nothing and cannot say so through a
///         <see langword="void" /> — which is how the seam stayed unfed and silent for a year. Zero
///         is the working state; anything else is a terrain being sculpted that the physics world was
///         never told about.
///     </para>
/// </remarks>
/// <param name="colliders">The system that owns the tile bodies.</param>
/// <example>
///     <code language="csharp" no-compile="a fragment; `physics` and `placements` are the host's">
///     var system = new TerrainColliderSystem(physics, placements);
///     terrainMode.Editing.Colliders = new TerrainColliders(system);
///     </code>
/// </example>
public sealed class TerrainColliders(TerrainColliderSystem colliders) : ITerrainColliders {
    readonly TerrainColliderSystem colliders = colliders ?? throw new ArgumentNullException(nameof(colliders));

    /// <summary>How many rebuilds named a terrain the collider system had no bodies for.</summary>
    /// <remarks>
    ///     ⚠ <b>Not an error, and not nothing either.</b> A scene sculpted before anything placed the
    ///     terrain in a physics world is the ordinary case <see cref="ITerrainColliders" />' own
    ///     remarks call "a terrain with no collision, not an error" — but a number that climbs while
    ///     a body is standing on that ground is an <c>ITerrainPlacements</c> that does not list the
    ///     terrain being edited, which has no other symptom.
    /// </remarks>
    public int Missed { get; private set; }

    /// <inheritdoc />
    public void Rebuild(TerrainMap terrain, int tileX, int tileZ) {
        if (!colliders.Rebuild(terrain, tileX, tileZ)) {
            Unknown();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Forwarded rather than left to the interface's default, which would be a tile at a
    ///     time.</b> Both walk <c>TerrainDescription.TilesOf</c> and answer with both sides of a
    ///     seam; the difference is that this one resolves the terrain once instead of once per tile.
    /// </remarks>
    public void Rebuild(TerrainMap terrain, TerrainRect rect) {
        if (!colliders.Rebuild(terrain, rect)) {
            Unknown();
        }
    }

    /// <summary>Asks the system to take in whatever is placed, since this terrain was not.</summary>
    /// <remarks>
    ///     ⚠ <b><see cref="TerrainColliderSystem.Sync" /> and not a second <c>Rebuild</c>.</b> A
    ///     terrain it has not seen is built in full by the sync — every tile, off the composite the
    ///     stroke has already resolved — so re-pushing the stroke's own rectangle afterwards would
    ///     register a duplicate Jolt shape for a tile that had just been built from the same samples,
    ///     and <c>PhysicsShapes</c> never releases one.
    /// </remarks>
    void Unknown() {
        Missed++;
        colliders.Sync();
    }
}
