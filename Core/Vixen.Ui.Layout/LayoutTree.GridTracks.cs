// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Layout;

/// <summary>
///     CSS Grid §12: the track sizing algorithm.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This is where grid actually lives.</b> Placement is fiddly and finite — a page of
///         integer rules that either match §8.3 or do not. §12 is five numbered phases over a base
///         size and a growth limit per track, and it is the part that will be subtly wrong while
///         passing a great many fixtures anyway, because most fixtures give every track a definite
///         size and never reach the intrinsic passes at all. The families that do reach them are the
///         <c>min-content</c>, <c>max-content</c>, <c>0fr</c> and <c>minmax</c> ones.
///     </para>
///     <para>
///         ⚠ <b>The algorithm runs twice per grid, and the second run is not a repeat of the
///         first.</b> §12.1 sizes the columns first, because an item's block-axis contribution is its
///         height <i>given</i> a known width — a paragraph in a 200-point column is a different
///         height from the same paragraph in a 100-point one. So the row pass measures every item
///         against the inline size the column pass settled, and reversing the two produces heights
///         computed against a width nothing ever used.
///     </para>
/// </remarks>
public sealed partial class LayoutTree {
    /// <summary>How the container's own size on an axis is being decided while its tracks are sized.</summary>
    /// <remarks>
    ///     §12's phases each read this: under a min-content constraint the free space is zero, under
    ///     a max-content constraint it is infinite, and only a definite size distributes anything
    ///     real. Passing the available space alone cannot express the difference between "there are
    ///     zero points to spare" and "the container is being asked how small it can be".
    /// </remarks>
    enum GridSizingConstraint {
        /// <summary>The container's content box is a known number on this axis.</summary>
        Definite,

        /// <summary>The container is being asked for its smallest size.</summary>
        MinContent,

        /// <summary>The container is being asked for its largest useful size.</summary>
        MaxContent
    }

    /// <summary>Everything one axis of one grid needs, gathered so the phases can be read in order.</summary>
    readonly record struct GridAxis(
        bool Inline,
        int TracksAt,
        int TrackCount,
        int ItemsAt,
        int ItemCount,
        float AvailableSpace,
        float Gap,
        GridSizingConstraint Constraint,
        bool StretchAuto
    );

    /// <summary>
    ///     Fills in a track's sizing function from the template, cycling the implicit list as needed.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b><c>grid-auto-rows</c> is a <i>list</i> and it cycles.</b> §7.5: the implicit tracks
    ///     take their sizes from the list in order and start over when it runs out, so
    ///     <c>grid-auto-rows: 10px 20px 30px</c> makes every fourth implicit row 10 points again.
    ///     Reading only the first entry is right for the overwhelmingly common one-value case and
    ///     silently wrong for the corpus's <c>10px 20px 30px</c> families.
    /// </remarks>
    GridTrackSize ImplicitTrackSize(in GridTemplate automatic, int ordinal) {
        var stored = tracks.Slice(automatic.Offset, automatic.Count);

        return stored.IsEmpty ? GridTrackSize.Auto : stored[((ordinal % stored.Length) + stored.Length) % stored.Length];
    }

    /// <summary>
    ///     How many times an automatic repetition fits, per CSS Grid §7.2.3.2.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>An indefinite container gets exactly one repetition, not as many as it likes.</b>
    ///         §7.2.3.2: if the container has no definite size on this axis, <c>auto-fill</c> computes
    ///         to 1. Treating an indefinite size as "infinite room" is how an <c>auto-fill</c> grid
    ///         inside a shrink-to-fit parent generates tracks until it runs out of memory.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Every track in the repetition must have a definite maximum for this to mean
    ///         anything</b>, and the spec requires it — a repetition containing an <c>auto</c> or
    ///         intrinsic track has no fixed width to divide into, so the count is 1. The corpus's
    ///         <c>repeat(auto-fill, minmax(150px,1fr))</c> works because §7.2.3.2 says to use the
    ///         <i>minimum</i> when the maximum is flexible.
    ///     </para>
    /// </remarks>
    int AutomaticRepetitions(in GridTemplate template, float availableSpace, float gap, int fixedTrackCount, float fixedTrackSize) {
        if (template.AutoRepeatKind == GridAutoRepeat.None || template.AutoRepeatCount <= 0) {
            return 0;
        }

        if (float.IsNaN(availableSpace)) {
            return 1;
        }

        var stored = tracks.Slice(template.Offset, template.Count);
        var repetition = 0f;

        for (var at = 0; at < template.AutoRepeatCount; at++) {
            var track = stored[template.AutoRepeatIndex + at];

            // §7.2.3.2: the maximum decides, unless it is flexible or intrinsic, in which case the
            // minimum does. A repetition with no definite size at all cannot be counted.
            var size = track.Max.IsFixed(availableSpace)
                ? track.Max.Resolve(availableSpace)
                : track.Min.IsFixed(availableSpace)
                    ? track.Min.Resolve(availableSpace)
                    : float.NaN;

            if (float.IsNaN(size)) {
                return 1;
            }

            repetition += MathF.Max(0f, size);
        }

        var perRepetition = repetition + (gap * template.AutoRepeatCount);
        if (perRepetition <= 0f) {
            return 1;
        }

        // The fixed tracks and their gutters come off the top; what is left is what repeats into.
        var room = availableSpace - fixedTrackSize - (gap * fixedTrackCount);
        var fits = (int) MathF.Floor((room + gap) / perRepetition);

        return int.Clamp(fits, 1, LayoutLimits.MaximumGridTracks / template.AutoRepeatCount);
    }

