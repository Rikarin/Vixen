// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core;
using Vixen.Editor.Inspector;
using Vixen.Terrain;
using TerrainMap = Vixen.Terrain.Terrain;

namespace Vixen.Editor.Terrain;

/// <summary>
///     What a new terrain costs, computed from the form as it is being filled in.
/// </summary>
/// <param name="WidthX">How far it reaches along X, in metres.</param>
/// <param name="WidthZ">And along Z.</param>
/// <param name="Samples">How many height samples it has in total.</param>
/// <param name="HeightBytes">What those samples occupy.</param>
/// <param name="WeightBytesPerLayer">What one paint layer adds.</param>
/// <param name="Tiles">How many tiles, which is how many collision shapes.</param>
/// <param name="MetresPerStep">What one step of the 16-bit height range is, in metres.</param>
/// <remarks>
///     <para>
///         <b>The <c>(derived)</c> readout convention, from [20 § B6]'s lighting panel, and it
///         belongs here more than there.</b> This is the dialog where a person accidentally asks for
///         eight gigabytes: four numbers that each look reasonable multiply into a terrain nothing can
///         load, and the multiplication is not one anybody does in their head.
///     </para>
///     <para>
///         ⚠ <b><see cref="MetresPerStep" /> is here because the height range is authored.</b> Unreal
///         fixes the range and nobody has to think about it; [§ D2] lets the author set it, which buys
///         a 40 m rolling landscape 0.6 mm of vertical precision instead of 8 mm — and makes it
///         possible to ask for a 20 km range and wonder later why a flatten will not settle. The
///         number that answers that has to be on the form.
///     </para>
/// </remarks>
public readonly record struct TerrainFacts(
    float WidthX,
    float WidthZ,
    long Samples,
    long HeightBytes,
    long WeightBytesPerLayer,
    int Tiles,
    float MetresPerStep
) {
    /// <summary>What a description costs.</summary>
    /// <param name="description">The shape.</param>
    /// <returns>The facts.</returns>
    public static TerrainFacts Of(in TerrainDescription description) =>
        new(
            description.WidthX,
            description.WidthZ,
            description.SampleCount,
            description.HeightBytes,
            description.WeightBytesPerLayer,
            description.TileCount,
            description.MetresPerStep
        );

    /// <summary>The rows the panel draws, each already labelled as derived.</summary>
    /// <returns>The label and value of each.</returns>
    /// <remarks>
    ///     Strings rather than a layout, because this assembly draws nothing — the panel takes these
    ///     rows and the shell decides what a row looks like, which is the same split
    ///     <c>EditorWorlds.Budgets</c> uses for the lighting panel's facts.
    /// </remarks>
    public IReadOnlyList<(string Label, string Value)> Rows() {
        var culture = CultureInfo.InvariantCulture;

        return [
            ("Extent", string.Create(culture, $"{WidthX:N0} × {WidthZ:N0} m (derived)")),
            ("Samples", string.Create(culture, $"{Samples:N0} (derived)")),
            ("Height storage", string.Create(culture, $"{Megabytes(HeightBytes):N1} MB (derived)")),
            (
                "Weightmap storage",
                string.Create(culture, $"{Megabytes(WeightBytesPerLayer):N1} MB per layer (derived)")
            ),
            ("Collision shapes", string.Create(culture, $"{Tiles:N0} height fields (derived)")),
            ("Vertical precision", string.Create(culture, $"{MetresPerStep * 1000f:N2} mm per step (derived)"))
        ];
    }

    static double Megabytes(long bytes) => bytes / (1024.0 * 1024.0);
}

/// <summary>
///     The create and manage section of the terrain panel.
/// </summary>
/// <remarks>
///     <para>
///         <b>[docs/plan/31 § The terrain panel], as a settings object rather than as dialog
///         code.</b> Every row is an <c>[Inspector]</c> member of a <c>[DataContract]</c> type, which
///         is [20 § B6]'s bargain for world settings and is what makes the form testable without a
///         window.
///     </para>
///     <para>
///         ⚠ <b>The tile size is offered in quads and stored in samples.</b> An artist reads
///         "63 / 127 / 255", because that is what both references call it and because it is the number
///         of quads a section spans; Jolt requires a power-of-two <em>sample</em> count and rejects
///         anything else by returning nothing at all. The two differ by one and the form is where the
///         translation happens, once — see [§ D2].
///     </para>
/// </remarks>
[DataContract("TerrainCreateSettings")]
public sealed class TerrainCreateSettings {
    /// <summary>The tile sizes the form offers, in quads.</summary>
    /// <remarks>
    ///     ⚠ <b>Written out rather than validated, because the constraint is not one an artist can
    ///     be expected to hold.</b> A power-of-two sample count with at least two collision blocks per
    ///     axis is three sentences of Jolt's manual; three radio buttons is the same rule with nothing
    ///     to remember. A project that wants another size sets <see cref="TileSamples" /> directly and
    ///     <see cref="Validate" /> answers for it.
    /// </remarks>
    public static IReadOnlyList<int> TileQuadChoices { get; } = [63, 127, 255];

    /// <summary>How many samples a tile spans, along each axis. A power of two.</summary>
    [Inspector(Name = "Tile size")]
    [Tooltip("In samples. 64, 128 or 256 — one more than the quads a tile spans.")]
    public int TileSamples { get; set; } = 128;

