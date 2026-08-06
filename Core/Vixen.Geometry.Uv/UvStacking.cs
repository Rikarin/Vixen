// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Geometry.Uv;

/// <summary>Two islands one region of texture could serve, and how well the match holds.</summary>
/// <param name="Representative">The island that would be packed. Always the lower index of the pair.</param>
/// <param name="Partner">The island that would share its region.</param>
/// <param name="Mirrored">Whether the partner matches the representative reflected in <c>u</c>.</param>
/// <param name="Residual">
///     The worst corner's disagreement, as a fraction of the representative's extent. Zero is an exact
///     match; docs/plan/41 § D11's exact mirror produces exactly zero.
/// </param>
/// <remarks>
///     ⚠ <b>An offer, and never a decision.</b> docs/plan/42 § D10: stacking forbids asymmetric detail,
///     and discovering that after texturing is expensive — a scar on one cheek, a logo on one sleeve, a
///     wear pattern on one boot. On an arbitrary mesh the match is approximate, so what a detector can
///     honestly produce is a list with a number beside each entry and somebody else's decision.
/// </remarks>
public readonly record struct UvStackOffer(int Representative, int Partner, bool Mirrored, float Residual);

/// <summary>docs/plan/42 § D10's symmetric stacking: opt-in, offered rather than applied.</summary>
/// <remarks>
///     <para>
///         <b>Symmetric islands can be deliberately overlapped so both halves share one region of
///         texture, halving what the atlas costs.</b> ⚠ <b>It is off by default</b> — nothing in this
///         library calls it — because it forbids asymmetric detail, and the cost of finding that out is
///         a retexture rather than a repack.
///     </para>
///     <para>
///         ⚠ <b>Doc 41 § D11's exact symmetry is what makes detection reliable, and the honest
///         limitation is stated rather than hidden.</b> A mesh remeshed with symmetry on has vertex
///         <i>k</i> and its mirror as exact negations, so the two islands come out with their corners
///         in the same order and the comparison below is an equality. On a mesh that was not remeshed
///         that way the corner orders are unrelated and no pair will match however symmetric the
///         surface is — this detector reports nothing rather than reporting a guess. Finding the
///         correspondence itself is a shape-matching problem and § D10 does not ask for one.
///     </para>
///     <para>
///         ⚠ <b>Folding is a change to which islands are packed and never to an island's
///         coordinates.</b> The partner keeps its own shape and is given the representative's
///         placement; <see cref="UvPlacement.Apply" /> normalizes by the island's own lower corner, so
///         a mirrored partner lands on the same rectangle with its own parameterization — which is what
///         "share one region" means, and it costs no resampling.
///     </para>
/// </remarks>
/// <example>
///     <code>
/// var offers = UvStacking.Detect(islands);                       // nothing has happened yet
/// var accepted = offers.Where(offer => offer.Residual &lt; 1e-4f).ToArray();
/// var folded = UvStacking.Fold(islands, accepted, out var source);
/// var placements = UvStacking.Unfold(UvUnwrap.Pack(folded, settings), source);
///     </code>
/// </example>
public static class UvStacking {
    /// <summary>How close two corners must sit, as a fraction of the extent, before a pair is offered.</summary>
    public const float DefaultTolerance = 1e-3f;

