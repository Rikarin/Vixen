// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Editor.Inspector;
using Vixen.Terrain;

namespace Vixen.Editor.Terrain;

/// <summary>
///     What each sculpt tool does beyond what the brush already says.
/// </summary>
/// <remarks>
///     <para>
///         <b>The parameters below the rule — [docs/plan/31 § The terrain panel].</b> The brush
///         section is shared by every tool and this is what is appended under it for whichever one is
///         held, which is both references' layout.
///     </para>
///     <para>
///         ⚠ <b>One object holding every tool's parameters, not one object per tool.</b> An artist
///         who sets the talus angle, goes to smooth a ridge and comes back expects the angle to still
///         be there — so the settings outlive the tool selection, and <c>[ShowIf]</c> is what makes
///         the panel show only the ones that apply. Per-tool objects would either lose the values or
///         need a dictionary keyed by tool, which is the same thing spelled less clearly.
///     </para>
/// </remarks>
[DataContract("TerrainToolSettings")]
public sealed class TerrainToolSettings {
    /// <summary>Which tool a drag runs.</summary>
    /// <remarks>Not itself drawn — the strip is what selects it — but it is what every
    ///     <c>[ShowIf]</c> below tests.</remarks>
    public TerrainTool Tool { get; set; } = TerrainTool.Sculpt;

    // --- Sculpt -------------------------------------------------------------

    /// <summary>How far a stamp at full strength moves the ground, in metres.</summary>
    [Inspector]
    [Header("Sculpt")]
    [Range(0.001f, 100f)]
    [ShowIf(nameof(IsSculpt))]
    public float Metres { get; set; } = 1f;

    /// <summary>Whether the tool accumulates against a plane rather than along the normal.</summary>
    /// <remarks>
    ///     Clay, in both references' vocabulary. The plane is the height under the first sample of
    ///     the stroke, so holding the brush down builds a mesa rather than sharpening a spike — which
    ///     is what makes the tool usable for a landform rather than only for a bump.
    /// </remarks>
    [Inspector]
    [ShowIf(nameof(IsSculpt))]
    [Tooltip("Builds up towards a plane taken from the start of the stroke, instead of pushing along the normal.")]
    public bool Clay { get; set; }

    // --- Smooth -------------------------------------------------------------

    /// <summary>How many passes of the averaging kernel one stamp runs.</summary>
    /// <remarks>
    ///     ⚠ <b>Passes, not a filter radius, and the design said radius.</b> A wider kernel and more
    ///     passes of a 3×3 one converge to the same Gaussian, and the kernel already reads exactly
    ///     one sample beyond what it writes — which is what <see cref="TerrainSculpt.NeighbourMargin" />
    ///     is and what every undo record is grown by. A settable radius would make that margin a
    ///     variable, so a stroke's record would have to be sized from a number the panel can change
    ///     mid-drag. Passes cost the same and change nothing structural.
    /// </remarks>
    [Inspector]
    [Header("Smooth")]
    [Range(1, 8)]
    [ShowIf(nameof(IsSmooth))]
    public int SmoothPasses { get; set; } = 1;

    // --- Flatten ------------------------------------------------------------

    /// <summary>The height the tool pulls towards, in metres.</summary>
    /// <remarks>
    ///     Picked from the first sample of the stroke rather than typed, normally — see
    ///     <see cref="TerrainEdit" /> — and left settable so a pad can be flattened to an exact
    ///     height a designer has in mind.
    /// </remarks>
    [Inspector]
    [Header("Flatten")]
    [ShowIf(nameof(IsFlatten))]
    public float FlattenTarget { get; set; }

    /// <summary>Whether the target is taken from the ground at the start of each stroke.</summary>
    [Inspector]
    [ShowIf(nameof(IsFlatten))]
    [Tooltip("Off keeps the height above, so the same pad height can be flattened to in several strokes.")]
    public bool PickTarget { get; set; } = true;

    // --- Ramp ---------------------------------------------------------------

    /// <summary>How far the ramp reaches either side of its line, in metres.</summary>
    [Inspector]
    [Header("Ramp")]
    [Range(0.1f, 512f)]
    [ShowIf(nameof(IsRamp))]
    public float RampWidth { get; set; } = 8f;

    /// <summary>How much of that half-width is falloff rather than flat, 0…1.</summary>
    [Inspector]
    [Range(0f, 1f)]
    [ShowIf(nameof(IsRamp))]
    public float RampFalloff { get; set; } = 0.5f;

    // --- Erosion ------------------------------------------------------------

    /// <summary>The steepest slope that holds, as a rise in metres over one quad.</summary>
    [Inspector]
    [Header("Erosion")]
    [Range(0f, 10f)]
    [ShowIf(nameof(IsErosion))]
    [Tooltip("The talus angle, as a rise per quad. Anything steeper slides downhill.")]
    public float Talus { get; set; } = 0.5f;

    /// <summary>How much of the excess moves per pass, 0…1.</summary>
    [Inspector]
    [Range(0f, 1f)]
    [ShowIf(nameof(IsErosion))]
    public float ErosionRate { get; set; } = 0.5f;

