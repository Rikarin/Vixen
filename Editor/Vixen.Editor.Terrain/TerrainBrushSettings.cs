// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Editor.Inspector;
using Vixen.Terrain;

namespace Vixen.Editor.Terrain;

/// <summary>
///     The brush section of the terrain panel.
/// </summary>
/// <remarks>
///     <para>
///         <b>One section, shared by every tool in both modes — [docs/plan/31 § D12].</b> Sculpt
///         strength, paint weight and foliage density over a falloff are the same function applied to
///         different targets, so a soft edge sculpted at strength 0.3 and a soft edge painted at
///         strength 0.3 are the same shape. Unreal implements them three times and they are not.
///     </para>
///     <para>
///         ⚠ <b>A mutable settings object beside the immutable brush, rather than a mutable
///         brush.</b> <see cref="TerrainBrush" /> is a <c>readonly record struct</c> because a stamp
///         has to be the same brush from the first sample of a stroke to the last — a tool that read
///         a live object would change radius halfway through a drag if the panel moved, and the
///         stroke's undo record is sized to a footprint that no longer matches. So this is what the
///         panel edits and <see cref="ToBrush" /> is what a stroke is begun with, once.
///     </para>
///     <para>
///         ⚠ <b>The radius is in metres and the spacing is a fraction of it.</b> Making both
///         distances would mean that turning the radius up thinned the stroke out until it was a row
///         of discs; Unity and every paint application spell it this way for that reason.
///     </para>
/// </remarks>
[DataContract("TerrainBrushSettings")]
public sealed class TerrainBrushSettings {
    /// <summary>How far the brush reaches, in metres.</summary>
    [Inspector]
    [Range(MinimumRadius, MaximumRadius)]
    [Tooltip("How far the brush reaches, in metres. [ and ] change it without leaving the viewport.")]
    public float Radius { get; set; } = 8f;

    /// <summary>How hard it presses, 0…1.</summary>
    [Inspector]
    [Range(0f, 1f)]
    [Tooltip("How hard it presses. - and = change it without leaving the viewport.")]
    public float Strength { get; set; } = 0.5f;

    /// <summary>What fraction of the radius is falloff rather than plateau.</summary>
    /// <remarks>
    ///     ⚠ <b>The fraction that falls off, not where the falloff starts.</b> The two read almost
    ///     the same on a slider and are inverses of each other, and the kernel's is this one — see
    ///     <see cref="TerrainBrush.Falloff" />.
    /// </remarks>
    [Inspector]
    [Range(0f, 1f)]
    [Tooltip("How much of the radius is soft edge. 0 is a hard disc and 1 is falloff all the way in.")]
    public float Falloff { get; set; } = 0.5f;

    /// <summary>Which falloff curve.</summary>
    [Inspector]
    public BrushFalloffKind Curve { get; set; } = BrushFalloffKind.Smooth;

    /// <summary>What footprint the brush has.</summary>
    [Inspector]
    public BrushShape Shape { get; set; } = BrushShape.Circle;

    /// <summary>How far apart stamps are along a stroke, as a fraction of the radius.</summary>
    [Inspector]
    [Range(0.01f, 2f)]
    [Tooltip("Stamp spacing as a fraction of the radius, so changing the radius keeps the stroke as dense.")]
    public float Spacing { get; set; } = 0.25f;

    /// <summary>How the stamps are turned.</summary>
    [Inspector]
    public BrushRotation Rotation { get; set; } = BrushRotation.Fixed;

    /// <summary>The brush's own angle, in degrees.</summary>
    /// <remarks>
    ///     ⚠ <b>Degrees here and radians in the kernel</b>, which is the convention every angle in
    ///     the editor follows: a person types 45 and a shader is handed 0.785. <see cref="ToBrush" />
    ///     is the only place the two meet.
    /// </remarks>
    [Inspector]
    [Range(0f, 360f)]
    [ShowIf(nameof(IsAngled))]
    public float Angle { get; set; }

    /// <summary>How many metres of world one tile of a pattern mask covers.</summary>
    [Inspector]
    [Range(0.1f, 512f)]
    [ShowIf(nameof(IsPatterned))]
    public float PatternScale { get; set; } = 4f;

    /// <summary>Whether the angle applies, which it does only for a brush that is turned by hand.</summary>
    public bool IsAngled => Rotation == BrushRotation.Fixed && Shape != BrushShape.Circle;

    /// <summary>Whether the pattern scale applies.</summary>
    public bool IsPatterned => Shape == BrushShape.Pattern;

    /// <summary>The smallest radius the panel offers, in metres.</summary>
    public const float MinimumRadius = 0.1f;

    /// <summary>And the largest.</summary>
    public const float MaximumRadius = 4096f;

    /// <summary>How much one press of the size keys changes the radius, as a fraction.</summary>
    /// <remarks>
    ///     ⚠ <b>Multiplicative, not additive.</b> A metre is most of a small brush and nothing at all
    ///     of a large one, so a fixed step means the keys are useless at one end of the range and
    ///     maddening at the other. An eighth is the step every reference toolset lands near.
    /// </remarks>
    public const float SizeStep = 0.125f;

    /// <summary>And how much one press of the strength keys changes it.</summary>
    /// <remarks>Additive, because strength is a fraction of a fixed range rather than a distance.</remarks>
    public const float StrengthStep = 0.05f;

    /// <summary>Makes the brush bigger or smaller by one step.</summary>
    /// <param name="steps">How many steps, negative to shrink.</param>
    public void Resize(int steps) {
        var scaled = Radius * MathF.Pow(1f + SizeStep, steps);

        Radius = Math.Clamp(scaled, MinimumRadius, MaximumRadius);
    }

    /// <summary>Presses harder or softer by one step.</summary>
    /// <param name="steps">How many steps, negative to soften.</param>
    public void Press(int steps) => Strength = Math.Clamp(Strength + (steps * StrengthStep), 0f, 1f);

    /// <summary>The brush a stroke is begun with.</summary>
    /// <returns>The brush.</returns>
    public TerrainBrush ToBrush() =>
        new() {
            Radius = Math.Clamp(Radius, MinimumRadius, MaximumRadius),
            Strength = Math.Clamp(Strength, 0f, 1f),
            Falloff = Math.Clamp(Falloff, 0f, 1f),
            Curve = Curve,
            Shape = Shape,
            Spacing = Spacing > 0f ? Spacing : 0.25f,
            Rotation = Rotation,
            Angle = float.DegreesToRadians(Angle),
            PatternScale = PatternScale > 0f ? PatternScale : 4f
        };
}
