// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Editor.Inspector;
using Vixen.Foliage;

namespace Vixen.Editor.Terrain;

/// <summary>
///     The grass section of the terrain panel — the numbers that are a <em>rule</em> rather than a
///     tool.
/// </summary>
/// <remarks>
///     <para>
///         <b>[docs/plan/31 § D8]'s consequence, spelled as chrome.</b> Grass is derived: nothing
///         about a blade is in any file, and a person does not paint it — they change the rule that
///         produces it and the whole field re-scatters. So there is no grass <em>mode</em>, no brush
///         and no stroke; there is a settings object beside the terrain panel, and a fifth viewport
///         mode would have been a mode with nothing to click on.
///     </para>
///     <para>
///         ⚠ <b>The density scalar multiplies and never reshuffles.</b> Lowering it removes a subset
///         of the blades that were there and moves none of the rest, because every candidate keeps
///         its own draw — a scalar that rearranged would make a quality slider look like a different
///         level. Both halves of the scatter are written that way and this is the control that
///         depends on it.
///     </para>
///     <para>
///         ⚠ <b>The wind is authored per type and scaled here, which is the same division.</b> A
///         <c>.vxgrass</c> says what its grass is like in a breeze; a scene says how much breeze
///         there is. Putting the whole profile on the panel would make every field on the level share
///         one flutter, and putting the scale on the asset would make a calm day a re-import.
///     </para>
/// </remarks>
[DataContract("TerrainGrassSettings")]
public sealed class TerrainGrassSettings {
    /// <summary>The largest range a panel offers, in metres.</summary>
    public const float MaximumRange = 2000f;

    /// <summary>Whether grass is scattered at all.</summary>
    /// <remarks>
    ///     ⚠ <b>A switch rather than a density of zero.</b> Zero density still dispatches the scatter
    ///     for every resident cell and rejects every candidate, which costs the whole pass to draw
    ///     nothing — and it reads to a profiler as grass being expensive when it is off.
    /// </remarks>
    [Inspector]
    public bool IsEnabled { get; set; } = true;

    /// <summary>What fraction of each field to keep, 0…1.</summary>
    [Inspector]
    [Range(0f, 1f)]
    [Tooltip("Thins every field. It removes blades rather than rearranging them, so it is a quality setting.")]
    public float Density { get; set; } = 1f;

    /// <summary>How far from the camera cells are kept resident, in metres.</summary>
    /// <remarks>
    ///     ⚠ <b>Residency, not the cull distance — the two are different questions.</b> A type's
    ///     <see cref="GrassType.EndCullDistance" /> says where its blades stop being drawn; this says
    ///     where their cells stop being <em>scattered</em>, which is memory rather than triangles. A
    ///     range below the furthest type's cull distance is a field that fades out early and cannot be
    ///     explained by any number on the asset.
    /// </remarks>
    [Inspector(Name = "Residency range (m)")]
    [Range(16f, MaximumRange)]
    public float Range { get; set; } = 160f;

    /// <summary>How much of each type's authored wind to apply, 0…2.</summary>
    [Inspector]
    [Range(0f, 2f)]
    [Tooltip("Scales every type's own wind profile. 0 is a still day and 1 is what the asset was authored for.")]
    public float Wind { get; set; } = 1f;

    /// <summary>Which way the wind blows, in degrees clockwise from north.</summary>
    /// <remarks>
    ///     Degrees here and a direction vector in the kernel, which is the convention every angle in
    ///     the editor follows: a person types 45 and a shader is handed a unit vector.
    /// </remarks>
    [Inspector(Name = "Wind bearing (°)")]
    [Range(0f, 360f)]
    public float Bearing { get; set; }

    /// <summary>How many blades one cell's run holds.</summary>
    /// <remarks>
    ///     ⚠ <b>A cap and not a hope, and it is on the panel because it is the memory.</b> The ring is
    ///     <see cref="Range" />'s worth of cells times this, times forty-eight bytes; a cell that
    ///     produces more candidates than this simply stops, which is the one refusal an author can act
    ///     on by lowering the density instead.
    /// </remarks>
    [Inspector(Name = "Blades per cell")]
    [Range(256f, 65536f)]
    public int BladesPerCell { get; set; } = 4096;

    /// <summary>The direction the bearing means, as the kernel takes it.</summary>
    public Vector2 Direction {
        get {
            var radians = MathUtil.DegreesToRadians(Bearing);

            return new(MathF.Sin(radians), MathF.Cos(radians));
        }
    }

    /// <summary>What fraction of the candidates survive, given the switch and the scalar.</summary>
    /// <remarks>
    ///     One number rather than two tests at every call site, because "off" and "none" produce the
    ///     same field and every consumer would otherwise have to remember both.
    /// </remarks>
    public float DensityScale => IsEnabled ? Math.Clamp(Density, 0f, 1f) : 0f;

    /// <summary>A type's wind, with this scene's strength and bearing applied.</summary>
    /// <param name="wind">What the asset authored.</param>
    /// <returns>The profile to scatter with.</returns>
    /// <remarks>
    ///     ⚠ <b>The strength scales and the direction replaces.</b> Two fields on one level blowing
    ///     in different directions is not weather, it is a bug — but two fields fluttering
    ///     differently in the same wind is exactly what an author authored them for.
    /// </remarks>
    public GrassWind Apply(in GrassWind wind) =>
        wind with { Strength = wind.Strength * MathF.Max(Wind, 0f), Direction = Direction };

    /// <summary>How many bytes the ring is, at this range and this cell size.</summary>
    /// <param name="cellSize">How many metres a cell spans.</param>
    /// <param name="instanceBytes">How many bytes one blade is.</param>
    /// <returns>The count.</returns>
    /// <remarks>
    ///     The derived number the panel shows, and the reason the two settings above it are on the
    ///     same page: a range doubled is four times the cells, and this is the dialog where that
    ///     becomes a gigabyte.
    /// </remarks>
    public long RingBytes(float cellSize = 32f, int instanceBytes = 48) {
        var side = (2L * (long)MathF.Ceiling(Range / MathF.Max(cellSize, 1f))) + 1;

        return side * side * BladesPerCell * instanceBytes;
    }

    /// <summary>What is wrong with these settings, or <see langword="null" /> if nothing is.</summary>
    /// <returns>The reason, phrased for a person.</returns>
    public string? Validate() {
        if (Range <= 0f) {
            return "The residency range is zero, so no cell is ever scattered.";
        }

        return BladesPerCell < 1 ? "A cell that holds no blades produces no grass." : null;
    }
}
