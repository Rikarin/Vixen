// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Rendering.SurfaceCache;

/// <summary>What one cache texel remembers about its surface.</summary>
/// <param name="Albedo">What fraction of each channel the surface reflects.</param>
/// <param name="Normal">Which way it faces, normalised.</param>
/// <param name="Depth">How far inside the card's near plane it sits, in world units.</param>
/// <param name="Emissive">What it emits, as radiance.</param>
public readonly record struct SurfaceTexel(Vector3 Albedo, Vector3 Normal, float Depth, Vector3 Emissive);

/// <summary>Every card's captured surface and its lighting — doc 19 § L4's cache.</summary>
/// <remarks>
///     <para>
///         <b>The storage does not know what filled it</b>, the property every pool here shares: a
///         texel captured by the CPU reference and one a rasteriser will someday write are the same
///         texel, and the lighting and the bounce read them the same way.
///     </para>
///     <para>
///         <b>A texel's outgoing radiance is <c>emissive + albedo · (direct + gathered)</c>.</b>
///         Direct and gathered are both incident irradiance over π — the package convention — so
///         multiplying by albedo is what turns them into diffuse outgoing radiance. Gathered is
///         double-buffered: a radiosity pass reads every texel's previous answer while writing the
///         next, because a bounce that reads its own pass converges to whatever the texel order
///         made of it.
///     </para>
///     <para>
///         <b>Sampling picks the best-facing card that contains the point at the stored depth.</b>
///         Containment says the point is in some card's box; the depth agreement says the card
///         actually captured <i>this</i> surface rather than one in front of or behind it; the
///         facing test says the card saw the surface's own side. A linear scan over cards is the
///         fixture-honest first form — the spatial index is an optimisation with this as its
///         baseline.
///     </para>
/// </remarks>
public sealed class SurfaceCacheStore {
    readonly List<(SurfaceCard Card, Int2 Origin)> cards = [];
    readonly SurfaceTexel[] texels;
    readonly bool[] valid;
    readonly Vector3[] direct;
    Vector3[] gathered;
    Vector3[] gatheredNext;

    /// <summary>Builds an empty cache over one atlas.</summary>
    /// <param name="atlas">Where the cards' texels live.</param>
    /// <exception cref="ArgumentNullException">There is no atlas.</exception>
    public SurfaceCacheStore(SurfaceCacheAtlas atlas) {
        ArgumentNullException.ThrowIfNull(atlas);

        Atlas = atlas;

        var count = atlas.Size.X * atlas.Size.Y;

        texels = new SurfaceTexel[count];
        valid = new bool[count];
        direct = new Vector3[count];
        gathered = new Vector3[count];
        gatheredNext = new Vector3[count];
    }

    /// <summary>The allocator deciding where cards live.</summary>
    public SurfaceCacheAtlas Atlas { get; }

    /// <summary>The cards in residence, in the order they arrived.</summary>
    public IReadOnlyList<(SurfaceCard Card, Int2 Origin)> Cards => cards;

    /// <summary>How far a sampled point may sit from a texel's stored depth and still be its surface.</summary>
    public float DepthTolerance { get; set; } = 0.1f;

    /// <summary>Adds a card, if the atlas has room.</summary>
    /// <param name="card">The card.</param>
    /// <returns>Its index, or −1 when the atlas is spent — a budget, not an error.</returns>
    public int AddCard(SurfaceCard card) {
        if (!Atlas.TryAllocate(card.Resolution, out var origin)) {
            return -1;
        }

        cards.Add((card, origin));

        // The slot may be a reused one: a fresh card starts invalid, not haunted.
        for (var y = 0; y < card.Resolution.Y; y++) {
            for (var x = 0; x < card.Resolution.X; x++) {
                var at = Index(origin, new(x, y));

                valid[at] = false;
                direct[at] = default;
                gathered[at] = default;
                gatheredNext[at] = default;
            }
        }

        return cards.Count - 1;
    }