    /// <summary>Builds one axis's track list: explicit tracks, repetitions, then implicit ones.</summary>
    /// <param name="template">The explicit template.</param>
    /// <param name="automatic">The implicit sizing list.</param>
    /// <param name="totalTracks">How many tracks the grid ended up with, from §8.</param>
    /// <param name="leadingImplicit">How many implicit tracks sit before the explicit grid.</param>
    /// <param name="explicitCount">How many explicit tracks there are, after repetitions.</param>
    /// <param name="availableSpace">The container's content-box size on this axis, or NaN.</param>
    /// <returns>Where the tracks start in the scratch.</returns>
    int BuildGridTracks(
        in GridTemplate template,
        in GridTemplate automatic,
        int totalTracks,
        int leadingImplicit,
        int explicitCount,
        float availableSpace
    ) {
        var at = Scratch.AllocateTracks(totalTracks);
        var stored = tracks.Slice(template.Offset, template.Count);

        for (var track = 0; track < totalTracks; track++) {
            var explicitIndex = track - leadingImplicit;

            GridTrackSize size;
            if (explicitIndex < 0) {
                // ⚠ <b>The implicit tracks are numbered from the start of the <i>implicit</i> grid,
                // not from the explicit one.</b> §7.5 assigns <c>grid-auto-*</c> in order beginning
                // at the very first track, so the leftmost implicit track takes the list's first
                // entry — counting backwards from the explicit grid instead gives the cycle the
                // wrong phase, and only when something was placed at a negative line.
                size = ImplicitTrackSize(in automatic, track);
            } else if (explicitIndex < explicitCount) {
                size = ExplicitTrackSize(stored, in template, explicitIndex);
            } else {
                size = ImplicitTrackSize(in automatic, leadingImplicit + (explicitIndex - explicitCount));
            }

            Scratch.Track(at + track).Size = size;
        }

        return at;

        GridTrackSize ExplicitTrackSize(ReadOnlySpan<GridTrackSize> list, in GridTemplate owner, int ordinal) {
            if (owner.AutoRepeatKind == GridAutoRepeat.None) {
                return ordinal < list.Length ? list[ordinal] : GridTrackSize.Auto;
            }

            // Before the repetition, inside it, or after it — the stored list holds exactly one
            // repetition inline, so the "after" case skips over however many were generated.
            var repetitions = (explicitCount - (owner.Count - owner.AutoRepeatCount)) / owner.AutoRepeatCount;
            var repeatedTracks = repetitions * owner.AutoRepeatCount;

            if (ordinal < owner.AutoRepeatIndex) {
                return list[ordinal];
            }

            if (ordinal < owner.AutoRepeatIndex + repeatedTracks) {
                return list[owner.AutoRepeatIndex + ((ordinal - owner.AutoRepeatIndex) % owner.AutoRepeatCount)];
            }

            // ⚠ Past the repetition, the stored list resumes one whole repetition further on — the
            // generated tracks stand in for the single repetition that is actually stored, so the
            // index has to skip the generated ones *and* step over the stored one. Subtracting only
            // the generated count re-reads the repetition's own tracks as though they were the
            // trailing ones, which is invisible until a template puts a fixed track after an
            // `auto-fill`.
            var after = ordinal - repeatedTracks + owner.AutoRepeatCount;
            return after < list.Length ? list[after] : GridTrackSize.Auto;
        }
    }

    /// <summary>The whole of §12, for one axis.</summary>
    void SizeGridTracks(in GridAxis axis, Direction direction, float ownerWidth, float ownerHeight, int currentDepth) {
        InitializeTrackSizes(in axis);
        ResolveIntrinsicTrackSizes(in axis, direction, ownerWidth, ownerHeight, currentDepth);
        MaximizeTracks(in axis);
        ExpandFlexibleTracks(in axis, direction, ownerWidth, ownerHeight, currentDepth);

        if (axis.StretchAuto) {
            StretchAutoTracks(in axis);
        }
    }

    /// <summary>§12.4: every track starts at its fixed minimum and its fixed maximum.</summary>
    /// <remarks>
    ///     ⚠ <b>An infinite growth limit is the normal case, not an error case.</b> Every intrinsic
    ///     and every flexible maximum starts unbounded and is brought down by the phases that follow;
    ///     §12.5's last step is the one that finally replaces whatever is still infinite with the
    ///     track's base size. Initialising to zero instead makes every content-sized track collapse.
    /// </remarks>
    void InitializeTrackSizes(in GridAxis axis) {
        for (var at = 0; at < axis.TrackCount; at++) {
            ref var track = ref Scratch.Track(axis.TracksAt + at);

            if (track.IsCollapsed) {
                track.BaseSize = 0f;
                track.GrowthLimit = 0f;
                continue;
            }

            var minimum = track.Size.Min.Resolve(axis.AvailableSpace);
            var maximum = track.Size.Max.Resolve(axis.AvailableSpace);

            track.BaseSize = float.IsNaN(minimum) ? 0f : MathF.Max(0f, minimum);
            track.GrowthLimit = float.IsNaN(maximum) ? float.PositiveInfinity : MathF.Max(0f, maximum);

            // ⚠ `minmax(40px, 10px)` is not invalid, it is a 40-point track. CSS Grid §7.2.2: "if the
            // max is less than the min, then the max will be floored by the min". The corpus writes
            // this on purpose — `minmax(max-content,10px)` appears 28 times — so a solver that
            // clamped the other way round would fail those and look like an arithmetic slip.
            if (track.GrowthLimit < track.BaseSize) {
                track.GrowthLimit = track.BaseSize;
            }
        }
    }

