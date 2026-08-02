// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Editor.Inspector;
using Vixen.Terrain;

namespace Vixen.Editor.Terrain;

/// <summary>
///     The spline section of the terrain panel — what a road does to the ground it crosses.
/// </summary>
/// <remarks>
///     <para>
///         <b>A mutable settings object beside the immutable <see cref="TerrainSplineProfile" />,</b>
///         for <see cref="TerrainBrushSettings" />'s reason: a deformation has to be the same profile
///         from one end of the road to the other, so what a panel edits and what a regeneration is
///         run with are two objects and <see cref="ToProfile" /> is where they meet.
///     </para>
///     <para>
///         ⚠ <b>The two side falloffs are separate, and that is not symmetry pedantry.</b> A road cut
///         into a hillside has a cutting on the uphill side and an embankment on the downhill one,
///         and one number for both makes every mountain road look like it was laid on a plain.
///     </para>
///     <para>
///         ⚠ <b>Splines write a reserved layer and the brush refuses it.</b> The layer is regenerated
///         wholesale from the roads, so anything sculpted into it would be erased the next time any
///         road moved — which is why <see cref="LayerName" /> names a layer rather than offering the
///         stack.
///     </para>
/// </remarks>
[DataContract("TerrainSplineSettings")]
public sealed class TerrainSplineSettings {
    /// <summary>The narrowest road worth deforming for, in metres of half-width.</summary>
    public const float MinimumHalfWidth = 0.1f;

    /// <summary>And the widest a panel offers.</summary>
    public const float MaximumHalfWidth = 100f;

    /// <summary>How far the flat part reaches from the centre line, in metres.</summary>
    /// <remarks>
    ///     ⚠ <b>Half-width and not width, because it is what every measurement against the curve
    ///     is.</b> A distance from the spline is compared against this directly; naming it width and
    ///     halving it inside would put the factor of two in the one place nobody looks.
    /// </remarks>
    [Inspector(Name = "Half-width (m)")]
    [Range(MinimumHalfWidth, MaximumHalfWidth)]
    public float HalfWidth { get; set; } = 4f;

    /// <summary>How far the ground blends back to itself on the left, in metres.</summary>
    [Inspector(Name = "Falloff left (m)")]
    [Range(0f, MaximumHalfWidth)]
    public float FalloffLeft { get; set; } = 6f;

    /// <summary>And on the right.</summary>
    [Inspector(Name = "Falloff right (m)")]
    [Range(0f, MaximumHalfWidth)]
    public float FalloffRight { get; set; } = 6f;

    /// <summary>How much of the way to the road's own height the ground is pulled, 0…1.</summary>
    [Inspector]
    [Range(0f, 1f)]
    [Tooltip("1 flattens the ground to the road exactly; less leaves the terrain's own shape showing.")]
    public float Strength { get; set; } = 1f;

    /// <summary>How far below the curve the surface sits, in metres.</summary>
    [Inspector(Name = "Depth (m)")]
    [Tooltip("Positive sinks the road into the ground — a trench, a canal, a sunken lane.")]
    public float Depth { get; set; }

    /// <summary>Which reserved layer the deformation is written to.</summary>
    [Inspector(Name = "Layer")]
    public string LayerName { get; set; } = "Splines";

    /// <summary>Which paint target the road's surface is painted into, or −1 for none.</summary>
    [Inspector(Name = "Paint target")]
    [Tooltip("The weight layer the road's own surface is painted with. −1 paints nothing.")]
    public int PaintTarget { get; set; } = -1;

    /// <summary>How far apart meshes are placed along the road, in metres. Zero places none.</summary>
    [Inspector(Name = "Mesh spacing (m)")]
    [Range(0f, 200f)]
    public float MeshSpacing { get; set; }

    /// <summary>What is placed at each of those points, by asset name. Empty places nothing.</summary>
    /// <remarks>
    ///     ⚠ <b>A name rather than a reference, for the reason every reference in content is one</b> —
    ///     and a path under <c>Assets</c> as readily as a <c>vx:</c> id, because this is a field
    ///     somebody types into. What resolves it is the application's asset database; the placement
    ///     kernel takes names and <c>TerrainSplineSpawner</c> takes a resolver.
    ///
    ///     ⚠ <b>One name, not a list, and that is a stated simplification.</b> A road wants a post
    ///     every twenty metres and a lamp every sixty, which is two profiles over one curve — and a
    ///     profile per road is a property of the spline asset, which needs a curve editor to author.
    ///     Until then the panel drives one profile and one mesh.
    /// </remarks>
    [Inspector(Name = "Mesh")]
    [Tooltip("What to place along the road — an asset path under Assets, or a vx: reference.")]
    public string Mesh { get; set; } = string.Empty;

    /// <summary>What a deformation of these settings is.</summary>
    /// <returns>The profile the kernel takes.</returns>
    public TerrainSplineProfile ToProfile() =>
        new() {
            HalfWidth = MathF.Max(HalfWidth, MinimumHalfWidth),
            FalloffLeft = MathF.Max(FalloffLeft, 0f),
            FalloffRight = MathF.Max(FalloffRight, 0f),
            Strength = Math.Clamp(Strength, 0f, 1f),
            Depth = Depth
        };

    /// <summary>How far from the centre line a road reaches at all, on its wider side.</summary>
    /// <remarks>
    ///     What the invalidated rect is sized from, and it is the wider side rather than the average:
    ///     a rect sized to the mean leaves the wide side's last metres unrebuilt, which draws as a
    ///     seam that only appears on one side of the road.
    /// </remarks>
    public float Reach => ToProfile().HalfWidth + MathF.Max(FalloffLeft, FalloffRight);

    /// <summary>Whether the road paints the ground under it as well as shaping it.</summary>
    public bool Paints => PaintTarget >= 0;

    /// <summary>Whether it places meshes along its length.</summary>
    public bool Places => MeshSpacing > 0f && Mesh.Length > 0;
}