    /// <summary>Writes one texel's captured surface.</summary>
    /// <param name="card">The card, by index.</param>
    /// <param name="texel">The texel within it.</param>
    /// <param name="surface">What the capture saw.</param>
    public void SetSurface(int card, Int2 texel, SurfaceTexel surface) {
        var at = Index(cards[card].Origin, Validated(card, texel));

        texels[at] = surface;
        valid[at] = true;
    }

    /// <summary>Marks a texel as having captured nothing.</summary>
    public void Invalidate(int card, Int2 texel) {
        var at = Index(cards[card].Origin, Validated(card, texel));

        valid[at] = false;
        direct[at] = default;
        gathered[at] = default;
    }

    /// <summary>Whether a texel captured a surface.</summary>
    public bool IsValid(int card, Int2 texel) => valid[Index(cards[card].Origin, Validated(card, texel))];

    /// <summary>One texel's captured surface.</summary>
    public SurfaceTexel Surface(int card, Int2 texel) => texels[Index(cards[card].Origin, Validated(card, texel))];

    /// <summary>One texel's direct incident irradiance over π.</summary>
    public Vector3 Direct(int card, Int2 texel) => direct[Index(cards[card].Origin, Validated(card, texel))];

    /// <summary>Sets one texel's direct incident irradiance over π.</summary>
    public void SetDirect(int card, Int2 texel, Vector3 value) =>
        direct[Index(cards[card].Origin, Validated(card, texel))] = value;

    /// <summary>One texel's gathered indirect irradiance over π, as of the last swap.</summary>
    public Vector3 Gathered(int card, Int2 texel) => gathered[Index(cards[card].Origin, Validated(card, texel))];

    /// <summary>Stages one texel's next gathered irradiance — visible after <see cref="SwapGathered" />.</summary>
    public void SetGatheredNext(int card, Int2 texel, Vector3 value) =>
        gatheredNext[Index(cards[card].Origin, Validated(card, texel))] = value;

    /// <summary>Makes the staged gather the one everything reads.</summary>
    public void SwapGathered() => (gathered, gatheredNext) = (gatheredNext, gathered);

    /// <summary>One texel's outgoing radiance: emissive plus albedo times what arrives.</summary>
    public Vector3 Outgoing(int card, Int2 texel) {
        var at = Index(cards[card].Origin, Validated(card, texel));

        return valid[at] ? texels[at].Emissive + (texels[at].Albedo * (direct[at] + gathered[at])) : Vector3.Zero;
    }

    /// <summary>What a surface at a point radiates, if some card captured it.</summary>
    /// <param name="position">Where the surface is, in world space.</param>
    /// <param name="normal">Which way it faces.</param>
    /// <param name="radiance">Its outgoing radiance.</param>
    /// <returns>False when no resident card holds that surface — the caller's answer is black, the
    ///     honest reading of an uncached hit, exactly what the tracers returned before a cache
    ///     existed at all.</returns>
    public bool TryRadiance(Vector3 position, Vector3 normal, out Vector3 radiance) {
        radiance = default;

        var best = -1;
        var bestFacing = 0f;
        var bestTexel = default(Int2);

        for (var index = 0; index < cards.Count; index++) {
            var (card, origin) = cards[index];
            var facing = Vector3.Dot(normal, card.Direction);

            if (facing <= bestFacing || !card.TryProject(position, out var texel, out var depth)) {
                continue;
            }

            var at = Index(origin, texel);

            if (!valid[at] || MathF.Abs(texels[at].Depth - depth) > DepthTolerance) {
                continue;
            }

            best = index;
            bestFacing = facing;
            bestTexel = texel;
        }

        if (best < 0) {
            return false;
        }

        radiance = Outgoing(best, bestTexel);

        return true;
    }

    int Index(Int2 origin, Int2 texel) => ((origin.Y + texel.Y) * Atlas.Size.X) + origin.X + texel.X;

    Int2 Validated(int card, Int2 texel) {
        var resolution = cards[card].Card.Resolution;

        ArgumentOutOfRangeException.ThrowIfNegative(texel.X);
        ArgumentOutOfRangeException.ThrowIfNegative(texel.Y);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(texel.X, resolution.X);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(texel.Y, resolution.Y);

        return texel;
    }
}
