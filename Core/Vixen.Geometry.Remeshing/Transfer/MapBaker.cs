// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Geometry.Remeshing;

/// <summary>Which frame a baked normal is expressed in.</summary>
public enum BakeSpace : byte {
    /// <summary>The output surface's own tangent frame, derived from the atlas.</summary>
    /// <remarks>
    ///     The usual answer, because a tangent-space map survives the mesh being deformed — which
    ///     for a skinned character is the entire point of baking one.
    /// </remarks>
    Tangent,

    /// <summary>The mesh's own space, with no frame at all.</summary>
    /// <remarks>
    ///     ⚠ Unambiguous and undeformable. Worth having for a static prop, and worth having as the
    ///     thing to compare against when a tangent-space bake looks wrong, because it removes the
    ///     handedness convention from the question.
    /// </remarks>
    Object
}

/// <summary>Which of the mesh maps to measure, beyond the normal and the displacement.</summary>
/// <remarks>
///     <para>
///         <b>The normal map and the displacement map are not in here because they are not
///         optional.</b> They fall out of the one ray the bake already casts, and the seven below are
///         further measurements at the same texel, on the surface point that ray found.
///     </para>
///     <para>
///         ⚠ <b><see cref="AmbientOcclusion" />, <see cref="BentNormal" /> and
///         <see cref="Thickness" /> are the expensive ones and nothing else here is.</b> They are the
///         only three that cast rays of their own — <see cref="BakeSettings.OcclusionSamples" /> of
///         them per texel, against the same tree — and the three of them together cost what one of
///         them costs, because they share both the sample set and the loop. The other four are
///         arithmetic on a hit the bake already has.
///     </para>
/// </remarks>
[Flags]
public enum MeshMaps {
    /// <summary>Just the normal and the displacement, which is what a bake has always returned.</summary>
    None = 0,

    /// <summary>The unoccluded fraction of the cosine-weighted hemisphere.</summary>
    AmbientOcclusion = 1,

    /// <summary>The average unoccluded direction, from the same rays.</summary>
    BentNormal = 2,

    /// <summary>Mean curvature, interpolated from the source's cotangent Laplacian.</summary>
    Curvature = 4,

    /// <summary>The mean depth through the surface, along the same hemisphere inverted.</summary>
    Thickness = 8,

    /// <summary>The surface point, normalised into the source's bounding box.</summary>
    Position = 16,

    /// <summary>The source's normal, in the source's own space.</summary>
    WorldNormal = 32,

    /// <summary>The source's face group, nearest-sampled and never filtered.</summary>
    Id = 64,

    /// <summary>All seven.</summary>
    All = AmbientOcclusion | BentNormal | Curvature | Thickness | Position | WorldNormal | Id
}

/// <summary>What the bake's distances are measured in.</summary>
public enum BakeUnits : byte {
    /// <summary>Fractions of the source's bounding-box diagonal, which mean the same at any model scale.</summary>
    Relative,

    /// <summary>The model's own units, for a studio that knows its scale and sets a cage distance in it.</summary>
    /// <remarks>
    ///     ⚠ <b>A claim about how big the model is</b>, which is the claim the relative default
    ///     exists to avoid: the same bake on the same model exported in centimetres finds nothing.
    ///     Offered because an artist working at a fixed scale thinks in it, not as the default.
    /// </remarks>
    Absolute
}

/// <summary>How much an occluder counts for by how far away it is.</summary>
public enum OcclusionFalloff : byte {
    /// <summary>Anything within reach blocks the ray completely.</summary>
    None,

    /// <summary>A block counts for less the further it is, falling straight to nothing at the reach.</summary>
    Linear,

    /// <summary>As <see cref="Linear" />, but along a smoothstep, so near contact stays dark longer.</summary>
    Smooth
}

/// <summary>How the occlusion rays are spread over the hemisphere.</summary>
public enum OcclusionDistribution : byte {
    /// <summary>By the cosine of the angle from the normal, which is the occlusion a diffuse surface sees.</summary>
    Cosine,

    /// <summary>Evenly over the solid angle, which darkens grazing occluders more.</summary>
    Uniform
}

/// <summary>What an id map labels each source face with.</summary>
public enum BakeIdSource : byte {
    /// <summary>The face groups when somebody assigned more than one, and the connected shells otherwise.</summary>
    /// <remarks>
    ///     ⚠ <b>A file with one material is read as one assigned group</b>, so an id map of its
    ///     groups is one flat colour and every shell in it is lost. The shells are the only thing
    ///     on such a source that tells parts apart.
    /// </remarks>
    Automatic,

    /// <summary>The face groups as they are, guessed or assigned.</summary>
    Groups,

    /// <summary>The connected shells, whatever the groups say.</summary>
    Shells
}

/// <summary>How an id map turns labels into colours.</summary>
public enum IdPalette : byte {
    /// <summary>A hue per id stepped by the golden ratio, the same colour for the same id in every bake.</summary>
    Random,

    /// <summary>The ids that occur, spread evenly round the hue wheel in order.</summary>
    HueShift,

    /// <summary>The ids that occur, as evenly spaced greys from dark to white, with black kept for none.</summary>
    Grey
}

/// <summary>What a height map's stored range is.</summary>
public enum HeightRange : byte {
    /// <summary>The largest displacement the bake found, so the map uses its whole range.</summary>
    Measured,

    /// <summary>The longer of the two ray distances, which is the same for every bake made with them.</summary>
    RayDistance,

    /// <summary><see cref="BakeSettings.HeightScale" />, for maps that must agree with each other.</summary>
    Custom
}

/// <summary>What to bake, at what size, and how far to look for the source.</summary>
/// <remarks>
///     ⚠ <b><see cref="SearchRadius" /> is a fraction of the source's bounding-box diagonal and
///     never a distance.</b> A ray cage measured in metres is a claim about how big a model is, and
///     it is the same claim <see cref="ScaleSafe" /> exists to stop this library making — a bake
///     tuned on a character silently finds nothing on the same character exported in centimetres.
/// </remarks>
public sealed record BakeSettings {
    /// <summary>The atlas's edge length in texels. Required, because a gutter is counted in them.</summary>
    public required int Resolution { get; init; }

    /// <summary>Whether the source's face groups are colours packed as RGB, which an id map shows as they are.</summary>
    public bool GroupsAreColours { get; init; }

    /// <summary>How many texels of dilation surround each chart.</summary>
    /// <remarks>
    ///     ⚠ <b>A bake that stops at the chart boundary bleeds background at low mips.</b> Four is
    ///     the usual answer for a 2K atlas — enough for a trilinear tap plus two mip levels — and it
    ///     is the same number <see cref="Vixen.Geometry.Uv.PackSettings.Margin" /> defaults to, which
    ///     is not a coincidence: the gutter has to reach at least as far as the packer's spacing or
    ///     the two disagree about where a chart ends.
    /// </remarks>
    public int Gutter { get; init; } = 4;

    /// <summary>Which frame the normals come back in.</summary>
    public BakeSpace Space { get; init; } = BakeSpace.Tangent;

