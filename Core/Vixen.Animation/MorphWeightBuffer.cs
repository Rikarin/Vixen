// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;

namespace Vixen.Animation;

/// <summary>Where a frame's blend-shape weights are collected, by shape name.</summary>
/// <remarks>
///     <para>
///         The same shape as <see cref="AnimationEventBuffer" /> and
///         <see cref="Constraints.ConstraintTagBuffer" />, and for the same reason: a weight is
///         produced in the middle of evaluating a blend tree, at a point where the pose is half-built
///         and the layer stack is mid-flight. Collecting here and reading afterwards is the only
///         ordering something outside the animator can work with.
///     </para>
///     <para>
///         ⚠ <b>Weights add across clips, which is what makes a blend continuous.</b> A tree mixing a
///         neutral and a smile at 0.6/0.4 contributes 0.6 and 0.4 of each clip's curves, and because
///         a tree's own child weights sum to one the result is exactly the linear blend of the two
///         curves. A clip that says nothing about a shape contributes nothing to it and the shape
///         simply weakens rather than snapping, which is the argument <c>ConstraintTagBuffer</c>
///         makes about a hand goal holding through a transition.
///     </para>
///     <para>
///         ⚠ <b>Across <em>layers</em> the sum is additive and not an override, and that is a real
///         limitation rather than a rounding of one.</b> A facial layer is normally additive, so a
///         sum is what it wants; two layers that both drive <c>jawOpen</c> as an override will
///         produce their sum rather than the upper one's value. Nothing here models an override,
///         because the pose machinery that does — <see cref="AnimationLayer.Apply" /> — works on
///         joints, and a shape is not one.
///     </para>
///     <para>
///         <b>A shape is in the buffer because a clip drove it, not because its weight is
///         non-zero.</b> Zero is a face at rest and is a value a curve returns to on purpose, so the
///         membership and the value are separate facts — see <see cref="TryGet" />, whose return
///         value is the first and whose out parameter is the second.
///     </para>
/// </remarks>
public sealed class MorphWeightBuffer {
    /// <summary>How many shapes fit in the sampling scratch before it goes to the heap.</summary>
    /// <remarks>
    ///     An ARKit face is fifty-two shapes and is the case worth not allocating for. A clip with
    ///     more than this many is rare enough that one array per collection is the right trade
    ///     against a stack frame everybody pays for.
    /// </remarks>
    const int ScratchShapes = 64;

    readonly Dictionary<string, int> slots = new(StringComparer.Ordinal);
    readonly List<string> shapes = [];
    readonly List<float> weights = [];

    /// <summary>How many shapes were driven this frame.</summary>
    public int Count => shapes.Count;

    /// <summary>One of them, by position.</summary>
    /// <param name="index">Which.</param>
    /// <returns>Its accumulated weight.</returns>
    public float this[int index] => weights[index];

    /// <summary>The shapes driven this frame, in the order they were first collected.</summary>
    public ReadOnlySpan<string> Shapes => CollectionsMarshal.AsSpan(shapes);

    /// <summary>Their weights, in the same order.</summary>
    public ReadOnlySpan<float> Weights => CollectionsMarshal.AsSpan(weights);

    /// <summary>Adds a contribution to one shape.</summary>
    /// <param name="shape">The shape's name, as the mesh calls it.</param>
    /// <param name="weight">How much to add. May be zero, negative, or past one.</param>
    /// <exception cref="ArgumentNullException"><paramref name="shape" /> is null.</exception>
    /// <remarks>
    ///     ⚠ <b>A contribution of zero still registers the shape.</b> That is what lets a clip hold a
    ///     face at rest against whatever was on the component before, which is a different outcome
    ///     from the clip saying nothing at all.
    /// </remarks>
    public void Add(string shape, float weight) {
        ArgumentNullException.ThrowIfNull(shape);

        if (slots.TryGetValue(shape, out var slot)) {
            weights[slot] += weight;
            return;
        }

        slots[shape] = shapes.Count;
        shapes.Add(shape);
        weights.Add(weight);
    }

    /// <summary>Adds every shape a clip drives, scaled by how much the clip is contributing.</summary>
    /// <param name="clip">The clip, or <see langword="null" /> for none.</param>
    /// <param name="time">Where playback is, in seconds.</param>
    /// <param name="weight">How much the clip is contributing overall.</param>
    /// <remarks>
    ///     A clip contributing nothing is skipped outright rather than collected at zero, which is
    ///     <c>ConstraintTagBuffer.Collect</c>'s rule: a motion that has faded out says nothing, and
    ///     registering its shapes would push them to rest instead of leaving them to whoever else is
    ///     driving them.
    /// </remarks>
    public void Collect(AnimationClip? clip, float time, float weight) {
        if (clip is null || clip.ShapeCount == 0 || weight <= 0f) {
            return;
        }

        Span<float> sampled = clip.ShapeCount <= ScratchShapes
            ? stackalloc float[ScratchShapes]
            : new float[clip.ShapeCount];

        clip.SampleWeights(time, sampled);

        var driven = clip.Shapes;

        for (var index = 0; index < driven.Length; index++) {
            Add(driven[index], sampled[index] * weight);
        }
    }

    /// <summary>One shape's accumulated weight, if anything drove it.</summary>
    /// <param name="shape">The shape's name.</param>
    /// <param name="weight">Its weight, or zero.</param>
    /// <returns>Whether anything drove it this frame.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="shape" /> is null.</exception>
    public bool TryGet(string shape, out float weight) {
        ArgumentNullException.ThrowIfNull(shape);

        if (slots.TryGetValue(shape, out var slot)) {
            weight = weights[slot];
            return true;
        }

        weight = 0f;
        return false;
    }

    /// <summary>Empties the buffer, keeping its capacity.</summary>
    public void Clear() {
        slots.Clear();
        shapes.Clear();
        weights.Clear();
    }
}