    /// <summary>§12.5: let the items decide how big the content-sized tracks are.</summary>
    void ResolveIntrinsicTrackSizes(in GridAxis axis, Direction direction, float ownerWidth, float ownerHeight, int currentDepth) {
        var anyIntrinsic = false;
        for (var at = 0; at < axis.TrackCount && !anyIntrinsic; at++) {
            ref var track = ref Scratch.Track(axis.TracksAt + at);
            anyIntrinsic = track.Size.Min.IsIntrinsic(axis.AvailableSpace) || track.Size.Max.IsIntrinsic(axis.AvailableSpace);
        }

        // Nothing here can move a track whose both ends are fixed, and the measurement it would take
        // to find that out is the expensive part of a layout pass.
        if (!anyIntrinsic) {
            FinalizeInfiniteGrowthLimits(in axis);
            return;
        }

        var widestSpan = 1;
        for (var at = 0; at < axis.ItemCount; at++) {
            widestSpan = int.Max(widestSpan, Scratch.Item(axis.ItemsAt + at).SpanOn(axis.Inline));
        }

        // ⚠ Ascending span order, and §12.5 spends a paragraph on why: a two-track item's demand is
        // distributed over tracks whose one-track demands are already settled, so the space it adds
        // is only what its own content needs *beyond* them. Processing a three-track item first
        // would hand it space that a one-track item was about to claim anyway, and the total comes
        // out larger than any browser's.
        for (var span = 1; span <= widestSpan; span++) {
            if (span == 1) {
                for (var at = 0; at < axis.ItemCount; at++) {
                    ref var item = ref Scratch.Item(axis.ItemsAt + at);

                    // §12.5 step 4 handles items over flexible tracks separately, after everything else.
                    if (item.SpanOn(axis.Inline) != 1 || SpansFlexibleTrack(in axis, in item)) {
                        continue;
                    }

                    var contributions = MeasureGridItem(in axis, in item, direction, ownerWidth, ownerHeight, currentDepth);
                    ApplyNonSpanningContribution(in axis, in item, in contributions);
                }

                continue;
            }

            // ⚠ <b>The round loop is outside the item loop, and swapping them is a real bug.</b>
            // §12.5 step 3 runs one round over <i>every</i> item in the span group, accumulating a
            // planned increase per track, and commits that increase once at the end of the round.
            // Committing per item instead makes each item grow the tracks that the previous item
            // already grew — the planned increase is a maximum across the group, and a commit in
            // between turns it into a sum. Two items each wanting ten extra points from one track
            // then take twenty.
            for (var round = GridDistribution.IntrinsicMinimum; round <= GridDistribution.MaxContentMaximum; round++) {
                var touched = false;

                for (var at = 0; at < axis.ItemCount; at++) {
                    ref var item = ref Scratch.Item(axis.ItemsAt + at);

                    if (item.SpanOn(axis.Inline) != span || SpansFlexibleTrack(in axis, in item)) {
                        continue;
                    }

                    var contributions = MeasureGridItem(in axis, in item, direction, ownerWidth, ownerHeight, currentDepth);
                    var gapsInside = axis.Gap * (span - 1);

                    var wanted = round switch {
                        GridDistribution.IntrinsicMinimum => contributions.Minimum,
                        GridDistribution.ContentBasedMinimum => contributions.MinContent,
                        GridDistribution.LimitedMaxContentMinimum => contributions.MaxContent,
                        GridDistribution.MaxContentMinimum => contributions.MaxContent,
                        GridDistribution.IntrinsicMaximum => contributions.MinContent,
                        _ => contributions.MaxContent
                    };

                    wanted -= gapsInside;

                    if (round == GridDistribution.LimitedMaxContentMinimum) {
                        wanted = LimitedMaxContent(in axis, in item, wanted);
                    }

                    DistributeExtraSpace(in axis, in item, wanted, round);
                    touched = true;
                }

                if (touched) {
                    CommitRound(
                        in axis,
                        round is GridDistribution.IntrinsicMinimum
                            or GridDistribution.ContentBasedMinimum
                            or GridDistribution.LimitedMaxContentMinimum
                            or GridDistribution.MaxContentMinimum
                    );
                }
            }
        }

        // §12.5 step 4: items crossing a flexible track give their minimum to those tracks' bases.
        // ⚠ <b>Together, not grouped by span.</b> §12.5 is explicit that this step considers every
        // such item at once rather than in ascending span order — the flexible tracks have no
        // growth limit to run out of, so the ordering that matters for the intrinsic rounds does
        // not apply here.
        var anyFlexible = false;

        for (var at = 0; at < axis.ItemCount; at++) {
            if (SpansFlexibleTrack(in axis, in Scratch.Item(axis.ItemsAt + at))) {
                anyFlexible = true;
                break;
            }
        }

        if (anyFlexible) {
            // ⚠ <b>The same base-size rounds as step 3, intersected with "is flexible".</b>
            // §12.5 step 4 says to <i>repeat the previous step</i> while distributing space only to
            // flexible tracks — so the round's own filter still applies on top. That intersection is
            // the whole difference between `0fr 1fr` and `0fr minmax(0px, 0fr)`: both pairs are
            // flexible, but only the first pair's second track has an intrinsic minimum, so the
            // first splits its item's 100 points 0/100 by flex factor and the second gives all 100
            // to the one track the round is allowed to touch. Dropping the intersection makes the
            // second pair 50/50, which is 252 fixtures.
            for (var round = GridDistribution.IntrinsicMinimum; round <= GridDistribution.MaxContentMinimum; round++) {
                for (var at = 0; at < axis.ItemCount; at++) {
                    ref var item = ref Scratch.Item(axis.ItemsAt + at);

                    if (!SpansFlexibleTrack(in axis, in item)) {
                        continue;
                    }

                    var contributions = MeasureGridItem(in axis, in item, direction, ownerWidth, ownerHeight, currentDepth);

                    var wanted = round switch {
                        GridDistribution.IntrinsicMinimum => contributions.Minimum,
                        GridDistribution.ContentBasedMinimum => contributions.MinContent,
                        _ => contributions.MaxContent
                    };

                    DistributeToFlexibleTracks(in axis, in item, wanted, round);
                }

                CommitRound(in axis, growsBase: true);
            }
        }

        FinalizeInfiniteGrowthLimits(in axis);
    }

    /// <summary>§12.5's last step: an unbounded track is bounded by whatever it grew to.</summary>
    void FinalizeInfiniteGrowthLimits(in GridAxis axis) {
        for (var at = 0; at < axis.TrackCount; at++) {
            ref var track = ref Scratch.Track(axis.TracksAt + at);

            if (float.IsPositiveInfinity(track.GrowthLimit)) {
                track.GrowthLimit = track.BaseSize;
            }
        }
    }