    /// <summary>How far a ray looks for the source in front of the target, in <see cref="Units" />.</summary>
    /// <remarks>
    ///     In front is outward, along the target's normal: a source standing proud of the target.
    ///     It is also how far a ray looks behind, unless <see cref="RearDistance" /> says otherwise.
    /// </remarks>
    public float SearchRadius { get; init; } = 0.05f;

    /// <summary>How far a ray looks behind the target, or null for <see cref="SearchRadius" />.</summary>
    /// <remarks>
    ///     ⚠ <b>Apart because the two sides are different questions.</b> A target modelled inside its
    ///     source wants a long front and a short rear, and one distance for both made the rear reach
    ///     through a thin part to the far side of it.
    /// </remarks>
    public float? RearDistance { get; init; }

    /// <summary>What <see cref="SearchRadius" />, <see cref="RearDistance" /> and the occlusion distances are in.</summary>
    public BakeUnits Units { get; init; } = BakeUnits.Relative;

    /// <summary>Whether a ray passes through a source face turned away from the target's own facing.</summary>
    /// <remarks>
    ///     ⚠ <b>The answer to a ray striking the inside of a neighbouring part.</b> A source face the
    ///     ray meets turned the wrong way is the back of something — the far wall of a thin plate, the
    ///     underside of a strap — and baking its normal is a dent where there is none.
    /// </remarks>
    public bool IgnoreBackfaces { get; init; }

    /// <summary>Which of the mesh maps to measure beyond the normal and the displacement.</summary>
    /// <remarks>
    ///     ⚠ <b>Empty by default, because three of them cast rays.</b> A caller after a normal map
    ///     gets one for what a normal map costs, and the two measurements that need a hemisphere are
    ///     spent only when something asked for them.
    /// </remarks>
    public MeshMaps Maps { get; init; } = MeshMaps.None;

    /// <summary>How many rays the hemisphere is estimated with, per texel.</summary>
    /// <remarks>
    ///     ⚠ <b>The estimator's error falls as the square root of this</b>, so it is the one setting
    ///     in this record where doubling the cost buys about forty percent. Sixty-four is a preview;
    ///     a shipping bake of a hero asset wants several hundred.
    /// </remarks>
    public int OcclusionSamples { get; init; } = 64;

    /// <summary>How far an occlusion ray reaches, in <see cref="Units" />.</summary>
    /// <remarks>
    ///     ⚠ <b>A fraction by default, for the reason <see cref="SearchRadius" /> is one</b> — and a
    ///     different fraction, because the two answer different questions. The search radius is how
    ///     far the cage strayed from the surface and is small; this is how far away an occluder still
    ///     counts and is a large part of the model. ⚠ <b><see cref="BakedMaps.Thickness" /> is
    ///     measured in it</b>: a depth of this or more reads one, which is why measuring the inside of
    ///     a closed shape wants it at or above the shape's widest part.
    /// </remarks>
    public float OcclusionRadius { get; init; } = 0.5f;

    /// <summary>The half-angle of the cone the occlusion rays fill, in degrees: ninety is the hemisphere.</summary>
    /// <remarks>
    ///     Narrower ignores occluders near the horizon, which keeps a large flat floor from going grey
    ///     beside every wall; the same rays answer the bent normal and thickness.
    /// </remarks>
    public float OcclusionSpread { get; init; } = 90f;

    /// <summary>How an occluder counts for less the further away it is.</summary>
    public OcclusionFalloff OcclusionFalloff { get; init; } = OcclusionFalloff.None;

    /// <summary>How the occlusion rays are spread over the cone.</summary>
    public OcclusionDistribution OcclusionDistribution { get; init; } = OcclusionDistribution.Cosine;

    /// <summary>How near an occluder may be and still not count, in <see cref="Units" />.</summary>
    /// <remarks>
    ///     A ray starts this far out, so a hit closer than it is ignored and anything beyond still
    ///     counts — which is what keeps a dense source's own neighbouring triangles from darkening it.
    ///     ⚠ <b>Less than <see cref="OcclusionRadius" /> when occlusion or the bent normal is baked</b>, or
    ///     <see cref="MapBaker.Bake(EditMesh, EditMesh, BakeSettings)" /> refuses it: a ray starting past its
    ///     reach sees nothing.
    /// </remarks>
    public float OcclusionMinimum { get; init; }

    /// <summary>Whether an occlusion ray passes through the back of a face rather than stopping at it.</summary>
    public bool OcclusionIgnoresBackfaces { get; init; }

    /// <summary>Whether only the texel's own part occludes it, rather than the whole source.</summary>
    /// <remarks>
    ///     ⚠ <b>Needs <see cref="Parts" />, and without them it is the whole source.</b> It is the
    ///     exploded bake for occlusion: a bolt's occlusion without the plate it sits on.
    /// </remarks>
    public bool OcclusionWithinPart { get; init; }

    /// <summary>Whether a ground plane under the source occludes as well, as if the model stood on a floor.</summary>
    /// <remarks>⚠ <b>Y is up.</b> The plane is at the source's lowest Y plus <see cref="GroundOffset" />.</remarks>
    public bool GroundPlane { get; init; }

    /// <summary>How far above the source's lowest point the ground plane is, in <see cref="Units" />.</summary>
    public float GroundOffset { get; init; }

    /// <summary>Whether thickness is divided by its largest value, so the thickest texel reads one.</summary>
    /// <remarks>
    ///     Without it, thickness is the mean depth through the surface as a fraction of
    ///     <see cref="OcclusionRadius" />, which is the same scale in every bake made with that radius.
    /// </remarks>
    public bool ThicknessNormalised { get; init; }

    /// <summary>What the id map labels source faces by.</summary>
    public BakeIdSource IdSource { get; init; } = BakeIdSource.Automatic;

    /// <summary>How the id map colours its labels, when they are not colours already.</summary>
    public IdPalette IdPalette { get; init; } = IdPalette.Random;

    /// <summary>What the height map's stored range is.</summary>
    public HeightRange HeightRange { get; init; } = HeightRange.Measured;

    /// <summary>The height map's range for <see cref="HeightRange.Custom" />, in <see cref="Units" />.</summary>
    public float HeightScale { get; init; }

    /// <summary>How many samples a texel is divided into along each side, so the total is the square.</summary>
    /// <remarks>
    ///     <para>
    ///         One is the texel's centre and what a bake has always done, and it aliases: a source
    ///         feature narrower than a texel is either struck or missed. Two costs four probes and is
    ///         the usual answer for a shipping map; four is for a map that is going to be read closely.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><see cref="OcclusionSamples" /> stays a count per texel.</b> The hemisphere set is
    ///         dealt out between the samples rather than cast again at each, so a texel's occlusion
    ///         costs the same at any setting and is estimated across its whole footprint. Supersampling
    ///         pays for the first ray, not for the two hundred and fifty-six behind it.
    ///     </para>
    /// </remarks>
    public int Supersampling { get; init; } = 1;

    /// <summary>A surface enclosing the target, which the rays start from, or null for none.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The same faces in the same order as the target, moved rather than rebuilt.</b> A
    ///         point of the target and the point of the cage with the same weights on the same face are
    ///         the two ends of one ray; a cage with another layout has no such pairing and is refused.
    ///     </para>
    ///     <para>
    ///         With a cage the ray runs from the cage through the target and on for
    ///         <see cref="SearchRadius" /> behind it, and the first thing it strikes wins. So an artist
    ///         decides what a ray can reach by moving the cage, which is the production answer to
    ///         geometry the nearer-of-two-hits rule picks wrongly: a strap lying close over a body.
    ///     </para>
    /// </remarks>
    public EditMesh? Cage { get; init; }