    /// <summary>Finds pairs of islands that one region of texture could serve.</summary>
    /// <param name="islands">The islands.</param>
    /// <param name="tolerance">The worst corner disagreement an offer may carry, as a fraction of the extent.</param>
    /// <returns>One offer per matched pair, ascending by representative. Nothing is modified.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="islands" /> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tolerance" /> is negative or not finite.</exception>
    /// <remarks>
    ///     ⚠ <b>Every island is visited in index order and paired with the lowest-index island still
    ///     free, which is the tie-break the whole thing rests on.</b> A greedy pass that enumerated a
    ///     <see cref="HashSet{T}" /> of candidates would pair differently on a different runtime, and
    ///     the atlas would differ with it — docs/plan/42 § D12 rules that out, and an <i>offer</i> that
    ///     moved between machines would be worse than no offer at all because a human would have
    ///     accepted it once.
    /// </remarks>
    public static IReadOnlyList<UvStackOffer> Detect(
        IReadOnlyList<UvIsland> islands,
        float tolerance = DefaultTolerance
    ) {
        ArgumentNullException.ThrowIfNull(islands);

        if (tolerance < 0f || !float.IsFinite(tolerance)) {
            throw new ArgumentOutOfRangeException(
                nameof(tolerance),
                tolerance,
                "The tolerance is a fraction of an island's extent, so it is non-negative and finite. "
                + "Zero asks for docs/plan/41 § D11's exact mirror and nothing else."
            );
        }

        var taken = new bool[islands.Count];
        var offers = new List<UvStackOffer>();

        for (var first = 0; first < islands.Count; first++) {
            if (taken[first]) {
                continue;
            }

            for (var second = first + 1; second < islands.Count; second++) {
                if (taken[second] || !Comparable(islands[first], islands[second])) {
                    continue;
                }

                var straight = Residual(islands[first], islands[second], false);
                var mirrored = Residual(islands[first], islands[second], true);
                var mirror = mirrored < straight;
                var residual = mirror ? mirrored : straight;

                if (!(residual <= tolerance)) {
                    continue;
                }

                taken[first] = true;
                taken[second] = true;
                offers.Add(new(first, second, mirror, residual));

                break;
            }
        }

        return offers;
    }

    /// <summary>Drops every accepted partner, leaving the islands to hand the packer.</summary>
    /// <param name="islands">The islands.</param>
    /// <param name="accepted">The offers the caller decided to take.</param>
    /// <param name="source">
    ///     One entry per original island, saying which folded island carries it. A representative and
    ///     its partner share an entry, which is what makes them share a region.
    /// </param>
    /// <returns>The representatives, in the islands' own order.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">An offer names an island twice or names one that does not exist.</exception>
    public static IReadOnlyList<UvIsland> Fold(
        IReadOnlyList<UvIsland> islands,
        IReadOnlyList<UvStackOffer> accepted,
        out int[] source
    ) {
        ArgumentNullException.ThrowIfNull(islands);
        ArgumentNullException.ThrowIfNull(accepted);

        var partnerOf = new int[islands.Count];

        Array.Fill(partnerOf, -1);

        foreach (var offer in accepted) {
            if (offer.Representative < 0 || offer.Representative >= islands.Count
                || offer.Partner < 0 || offer.Partner >= islands.Count) {
                throw new ArgumentException(
                    $"An offer names islands {offer.Representative} and {offer.Partner} of {islands.Count}.",
                    nameof(accepted)
                );
            }

            if (offer.Representative == offer.Partner || partnerOf[offer.Partner] >= 0) {
                throw new ArgumentException(
                    $"Island {offer.Partner} is stacked onto more than one representative. An island "
                    + "shares one region or none; a chain of them would be three halves of a mirror.",
                    nameof(accepted)
                );
            }

            partnerOf[offer.Partner] = offer.Representative;
        }

        // A representative that is itself somebody's partner would fold into a fold, so the two passes
        // are separated: the first records the pairing, the second rejects a chain.
        foreach (var offer in accepted) {
            if (partnerOf[offer.Representative] >= 0) {
                throw new ArgumentException(
                    $"Island {offer.Representative} is both a representative and a partner.",
                    nameof(accepted)
                );
            }
        }

        var folded = new List<UvIsland>(islands.Count);
        var slot = new int[islands.Count];

        source = new int[islands.Count];

        for (var index = 0; index < islands.Count; index++) {
            if (partnerOf[index] >= 0) {
                continue;
            }

            slot[index] = folded.Count;
            folded.Add(islands[index]);
        }

        for (var index = 0; index < islands.Count; index++) {
            source[index] = slot[partnerOf[index] >= 0 ? partnerOf[index] : index];
        }

        return folded;
    }

