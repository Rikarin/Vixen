// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Ui.Rendering;

namespace Vixen.Ui;

/// <summary>The half of <see cref="TransformReader" /> a transition reaches: <c>transform-mix()</c>.</summary>
/// <remarks>
///     <para>
///         <b>How a <c>transform</c> transition arrives here, and why it is text.</b> The animator lives
///         in <c>Vixen.Ui.Styling</c>, which cannot see <see cref="Matrix4x4" /> compositions, an
///         element's box or its <see cref="LengthContext" /> — so it cannot interpolate
///         <c>translateX(50%)</c> against <c>translateX(2em)</c>, and a second copy of the arithmetic
///         in that assembly is the failure <see cref="UiTransform" />'s own remarks are written
///         against. What it can do is time the transition and write down what it wants: CSS Values 5's
///         <c>transform-mix(&lt;progress&gt;, &lt;from&gt;, &lt;to&gt;)</c>, which is a real value
///         of the property and says exactly "this far between these two". This reader is the only
///         thing that can answer it, and it answers it here — per element per pass, against the box
///         and the context it already holds, before the origin is folded in and before the element's
///         own <c>rotate</c>/<c>scale</c> and the parent's perspective are multiplied on. #174.
///     </para>
///     <para>
///         ⚠ <b>So the decomposition runs on the list's own composition and never on the matrix the
///         consumers see</b>, which carries the origin as a translation. Interpolating that would
///         swing a rotation about a moving origin along a straight line — the constraint #51's
///         third audit recorded before anything was built.
///     </para>
///     <para>
///         ⚠ <b>And <c>backface-visibility</c> is decided again at every step, for free.</b> Each
///         pass resolves the mix to steps, composes the steps, and asks
///         <c>TransformReader.TurnedAway</c> of the result like any other list — so a card
///         transitioning <c>rotateY(0deg)</c> → <c>rotateY(180deg)</c> under <c>backface-hidden</c>
///         disappears at 90°, not at either end.
///     </para>
/// </remarks>
sealed partial class TransformReader {
    /// <summary>How deeply one <c>transform-mix()</c> may nest inside another.</summary>
    /// <remarks>
    ///     ⚠ <b>A bound on a recursion a stylesheet could otherwise drive</b>: the animator nests a
    ///     mix inside a new one each time a transition is interrupted towards a THIRD value (a
    ///     reversal does not nest — see <c>Animator</c>), and nothing but this stops a hand-written
    ///     value doing the same a thousand times over. Past it the declaration is refused, like any
    ///     other this reader cannot read.
    /// </remarks>
    const int DeepestMix = 16;

    /// <summary>A whole transform value — a list, <c>none</c>, or a <c>transform-mix()</c> — as steps.</summary>
    /// <param name="text">The value, trimmed.</param>
    /// <param name="element">The element whose box a percentage resolves against.</param>
    /// <param name="metrics">The lengths its relative units resolve against.</param>
    /// <param name="into">Receives the steps, in written order. <c>none</c> adds nothing.</param>
    /// <param name="depth">How many mixes this one is inside.</param>
    /// <returns>Whether the value could be read; false drops the whole declaration.</returns>
    bool Steps(ReadOnlySpan<char> text, UiElement element, LengthContext metrics, List<TransformStep> into, int depth) {
        if (text.IsEmpty) {
            return false;
        }

        if (text.Equals("none", StringComparison.OrdinalIgnoreCase)) {
            return true;
        }

        const string mix = "transform-mix(";

        if (text.StartsWith(mix, StringComparison.OrdinalIgnoreCase) && Closes(text, mix.Length - 1) == text.Length - 1) {
            return depth < DeepestMix && Mix(text[mix.Length..^1], element, metrics, into, depth + 1);
        }

        var at = 0;

        while (at < text.Length) {
            if (char.IsWhiteSpace(text[at]) || text[at] == ',') {
                at++;
                continue;
            }

            var open = text[at..].IndexOf('(');

            if (open <= 0) {
                return false;
            }

            var name = text.Slice(at, open).Trim();
            at += open;

            // ⚠ <b>The MATCHING close bracket and not the first one, and the difference only became
            // reachable when a nested parenthesis stopped being a refusal.</b> `translate-z-4`
            // resolves to `translateZ(calc(var(--spacing) * 4))`; a scan for the first `)` stops
            // inside the `calc(`, which leaves `calc(var(--spacing) * 4` as the argument and a stray
            // `)` where the next function's name should be — so the list is refused for the wrong
            // reason and the diagnosis points at the fold rather than at the scan.
            var close = Closes(text, at);

            if (close < 0) {
                return false;
            }

            var arguments = text[(at + 1)..close];
            at = close + 1;

            if (!Function(name, arguments, element, metrics, out var step)) {
                return false;
            }

            into.Add(step);
        }

        return true;
    }