    /// <summary>How far rays lean from the smoothed direction towards the target's own normal.</summary>
    /// <remarks>
    ///     <para>
    ///         Zero, the default, follows the direction that is continuous across a hard edge, which is
    ///         what stops a seam there; see <see cref="MapBaker" />. ⚠ <b>The price is that rays near a
    ///         hard edge are slanted</b>, so a bolt sitting on a flat face next to one bakes sheared
    ///         towards the edge. One casts along the target's own normal, which keeps the bolt round
    ///         and brings the seam back. Between the two is the trade, and a map says where to make it.
    ///     </para>
    /// </remarks>
    public float Skew { get; init; }

    /// <summary>Where to apply <see cref="Skew" />, over the atlas, or null for everywhere.</summary>
    public BakeMask? SkewMap { get; init; }

    /// <summary>Which target faces may strike which source faces, or null for all of them.</summary>
    /// <remarks>
    ///     ⚠ <b>Occlusion ignores this on purpose.</b> Matching stops a neighbouring part's surface
    ///     being baked into a part's normal map, and a neighbouring part still shades it, so contact
    ///     shadows between parts stay in the occlusion map.
    /// </remarks>
    public BakeParts? Parts { get; init; }

    /// <summary>How many threads the casting may use, or zero for all of them.</summary>
    /// <remarks>
    ///     ⚠ <b>Not a quality setting.</b> Nothing about a texel's value depends on when it was reached,
    ///     so the output is byte-identical at any count, which the content hash a bake rests on needs.
    /// </remarks>
    public int Threads { get; init; }
}

/// <summary>One value per atlas texel, read nearest.</summary>
/// <param name="Resolution">The edge length, which need not be the bake's.</param>
/// <param name="Values">Row-major from <c>v = 0</c>, the image's top, as <see cref="BakedMaps" /> is, in <c>[0, 1]</c>.</param>
public sealed record BakeMask(int Resolution, IReadOnlyList<float> Values) {
    /// <summary>The value at an atlas coordinate.</summary>
    /// <param name="uv">The coordinate; anything outside the atlas reads its edge.</param>
    /// <returns>The value.</returns>
    public float At(Vector2 uv) {
        var x = Math.Clamp((int) (uv.X * Resolution), 0, Resolution - 1);
        var y = Math.Clamp((int) (uv.Y * Resolution), 0, Resolution - 1);

        return Values[(y * Resolution) + x];
    }
}

/// <summary>Which part each face of the source and of the target belongs to.</summary>
/// <param name="Source">A part per source face, or <c>-1</c> for no part.</param>
/// <param name="Target">A part per target face, or <c>-1</c> for one that may strike anything.</param>
/// <remarks>
///     <para>
///         <b>Exploding a mesh apart by hand is what this replaces.</b> Parts modelled close together
///         strike each other's rays, so the hand of a gun bakes the grip's bolts. Named parts need
///         nothing moved: a ray from a part strikes only the source of that part.
///     </para>
///     <para>
///         ⚠ <b>Numbers rather than names, because a name is a file's idea.</b> This assembly has no
///         file in it. <c>ByName</c> is the usual way of getting these.
///     </para>
/// </remarks>
public sealed record BakeParts(IReadOnlyList<int> Source, IReadOnlyList<int> Target) {
    static readonly string[] Suffixes = ["_high", "_low", "_hi", "_lo", "_hp", "_lp"];

    /// <summary>The name two parts have in common, once what says which end they are is gone.</summary>
    /// <param name="name">A mesh's name: <c>Bolt_high</c>, <c>bolt_low.001</c>.</param>
    /// <returns>The part's own name, lower case: <c>bolt</c>.</returns>
    public static string Key(string name) => Key(name, Suffixes);

    /// <summary>The name two parts have in common, once one of the given endings is gone.</summary>
    /// <param name="name">A mesh's name: <c>Bolt_high</c>, <c>bolt_low.001</c>.</param>
    /// <param name="suffixes">The endings that say which end a part is, compared without case.</param>
    /// <returns>The part's own name, lower case.</returns>
    /// <remarks>The longest ending that fits wins, so <c>_high</c> is taken before <c>_hi</c> would leave a <c>gh</c>.</remarks>
    public static string Key(string name, IReadOnlyList<string> suffixes) {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(suffixes);

        var key = name.Trim().ToLowerInvariant();
        var dot = key.LastIndexOf('.');

        // An exporter's copy number, which is not part of the name.
        if (dot > 0 && dot < key.Length - 1 && key[(dot + 1)..].All(char.IsAsciiDigit)) {
            key = key[..dot];
        }

        foreach (var suffix in suffixes.Select(static s => s.Trim().ToLowerInvariant()).OrderByDescending(static s => s.Length)) {
            if (suffix.Length > 0 && key.Length > suffix.Length && key.EndsWith(suffix, StringComparison.Ordinal)) {
                return key[..^suffix.Length];
            }
        }

        return key;
    }

    /// <summary>Numbers named meshes laid end to end in two merged meshes.</summary>
    /// <param name="source">Each source mesh's name and face count, in merge order.</param>
    /// <param name="target">Each target mesh's name and face count, in merge order.</param>
    /// <returns>The parts. A target mesh whose name no source shares is <c>-1</c>.</returns>
    public static BakeParts ByName(
        IReadOnlyList<(string Name, int Faces)> source,
        IReadOnlyList<(string Name, int Faces)> target
    ) => ByName(source, target, Suffixes, Suffixes);

    /// <summary>Numbers named meshes, with the endings each side's names carry.</summary>
    /// <param name="source">Each source mesh's name and face count, in merge order.</param>
    /// <param name="target">Each target mesh's name and face count, in merge order.</param>
    /// <param name="sourceSuffixes">What the source's names end in: <c>_high</c>.</param>
    /// <param name="targetSuffixes">What the target's names end in: <c>_low</c>.</param>
    /// <returns>The parts. A target mesh whose name no source shares is <c>-1</c>.</returns>
    /// <remarks>
    ///     ⚠ <b>Apart, because a studio's convention is a pair.</b> A source called <c>bolt_low</c> is
    ///     not the low end of anything, and stripping either ending from either side matched it.
    /// </remarks>
    public static BakeParts ByName(
        IReadOnlyList<(string Name, int Faces)> source,
        IReadOnlyList<(string Name, int Faces)> target,
        IReadOnlyList<string> sourceSuffixes,
        IReadOnlyList<string> targetSuffixes
    ) {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(sourceSuffixes);
        ArgumentNullException.ThrowIfNull(targetSuffixes);

        Dictionary<string, int> numbers = new(StringComparer.Ordinal);
        List<int> sources = [];
        List<int> targets = [];

        foreach (var (name, faces) in source) {
            var key = Key(name, sourceSuffixes);

            if (!numbers.TryGetValue(key, out var number)) {
                number = numbers.Count;
                numbers.Add(key, number);
            }

            sources.AddRange(Enumerable.Repeat(number, faces));
        }

        foreach (var (name, faces) in target) {
            targets.AddRange(Enumerable.Repeat(numbers.GetValueOrDefault(Key(name, targetSuffixes), -1), faces));
        }

        return new(sources, targets);
    }
}