    /// <summary>§12.5 step 2: an item in exactly one track sizes that track directly.</summary>
    void ApplyNonSpanningContribution(in GridAxis axis, in GridItem item, in GridContribution contributions) {
        ref var track = ref Scratch.Track(axis.TracksAt + item.StartOn(axis.Inline));

        if (track.IsCollapsed) {
            return;
        }

        var minimum = track.Size.Min;
        var maximum = track.Size.Max;

        if (minimum.IsIntrinsic(axis.AvailableSpace)) {
            var wanted = minimum.Kind switch {
                GridSizingKind.MinContent => contributions.MinContent,
                GridSizingKind.MaxContent => contributions.MaxContent,
                _ => contributions.Minimum
            };

            track.BaseSize = MathF.Max(track.BaseSize, wanted);
        }

        if (maximum.IsIntrinsic(axis.AvailableSpace)) {
            var wanted = maximum.Kind == GridSizingKind.MinContent ? contributions.MinContent : contributions.MaxContent;
            var limit = float.IsPositiveInfinity(track.GrowthLimit) ? wanted : MathF.Max(track.GrowthLimit, wanted);

            // ⚠ `fit-content(x)` clamps the GROWTH LIMIT here. §7.2.2 makes it a max-content ceiling
            // limited by its argument, so the growth limit is the smaller of the two — and a
            // percentage argument against an indefinite container has no value, in which case the
            // clamp simply does not apply. A base size grown past the argument by a spanning item is
            // a separate question, and §12.5 answers it with the limited contribution rather than
            // with a ceiling — see <see cref="LimitedMaxContent" />.
            if (maximum.Kind == GridSizingKind.FitContent) {
                var clamp = FitContentLimit(maximum, axis.AvailableSpace);

                if (!float.IsNaN(clamp)) {
                    limit = MathF.Min(limit, MathF.Max(clamp, track.BaseSize));
                }
            }

            track.GrowthLimit = limit;
        }

        if (track.GrowthLimit < track.BaseSize) {
            track.GrowthLimit = track.BaseSize;
        }
    }

    /// <summary>§12.5 step 3.3's <i>limited</i> max-content contribution.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A <c>fit-content()</c> track spanned under a max-content constraint is already
    ///         limited, and the contribution is what carries the limit.</b> The round this feeds is
    ///         the one that lets an <c>auto</c> track absorb a spanning item's max-content size while
    ///         the container's own width is still being asked for — which is right for an <c>auto</c>
    ///         track, whose ceiling is genuinely the content, and wrong for `fit-content(10px)`,
    ///         whose ceiling is ten. §7.2.2 already says so; the growth limit says so too. But this
    ///         round grows BASE sizes, and §12.5.1's "distribute space beyond limits" then hands a
    ///         frozen track the leftover anyway, so the growth limit alone does not hold the line.
    ///     </para>
    ///     <para>
    ///         So the cap is applied to the contribution instead: the item may ask the span for no
    ///         more than each <c>fit-content()</c> track's argument plus what the other tracks
    ///         already hold. `min-content fit-content(10px)` spanned by an 80-point item asks for
    ///         30 rather than 80, the 20 already there covers it, and the grid is 40 wide instead of
    ///         80 — with `fit-content(30px)` the same sum comes to 50, which is exactly the ten
    ///         points of headroom the larger argument buys.
    ///     </para>
    ///     <para>
    ///         ⚠ An argument a track has already outgrown does not shrink it. A base size that
    ///         exceeded the argument got there through §12.5's earlier rounds, which the argument
    ///         does not govern — floor the per-track allowance at the base size so the cap can only
    ///         ever refuse growth, never claw any back.
    ///     </para>
    ///     <para>
    ///         An item spanning no <c>fit-content()</c> track has nothing to be limited by and keeps
    ///         its plain max-content contribution — capping by the base sizes alone would leave
    ///         every <c>auto</c> track in the span unable to grow at all, which is the round's whole
    ///         purpose.
    ///     </para>
    /// </remarks>
    float LimitedMaxContent(in GridAxis axis, in GridItem item, float maxContent) {
        var start = item.StartOn(axis.Inline);
        var span = item.SpanOn(axis.Inline);

        var cap = 0f;
        var limits = false;

        for (var at = start; at < start + span; at++) {
            ref var track = ref Scratch.Track(axis.TracksAt + at);

            if (track.IsCollapsed) {
                continue;
            }

            var allowance = track.BaseSize;

            if (track.Size.Max.Kind == GridSizingKind.FitContent) {
                var argument = FitContentLimit(track.Size.Max, axis.AvailableSpace);

                if (!float.IsNaN(argument)) {
                    allowance = MathF.Max(argument, track.BaseSize);
                    limits = true;
                }
            }

            cap += allowance;
        }

        return limits ? MathF.Min(maxContent, cap) : maxContent;
    }

    /// <summary>The number a <c>fit-content()</c> argument stands for, or NaN.</summary>
    static float FitContentLimit(GridSizingFunction maximum, float availableSpace) =>
        maximum.IsFitContentPercent
            ? float.IsNaN(availableSpace) ? float.NaN : maximum.Value * availableSpace * 0.01f
            : maximum.Value;

    /// <summary>Which of §12.5.1's five rounds is running.</summary>
    enum GridDistribution {
        /// <summary>Grow base sizes of tracks with any intrinsic minimum, to fit minimum contributions.</summary>
        IntrinsicMinimum,

        /// <summary>Grow base sizes of tracks with a content-based minimum, to fit min-content.</summary>
        ContentBasedMinimum,

        /// <summary>
        ///     Under a max-content constraint only: grow base sizes of tracks with an <c>auto</c> or
        ///     <c>max-content</c> minimum, to fit the item's <i>limited</i> max-content contribution.
        /// </summary>
        /// <remarks>
        ///     ⚠ <b>This is a separate round from <see cref="MaxContentMinimum" /> because the two
        ///     sentences of §12.5 step 3.3 hand out two different numbers.</b> The conditional one
        ///     spends the <i>limited</i> max-content contribution over a wider set of tracks; the
        ///     unconditional one spends the full max-content contribution over a narrower set. Fusing
        ///     them into a single round — one `IsAffectedBy` reading both min sizing functions, one
        ///     contribution for both — gives the wider set the larger number, which is the one
        ///     combination the step never authorises. See <see cref="LimitedMaxContent" />.
        /// </remarks>
        LimitedMaxContentMinimum,

        /// <summary>Grow base sizes of tracks with a max-content minimum, to fit max-content.</summary>
        MaxContentMinimum,

        /// <summary>Grow growth limits of tracks with an intrinsic maximum, to fit min-content.</summary>
        IntrinsicMaximum,

        /// <summary>Grow growth limits of tracks with a max-content maximum, to fit max-content.</summary>
        MaxContentMaximum,