    /// <summary>Turns a pack of the folded islands back into one placement per original island.</summary>
    /// <param name="packed">What the packer returned for the folded list.</param>
    /// <param name="source">What <see cref="Fold" /> handed back.</param>
    /// <returns>One placement per original island, in the original order.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">A folded island has no placement.</exception>
    /// <remarks>
    ///     ⚠ <b>The partner gets the representative's offset, scale, rotation and tile unchanged, and
    ///     only <see cref="UvPlacement.Island" /> moves.</b> That is the whole of stacking: two islands
    ///     with one transform is two islands on one region of texture. It also means the partner's
    ///     texels are the representative's, so a bake has to write one of them and not both — which is
    ///     the cost § D10 says is paid at texturing time rather than here.
    /// </remarks>
    public static IReadOnlyList<UvPlacement> Unfold(IReadOnlyList<UvPlacement> packed, IReadOnlyList<int> source) {
        ArgumentNullException.ThrowIfNull(packed);
        ArgumentNullException.ThrowIfNull(source);

        var byIsland = new UvPlacement?[packed.Count];

        foreach (var placement in packed) {
            if (placement.Island < 0 || placement.Island >= packed.Count) {
                throw new ArgumentException(
                    $"A placement names folded island {placement.Island} of {packed.Count}.",
                    nameof(packed)
                );
            }

            byIsland[placement.Island] = placement;
        }

        var expanded = new UvPlacement[source.Count];

        for (var index = 0; index < source.Count; index++) {
            var folded = source[index];

            if (folded < 0 || folded >= byIsland.Length || byIsland[folded] is not { } placement) {
                throw new ArgumentException(
                    $"Island {index} folds onto {folded}, which the packer did not place.",
                    nameof(source)
                );
            }

            expanded[index] = placement with { Island = index };
        }

        return expanded;
    }

    /// <summary>Whether two islands are the same size and shape of thing at all.</summary>
    /// <remarks>
    ///     The cheap rejection that keeps the pairwise search affordable: two islands with different
    ///     corner counts, or extents that differ by more than the tolerance a mirror would ever need,
    ///     cannot be one region however they are aligned.
    /// </remarks>
    static bool Comparable(UvIsland first, UvIsland second) {
        if (first.Coordinates is null || second.Coordinates is null || first.Corners is null
            || second.Corners is null) {
            return false;
        }

        if (first.Coordinates.Count != second.Coordinates.Count || first.Coordinates.Count == 0) {
            return false;
        }

        var one = first.Size;
        var two = second.Size;
        var extent = MathF.Max(MathF.Max(one.X, one.Y), MathF.Max(two.X, two.Y));

        if (!(extent > 0f) || !float.IsFinite(extent)) {
            return false;
        }

        // A mirror in u swaps nothing about the extent, so the two boxes have to agree either way.
        return MathF.Abs(one.X - two.X) <= 0.05f * extent && MathF.Abs(one.Y - two.Y) <= 0.05f * extent;
    }

    /// <summary>The worst corner's disagreement once both islands are put in their own lower corner.</summary>
    /// <remarks>
    ///     ⚠ <b>Both islands are taken relative to their own <see cref="UvIsland.Minimum" />, which is
    ///     the same normalization <see cref="UvPlacement.Apply" /> makes.</b> Comparing raw coordinates
    ///     would measure where the flattener happened to leave the gauge, and a conformal map's gauge is
    ///     arbitrary — two islands could be the same shape in different corners of the plane and read as
    ///     completely different.
    /// </remarks>
    static float Residual(UvIsland first, UvIsland second, bool mirrored) {
        var size = first.Size;
        var extent = MathF.Max(size.X, size.Y);

        if (!(extent > 0f) || !float.IsFinite(extent)) {
            return float.PositiveInfinity;
        }

        var worst = 0f;

        for (var corner = 0; corner < first.Coordinates.Count; corner++) {
            var here = first.Coordinates[corner] - first.Minimum;
            var there = second.Coordinates[corner] - second.Minimum;

            if (mirrored) {
                there = new(second.Size.X - there.X, there.Y);
            }

            worst = MathF.Max(worst, Vector2.Distance(here, there) / extent);
        }

        return worst;
    }
}