/// <summary>A normal map and a displacement map, as pixels, with no file anywhere in sight.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A bake returns pixels and writing them is the caller's job.</b> <c>Core/</c> is
///         under the virtual-path rule — no <c>System.IO.Path</c>, no <c>File</c> — so an asset
///         compiler, a CLI and an editor each write these where their own conventions say, and none
///         of that reaches this assembly.
///     </para>
///     <para>
///         ⚠ <b><see cref="Displacement" /> is in the model's own units and is deliberately not
///         normalised.</b> A displacement map quantized into <c>[0, 1]</c> needs a scale stored
///         beside it or it means nothing, and half of the ways that goes wrong are the scale and the
///         pixels being written by different code. <see cref="DisplacementRange" /> is what a caller
///         quantizes with, and it is measured rather than assumed.
///     </para>
/// </remarks>
public sealed record BakedMaps {
    /// <summary>The edge length, in texels. Both maps are this square.</summary>
    public required int Resolution { get; init; }

    /// <summary>One normal per texel, row-major from <c>v = 0</c>, which is the image's top row, in <see cref="Space" />.</summary>
    public required IReadOnlyList<Vector3> Normals { get; init; }

    /// <summary>One signed distance per texel, in the model's own units.</summary>
    /// <remarks>Positive is outward along the output's normal — the source stands proud of the cage.</remarks>
    public required IReadOnlyList<float> Displacement { get; init; }

    /// <summary>Whether a texel is chart content rather than gutter or background.</summary>
    /// <remarks>
    ///     ⚠ <b>The dilation reads this and never writes it</b>, which is what stops one chart's
    ///     gutter from overwriting the chart next to it in the atlas.
    /// </remarks>
    public required IReadOnlyList<bool> Coverage { get; init; }

    /// <summary>Which frame <see cref="Normals" /> is in.</summary>
    public required BakeSpace Space { get; init; }

    /// <summary>How many texels the charts covered.</summary>
    public required int Covered { get; init; }

    /// <summary>How many the gutter filled afterwards.</summary>
    public required int Dilated { get; init; }

    /// <summary>How many covered texels found no source at all within the search radius.</summary>
    /// <remarks>
    ///     ⚠ Worth reading. A handful is a thin feature the output cut through; a large fraction
    ///     means <see cref="BakeSettings.SearchRadius" /> is smaller than the deviation the remesh
    ///     actually produced, and the map is mostly the fallback rather than the bake.
    /// </remarks>
    public required int Missed { get; init; }

    /// <summary>The largest absolute displacement, which is what a caller quantizes with.</summary>
    public required float DisplacementRange { get; init; }

    /// <summary>The unoccluded fraction of the hemisphere per texel, or null if it was not asked for.</summary>
    /// <remarks>
    ///     One is open sky and zero is sealed. ⚠ <b>Measured at the <i>source</i>'s surface point and
    ///     about the <i>source</i>'s normal</b>, not at the cage's — the whole reason to bake a mesh
    ///     map is that the cage does not have the geometry that does the occluding.
    /// </remarks>
    public IReadOnlyList<float>? AmbientOcclusion { get; init; }

    /// <summary>The average unoccluded direction per texel, or null if it was not asked for.</summary>
    /// <remarks>
    ///     ⚠ <b>In <see cref="Space" />, the same frame <see cref="Normals" /> is in</b>, so a
    ///     tangent-space bake's bent normal is comparable with its normal map texel for texel. It
    ///     comes off the same rays as <see cref="AmbientOcclusion" /> and costs nothing beside it;
    ///     where every ray was blocked it falls back to the surface normal, because a zero vector is
    ///     not a direction.
    /// </remarks>
    public IReadOnlyList<Vector3>? BentNormal { get; init; }

    /// <summary>Mean curvature per texel, in reciprocal model units, or null if it was not asked for.</summary>
    /// <remarks>
    ///     ⚠ <b>A sphere of radius <i>r</i> reads <c>1/r</c>, so this is a length⁻¹ and it moves with
    ///     the model's scale.</b> Positive is convex along the source's own normal — an edge —
    ///     and negative is a crease. <see cref="CurvatureRange" /> is what a caller quantizes with,
    ///     exactly as <see cref="DisplacementRange" /> is. ⚠ An open rim reads zero rather than a
    ///     number: the operator needs a closed one-ring and the missing half of one is not a
    ///     measurement.
    /// </remarks>
    public IReadOnlyList<float>? Curvature { get; init; }

    /// <summary>How deep the surface goes per texel, or null if it was not asked for.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The mean depth along the same hemisphere turned through the surface, over
    ///         <see cref="BakeSettings.OcclusionRadius" /></b>: <c>4r/3</c> of the reach inside a sphere
    ///         of radius <i>r</i>, and zero on a single sheet with nothing behind it, because a ray that
    ///         finds no far wall counts nothing.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It was the fraction of inward rays that hit anything</b>, which is one inside every
    ///         closed shape whatever its size — and a real model is all closed shapes, so the map was
    ///         white. <see cref="BakeSettings.ThicknessNormalised" /> divides it by its largest value.
    ///     </para>
    /// </remarks>
    public IReadOnlyList<float>? Thickness { get; init; }

    /// <summary>The surface point per texel, normalised into the source's box, or null.</summary>
    /// <remarks>
    ///     Each axis runs <c>[0, 1]</c> across the source's bounding box. ⚠ An axis with no extent —
    ///     the third one of a flat source — reads zero, because every point on it <i>is</i> the
    ///     minimum and there is nothing to normalise.
    /// </remarks>
    public IReadOnlyList<Vector3>? Position { get; init; }

    /// <summary>The source's normal per texel, in the source's own space, or null.</summary>
    /// <remarks>
    ///     Unrotated and independent of <see cref="Space" />, which is the point: it is the same map
    ///     whether the normal map beside it came back in tangent space or object space.
    /// </remarks>
    public IReadOnlyList<Vector3>? WorldNormal { get; init; }

    /// <summary>The source's material or island index per texel, <c>-1</c> where there is none, or null.</summary>
    /// <remarks>
    ///     ⚠ <b>The face group only where somebody assigned it, and the connected shell otherwise.</b>
    ///     A group id off <c>EditMesh.Regroup</c> is a coplanarity guess, which on a generated or
    ///     sculpted surface is one group per triangle — baked as ids that is confetti, and it is what
    ///     <see cref="Warnings" /> says when a bake had to fall back. See <c>MapBaker.Labels</c>.
    ///     <br />
    ///     ⚠ <b>Nearest, everywhere, including through the gutter.</b> An id is a label and not a
    ///     quantity: dilation copies a neighbour's id rather than averaging four of them, because the
    ///     average of ids 0 and 2 is id 1, which is a material that does not exist — and every
    ///     generator keyed off the id map then grows a hairline of it along every chart border. The
    ///     channel is an <c>int</c> rather than a colour for the same reason; <see cref="MapBaker.IdColour" />
    ///     turns one into a colour at the point the pixels are written, where no filter can reach it.
    /// </remarks>
    public IReadOnlyList<int>? Ids { get; init; }

