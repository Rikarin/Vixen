// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Foliage;

/// <summary>Whether a type's instances are stored or derived.</summary>
/// <remarks>
///     <para>
///         <b>[docs/plan/31 § D8], and it is the design decision most likely to be got wrong from a
///         feature list.</b> The dividing line is <em>density × identity</em>: something you would
///         place ten thousand of and never name individually is derived; something you would place a
///         hundred of and might name is stored.
///     </para>
///     <para>
///         ⚠ <b>The tools differ accordingly, and that is the point of the flag.</b> The grass tools
///         change a <em>rule</em> — which layer, how dense, what mesh — and the foliage tools change
///         <em>instances</em>. A type that got this wrong would offer an artist a gizmo on something
///         that is regenerated every time a cell enters range.
///     </para>
/// </remarks>
public enum FoliageStorage {
    /// <summary>
    ///     Placed instances, saved beside the scene. They have identity: moved, deleted, found again
    ///     tomorrow, and a quest marker can be attached to one.
    /// </summary>
    Stored,

    /// <summary>
    ///     Scattered from a rule and never persisted. Nothing about a blade of grass is in any file.
    /// </summary>
    Derived
}

/// <summary>
///     One kind of thing that gets painted onto a surface: what a <c>.vxfoliage</c> asset holds.
/// </summary>
/// <remarks>
///     <para>
///         <b>The palette's unit — [docs/plan/31 § The palette].</b> A type carries the mesh, how
///         densely it is painted, how far apart two of them may be, the ranges its randomness draws
///         from, the filters that refuse a placement, how far it is drawn, and whether it collides.
///     </para>
///     <para>
///         ⚠ <b>Assets are named, not handled.</b> This assembly has no device and no asset database
///         — [§ D1] — and a handle names a slot in a world that issued it, while a <c>.vxfoliage</c>
///         is read by a world that has not run yet. It is the same choice <c>TerrainComponent</c> and
///         <c>TerrainLayerDescription</c> make.
///     </para>
///     <para>
///         ⚠ <b><see cref="Radius" /> is a minimum spacing, not a size.</b> It is what makes a
///         painted forest look scattered rather than clumped, and it is the one setting an artist
///         reaches for when a stroke produces a mat of overlapping trunks. The mesh's own size is the
///         mesh's business.
///     </para>
/// </remarks>
public readonly record struct FoliageType {
    /// <summary>What the type is called, which is what the palette lists it as.</summary>
    public string Name { get; init; }

    /// <summary>The mesh or LOD group this places, by asset name.</summary>
    public string Mesh { get; init; }

    /// <summary>Whether its instances are stored or scattered from a rule.</summary>
    public FoliageStorage Storage { get; init; }

    /// <summary>How many instances a square metre of full-strength brush places.</summary>
    public float Density { get; init; }

    /// <summary>How close together two instances of this type may be, in metres.</summary>
    public float Radius { get; init; }

    /// <summary>The smallest uniform scale an instance is given.</summary>
    public float MinScale { get; init; }

    /// <summary>And the largest.</summary>
    public float MaxScale { get; init; }

    /// <summary>How much an instance turns to face the surface normal, 0…1.</summary>
    /// <remarks>
    ///     One is flat against the ground and zero is upright. ⚠ <b>Trees want a value near zero and
    ///     rocks want one near one</b>, which is why it is a fraction rather than a flag: a tree
    ///     leaning ten per cent into the hill reads as growth, and a tree lying flat on it reads as
    ///     felled.
    /// </remarks>
    public float AlignToNormal { get; init; }

    /// <summary>How far an instance may be tilted at random, in radians.</summary>
    public float MaxPitch { get; init; }

    /// <summary>Whether each instance is turned to a random heading.</summary>
    public bool RandomYaw { get; init; }

    /// <summary>The shallowest ground this may be placed on, as a slope in radians from flat.</summary>
    public float MinSlope { get; init; }

    /// <summary>And the steepest.</summary>
    public float MaxSlope { get; init; }

    /// <summary>The lowest altitude this may be placed at, in metres.</summary>
    public float MinAltitude { get; init; }

    /// <summary>And the highest.</summary>
    public float MaxAltitude { get; init; }

    /// <summary>
    ///     A terrain layer this only spawns on, by name, or empty for anywhere.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A name rather than an index, because a layer's index moves.</b> Removing the second of
    ///     six shifts the rest down, and a type holding the index would silently start spawning on a
    ///     different ground — a forest that migrates when somebody tidies the layer list.
    /// </remarks>
    public string LayerFilter { get; init; }

    /// <summary>How much of <see cref="LayerFilter" /> a place needs before this may spawn, 0…1.</summary>
    public float LayerThreshold { get; init; }

    /// <summary>How far away instances start fading out, in metres.</summary>
    public float StartCullDistance { get; init; }

    /// <summary>And where they are gone.</summary>
    public float EndCullDistance { get; init; }

    /// <summary>Whether instances cast shadows.</summary>
    public bool CastShadows { get; init; }

    /// <summary>The collision shape an instance gets, by asset name, or empty for none.</summary>
    public string CollisionShape { get; init; }

    /// <summary>
    ///     How close a physics-relevant entity has to be before an instance gets a body, in metres.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>[§ D10]'s visible behavioural difference, and it is stated rather than hidden</b>: a
    ///     projectile fired at a tree four hundred metres away passes through it. Ten thousand static
    ///     bodies is not a scene, it is a broadphase problem; the mitigation available to a project
    ///     that needs it is to raise this for that type.
    /// </remarks>
    public float ActivationRadius { get; init; }

    /// <summary>How it behaves in a growth simulation, or <see cref="FoliageEcology.None" />.</summary>
    /// <remarks>
    ///     ⚠ <b>On the type rather than in a second asset, because it is the same species.</b> An
    ///     ecology beside the palette would make an author keep two files in step for one tree, and
    ///     the first thing to drift would be the spacing radius — which both the brush and the
    ///     simulation read.
    /// </remarks>
    public FoliageEcology Ecology { get; init; }

    /// <summary>A type with the settings a new one starts from.</summary>
    /// <param name="name">What it is called.</param>
    /// <returns>The type.</returns>
    public static FoliageType Of(string name) =>
        new() {
            Name = name,
            Mesh = "",
            Storage = FoliageStorage.Stored,
            Density = 0.1f,
            Radius = 2f,
            MinScale = 0.8f,
            MaxScale = 1.2f,
            AlignToNormal = 0f,
            MaxPitch = 0f,
            RandomYaw = true,
            MinSlope = 0f,
            MaxSlope = MathF.PI / 4f,
            MinAltitude = float.NegativeInfinity,
            MaxAltitude = float.PositiveInfinity,
            LayerFilter = "",
            LayerThreshold = 0.5f,
            StartCullDistance = 200f,
            EndCullDistance = 250f,
            CastShadows = true,
            CollisionShape = "",
            ActivationRadius = 50f,
            Ecology = FoliageEcology.None
        };

    /// <summary>Whether an instance of this type ever gets a physics body.</summary>
    public bool Collides => !string.IsNullOrEmpty(CollisionShape) && ActivationRadius > 0f;

    /// <summary>Whether a placement has to ask what is painted underneath it.</summary>
    public bool NeedsSurfaceWeight => !string.IsNullOrEmpty(LayerFilter);

    /// <summary>How many instances a stamp of this radius would place at full strength.</summary>
    /// <param name="brushRadius">The brush's radius, in metres.</param>
    /// <returns>The count, at least one for a type with any density at all.</returns>
    /// <remarks>
    ///     The <em>candidate</em> count, before any filter has refused anything. Density is per square
    ///     metre because that is the number that stays meaningful when the brush changes size — a
    ///     density expressed per stamp would thin out as the brush grew.
    /// </remarks>
    public int CandidatesFor(float brushRadius) {
        if (!(Density > 0f) || !(brushRadius > 0f)) {
            return 0;
        }

        return Math.Max(1, (int)MathF.Round(Density * MathF.PI * brushRadius * brushRadius));
    }

    /// <summary>Why this type cannot be used, or <see langword="null" /> if it can.</summary>
    public string? Validate() {
        if (string.IsNullOrWhiteSpace(Name)) {
            return "A foliage type needs a name; it is what the palette lists it as.";
        }

        if (!(Radius > 0f)) {
            return $"'{Name}' has a spacing of {Radius} m, which would let every instance land on "
                + "the same point. The radius is the closest two of them may be.";
        }

        if (MinScale <= 0f || MaxScale < MinScale) {
            return $"'{Name}' scales between {MinScale} and {MaxScale}, which is not a range.";
        }

        if (MaxSlope < MinSlope) {
            return $"'{Name}' accepts slopes between {MinSlope} and {MaxSlope} radians, which is "
                + "not a range — nothing would ever be placed.";
        }

        if (EndCullDistance < StartCullDistance) {
            return $"'{Name}' starts fading at {StartCullDistance} m and is gone by "
                + $"{EndCullDistance} m, which is backwards.";
        }

        return Ecology.Validate() is { } ecology ? $"'{Name}': {ecology}" : null;
    }
}
