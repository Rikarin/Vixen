// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Runtime.InteropServices;

namespace Vixen.Ui.Styling;

/// <summary>One stop of a <c>@keyframes</c> rule.</summary>
/// <param name="Offset">Where in the animation it sits, from 0 to 1.</param>
/// <param name="Declarations">What it sets there.</param>
public readonly record struct Keyframe(float Offset, DeclarationRange Declarations);

/// <summary>The <c>@keyframes</c> rules a stylesheet declared.</summary>
/// <remarks>
///     <para>
///         ExCSS parses these, which the spike did not establish and which saves the work
///         <c>@layer</c> needed: a keyframes rule arrives typed, with <c>from</c> and <c>to</c>
///         already normalised to <c>0%</c> and <c>100%</c>.
///     </para>
///     <para>
///         Stops are kept sorted by offset, because an animation asks "which two stops am I between"
///         on every element every frame and a stylesheet may list them in any order. Declarations go
///         in one flat arena for the same reason the rules' do.
///     </para>
/// </remarks>
public sealed class KeyframesTable {
    readonly Dictionary<string, List<Keyframe>> byName = new(StringComparer.Ordinal);
    readonly List<Declaration> declarations = [];

    /// <summary>How many named rules there are.</summary>
    public int Count => byName.Count;

    /// <summary>Adds a stop to a named rule.</summary>
    /// <param name="name">The animation name.</param>
    /// <param name="offset">Where the stop sits, from 0 to 1.</param>
    /// <param name="block">What it sets.</param>
    /// <remarks>
    ///     A later rule with the same name <i>replaces</i> the earlier one entirely rather than
    ///     merging into it, which is what CSS specifies and is easy to get wrong — merging would let
    ///     a stop from a discarded definition survive into the one that replaced it.
    /// </remarks>
    public void Add(string name, float offset, ReadOnlySpan<Declaration> block) {
        ArgumentException.ThrowIfNullOrEmpty(name);

        if (!byName.TryGetValue(name, out var stops)) {
            stops = [];
            byName[name] = stops;
        }

        var start = declarations.Count;
        foreach (var declaration in block) {
            declarations.Add(declaration);
        }

        stops.Add(new Keyframe(Math.Clamp(offset, 0f, 1f), new DeclarationRange(start, block.Length)));
        stops.Sort(static (left, right) => left.Offset.CompareTo(right.Offset));
    }

    /// <summary>Forgets a name, so that a redefinition replaces rather than merges.</summary>
    /// <param name="name">The animation name.</param>
    public void Remove(string name) {
        ArgumentNullException.ThrowIfNull(name);
        byName.Remove(name);
    }

    /// <summary>The stops of a named rule.</summary>
    /// <param name="name">The animation name.</param>
    /// <param name="stops">Receives the stops, ascending by offset.</param>
    /// <returns>Whether the name is defined.</returns>
    public bool TryGet(string name, out IReadOnlyList<Keyframe> stops) {
        ArgumentNullException.ThrowIfNull(name);

        if (byName.TryGetValue(name, out var found)) {
            stops = found;
            return true;
        }

        stops = [];
        return false;
    }

    /// <summary>The declarations of a stop.</summary>
    /// <param name="range">The stop's range.</param>
    /// <returns>The declarations.</returns>
    public ReadOnlySpan<Declaration> DeclarationsOf(DeclarationRange range) =>
        CollectionsMarshal.AsSpan(declarations).Slice(range.Start, range.Count);

    /// <summary>Reads a keyframe selector such as <c>0%</c>, <c>from</c> or <c>50%</c>.</summary>
    /// <param name="text">The selector.</param>
    /// <param name="offset">Receives the offset from 0 to 1.</param>
    /// <returns>Whether it is one.</returns>
    public static bool TryParseOffset(ReadOnlySpan<char> text, out float offset) {
        text = text.Trim();
        offset = 0f;

        if (text.Equals("from", StringComparison.OrdinalIgnoreCase)) {
            return true;
        }

        if (text.Equals("to", StringComparison.OrdinalIgnoreCase)) {
            offset = 1f;
            return true;
        }

        if (text.EndsWith("%", StringComparison.Ordinal)) {
            text = text[..^1];
        }

        if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent)) {
            return false;
        }

        offset = percent / 100f;
        return true;
    }
}