    /// <summary>Whether <see cref="Ids" /> are colours packed as RGB rather than labels.</summary>
    /// <remarks>
    ///     ⚠ Only when the ids are the groups. Shells numbered 0, 1, 2 read as packed colours are three
    ///     shades of black.
    /// </remarks>
    public bool IdsAreColours { get; init; }

    /// <summary>How labels are coloured when <see cref="Ids" /> are not colours already.</summary>
    public IdPalette IdPalette { get; init; }

    /// <summary>The largest absolute curvature, which is what a caller quantizes with.</summary>
    public float CurvatureRange { get; init; }

    /// <summary>What could not be baked.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>docs/plan/41 § D12's bake: the output's normal, cast at the source, into the atlas.</summary>
/// <remarks>
///     <para>
///         <b>This is where the pipeline's arithmetic closes.</b> § D12: a four-million-triangle
///         generated blob is not expensive because it is four million triangles, it is expensive
///         because it is four million triangles <i>of noise with no UVs</i>. Five thousand quads plus
///         a 2K normal map is smaller, looks better under a moving light, subdivides and can be
///         rigged. Retopology without baking is a downgrade; retopology with baking is the pipeline.
///     </para>
///     <para>
///         ⚠ <b>The ray is cast both ways and the nearer hit wins.</b> Casting only outward loses
///         every part of the source the output enclosed, which on a smoothed remesh of a noisy
///         surface is about half of it; casting only inward loses the other half. A cage mesh is the
///         production answer to the ambiguity and it is a thing an artist authors — the nearer of two
///         opposed hits is the answer available to a content build with nobody watching.
///     </para>
///     <para>
///         ⚠ <b>The ray follows the direction averaged over every corner on a position, not the
///         corner's own normal.</b> The two are the same on a smooth surface. At a hard edge the
///         corner normals disagree, so rays cast along them leave a wedge of the source nothing
///         strikes and two charts that see different things at the same point, which is the seam a
///         hard edge shows. The averaged direction is continuous there: both sides of the edge cast
///         the same ray and find the same surface. The tangent frame still uses the corner's own
///         normal, because that is what will shade the texel. <see cref="BakeSettings.Skew" /> is the
///         way back, for the detail this slants.
///     </para>
///     <para>
///         ⚠ <b>A texel's value is a function of the texel alone.</b> The chart triangles claim texels
///         first, the lowest triangle winning whatever the order they ran in; then each row is baked on
///         whichever thread reaches it, into its own texels, and the maximums are merged afterwards.
///         So the atlas does not depend on the thread count, which a content hash needs.
///     </para>
///     <para>
///         ⚠ <b>A texel that finds nothing falls back to the closest point rather than to a
///         default.</b> A default normal in the middle of a chart is a flat patch that reads as a
///         modelling error; the closest point is at worst the right answer measured from slightly the
///         wrong place, and <see cref="BakedMaps.Missed" /> says how often it happened.
///     </para>
///     <para>
///         ⚠ <b>Content is rasterized before anything is dilated, in two full passes.</b> Two charts
///         whose texels abut in the atlas is the common case, not the exotic one — the packer's whole
///         job is to make it common — and a dilation interleaved with the rasterization would let
///         whichever chart was drawn first bleed its gutter over the second chart's content. The
///         gutter only ever writes where <see cref="BakedMaps.Coverage" /> is false.
///     </para>
/// </remarks>
public static class MapBaker {
    /// <summary>The most a texel is divided along each side.</summary>
    const int MaxSupersampling = 8;

    /// <summary>Bakes a normal and a displacement map from the source onto the output's atlas.</summary>
    /// <param name="source">The high-resolution surface. Read, never modified.</param>
    /// <param name="target">The remeshed output. Must carry texture coordinates.</param>
    /// <param name="settings">The size, the gutter and the search radius.</param>
    /// <returns>The pixels, and what was measured about them.</returns>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     The resolution is not positive, the gutter is negative, or occlusion is baked with a minimum occluder
    ///     distance at or past the occlusion radius.
    /// </exception>
    /// <exception cref="ArgumentException">The target has no texture-coordinate layer to bake into.</exception>
    /// <remarks>
    ///     ⚠ <b>This overload cannot be stopped and says nothing while it runs</b>, which for a 4K
    ///     atlas at several hundred rays a texel is minutes. Anything with a Cancel button wants the
    ///     one below it.
    /// </remarks>
    public static BakedMaps Bake(EditMesh source, EditMesh target, BakeSettings settings) =>
        Bake(source, target, settings, progress: null, CancellationToken.None);

