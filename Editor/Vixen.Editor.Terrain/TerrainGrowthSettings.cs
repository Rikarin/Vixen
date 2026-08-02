// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Editor.Inspector;
using Vixen.Foliage;

namespace Vixen.Editor.Terrain;

/// <summary>
///     The growth section of the foliage panel — what [docs/plan/31 § T9]'s four sliders are.
/// </summary>
/// <remarks>
///     <para>
///         <b>A mutable settings object beside the immutable <see cref="FoliageGrowthSettings" />,</b>
///         for <see cref="TerrainBrushSettings" />'s reason: a simulation has to be the same settings
///         from its first step to its last, so what a panel edits and what a run is begun with are
///         two objects and <see cref="ToSettings" /> is where they meet.
///     </para>
///     <para>
///         ⚠ <b>The seed is a field and not a hidden number, and that is the whole feature.</b> "The
///         same rules, a different forest" is what a procedural forest is for; a generator that
///         reseeded itself every run would make an author who liked what they saw unable to get it
///         back, and one that never reseeded would make every hillside the same hillside.
///     </para>
///     <para>
///         ⚠ <b>The plant cap is announced rather than silent.</b> Spread is exponential until shade
///         catches up with it, so a region an author made ten times too large is ten thousand times
///         the plants — and a simulation that quietly stopped sowing reads as a rule that stopped
///         working. <see cref="FoliageGrowthResult.Capped" /> is what the panel shows instead.
///     </para>
/// </remarks>
[DataContract("TerrainGrowthSettings")]
public sealed class TerrainGrowthSettings {
    /// <summary>The smallest region worth simulating, in metres on a side.</summary>
    public const float MinimumSize = 1f;

    /// <summary>And the largest a panel offers, which is a kilometre.</summary>
    public const float MaximumSize = 1000f;

    /// <summary>Where the region's low corner is, along X.</summary>
    [Inspector]
    [Tooltip("The low corner of the region to simulate, in world X.")]
    public float OriginX { get; set; }

    /// <summary>And along Z.</summary>
    [Inspector]
    public float OriginZ { get; set; }

    /// <summary>How far the region reaches along X, in metres.</summary>
    [Inspector]
    [Range(MinimumSize, MaximumSize)]
    public float SizeX { get; set; } = 200f;

    /// <summary>And along Z.</summary>
    [Inspector]
    [Range(MinimumSize, MaximumSize)]
    public float SizeZ { get; set; } = 200f;

    /// <summary>How many steps to run.</summary>
    /// <remarks>
    ///     ⚠ <b>A fixed count and not a convergence test.</b> A simulation that ran until it settled
    ///     would take a different number of steps on a different seed, which makes "the same rules, a
    ///     different forest" produce two forests of different maturity — and it makes the cost
    ///     unpredictable in the one place an author is waiting for it.
    /// </remarks>
    [Inspector]
    [Range(1f, 64f)]
    [Tooltip("How many generations to age the forest. More steps is an older, more competitive forest.")]
    public int Steps { get; set; } = 8;

    /// <summary>What every random draw derives from.</summary>
    [Inspector]
    [Tooltip("Change it for a different forest under the same rules. Keep it to get this one back.")]
    public int Seed { get; set; } = unchecked((int)0x9E3779B9u);

    /// <summary>How many plants the region may hold before the simulation stops sowing.</summary>
    [Inspector]
    [Range(16f, 1_000_000f)]
    public int MaxPlants { get; set; } = 50_000;

    /// <summary>Whether an existing scatter layer is emptied before the run.</summary>
    /// <remarks>
    ///     ⚠ <b>On, and the alternative is not a feature.</b> A generated layer that accumulated
    ///     would double its forest every time the button was pressed, which is the one behaviour an
    ///     author reads as the simulation being broken rather than as a setting.
    /// </remarks>
    [Inspector(Name = "Replace the layer")]
    public bool Replace { get; set; } = true;

    /// <summary>What a run of these settings is.</summary>
    /// <returns>The settings the kernel takes.</returns>
    public FoliageGrowthSettings ToSettings() =>
        new() {
            Origin = new(OriginX, OriginZ),
            Size = new(MathF.Max(SizeX, MinimumSize), MathF.Max(SizeZ, MinimumSize)),
            Seed = unchecked((uint)Seed),
            Steps = Math.Max(Steps, 1),
            MaxPlants = Math.Max(MaxPlants, 1)
        };

    /// <summary>What is wrong with these settings, or <see langword="null" /> if nothing is.</summary>
    /// <returns>The reason, phrased for a person.</returns>
    public string? Validate() => ToSettings().Validate();

    /// <summary>Whether a run would be accepted.</summary>
    public bool IsValid => Validate() is null;

    /// <summary>Centres the region on a place, keeping its size.</summary>
    /// <param name="centre">Where to centre it, in world XZ.</param>
    /// <remarks>
    ///     What "grow around the cursor" is. The corner is what the kernel takes and the centre is
    ///     what a person points at, and converting between them in the panel rather than in the
    ///     kernel is the same division of labour <see cref="TerrainBrushSettings" /> makes.
    /// </remarks>
    public void CentreOn(Vector2 centre) {
        OriginX = centre.X - (SizeX * 0.5f);
        OriginZ = centre.Y - (SizeZ * 0.5f);
    }
}