    /// <summary>How many tiles across.</summary>
    [Inspector]
    [Range(1, 64)]
    public int TilesX { get; set; } = 4;

    /// <summary>How many tiles deep.</summary>
    [Inspector]
    [Range(1, 64)]
    public int TilesZ { get; set; } = 4;

    /// <summary>How far apart two samples are, in metres.</summary>
    [Inspector]
    [Range(0.01f, 64f)]
    public float MetresPerQuad { get; set; } = 1f;

    /// <summary>The lowest height the terrain can hold, in metres.</summary>
    [Inspector]
    [Tooltip("The bottom of the 16-bit range. Everything below it is not representable.")]
    public float MinHeight { get; set; } = -256f;

    /// <summary>And the highest.</summary>
    [Inspector]
    public float MaxHeight { get; set; } = 256f;

    /// <summary>What the ground starts at, in metres.</summary>
    [Inspector]
    [Tooltip("The flat height a new terrain is filled with, before anything is sculpted on it.")]
    public float BaseHeight { get; set; }

    /// <summary>What the form describes.</summary>
    public TerrainDescription Description =>
        new() {
            TileSamples = TileSamples,
            TilesX = TilesX,
            TilesZ = TilesZ,
            MetresPerQuad = MetresPerQuad,
            MinHeight = MinHeight,
            MaxHeight = MaxHeight
        };

    /// <summary>What it costs, as the form is being filled in.</summary>
    public TerrainFacts Facts => TerrainFacts.Of(Description);

    /// <summary>Why the form cannot be submitted, or <see langword="null" /> if it can.</summary>
    /// <remarks>
    ///     The kernel's own answer, forwarded. A second implementation of the rules in the panel is a
    ///     second implementation that will disagree with the first, and it would disagree exactly at
    ///     the sizes nobody tests.
    /// </remarks>
    public string? Validate() {
        if (BaseHeight < MinHeight || BaseHeight > MaxHeight) {
            return $"The base height {BaseHeight} m is outside the range {MinHeight}…{MaxHeight} m.";
        }

        return Description.Validate();
    }

    /// <summary>Whether the form can be submitted.</summary>
    public bool IsValid => Validate() is null;

    /// <summary>Builds the terrain the form describes.</summary>
    /// <returns>The terrain, flat at <see cref="BaseHeight" />.</returns>
    /// <exception cref="InvalidOperationException">The form is not valid.</exception>
    public TerrainMap Build() {
        if (Validate() is { } refusal) {
            throw new InvalidOperationException(refusal);
        }

        return new(Description, BaseHeight);
    }

    /// <summary>Fills the form in from a terrain that already exists, for the manage section.</summary>
    /// <param name="terrain">The terrain.</param>
    /// <returns>The form.</returns>
    public static TerrainCreateSettings Of(TerrainMap terrain) {
        ArgumentNullException.ThrowIfNull(terrain);

        var description = terrain.Description;

        return new() {
            TileSamples = description.TileSamples,
            TilesX = description.TilesX,
            TilesZ = description.TilesZ,
            MetresPerQuad = description.MetresPerQuad,
            MinHeight = description.MinHeight,
            MaxHeight = description.MaxHeight
        };
    }

    /// <summary>What applying the form to an existing terrain would do to it.</summary>
    /// <param name="terrain">The terrain as it is.</param>
    /// <returns>The warning, or <see langword="null" /> if nothing is lost.</returns>
    /// <remarks>
    ///     ⚠ <b>[§ D2] asks for the dialog to say that changing the height range rescales, and this
    ///     is that sentence.</b> Cropping and rescaling are both reasonable things to ask for and both
    ///     lose something that cannot be recovered by asking for the old numbers back — so the panel
    ///     says which, before rather than after.
    /// </remarks>
    public string? Consequence(TerrainMap terrain) {
        ArgumentNullException.ThrowIfNull(terrain);

        var from = terrain.Description;
        var to = Description;
        var warnings = new List<string>();

        if (to.SamplesX < from.SamplesX || to.SamplesZ < from.SamplesZ) {
            warnings.Add(
                $"the terrain is cropped from {from.SamplesX}×{from.SamplesZ} to "
                + $"{to.SamplesX}×{to.SamplesZ} samples, and what is outside is discarded"
            );
        }

        if (from.MinHeight != to.MinHeight || from.MaxHeight != to.MaxHeight) {
            warnings.Add(
                $"every height is rescaled to keep its metres, and one step goes from "
                + string.Create(CultureInfo.InvariantCulture, $"{from.MetresPerStep * 1000f:N2} mm to ")
                + string.Create(CultureInfo.InvariantCulture, $"{to.MetresPerStep * 1000f:N2} mm")
            );
        }

        if (from.MetresPerQuad != to.MetresPerQuad) {
            warnings.Add(
                "the samples are kept and the world scale changes, so the landscape becomes "
                + string.Create(CultureInfo.InvariantCulture, $"{to.WidthX / from.WidthX:N2}× as wide")
            );
        }

        return warnings.Count == 0 ? null : string.Concat("Applying this: ", string.Join("; ", warnings), ".");
    }
}