    /// <summary>Bakes a normal and a displacement map, reporting progress and stopping when asked.</summary>
    /// <param name="source">The high-resolution surface. Read, never modified.</param>
    /// <param name="target">The remeshed output. Must carry texture coordinates.</param>
    /// <param name="settings">The size, the gutter and the search radius.</param>
    /// <param name="progress">Told what fraction of the casting is done, or null.</param>
    /// <param name="cancellationToken">Checked once per atlas row.</param>
    /// <returns>The pixels, and what was measured about them.</returns>
    /// <exception cref="ArgumentNullException">Any argument but <paramref name="progress" /> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     The resolution is not positive, the gutter is negative, or occlusion is baked with a minimum occluder
    ///     distance at or past the occlusion radius.
    /// </exception>
    /// <exception cref="ArgumentException">The target has no texture-coordinate layer to bake into.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken" /> was signalled.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The casting is the whole of the cost and it was one uninterruptible call</b> —
    ///         <a href="https://github.com/Rikarin/Vixen/issues/700">#700</a>. An editor Cancel that
    ///         is read on either side of a call it cannot enter is a button that does nothing for the
    ///         whole of the minutes somebody presses it in, which is worse than no button: it teaches
    ///         that the task centre's controls are decorative.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An atlas row is the granularity.</b> Per texel is a branch inside the hemisphere
    ///         loop; per chart triangle is not a bound at all, because a single quad can cover the whole
    ///         atlas. A row is one thread's few milliseconds at most resolutions, and the threads
    ///         already running finish the row they are in, so a cancel lands within a row or so.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><paramref name="progress" /> is a fraction of the rows holding chart content, and is
    ///         called from whichever thread finished one, one call at a time and never going back.</b>
    ///         An atlas row with nothing on it costs nothing, so counting it would be a bar that jumps
    ///         through the gutters.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The dilation is deliberately outside the reported fraction and cannot be
    ///         cancelled.</b> It is a fixed number of passes over the atlas with no rays in it, so it
    ///         is the part a caller is never waiting on — and a half-dilated buffer handed back as a
    ///         finished bake is a set of maps whose gutters lie.
    ///     </para>
    /// </remarks>
    public static BakedMaps Bake(
        EditMesh source,
        EditMesh target,
        BakeSettings settings,
        Action<float>? progress,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(settings.Resolution);
        ArgumentOutOfRangeException.ThrowIfNegative(settings.Gutter);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(settings.OcclusionSamples);
        ArgumentOutOfRangeException.ThrowIfNegative(settings.OcclusionRadius);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(settings.Supersampling);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(settings.Supersampling, MaxSupersampling);
        ArgumentOutOfRangeException.ThrowIfNegative(settings.Threads);
        ArgumentOutOfRangeException.ThrowIfNegative(settings.Skew);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(settings.Skew, 1f);
        ArgumentOutOfRangeException.ThrowIfNegative(settings.RearDistance ?? 0f);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(settings.OcclusionSpread);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(settings.OcclusionSpread, 90f);
        ArgumentOutOfRangeException.ThrowIfNegative(settings.OcclusionMinimum);
        ArgumentOutOfRangeException.ThrowIfNegative(settings.HeightScale);

        // A ray that starts at or past its own reach sees nothing, so the occlusion would be white and the bent
        // normal the normal, with no word of why. Thickness reads the radius alone.
        if ((settings.Maps & (MeshMaps.AmbientOcclusion | MeshMaps.BentNormal)) != 0
            && settings.OcclusionMinimum >= settings.OcclusionRadius) {
            throw new ArgumentOutOfRangeException(
                nameof(settings),
                settings.OcclusionMinimum,
                "The minimum occluder distance must be less than the occlusion radius, or every ray starts past its reach."
            );
        }

        if (target.TexCoords.Length != target.CornerCount) {
            throw new ArgumentException(
                "The target has no texture-coordinate layer, so there is no atlas to bake into.",
                nameof(target)
            );
        }

        if (settings.Cage is { } cage && !SameLayout(cage, target)) {
            throw new ArgumentException(
                "The cage does not have the target's faces, so its points have no partner on the target.",
                nameof(settings)
            );
        }

        if (settings.Parts is { } parts
            && (parts.Source.Count != source.FaceCount || parts.Target.Count != target.FaceCount)) {
            throw new ArgumentException(
                "The parts must name one part per source face and one per target face.",
                nameof(settings)
            );
        }

        var resolution = settings.Resolution;
        var buffers = new BakeBuffers(settings);
        var warnings = new List<string>();

        var surface = SourceSurface.From(source);

        if (surface.TriangleCount == 0) {
            warnings.Add("The source triangulated to nothing, so there was nothing to bake.");

            return Assemble(settings, buffers, 0, warnings);
        }

        if (buffers.Ids is not null) {
            (buffers.Labels, buffers.LabelsAreColours) = Labels(source, settings, warnings);
        }

        // ⚠ One length for every distance in the settings, so the units are decided in one place and a
        // distance cannot be read as a fraction in one call and as a length in the next.
        var unit = settings.Units is BakeUnits.Absolute ? 1f : surface.Diagonal;
        var radius = unit * MathF.Max(settings.SearchRadius, 0f);
        var rear = unit * MathF.Max(settings.RearDistance ?? settings.SearchRadius, 0f);

        if (radius <= 0f && rear <= 0f) {
            warnings.Add("The source has no extent, so the search radius is zero and every ray misses.");
        }

        // ⚠ Once for the whole source, not once per texel. The Laplacian is a property of the source
        // mesh and a million texels asking it the same question is the shape of bug § D12's "the
        // expensive half is already built" is about — the tree is built once for the same reason.
        var curvature = settings.Maps.HasFlag(MeshMaps.Curvature) ? MeanCurvature.Build(surface) : null;
        var rays = BakeRays.Build(surface, source, target, settings.Parts, warnings);
        var bake = new TexelBake(surface, rays, settings, new(radius, rear, unit), curvature, buffers);
        var options = new ParallelOptions { MaxDegreeOfParallelism = settings.Threads > 0 ? settings.Threads : -1 };
        var charts = BakeCharts.Build(target, settings, options, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        buffers.Covered = charts.ClaimCoverage(buffers.Coverage);

        var cast = new Casting(progress, charts.RowsWithContent(), cancellationToken);

        Parallel.For(0, resolution, options, (row, loop) => {
            if (cancellationToken.IsCancellationRequested) {
                loop.Stop();

                return;
            }

            if (charts.RowIsEmpty(row)) {
                return;
            }

            bake.Row(row, charts);
            cast.Row();
        });

        cancellationToken.ThrowIfCancellationRequested();
        cast.Done();

        // ⚠ Before the dilation, so the gutter grows from the normalised values and agrees with them.
        if (settings.ThicknessNormalised && buffers.Thickness is { } thickness) {
            Normalise(thickness, buffers.Coverage);
        }

        var dilated = Dilate(settings, buffers, options);

        if (buffers.Covered == 0) {
            warnings.Add("No chart covered any texel — the target's coordinates are outside the unit square.");
        }

        buffers.DisplacementRange = settings.HeightRange switch {
            HeightRange.RayDistance => MathF.Max(radius, rear),
            HeightRange.Custom when settings.HeightScale > 0f => settings.HeightScale * unit,
            _ => buffers.DisplacementRange
        };

        return Assemble(settings, buffers, dilated, warnings);
    }

    /// <summary>Divides a channel by its largest covered value, so that one reads one.</summary>
    /// <remarks>A channel that is zero everywhere stays zero: there is nothing to scale and no ratio to keep.</remarks>
    static void Normalise(float[] channel, bool[] coverage) {
        var largest = 0f;

        for (var index = 0; index < channel.Length; index++) {
            if (coverage[index]) {
                largest = MathF.Max(largest, channel[index]);
            }
        }

        if (largest <= 0f) {
            return;
        }

        for (var index = 0; index < channel.Length; index++) {
            channel[index] /= largest;
        }
    }

    /// <summary>Whether two meshes have the same faces in the same order.</summary>
    static bool SameLayout(EditMesh one, EditMesh other) {
        if (one.FaceCount != other.FaceCount || one.CornerCount != other.CornerCount) {
            return false;
        }

        for (var face = 0; face < one.FaceCount; face++) {
            if (one.Faces[face].Count != other.Faces[face].Count) {
                return false;
            }
        }

        return true;
    }

    /// <summary>Which id each source face bakes as: the group somebody assigned, or its shell.</summary>
    /// <param name="source">The mesh being baked from.</param>
    /// <param name="settings">Which source the ids come from, and whether groups are colours.</param>
    /// <param name="warnings">What the bake could not do, appended to when the groups are a guess.</param>
    /// <returns>An id per face, and whether the ids are packed colours.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A face group is a material boundary only where somebody assigned it, and reading it
    ///         as one otherwise is the § D12 defect at its loudest.</b> <c>EditMesh.FromTriangles</c>
    ///         ends in <c>Regroup</c>, whose groups are coplanar connected components — on a generated
    ///         or sculpted blob almost no two adjacent triangles are within half a degree, so every
    ///         triangle is its own group and 25 439 of them measured 13 965. Baked straight, an id map
    ///         of that is per-triangle confetti that <see cref="IdColour" /> paints in as many hues,
    ///         and nothing about it fails: it looks like an id map. <c>FeatureDetector</c>,
    ///         <c>Charter</c> and <c>SeamGraph</c> all gate on <see cref="MeshGroupSource" /> already;
    ///         this was the one consumer that did not.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Shells rather than a refusal, because § D12 asks for "the source's material
    ///         <i>or island</i> index" and the island is the half that survives a guess.</b> A
    ///         connected component is a fact about the mesh whatever its groups mean — two props in
    ///         one file are two ids, and one closed blob is one id, which is the honest answer for a
    ///         surface with no material boundaries on it rather than a hole in the output. A caller
    ///         that knows the real assignment sets <c>EditMesh.GroupSource</c> and gets its own ids.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And one assigned group is no more use than a guessed one.</b> A model file's reader
    ///         assigns every face its material, so a prop with one material arrives as one assigned
    ///         group, and an id map of it was a single flat colour with every shell in it lost.
    ///         <see cref="BakeIdSource.Automatic" /> reads that as the shells; asking for
    ///         <see cref="BakeIdSource.Groups" /> still gets the one colour.
    ///     </para>
    /// </remarks>
    static (int[] Labels, bool Colours) Labels(EditMesh source, BakeSettings settings, List<string> warnings) {
        var labels = new int[source.FaceCount];
        var assigned = source.GroupSource is MeshGroupSource.Assigned;
        var groups = settings.IdSource switch {
            BakeIdSource.Groups => true,
            BakeIdSource.Shells => false,
            _ => assigned && Distinct(source) > 1
        };

        if (groups) {
            for (var face = 0; face < labels.Length; face++) {
                labels[face] = source.Faces[face].Group;
            }

            return (labels, settings.GroupsAreColours);
        }

        List<int> shells = [];
        var count = MeshCollision.Shells(source, shells);

        shells.CopyTo(labels);

        if (settings.IdSource is BakeIdSource.Automatic && !assigned) {
            warnings.Add(
                $"The source's face groups came from EditMesh.Regroup's coplanarity guess rather than from "
                + $"an assignment, so they are not material boundaries — on a faceted surface they are one "
                + $"group per triangle. The id map holds the {count} connected shell(s) instead. Set "
                + "EditMesh.GroupSource to Assigned on a mesh whose groups are materials somebody chose."
            );
        }

        return (labels, false);
    }

    /// <summary>How many different groups a mesh's faces carry, stopping at two.</summary>
    static int Distinct(EditMesh mesh) {
        for (var face = 1; face < mesh.FaceCount; face++) {
            if (mesh.Faces[face].Group != mesh.Faces[0].Group) {
                return 2;
            }
        }

        return Math.Min(mesh.FaceCount, 1);
    }

    /// <summary>The colour an id packed as RGB stands for, in <c>[0, 1]³</c>, black for <c>-1</c>.</summary>
    /// <param name="id">A packed colour, or <c>-1</c> for a texel with no source.</param>
    /// <returns>The colour.</returns>
    public static Vector3 PackedColour(int id) =>
        id < 0 ? Vector3.Zero : new Vector3((id >> 16) & 0xFF, (id >> 8) & 0xFF, id & 0xFF) / 255f;

    /// <summary>A distinct colour for an id, for the caller that has to write pixels rather than ints.</summary>
    /// <param name="id">A face group, or <c>-1</c> for a texel with no source.</param>
    /// <returns>A colour in <c>[0, 1]³</c>, black for <c>-1</c>.</returns>
    /// <remarks>
    ///     ⚠ <b>Distinct rather than pretty, and a pure function of the id.</b> Hues are spaced by
    ///     the golden ratio, which is what keeps the first dozen ids far apart on the wheel instead
    ///     of the first three being three shades of red; the saturation and value are fixed so that
    ///     two ids never differ only in brightness, which is the difference a mask threshold cannot
    ///     see. ⚠ <b>Nothing interpolates this.</b> It is applied to a texel's id after the bake,
    ///     never to a blend of two of them — the map that gets filtered is the one that grows a
    ///     fourth material along every border.
    /// </remarks>
    public static Vector3 IdColour(int id) => id < 0 ? Vector3.Zero : Hue((id * 0.6180339887f) % 1f);

    /// <summary>Colours every texel's id as a palette says, black where there is none.</summary>
    /// <param name="ids">An id per texel, <c>-1</c> for none.</param>
    /// <param name="colours">Whether the ids are packed colours already, which every palette leaves alone.</param>
    /// <param name="palette">How labels become colours.</param>
    /// <returns>A colour per texel, in <c>[0, 1]³</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="ids" /> is null.</exception>
    /// <remarks>
    ///     ⚠ <b><see cref="IdPalette.HueShift" /> and <see cref="IdPalette.Grey" /> rank the ids that
    ///     occur, so they depend on the whole map and not on the id alone.</b> That is the point of
    ///     them — three ids get three far-apart colours rather than wherever the golden ratio puts
    ///     them — and the reason <see cref="IdColour" /> stays the default: the same id is the same
    ///     colour in every bake only there.
    /// </remarks>
    public static Vector3[] IdColours(IReadOnlyList<int> ids, bool colours, IdPalette palette) {
        ArgumentNullException.ThrowIfNull(ids);

        var made = new Vector3[ids.Count];

        if (colours || palette is IdPalette.Random) {
            for (var index = 0; index < made.Length; index++) {
                made[index] = colours ? PackedColour(ids[index]) : IdColour(ids[index]);
            }

            return made;
        }

        var ranks = ids.Where(static id => id >= 0).Distinct().Order().Select(static (id, rank) => (id, rank))
            .ToDictionary(static pair => pair.id, static pair => pair.rank);
        var count = Math.Max(ranks.Count, 1);

        for (var index = 0; index < made.Length; index++) {
            if (!ranks.TryGetValue(ids[index], out var rank)) {
                continue;
            }

            made[index] = palette is IdPalette.Grey
                ? new Vector3((rank + 1f) / count)
                : Hue((float) rank / count);
        }

        return made;
    }

    /// <summary>A bright, well-saturated colour at a point round the hue wheel.</summary>
    /// <param name="turn">Where on the wheel, in turns, <c>[0, 1)</c>.</param>
    static Vector3 Hue(float turn) {
        var hue = turn * 6f;
        var sector = (int) hue;
        var fraction = hue - sector;

        const float value = 0.95f;
        const float low = value * (1f - 0.65f);

        var rising = low + ((value - low) * fraction);
        var falling = value - ((value - low) * fraction);

        return sector switch {
            0 => new(value, rising, low),
            1 => new(falling, value, low),
            2 => new(low, value, rising),
            3 => new(low, falling, value),
            4 => new(rising, low, value),
            _ => new(value, low, falling)
        };
    }

    /// <summary>How far through the casting the bake is, and whether anyone is listening.</summary>
    /// <param name="progress">Told the fraction, or null.</param>
    /// <param name="rows">How many rows the whole pass will walk.</param>
    /// <param name="cancellationToken">What stops it.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Reported on a step rather than on every row.</b> A bar is two hundred pixels wide, so
    ///         all but one report in every few is a callback nobody could see the effect of.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An <see cref="Action{T}" /> rather than an <see cref="IProgress{T}" />, which is
    ///         the opposite of the usual advice and is right here.</b> <c>Progress&lt;T&gt;</c>
    ///         captures a synchronisation context at construction and <i>posts</i> to it — so the
    ///         reports arrive out of band, possibly out of order, and possibly after the bake has
    ///         returned. This callback runs before the row's thread moves on, under a lock, so a caller
    ///         reading a counter sees it rise.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Nothing is reported once the bake is cancelled</b>, so a cancelled bake's last report
    ///         is near the point it stopped however many threads were still finishing a row.
    ///     </para>
    /// </remarks>
    sealed class Casting(Action<float>? progress, long rows, CancellationToken cancellationToken) {
        readonly Lock reporting = new();
        long done;
        float said = -1f;

        /// <summary>Counts a finished row.</summary>
        public void Row() {
            lock (reporting) {
                done++;

                if (progress is null || rows <= 0 || cancellationToken.IsCancellationRequested) {
                    return;
                }

                var fraction = MathF.Min((float) ((double) done / rows), 1f);

                if (fraction - said < Step) {
                    return;
                }

                said = fraction;
                progress(fraction);
            }
        }

        /// <summary>Says the casting is over, whatever the last step reported.</summary>
        /// <remarks>
        ///     ⚠ <b>Because the step means the last row usually reports nothing.</b> A bar that stops
        ///     at 0.996 and waits for the dilation is a bar somebody reads as a hang.
        /// </remarks>
        public void Done() => progress?.Invoke(1f);

        /// <summary>How much the fraction must move before it is worth saying again.</summary>
        const float Step = 0.005f;
    }

    /// <summary>Grows the charts outward into the background, never over another chart's content.</summary>
    /// <remarks>
    ///     <para>
    ///         <b><c>ImpostorBake</c> solves the same problem as a compute shader and the idea
    ///         transfers even though the code does not</b> — it is one layer above this assembly and
    ///         it needs a device. Rounds of four-neighbour flood, each round reading what the last one
    ///         wrote, which is a jump flood's cheap cousin and is exact for a distance of
    ///         <see cref="BakeSettings.Gutter" /> texels.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>Coverage</c> is read and never written, so a chart's gutter cannot overwrite
    ///         the chart beside it.</b> The filled set is a second array; a texel joins it and never
    ///         joins <c>Coverage</c>, so the next round can spread from it and the content pass's
    ///         answer stays authoritative.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Each round commits after it finishes, not as it goes.</b> Writing in place would
    ///         let a texel filled early in the scan seed one later in the same round, so the gutter
    ///         would reach further to the right and upward than to the left and downward — a
    ///         directional bias that shows up as a lopsided halo at low mips.
    ///     </para>
    /// </remarks>
    static int Dilate(BakeSettings settings, BakeBuffers buffers, ParallelOptions options) {
        var resolution = settings.Resolution;
        var filled = (bool[]) buffers.Coverage.Clone();
        var next = (bool[]) filled.Clone();
        var total = 0;

        for (var round = 0; round < settings.Gutter; round++) {
            var grown = 0;

            // ⚠ A row writes only texels `filled` says are empty and reads only texels it says are
            // full, so rows cannot see each other's writes and may run in any order.
            Parallel.For(0, resolution, options, y => {
                Span<int> offsets = stackalloc int[4];
                var added = 0;

                for (var x = 0; x < resolution; x++) {
                    var index = (y * resolution) + x;

                    if (filled[index]) {
                        continue;
                    }

                    offsets[0] = x > 0 ? index - 1 : -1;
                    offsets[1] = x + 1 < resolution ? index + 1 : -1;
                    offsets[2] = y > 0 ? index - resolution : -1;
                    offsets[3] = y + 1 < resolution ? index + resolution : -1;

                    var normal = Vector3.Zero;
                    var height = 0f;
                    var open = 0f;
                    var unoccluded = Vector3.Zero;
                    var curve = 0f;
                    var inside = 0f;
                    var position = Vector3.Zero;
                    var world = Vector3.Zero;
                    var found = 0;

                    foreach (var neighbour in offsets) {
                        if (neighbour < 0 || !filled[neighbour]) {
                            continue;
                        }

                        // ⚠ Nearest, and only for this channel. An id is a label: the mean of ids 0
                        // and 2 is id 1, a material that exists nowhere in the source, and every
                        // generator keyed off the map then grows a hairline of it along every chart
                        // border. The first filled neighbour in a fixed order is a real id and is
                        // the same id on every run — which averaging is not, and which "whichever
                        // the scan happened to reach" would not be either.
                        if (found == 0 && buffers.Ids is { } ids) {
                            ids[index] = ids[neighbour];
                        }

                        normal += buffers.Normals[neighbour];
                        height += buffers.Displacement[neighbour];
                        found++;

                        if (buffers.Occlusion is { } occlusion) {
                            open += occlusion[neighbour];
                        }

                        if (buffers.Bent is { } average) {
                            unoccluded += average[neighbour];
                        }

                        if (buffers.Curvature is { } curvature) {
                            curve += curvature[neighbour];
                        }

                        if (buffers.Thickness is { } thickness) {
                            inside += thickness[neighbour];
                        }

                        if (buffers.Position is { } points) {
                            position += points[neighbour];
                        }

                        if (buffers.WorldNormal is { } normals) {
                            world += normals[neighbour];
                        }
                    }

                    if (found == 0) {
                        continue;
                    }

                    // ⚠ The value is written now and the texel is committed at the end of the round.
                    // The scan reads `filled` and never the values, so an early write cannot seed a
                    // later texel in the same round — and it is the flag, not the write, that would
                    // let the gutter reach further right and upward than left and downward.
                    buffers.Normals[index] = Unit(normal / found);
                    buffers.Displacement[index] = height / found;

                    Write(buffers.Occlusion, index, open / found);
                    Write(buffers.Curvature, index, curve / found);
                    Write(buffers.Thickness, index, inside / found);
                    Write(buffers.Position, index, position / found);
                    Write(buffers.Bent, index, Unit(unoccluded / found));
                    Write(buffers.WorldNormal, index, Unit(world / found));

                    next[index] = true;
                    added++;
                }

                Interlocked.Add(ref grown, added);
            });

            total += grown;
            next.CopyTo(filled, 0);
        }

        return total;
    }

    /// <summary>Writes into a channel that may not have been asked for.</summary>
    static void Write<T>(T[]? channel, int index, T value) {
        if (channel is not null) {
            channel[index] = value;
        }
    }

    /// <summary>A direction, or the sum itself when four neighbours cancelled and left nothing.</summary>
    static Vector3 Unit(Vector3 value) {
        var unit = ScaleSafe.Unit(value);

        return unit.LengthSquared() > 0f ? unit : value;
    }

    /// <summary>The buffers as a result, including a bake that found nothing to do.</summary>
    static BakedMaps Assemble(BakeSettings settings, BakeBuffers buffers, int dilated, List<string> warnings) =>
        new() {
            Resolution = settings.Resolution,
            Normals = buffers.Normals,
            Displacement = buffers.Displacement,
            Coverage = buffers.Coverage,
            Space = settings.Space,
            Covered = buffers.Covered,
            Dilated = dilated,
            Missed = buffers.Missed,
            DisplacementRange = buffers.DisplacementRange,
            AmbientOcclusion = buffers.Occlusion,
            BentNormal = buffers.Bent,
            Curvature = buffers.Curvature,
            Thickness = buffers.Thickness,
            Position = buffers.Position,
            WorldNormal = buffers.WorldNormal,
            Ids = buffers.Ids,
            IdsAreColours = buffers.Ids is not null && buffers.LabelsAreColours,
            IdPalette = settings.IdPalette,
            CurvatureRange = buffers.CurvatureRange,
            Warnings = warnings
        };

}