        /// <summary>§12.5 step 4: grow base sizes of the flexible tracks only, to fit minimums.</summary>
        /// <remarks>
        ///     ⚠ <b>This is the round that makes <c>0fr</c> mean anything, and it is the one round
        ///     that does <i>not</i> distribute equally.</b> A bare <c>Nfr</c> is
        ///     <c>minmax(auto, Nfr)</c>, and §12.5's step 2 skips any track with a flexible sizing
        ///     function — so nothing before this point has looked at the item inside a <c>0fr</c>
        ///     track, and §12.7 then multiplies by a flex factor of zero and adds nothing. See
        ///     <see cref="LayoutTree.DistributeToFlexibleTracks" /> for why the share is proportional.
        /// </remarks>
        FlexibleBase
    }

    /// <summary>§12.5.1: hand a contribution to the tracks an item spans, without over-paying.</summary>
    void DistributeExtraSpace(in GridAxis axis, in GridItem item, float contribution, GridDistribution round) {
        if (float.IsNaN(contribution) || contribution <= 0f) {
            return;
        }

        var growsBase = round is GridDistribution.IntrinsicMinimum
            or GridDistribution.ContentBasedMinimum
            or GridDistribution.LimitedMaxContentMinimum
            or GridDistribution.MaxContentMinimum
            or GridDistribution.FlexibleBase;

        var start = item.StartOn(axis.Inline);
        var span = item.SpanOn(axis.Inline);

        // The space to distribute is what the item wants beyond what the tracks already have.
        var occupied = 0f;
        var affected = 0;

        for (var at = start; at < start + span; at++) {
            ref var track = ref Scratch.Track(axis.TracksAt + at);
            track.ItemIncurredIncrease = 0f;
            track.IsMarked = false;

            if (track.IsCollapsed) {
                continue;
            }

            occupied += growsBase
                ? track.BaseSize
                : float.IsPositiveInfinity(track.GrowthLimit) ? track.BaseSize : track.GrowthLimit;

            if (!IsAffectedBy(in track, round, axis.AvailableSpace, axis.Constraint)) {
                continue;
            }

            track.IsMarked = true;
            affected++;
        }

        if (affected == 0) {
            return;
        }

        var space = contribution - occupied;
        if (space <= 0f) {
            return;
        }

        // ── Distribute equally, freezing each track as it reaches its own ceiling ────────────────
        // ⚠ The ceiling is the growth limit when growing a base size, and unbounded when growing a
        // growth limit. Using the growth limit in both would make the second half of §12.5.1 a
        // no-op, which is exactly the shape of bug that leaves `min-content` columns too narrow
        // only when something spans them.
        var remaining = space;
        var open = affected;

        while (open > 0 && remaining > 1e-6f) {
            var share = remaining / open;
            var distributed = 0f;
            var frozen = 0;

            for (var at = start; at < start + span; at++) {
                ref var track = ref Scratch.Track(axis.TracksAt + at);

                if (!track.IsMarked) {
                    continue;
                }

                var ceiling = growsBase
                    ? float.IsPositiveInfinity(track.GrowthLimit) ? float.PositiveInfinity : track.GrowthLimit - track.BaseSize
                    : float.PositiveInfinity;

                var headroom = ceiling - track.ItemIncurredIncrease;

                if (headroom <= share) {
                    if (headroom > 0f) {
                        track.ItemIncurredIncrease += headroom;
                        distributed += headroom;
                    }

                    track.IsMarked = false;
                    frozen++;
                } else {
                    track.ItemIncurredIncrease += share;
                    distributed += share;
                }
            }

            remaining -= distributed;
            open -= frozen;

            if (frozen == 0) {
                break;
            }
        }

        // ⚠ §12.5.1's "distribute space beyond limits": space that nobody could take because every
        // affected track hit its growth limit still has to go somewhere, and it goes to a NAMED
        // subset of the affected tracks — or, failing that, back to all of them. Dropping the clause
        // makes a spanning item overflow its own tracks.
        if (remaining > 1e-6f) {
            var recipients = 0;

            for (var at = start; at < start + span; at++) {
                ref var track = ref Scratch.Track(axis.TracksAt + at);

                if (!track.IsCollapsed
                    && IsAffectedBy(in track, round, axis.AvailableSpace, axis.Constraint)
                    && TakesSpaceBeyondLimits(in track, round, axis.AvailableSpace)) {
                    recipients++;
                }
            }

            var share = remaining / (recipients > 0 ? recipients : affected);

            for (var at = start; at < start + span; at++) {
                ref var track = ref Scratch.Track(axis.TracksAt + at);

                if (track.IsCollapsed || !IsAffectedBy(in track, round, axis.AvailableSpace, axis.Constraint)) {
                    continue;
                }

                if (recipients == 0 || TakesSpaceBeyondLimits(in track, round, axis.AvailableSpace)) {
                    track.ItemIncurredIncrease += share;
                }
            }
        }

        // The planned increase is a maximum across items, not a sum: two items each needing ten
        // extra points from the same track need ten between them, not twenty.
        for (var at = start; at < start + span; at++) {
            ref var track = ref Scratch.Track(axis.TracksAt + at);

            if (track.ItemIncurredIncrease > 0f) {
                track.PlannedIncrease = MathF.Max(track.PlannedIncrease, track.ItemIncurredIncrease);
            }
        }
    }

