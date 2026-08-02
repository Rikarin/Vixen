// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Foliage;

/// <summary>How a grass type moves.</summary>
/// <remarks>
///     <para>
///         <b>The host half of <c>Displacement.WindParameters</c>, field for field.</b> Two frequencies
///         rather than one, because a single sine reads as mechanical: a slow bend plus a fast flutter
///         is the cheapest thing that looks alive, and it is what the shader already implements.
///     </para>
///     <para>
///         ⚠ <b>No phase, and that is the point of <c>InstanceParameters.WindPhase</c>.</b> The
///         shader's phase comes from world position, which decorrelates two blades a hundred metres
///         apart and does nothing at all for two that are ten centimetres apart — which is every pair
///         of blades in a clump. The per-instance offset is what the scatter draws, so it belongs to
///         the blade rather than to the profile.
///     </para>
/// </remarks>
public readonly record struct GrassWind {
    /// <summary>Which way it blows, in the XZ plane. Normalised on use.</summary>
    public Vector2 Direction { get; init; }

    /// <summary>How far the slow bend carries, in metres at full stiffness.</summary>
    public float Strength { get; init; }

    /// <summary>How many radians of phase a metre of world costs.</summary>
    public float Frequency { get; init; }

    /// <summary>How fast the phase advances with time.</summary>
    public float Speed { get; init; }

    /// <summary>The high-frequency component, which reads as leaves rather than branches.</summary>
    public float Flutter { get; init; }

    /// <summary>
    ///     The object-space height the sway is normalised against, so the base stays planted.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A zero here is not "no wind", it is <em>all</em> wind.</b> The shader divides by it,
    ///     guarded, so a blade whose reference height is zero is stiff nowhere and slides off its own
    ///     root. <see cref="Validate" /> refuses it for that reason.
    /// </remarks>
    public float Height { get; init; }

    /// <summary>Wind that does nothing.</summary>
    public static GrassWind Still =>
        new() {
            Direction = new(1f, 0f),
            Strength = 0f,
            Frequency = 0.1f,
            Speed = 1f,
            Flutter = 0f,
            Height = 1f
        };

    /// <summary>The settings a new grass type starts from.</summary>
    public static GrassWind Breeze =>
        new() {
            Direction = new(1f, 0.3f),
            Strength = 0.12f,
            Frequency = 0.35f,
            Speed = 1.4f,
            Flutter = 0.03f,
            Height = 0.6f
        };

    /// <summary>Why this profile cannot be used, or <see langword="null" /> if it can.</summary>
    public string? Validate() {
        if (Direction.IsZero) {
            return "A wind profile needs a direction; a zero one has no plane to bend in.";
        }

        if (!(Height > 0f)) {
            return $"A wind profile's reference height is {Height} m, which makes every part of a "
                + "blade equally floppy — including its root, which then slides.";
        }

        return null;
    }
}