    /// <summary>How many passes one stamp runs.</summary>
    /// <remarks>
    ///     ⚠ <b>Per stamp rather than per stroke, which is what makes erosion a brush.</b> A stroke
    ///     is many stamps and an artist holding the button down is what "more erosion" means; a loop
    ///     over the whole rectangle at pointer-up would be a batch job with a progress bar, which is
    ///     the version of this tool nobody uses.
    /// </remarks>
    [Inspector]
    [Range(1, 16)]
    [ShowIf(nameof(IsIterative))]
    public int Iterations { get; set; } = 1;

    // --- Hydro --------------------------------------------------------------

    /// <summary>How fast water moves material, 0…1.</summary>
    [Inspector]
    [Header("Hydro")]
    [Range(0f, 1f)]
    [ShowIf(nameof(IsHydro))]
    public float HydroRate { get; set; } = 0.5f;

    // --- Noise --------------------------------------------------------------

    /// <summary>How far the noise moves the ground, in metres.</summary>
    [Inspector]
    [Header("Noise")]
    [Range(0.001f, 100f)]
    [ShowIf(nameof(IsNoise))]
    public float Amplitude { get; set; } = 1f;

    /// <summary>How many layers of detail.</summary>
    [Inspector]
    [Range(1, 8)]
    [ShowIf(nameof(IsNoise))]
    public int Octaves { get; set; } = 4;

    /// <summary>How many samples one period of the coarsest octave spans, inverted.</summary>
    [Inspector]
    [Range(0.001f, 1f)]
    [ShowIf(nameof(IsNoise))]
    public float Frequency { get; set; } = 0.05f;

    /// <summary>How much finer each octave is than the last.</summary>
    [Inspector]
    [Range(1f, 4f)]
    [ShowIf(nameof(IsNoise))]
    public float Lacunarity { get; set; } = 2f;

    /// <summary>How much quieter each octave is than the last.</summary>
    [Inspector]
    [Range(0f, 1f)]
    [ShowIf(nameof(IsNoise))]
    public float Gain { get; set; } = 0.5f;

    /// <summary>Whether the noise is folded about zero, which makes ridges instead of hills.</summary>
    [Inspector]
    [ShowIf(nameof(IsNoise))]
    public bool Ridged { get; set; }

    /// <summary>What the noise lattice derives from.</summary>
    [Inspector]
    [ShowIf(nameof(IsNoise))]
    public int Seed { get; set; } = 1;

    // --- Holes --------------------------------------------------------------

    /// <summary>How much brush weight a sample needs before it becomes a hole, 0…1.</summary>
    /// <remarks>
    ///     Thresholded rather than blended, because a hole is a bit. Without it the soft edge of the
    ///     brush punches a ragged fringe two samples wider than the artist aimed at.
    /// </remarks>
    [Inspector]
    [Header("Holes")]
    [Range(0.01f, 1f)]
    [ShowIf(nameof(IsHoles))]
    public float HoleThreshold { get; set; } = 0.5f;

    // --- Which rows apply ---------------------------------------------------

    /// <summary>Whether the sculpt rows apply.</summary>
    public bool IsSculpt => Tool == TerrainTool.Sculpt;

    /// <summary>Whether the smooth rows apply.</summary>
    public bool IsSmooth => Tool == TerrainTool.Smooth;

    /// <summary>Whether the flatten rows apply.</summary>
    public bool IsFlatten => Tool == TerrainTool.Flatten;

    /// <summary>Whether the ramp rows apply.</summary>
    public bool IsRamp => Tool == TerrainTool.Ramp;

    /// <summary>Whether the erosion rows apply.</summary>
    public bool IsErosion => Tool == TerrainTool.Erosion;

    /// <summary>Whether the hydro rows apply.</summary>
    public bool IsHydro => Tool == TerrainTool.Hydro;

    /// <summary>Whether the noise rows apply.</summary>
    public bool IsNoise => Tool == TerrainTool.Noise;

    /// <summary>Whether the hole rows apply.</summary>
    public bool IsHoles => Tool == TerrainTool.Holes;

    /// <summary>Whether the tool runs a settable number of passes per stamp.</summary>
    public bool IsIterative => IsErosion || IsHydro;

    /// <summary>The noise the Noise tool adds, as the kernel takes it.</summary>
    /// <returns>The settings.</returns>
    public TerrainNoise ToNoise() =>
        new(
            Math.Clamp(Octaves, 1, 8),
            Frequency > 0f ? Frequency : 0.05f,
            Lacunarity,
            Gain,
            Ridged,
            (uint)Seed
        );

    /// <summary>How many passes of the held tool one stamp runs.</summary>
    /// <returns>At least one.</returns>
    public int PassesOf(TerrainTool tool) =>
        tool switch {
            TerrainTool.Smooth => Math.Max(1, SmoothPasses),
            TerrainTool.Erosion or TerrainTool.Hydro => Math.Max(1, Iterations),
            _ => 1
        };
}