    /// <summary>Where the bracket opened at <paramref name="open" /> closes, or −1.</summary>
    static int Closes(ReadOnlySpan<char> text, int open) {
        var depth = 0;

        for (var index = open; index < text.Length; index++) {
            depth += text[index] switch {
                '(' => 1,
                ')' => -1,
                _ => 0
            };

            if (depth == 0) {
                return index;
            }
        }

        return -1;
    }

    /// <summary>Reads <c>transform-mix(p, A, B)</c>'s three arguments and interpolates.</summary>
    /// <remarks>
    ///     ⚠ <b>The progress may lie outside zero to one</b> — a <c>cubic-bezier()</c> with a
    ///     handle past the unit square, or a spring, overshoots on purpose — and every interpolation
    ///     below extrapolates rather than clamping, which is what makes the overshoot visible.
    /// </remarks>
    bool Mix(ReadOnlySpan<char> arguments, UiElement element, LengthContext metrics, List<TransformStep> into, int depth) {
        Span<Range> parts = stackalloc Range[4];

        if (Commas(arguments, parts) != 3) {
            return false;
        }

        if (!Number(arguments[parts[0]].Trim(), out var progress) || !float.IsFinite(progress)) {
            return false;
        }

        var from = new List<TransformStep>();
        var to = new List<TransformStep>();

        if (!Steps(arguments[parts[1]].Trim(), element, metrics, from, depth)
            || !Steps(arguments[parts[2]].Trim(), element, metrics, to, depth)) {
            return false;
        }

        Interpolate(from, to, progress, into);
        return true;
    }

    /// <summary>Cuts on the commas at bracket depth zero, and nowhere else.</summary>
    /// <returns>How many parts, or −1 for more than there is room for or an unbalanced bracket.</returns>
    static int Commas(ReadOnlySpan<char> text, Span<Range> parts) {
        var count = 0;
        var depth = 0;
        var start = 0;

        for (var index = 0; index <= text.Length; index++) {
            if (index < text.Length) {
                var character = text[index];

                if (character == '(') {
                    depth++;
                    continue;
                }

                if (character == ')') {
                    if (--depth < 0) {
                        return -1;
                    }

                    continue;
                }

                if (character != ',' || depth != 0) {
                    continue;
                }
            }

            if (count == parts.Length) {
                return -1;
            }

            parts[count++] = new Range(start, index);
            start = index + 1;
        }

        return depth == 0 ? count : -1;
    }

    /// <summary>Two transform lists, <paramref name="progress" /> of the way from one to the other.</summary>
    /// <param name="from">The start, in written order. Empty is <c>none</c>.</param>
    /// <param name="to">The end.</param>
    /// <param name="progress">How far.</param>
    /// <param name="into">Receives the result, in written order.</param>
    /// <remarks>
    ///     <para>
    ///         CSS Transforms 2 § 12, in three moves. <c>none</c> and the shorter list are padded with
    ///         the identity of whatever the other list has in that place, so <c>none</c> →
    ///         <c>rotate(90deg)</c> is <c>rotate(0deg)</c> → <c>rotate(90deg)</c>. Then pair by pair,
    ///         a pair sharing a primitive interpolates its arguments. The first pair that does not
    ///         ends the walk: everything from there on is composed into one matrix per side and the
    ///         two matrices are interpolated by decomposition — see
    ///         <see cref="TransformDecomposition" />.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Function by function is not an optimisation of the matrix path; it is a different
    ///         answer, and the one CSS asks for.</b> <c>rotate(0deg)</c> → <c>rotate(360deg)</c> is a
    ///         whole turn, and both ends are the identity matrix. Every Tailwind transform class
    ///         writes the same seven slots in the same order, so a <c>hover:rotate-z-180</c> is a pair
    ///         of lists this path handles all the way along.
    ///     </para>
    /// </remarks>
    static void Interpolate(List<TransformStep> from, List<TransformStep> to, float progress, List<TransformStep> into) {
        var length = Math.Max(from.Count, to.Count);

        for (var index = 0; index < length; index++) {
            var a = index < from.Count ? from[index] : IdentityOf(to[index]);
            var b = index < to.Count ? to[index] : IdentityOf(from[index]);

            if (TryPair(a, b, progress, out var step)) {
                into.Add(step);
                continue;
            }

            // The rest of each list as one matrix, padded the same way, and the two decomposed.
            var restFrom = new List<TransformStep>();
            var restTo = new List<TransformStep>();

            for (var rest = index; rest < length; rest++) {
                restFrom.Add(rest < from.Count ? from[rest] : IdentityOf(to[rest]));
                restTo.Add(rest < to.Count ? to[rest] : IdentityOf(from[rest]));
            }

            var spatial = Compose(restFrom, 0, out var matrixFrom) | Compose(restTo, 0, out var matrixTo);

            into.Add(
                new TransformStep(TransformStepKind.Matrix, 0f, 0f, 0f, 0f, spatial) {
                    Cells = TransformDecomposition.Interpolate(matrixFrom, matrixTo, progress)
                }
            );

            return;
        }
    }