/// <summary>
///     One kind of grass: what a <c>.vxgrass</c> asset holds.
/// </summary>
/// <remarks>
///     <para>
///         <b>[docs/plan/31 § D8]'s derived counterpart to <see cref="FoliageType" />, and it is
///         smaller because most of that does not apply.</b> Grass is scattered from a rule and never
///         persisted: nothing about a blade of grass is in any file, so there is no identity to keep,
///         no collision to allocate and no undo record to hold. What a level says is "this type, on
///         this layer", and a million blades follow.
///     </para>
///     <para>
///         ⚠ <b><see cref="Density" /> is the <em>candidate</em> density and never the placed
///         one.</b> It fixes the grid a cell is scattered over — which on the device is the dispatch
///         extent and therefore cannot depend on anything sampled — and what the painted weight
///         decides is what fraction of those candidates survive. A density that varied with the
///         weight would mean a dispatch whose size depends on a texture read, which is not a thing.
///     </para>
///     <para>
///         ⚠ <b>The layer is a <em>name</em>, for <see cref="FoliageType.LayerFilter" />'s
///         reason.</b> Removing the second of six terrain layers shifts the rest down, and a type
///         holding the index would silently start growing on different ground.
///     </para>
/// </remarks>
public readonly record struct GrassType {
    /// <summary>What the type is called.</summary>
    public string Name { get; init; }

    /// <summary>The mesh this scatters, by asset name.</summary>
    public string Mesh { get; init; }

    /// <summary>Which terrain layer's weight it reads, or empty to grow anywhere.</summary>
    public string Layer { get; init; }

    /// <summary>The weight below which nothing grows, 0…1.</summary>
    public float MinWeight { get; init; }

    /// <summary>And the weight at which the field is at full density.</summary>
    public float MaxWeight { get; init; }

    /// <summary>How many candidates a square metre is scattered with.</summary>
    public float Density { get; init; }

    /// <summary>How far a candidate may wander from its grid slot, 0…1 of half a step.</summary>
    /// <remarks>
    ///     ⚠ <b>Half a step at one, not a whole one.</b> A jitter that reached a whole step would let
    ///     two neighbours swap places and land on each other, which is a clump with a bald patch
    ///     beside it — and it would make the same candidate reachable from two slots, which breaks
    ///     the one-invocation-per-slot shape the dispatch has.
    /// </remarks>
    public float Jitter { get; init; }

    /// <summary>The smallest uniform scale a blade is given.</summary>
    public float MinScale { get; init; }

    /// <summary>And the largest.</summary>
    public float MaxScale { get; init; }

    /// <summary>Whether each blade is turned to a random heading.</summary>
    public bool RandomYaw { get; init; }

    /// <summary>How much a blade turns to face the surface normal, 0…1.</summary>
    public float AlignToNormal { get; init; }

    /// <summary>The steepest ground it grows on, as a slope in radians from flat.</summary>
    public float MaxSlope { get; init; }

    /// <summary>How far away blades start fading out, in metres.</summary>
    public float StartCullDistance { get; init; }

    /// <summary>And where they are gone.</summary>
    public float EndCullDistance { get; init; }

    /// <summary>How it moves.</summary>
    public GrassWind Wind { get; init; }

    /// <summary>A type with the settings a new one starts from.</summary>
    /// <param name="name">What it is called.</param>
    /// <returns>The type.</returns>
    public static GrassType Of(string name) =>
        new() {
            Name = name,
            Mesh = "",
            Layer = "",
            MinWeight = 0.15f,
            MaxWeight = 0.6f,
            Density = 16f,
            Jitter = 0.8f,
            MinScale = 0.7f,
            MaxScale = 1.3f,
            RandomYaw = true,
            AlignToNormal = 0.35f,
            MaxSlope = MathF.PI / 4f,
            StartCullDistance = 30f,
            EndCullDistance = 40f,
            Wind = GrassWind.Breeze
        };

    /// <summary>Whether a candidate has to ask what is painted underneath it.</summary>
    public bool NeedsSurfaceWeight => !string.IsNullOrEmpty(Layer);

    /// <summary>What fraction of this type's candidates a painted weight keeps, 0…1.</summary>
    /// <param name="weight">How much of <see cref="Layer" /> is painted there, 0…1.</param>
    /// <returns>The fraction.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A smoothstep and not a linear ramp, because a ramp has a corner at each end.</b>
    ///         The foot of a linear ramp is a straight line across the ground wherever the painted
    ///         weight crosses the threshold — a visible edge that follows nothing in the terrain — and
    ///         a smoothstep's derivative is zero at both ends, so the field feathers out instead.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A type with no layer answers one everywhere</b>, rather than answering zero and
    ///         growing nothing. An unbound type is "grow on all of it", which is what somebody
    ///         dragging a new grass type onto a terrain expects to see.
    ///     </para>
    /// </remarks>
    public float DensityAt(float weight) {
        if (!NeedsSurfaceWeight) {
            return 1f;
        }

        var low = MinWeight;
        var high = MathF.Max(MaxWeight, low + 1e-4f);
        var t = Math.Clamp((weight - low) / (high - low), 0f, 1f);

        return t * t * (3f - (2f * t));
    }

    /// <summary>How many candidates a cell is scattered over, along one axis.</summary>
    /// <param name="cellSize">How many metres the cell spans.</param>
    /// <returns>The count, at least one.</returns>
    /// <remarks>
    ///     ⚠ <b>A square grid, so a candidate index is a slot without a division by anything
    ///     variable.</b> The device form is one invocation per slot and recovers its 2D position from
    ///     <c>SV_DispatchThreadID</c> directly; a count that was not a square would make every
    ///     invocation divide by a number it had to read first.
    /// </remarks>
    public int GridOf(float cellSize) {
        if (!(Density > 0f) || !(cellSize > 0f)) {
            return 1;
        }

        return Math.Max(1, (int)MathF.Round(cellSize * MathF.Sqrt(Density)));
    }

    /// <summary>How many candidates one cell generates.</summary>
    /// <param name="cellSize">How many metres the cell spans.</param>
    /// <returns>The count.</returns>
    public int CandidatesFor(float cellSize) {
        var grid = GridOf(cellSize);

        return grid * grid;
    }

    /// <summary>Why this type cannot be used, or <see langword="null" /> if it can.</summary>
    public string? Validate() {
        if (string.IsNullOrWhiteSpace(Name)) {
            return "A grass type needs a name; it is what a level names it by.";
        }

        if (!(Density > 0f)) {
            return $"'{Name}' has a candidate density of {Density} per square metre, so no cell "
                + "would ever generate a blade.";
        }

        if (MinScale <= 0f || MaxScale < MinScale) {
            return $"'{Name}' scales between {MinScale} and {MaxScale}, which is not a range.";
        }

        if (MaxWeight < MinWeight) {
            return $"'{Name}' grows between weights {MinWeight} and {MaxWeight}, which is backwards "
                + "— the field would be densest where the layer is not painted.";
        }

        if (EndCullDistance < StartCullDistance) {
            return $"'{Name}' starts fading at {StartCullDistance} m and is gone by "
                + $"{EndCullDistance} m, which is backwards.";
        }

        return Wind.Validate() is { } wind ? $"'{Name}': {wind}" : null;
    }
}