    /// <summary>
    ///     Whether this track is one of the ones §12.5.1 hands the leftover to, once every affected
    ///     track has frozen at its growth limit.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The subset is named by the track's MAX sizing function, and "its growth limit is
    ///         still infinite" is not the same test.</b> §12.5.1's "distribute space beyond limits"
    ///         gives the leftover to any affected track that <i>also</i> has an intrinsic max track
    ///         sizing function when a minimum or a min-content contribution is being accommodated, or
    ///         a max-content max track sizing function when a max-content one is — and only if there
    ///         are none of those does it fall back to every affected track.
    ///     </para>
    ///     <para>
    ///         ⚠ An infinite growth limit was the proxy here, and it is wrong in one direction that a
    ///         corpus fixture catches: a <c>max-content</c> track that a NON-spanning item already
    ///         sized has an intrinsic max sizing function and a <i>finite</i> growth limit, because
    ///         §12.5 step 2 set it. Under the proxy such a track was skipped, the "no such tracks"
    ///         fallback fired, and the leftover went to every affected track including a
    ///         <c>minmax(max-content, 20px)</c> one whose whole point is that it stops at 20 —
    ///         `grid_content_sized_columns_max_content_and_max_content_fixed_and_max_content` splits
    ///         140 points 64/32/44 that way instead of Chrome's 70/20/50.
    ///     </para>
    ///     <para>
    ///         The growth-limit rounds take the third bullet, "all affected tracks", which is also
    ///         moot: those rounds grow towards infinity, so nothing freezes and no space is ever left.
    ///     </para>
    /// </remarks>
    static bool TakesSpaceBeyondLimits(in GridTrackState track, GridDistribution round, float availableSpace) =>
        round switch {
            GridDistribution.IntrinsicMinimum or GridDistribution.ContentBasedMinimum =>
                track.Size.Max.IsIntrinsic(availableSpace),
            GridDistribution.LimitedMaxContentMinimum or GridDistribution.MaxContentMinimum =>
                track.Size.Max.Kind is GridSizingKind.MaxContent or GridSizingKind.Auto or GridSizingKind.FitContent,
            _ => true
        };

    /// <summary>Whether one round of §12.5.1 is allowed to grow this track.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><c>auto</c> is deliberately absent from the content-based round, and it is not an
    ///         omission.</b> §12.5 step 3.2 names "a min track sizing function of <c>min-content</c>
    ///         or <c>max-content</c>" — <c>auto</c> is an <i>intrinsic</i> minimum, handled by 3.1
    ///         with the item's <i>minimum contribution</i>, which is a smaller number than its
    ///         min-content contribution whenever CSS Grid §6.6 zeroes the automatic minimum. Adding
    ///         <c>auto</c> here re-grows the track by the min-content contribution one round later
    ///         and undoes §6.6 entirely.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>§12.5 step 3.3 is TWO rounds, not one round with a widened membership.</b> It
    ///         opens with "if the grid container is being sized under a max-content constraint,
    ///         continue to increase the base size of tracks with a min track sizing function of
    ///         <c>auto</c> or <c>max-content</c> … to account for these items' <i>limited</i>
    ///         max-content contributions", and ends with "<i>in all cases</i>, continue to increase
    ///         the base size of tracks with a min track sizing function of <c>max-content</c> … to
    ///         account for these items' max-content contributions". Two memberships and two
    ///         contributions, and they are crossed: the wider membership gets the smaller number.
    ///         Fusing them — one round whose membership is the union and whose contribution is the
    ///         unlimited one — hands the union the larger number, which is the pairing the step never
    ///         authorises, and a `fit-content()` track spanned under a max-content constraint grows
    ///         straight past its own argument. Hence <see cref="GridDistribution.LimitedMaxContentMinimum" />.
    ///     </para>
    ///     <para>
    ///         ⚠ A track that literally says <c>max-content</c> accommodates a spanning item's
    ///         max-content contribution however the container is being sized, exactly as step 2 does
    ///         for a non-spanning one — see <see cref="ApplyNonSpanningContribution" />, which has
    ///         always read the two the same way. Gating that left `minmax(max-content, 6px)` a
    ///         min-content track the moment something spanned it, and the same track alone was right.
    ///     </para>
    ///     <para>
    ///         ⚠ <c>auto</c> keeps its gate, and that is the half the opening sentence is about:
    ///         running it unconditionally makes every <c>auto</c> track as wide as its widest item's
    ///         max-content size even when the container had a fixed width to divide up, which is the
    ///         difference between a grid that fits and one that overflows.
    ///     </para>
    /// </remarks>
    static bool IsAffectedBy(in GridTrackState track, GridDistribution round, float availableSpace, GridSizingConstraint constraint) =>
        round switch {
            GridDistribution.IntrinsicMinimum => track.Size.Min.IsIntrinsic(availableSpace),
            GridDistribution.ContentBasedMinimum =>
                track.Size.Min.Kind is GridSizingKind.MinContent or GridSizingKind.MaxContent,
            GridDistribution.LimitedMaxContentMinimum =>
                constraint == GridSizingConstraint.MaxContent
                && track.Size.Min.Kind is GridSizingKind.Auto or GridSizingKind.MaxContent,
            GridDistribution.MaxContentMinimum => track.Size.Min.Kind == GridSizingKind.MaxContent,
            GridDistribution.IntrinsicMaximum => track.Size.Max.IsIntrinsic(availableSpace),
            GridDistribution.MaxContentMaximum =>
                track.Size.Max.Kind is GridSizingKind.MaxContent or GridSizingKind.Auto or GridSizingKind.FitContent,
            _ => track.Size.IsFlexible
        };

    /// <summary>Folds one round's planned increases into the sizes they were planned against.</summary>
    void CommitRound(in GridAxis axis, bool growsBase) {
        for (var at = 0; at < axis.TrackCount; at++) {
            ref var track = ref Scratch.Track(axis.TracksAt + at);

            if (track.PlannedIncrease <= 0f) {
                continue;
            }

            if (growsBase) {
                track.BaseSize += track.PlannedIncrease;

                if (track.GrowthLimit < track.BaseSize) {
                    track.GrowthLimit = track.BaseSize;
                }
            } else {
                var grown = float.IsPositiveInfinity(track.GrowthLimit)
                    ? track.BaseSize + track.PlannedIncrease
                    : track.GrowthLimit + track.PlannedIncrease;

                // ⚠ <b>`fit-content(x)`'s ceiling is x however the contribution arrived.</b> §7.2.2
                // makes the growth limit `min(max-content, x)`, and `ApplyNonSpanningContribution`
                // has always honoured that — but only for an item sitting in exactly one track. An
                // item SPANNING the track reaches the same growth limit through §12.5.1 instead, and
                // that path had no clamp, so `fit-content(10px)` grew to whatever the spanning item
                // wanted. Nothing looks wrong until §12.6, which under a max-content constraint has
                // infinite free space and raises every base size to its growth limit: the argument
                // is then not a ceiling on anything, and a `min-content fit-content(10px)` grid
                // measures 80 where Chrome says 40.
                //
                // The floor is the base size, not zero: a base that already exceeds the argument got
                // there through the base-size rounds, which §7.2.2 does not govern, and a growth
                // limit below its own base size is not a state the rest of §12 is written for.
                if (track.Size.Max.Kind == GridSizingKind.FitContent) {
                    var argument = FitContentLimit(track.Size.Max, axis.AvailableSpace);

                    if (!float.IsNaN(argument)) {
                        grown = MathF.Min(grown, MathF.Max(argument, track.BaseSize));
                    }
                }

                track.GrowthLimit = grown;
            }

            track.PlannedIncrease = 0f;
            track.ItemIncurredIncrease = 0f;
        }
    }