    /// <summary>One pair of functions interpolated through the primitive they share, if they share one.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The flat and the spatial spelling of one primitive pair with each other</b> —
    ///         <c>translateX(10px)</c> with <c>translate3d(0, 0, 40px)</c> — and the result is spatial
    ///         if either end is, so a transition out of a flat list into a card flip takes the
    ///         four-dimensional branch for the whole of its run rather than switching halfway.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Two rotations about different axes, both of them turning, are the one pair that
    ///         shares a primitive and still interpolates as matrices</b> — Transforms 2's own rule for
    ///         <c>rotate3d()</c>. The pair, not the rest of the list: it answers with a matrix step
    ///         and the walk goes on.
    ///     </para>
    ///     <para>
    ///         <c>perspective()</c> pairs interpolate as matrices too, which comes to interpolating
    ///         <c>−1/d</c> — the decomposition reads it straight out of the perspective column — and
    ///         is what makes <c>perspective(none)</c>, the padding identity, a distance of infinity
    ///         rather than a division by zero.
    ///     </para>
    /// </remarks>
    static bool TryPair(in TransformStep a, in TransformStep b, float t, out TransformStep result) {
        result = default;
        var spatial = a.Spatial || b.Spatial;

        static float L(float from, float to, float t) => from + ((to - from) * t);

        switch (a.Kind, b.Kind) {
            case (TransformStepKind.Translate, TransformStepKind.Translate):
            case (TransformStepKind.Scale, TransformStepKind.Scale):
                result = new TransformStep(a.Kind, L(a.X, b.X, t), L(a.Y, b.Y, t), L(a.Z, b.Z, t), 0f, spatial);
                return true;

            case (TransformStepKind.Skew, TransformStepKind.Skew):
                result = new TransformStep(TransformStepKind.Skew, L(a.X, b.X, t), L(a.Y, b.Y, t), 0f, 0f, false);
                return true;

            case (TransformStepKind.Rotate, TransformStepKind.Rotate):
                result = a with { Angle = L(a.Angle, b.Angle, t), Spatial = spatial };
                return true;

            case (TransformStepKind.Perspective, TransformStepKind.Perspective):
            case (TransformStepKind.Matrix, TransformStepKind.Matrix):
                result = new TransformStep(TransformStepKind.Matrix, 0f, 0f, 0f, 0f, spatial) {
                    Cells = TransformDecomposition.Interpolate(CellsOf(a), CellsOf(b), t)
                };

                return true;
        }

        if (!Turns(a.Kind) || !Turns(b.Kind)) {
            return false;
        }

        // Both are rotations and at least one is about an axis other than z's plane turn, so their
        // primitive is `rotate3d()`. The two fixed-axis spellings keep their own kind, and with it the
        // closed-form matrix `CellsOf` builds for them.
        if (a.Kind == b.Kind && a.Kind is TransformStepKind.RotateX or TransformStepKind.RotateY) {
            result = a with { Angle = L(a.Angle, b.Angle, t) };
            return true;
        }

        var same = MathF.Abs(a.X - b.X) < 1e-6f && MathF.Abs(a.Y - b.Y) < 1e-6f && MathF.Abs(a.Z - b.Z) < 1e-6f;

        if (same || a.Angle == 0f || b.Angle == 0f) {
            // The axis of whichever end turns — or z, where neither does, which is the
            // specification's choice — and the angle between.
            var (x, y, z) = same || a.Angle != 0f ? (a.X, a.Y, a.Z)
                : b.Angle != 0f ? (b.X, b.Y, b.Z)
                : (0f, 0f, 1f);

            result = new TransformStep(TransformStepKind.Rotate3d, x, y, z, L(a.Angle, b.Angle, t), true);
            return true;
        }

        result = new TransformStep(TransformStepKind.Matrix, 0f, 0f, 0f, 0f, true) {
            Cells = TransformDecomposition.Interpolate(CellsOf(a), CellsOf(b), t)
        };

        return true;

        static bool Turns(TransformStepKind kind) =>
            kind is TransformStepKind.Rotate or TransformStepKind.RotateX or TransformStepKind.RotateY
                or TransformStepKind.Rotate3d;
    }

    /// <summary>The function of the same primitive that changes nothing.</summary>
    /// <remarks>
    ///     ⚠ <b>Of the same kind and the same spelling, so that padding never changes which branch a
    ///     list takes</b> — a <c>none</c> end paired with <c>rotateY(180deg)</c> pads with
    ///     <c>rotateY(0deg)</c>, spatial, and the pair stays a <c>rotateY</c> the whole way.
    /// </remarks>
    static TransformStep IdentityOf(in TransformStep step) =>
        step.Kind switch {
            TransformStepKind.Translate => step with { X = 0f, Y = 0f, Z = 0f },
            TransformStepKind.Scale => step with { X = 1f, Y = 1f, Z = 1f },
            TransformStepKind.Skew => step with { X = 0f, Y = 0f },
            TransformStepKind.Perspective => step with { X = float.PositiveInfinity },
            TransformStepKind.Matrix => step with { Cells = Matrix4x4.Identity },
            _ => step with { Angle = 0f }
        };
}