/// <summary>Which way round an animation runs on each pass.</summary>
public enum AnimationDirection : byte {
    /// <summary>Always forwards.</summary>
    Normal,

    /// <summary>Always backwards.</summary>
    Reverse,

    /// <summary>Forwards, then backwards.</summary>
    Alternate,

    /// <summary>Backwards, then forwards.</summary>
    AlternateReverse
}

/// <summary>What an animation leaves behind outside its active period.</summary>
public enum AnimationFill : byte {
    /// <summary>Nothing.</summary>
    None,

    /// <summary>The last frame, after it ends.</summary>
    Forwards,

    /// <summary>The first frame, during its delay.</summary>
    Backwards,

    /// <summary>Both.</summary>
    Both
}

/// <summary>An <c>animation</c> declaration, read.</summary>
/// <param name="Name">The <c>@keyframes</c> name.</param>
/// <param name="Duration">One iteration's length, in seconds.</param>
/// <param name="Delay">How long to wait first, in seconds.</param>
/// <param name="Timing">The easing applied within each iteration.</param>
/// <param name="Iterations">How many times, or <see cref="float.PositiveInfinity" />.</param>
/// <param name="Direction">Which way round each pass runs.</param>
/// <param name="Fill">What it leaves behind.</param>
public readonly record struct AnimationSpec(
    string Name,
    float Duration,
    float Delay,
    TimingFunction Timing,
    float Iterations,
    AnimationDirection Direction,
    AnimationFill Fill
) {
    /// <summary>Where in the keyframe timeline the animation is at a given moment.</summary>
    /// <param name="elapsed">Seconds since the animation started, delay included.</param>
    /// <param name="offset">Receives the position from 0 to 1.</param>
    /// <returns>Whether the animation is contributing a value at all.</returns>
    /// <remarks>
    ///     <para>
    ///         Iteration, direction and fill all live here rather than in the animator, because they
    ///         are one calculation and splitting them is how <c>alternate</c> with a fractional
    ///         iteration count ends up running the wrong way on the last pass.
    ///     </para>
    ///     <para>
    ///         The easing is applied <i>per iteration</i>, which is CSS's rule and the one people are
    ///         surprised by: <c>animation: spin 2s ease-in-out infinite</c> eases in and out on every
    ///         revolution rather than once over the whole run.
    ///     </para>
    /// </remarks>
    public bool TryOffsetAt(float elapsed, out float offset) {
        offset = 0f;

        if (Duration <= 0f) {
            return false;
        }

        var active = elapsed - Delay;

        if (active < 0f) {
            // Still in the delay. `backwards` fill is what makes an element wait in its first frame
            // rather than in its unanimated state.
            if (Fill is not (AnimationFill.Backwards or AnimationFill.Both)) {
                return false;
            }

            offset = Timing.Evaluate(0f);
            return true;
        }

        var total = Iterations * Duration;
        var finished = !float.IsPositiveInfinity(Iterations) && active >= total;

        if (finished) {
            if (Fill is not (AnimationFill.Forwards or AnimationFill.Both)) {
                return false;
            }

            // Held at whichever end the final iteration finished on, which for `alternate` depends
            // on whether that iteration was an odd one.
            active = total;
        }

        var iteration = MathF.Floor(active / Duration);
        var within = (active / Duration) - iteration;

        if (finished && within == 0f && iteration > 0f) {
            // Landing exactly on the boundary belongs to the iteration that just ended, not to the
            // one that would have started.
            iteration -= 1f;
            within = 1f;
        }

        var backwards = Direction switch {
            AnimationDirection.Reverse => true,
            AnimationDirection.Alternate => iteration % 2f >= 1f,
            AnimationDirection.AlternateReverse => iteration % 2f < 1f,
            _ => false
        };

        offset = Timing.Evaluate(backwards ? 1f - within : within);
        return true;
    }
}