    /// <summary>Whether an item crosses any track §12.7 will grow.</summary>
    bool SpansFlexibleTrack(in GridAxis axis, in GridItem item) {
        var start = item.StartOn(axis.Inline);

        for (var at = start; at < start + item.SpanOn(axis.Inline); at++) {
            if (Scratch.Track(axis.TracksAt + at).Size.IsFlexible) {
                return true;
            }
        }

        return false;
    }

    /// <summary>§12.5 step 4: an item over flexible tracks floors them, in proportion.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Proportional to the flex factors, not equal — and the corpus separates the two
    ///         cleanly.</b> A 100-point item spanning <c>0fr 1fr</c> makes the first track 0 and the
    ///         second 100, because a <c>0fr</c> track asked for none of the space; an equal split
    ///         gives 50 and 50 and fails 280 fixtures. §12.5 step 4 says to distribute "only to
    ///         flexible tracks", and §12.7.1's proportionality is what "flexible" means.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A total flex factor of zero falls back to an equal split, and that is not the
    ///         same as <c>max(sum, 1)</c>.</b> Clamping the divisor to one — which is right in
    ///         §12.7.1, where the leftover space genuinely goes unclaimed — would give a lone
    ///         <c>0fr</c> track a share of zero and collapse it. Chrome gives it the whole
    ///         contribution: <c>span1_0fr</c> expects 100, and <c>span2_0fr_0fr</c> expects 50 and
    ///         50. The two rules look alike and disagree exactly where every <c>0fr</c> fixture
    ///         lives.
    ///     </para>
    /// </remarks>
    void DistributeToFlexibleTracks(in GridAxis axis, in GridItem item, float contribution, GridDistribution round) {
        if (float.IsNaN(contribution) || contribution <= 0f) {
            return;
        }

        var start = item.StartOn(axis.Inline);
        var span = item.SpanOn(axis.Inline);

        var flexSum = 0f;
        var affected = 0;
        var occupied = axis.Gap * (span - 1);

        for (var at = start; at < start + span; at++) {
            ref var track = ref Scratch.Track(axis.TracksAt + at);

            if (track.IsCollapsed) {
                continue;
            }

            // Whatever this round may not grow keeps what it has and comes off the space.
            if (track.Size.IsFlexible && IsAffectedBy(in track, round, axis.AvailableSpace, axis.Constraint)) {
                flexSum += track.Size.Max.Value;
                affected++;
            } else {
                occupied += track.BaseSize;
            }
        }

        var space = contribution - occupied;
        if (space <= 0f || affected == 0) {
            return;
        }

        for (var at = start; at < start + span; at++) {
            ref var track = ref Scratch.Track(axis.TracksAt + at);

            if (track.IsCollapsed || !track.Size.IsFlexible || !IsAffectedBy(in track, round, axis.AvailableSpace, axis.Constraint)) {
                continue;
            }

            var share = flexSum > 0f ? space * (track.Size.Max.Value / flexSum) : space / affected;

            // A planned increase, so that two items wanting the same track take the larger of the
            // two rather than the sum — the same rule as every other round.
            track.PlannedIncrease = MathF.Max(track.PlannedIncrease, MathF.Max(0f, share - track.BaseSize));
        }
    }

    /// <summary>§12.6: spend whatever is left over, up to each track's growth limit.</summary>
    void MaximizeTracks(in GridAxis axis) {
        // ⚠ Under a max-content constraint the free space is *infinite*, which is not the same as
        // "there is a lot" — it means every track goes straight to its growth limit and none of them
        // shares anything. Under a min-content constraint it is zero. Only a definite container size
        // produces a number to divide.
        if (axis.Constraint == GridSizingConstraint.MinContent) {
            return;
        }

        if (axis.Constraint == GridSizingConstraint.MaxContent) {
            for (var at = 0; at < axis.TrackCount; at++) {
                ref var track = ref Scratch.Track(axis.TracksAt + at);

                if (!float.IsPositiveInfinity(track.GrowthLimit)) {
                    track.BaseSize = MathF.Max(track.BaseSize, track.GrowthLimit);
                }
            }

            return;
        }

        var free = axis.AvailableSpace - UsedTrackSpace(in axis);
        if (free <= 0f) {
            return;
        }

        var open = 0;
        for (var at = 0; at < axis.TrackCount; at++) {
            ref var track = ref Scratch.Track(axis.TracksAt + at);
            track.IsMarked = !track.IsCollapsed && track.GrowthLimit > track.BaseSize;

            if (track.IsMarked) {
                open++;
            }
        }

        while (open > 0 && free > 1e-6f) {
            var share = free / open;
            var spent = 0f;
            var frozen = 0;

            for (var at = 0; at < axis.TrackCount; at++) {
                ref var track = ref Scratch.Track(axis.TracksAt + at);

                if (!track.IsMarked) {
                    continue;
                }

                var headroom = track.GrowthLimit - track.BaseSize;

                if (headroom <= share) {
                    track.BaseSize += headroom;
                    spent += headroom;
                    track.IsMarked = false;
                    frozen++;
                } else {
                    track.BaseSize += share;
                    spent += share;
                }
            }

            free -= spent;
            open -= frozen;

            if (frozen == 0) {
                break;
            }
        }
    }

    /// <summary>§12.7: give the <c>fr</c> tracks their share of what is left.</summary>
    void ExpandFlexibleTracks(in GridAxis axis, Direction direction, float ownerWidth, float ownerHeight, int currentDepth) {
        var anyFlexible = false;
        for (var at = 0; at < axis.TrackCount && !anyFlexible; at++) {
            anyFlexible = Scratch.Track(axis.TracksAt + at).Size.IsFlexible;
        }

        if (!anyFlexible) {
            return;
        }

        float fraction;

        if (axis.Constraint == GridSizingConstraint.MinContent) {
            fraction = 0f;
        } else if (axis.Constraint == GridSizingConstraint.Definite) {
            fraction = FindFlexFraction(in axis, 0, axis.TrackCount, axis.AvailableSpace - (axis.Gap * int.Max(0, axis.TrackCount - 1)));
        } else {
            // ⚠ The indefinite case is a maximum over two families, and leaving out the second one is
            // the classic `1fr` bug: a max-content-sized grid whose `1fr` column holds a 200-point
            // item must be 200 wide, and only the per-item term says so. §12.7's first term alone
            // reports whatever the base sizes already were.
            fraction = 0f;

            for (var at = 0; at < axis.TrackCount; at++) {
                ref var track = ref Scratch.Track(axis.TracksAt + at);

                if (!track.Size.IsFlexible) {
                    continue;
                }

                var factor = track.Size.Max.Value;
                fraction = MathF.Max(fraction, factor > 1f ? track.BaseSize / factor : track.BaseSize);
            }

            for (var at = 0; at < axis.ItemCount; at++) {
                ref var item = ref Scratch.Item(axis.ItemsAt + at);

                if (!SpansFlexibleTrack(in axis, in item)) {
                    continue;
                }

                var contributions = MeasureGridItem(in axis, in item, direction, ownerWidth, ownerHeight, currentDepth);
                var span = item.SpanOn(axis.Inline);
                var room = contributions.MaxContent - (axis.Gap * (span - 1));

                fraction = MathF.Max(fraction, FindFlexFraction(in axis, item.StartOn(axis.Inline), span, room));
            }
        }

        if (fraction <= 0f) {
            return;
        }

        for (var at = 0; at < axis.TrackCount; at++) {
            ref var track = ref Scratch.Track(axis.TracksAt + at);

            if (!track.Size.IsFlexible) {
                continue;
            }

            var wanted = fraction * track.Size.Max.Value;

            if (wanted > track.BaseSize) {
                track.BaseSize = wanted;
            }
        }
    }

    /// <summary>§12.7.1: how big one <c>fr</c> is, over a run of tracks and a space to fill.</summary>
    /// <remarks>
    ///     ⚠ <b>The restart loop is the whole subtlety.</b> A flexible track whose base size already
    ///     exceeds its share stops being flexible for the purposes of this calculation — its base
    ///     size comes off the space and its factor comes out of the sum — and that changes the
    ///     answer, which may disqualify another track. Computing the quotient once gives every
    ///     <c>1fr</c> track the same width even when one of them holds something that does not fit,
    ///     which is the difference between a grid that overflows and one that does not.
    /// </remarks>
    float FindFlexFraction(in GridAxis axis, int start, int span, float spaceToFill) {
        if (float.IsNaN(spaceToFill) || spaceToFill <= 0f) {
            return 0f;
        }

        for (var at = start; at < start + span; at++) {
            Scratch.Track(axis.TracksAt + at).IsMarked = Scratch.Track(axis.TracksAt + at).Size.IsFlexible;
        }

        while (true) {
            var leftover = spaceToFill;
            var flexSum = 0f;

            for (var at = start; at < start + span; at++) {
                ref var track = ref Scratch.Track(axis.TracksAt + at);

                if (track.IsMarked) {
                    flexSum += track.Size.Max.Value;
                } else {
                    leftover -= track.BaseSize;
                }
            }

            if (flexSum <= 0f) {
                return 0f;
            }

            // §12.7.1: a total flex factor below one is treated as one, so `0.5fr` takes half the
            // free space rather than all of it.
            var hypothetical = leftover / MathF.Max(1f, flexSum);
            var disqualified = false;

            for (var at = start; at < start + span; at++) {
                ref var track = ref Scratch.Track(axis.TracksAt + at);

                if (track.IsMarked && track.BaseSize > hypothetical * track.Size.Max.Value) {
                    track.IsMarked = false;
                    disqualified = true;
                }
            }

            if (!disqualified) {
                return MathF.Max(0f, hypothetical);
            }
        }
    }

    /// <summary>§12.8: <c>align-content: normal</c> stretches the <c>auto</c> tracks, not the others.</summary>
    void StretchAutoTracks(in GridAxis axis) {
        if (axis.Constraint != GridSizingConstraint.Definite) {
            return;
        }

        var free = axis.AvailableSpace - UsedTrackSpace(in axis);
        if (free <= 0f) {
            return;
        }

        var stretchable = 0;
        for (var at = 0; at < axis.TrackCount; at++) {
            ref var track = ref Scratch.Track(axis.TracksAt + at);

            if (!track.IsCollapsed && track.Size.Max.Kind == GridSizingKind.Auto) {
                stretchable++;
            }
        }

        if (stretchable == 0) {
            return;
        }

        var share = free / stretchable;

        for (var at = 0; at < axis.TrackCount; at++) {
            ref var track = ref Scratch.Track(axis.TracksAt + at);

            if (!track.IsCollapsed && track.Size.Max.Kind == GridSizingKind.Auto) {
                track.BaseSize += share;
            }
        }
    }

    /// <summary>Every track's base size, plus the gutters between the ones that survived.</summary>
    /// <remarks>
    ///     ⚠ A collapsed <c>auto-fit</c> track takes no gutter with it. §7.2.3.2 gives a collapsed
    ///     track a single shared line, so the gap that would have sat beside it disappears too —
    ///     which is why this counts surviving tracks rather than subtracting one from the total.
    /// </remarks>
    float UsedTrackSpace(in GridAxis axis) {
        var total = 0f;
        var alive = 0;

        for (var at = 0; at < axis.TrackCount; at++) {
            ref var track = ref Scratch.Track(axis.TracksAt + at);

            if (track.IsCollapsed) {
                continue;
            }

            total += track.BaseSize;
            alive++;
        }

        return total + (axis.Gap * int.Max(0, alive - 1));
    }
}
