// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;

namespace Vixen.Rendering.Ecs;

/// <summary>A look, as a set of opinions rather than as a complete description.</summary>
/// <remarks>
///     <para>
///         <b>Every field is optional, and that is the whole design.</b> A volume contributes only
///         what it opted into and everything else falls through to whatever is underneath — so two
///         volumes that each care about one thing can both apply, and a volume that only wants the
///         cellar darker does not also silently reset the grade. Unreal spells the same idea with a
///         <c>bOverride_</c> boolean beside every property; C#'s nullable <em>is</em> that boolean,
///         and the <c>[DataContract]</c> generator already writes one.
///     </para>
///     <para>
///         ⚠ <b>An unset field is not a zero.</b> That distinction is the feature. A bloom intensity
///         of zero means "no bloom here", which is a real thing to say; <see langword="null" /> means
///         "I have no opinion about bloom", which is a different one. Anything that flattens the two
///         — an inspector showing an unset float as 0, a serializer writing defaults — turns every
///         volume into a full replacement and the overlap rule into nonsense.
///     </para>
///     <para>
///         ⚠ <b>No field here is a resource name, and none is part of the lens.</b> A name would
///         change the frame's graph, which a volume cannot do — see
///         [32](../../docs/plan/32-post-process-volumes.md) — and the aperture, the shutter, the ISO
///         and the focal length belong to <c>Camera</c>, which is the one place they can be without
///         two claimants on one number. Unreal puts them here, and a cine camera's depth of field
///         being overridden by a level's volume is the well-known consequence.
///     </para>
///     <para>
///         <b>Exposure is a compensation rather than a value</b>, for the same reason. "This cellar
///         is two stops under what the meter says" is a statement about the place and composes with
///         whatever the camera and the auto-exposure decided; "this cellar is EV 9" is a second
///         claimant on a number something else owns, and two overlapping volumes would fight over an
///         absolute instead of adding two offsets.
///     </para>
///     <para>
///         <b>This is what a volume <em>authors</em>.</b> What the frame consumes is
///         <see cref="PostProcessOverlay" />, which is the fold of several of these and carries a
///         weight per field — see there for why the two are different types.
///     </para>
/// </remarks>
[DataContract]
public struct PostProcessSettings {
    // --- Exposure ------------------------------------------------------------

    /// <summary>Stops added to whatever exposure the camera or the meter arrived at.</summary>
    /// <remarks>
    ///     Negative is darker. ⚠ A compensation and not an exposure value; see the type's remarks for
    ///     why the absolute belongs to the camera.
    /// </remarks>
    public float? ExposureCompensation;

    /// <summary>How much the highlights are brought down by the local exposure, 0 for none.</summary>
    public float? LocalHighlightContrast;

    /// <summary>And how much the shadows are brought up.</summary>
    public float? LocalShadowContrast;

    /// <summary>The exposure the frame is pinned to, as an EV at ISO 100.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The one absolute in this struct, and it exists for the look profile.</b>
    ///         <see cref="ExposureCompensation" /> is what a <em>place</em> says — an offset that
    ///         composes — and stays the right field for a volume. What a project's look says is
    ///         different in kind: "this game's dusk is EV 13" is the base the offsets compose over,
    ///         and doc 39 puts exactly one artifact at the bottom of the stack where an absolute has
    ///         no second claimant. A bounded volume asserting it over the look is taking the same
    ///         seat, which the precedence makes explicit rather than a fight.
    ///     </para>
    ///     <para>
    ///         An EV rather than a linear exposure because stops are the space exposure blends in:
    ///         half way between EV 10 and EV 14 is EV 12, where the linear midpoint is nearly a stop
    ///         off. Consumers convert at the edge — <c>Photometry.ExposureFromEv100</c> — and the
    ///         tonemap only reads it in the frames whose exposure is authored at all: a metered frame
    ///         ignores it, because the meter's buffer outranks every authored exposure.
    ///     </para>
    /// </remarks>
    public float? Ev100;

    /// <summary>The darkest scene the meter may expose for, as an EV at ISO 100.</summary>
    /// <remarks>
    ///     <para>
    ///         The meter's clamps are how a look keeps the auto-exposure inside the range its grade
    ///         was authored for — sample 13's dusk works because the clamps and the sky agree, and
    ///         this pair is where that agreement lives once the look profile owns it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>EVs, not raw exposures, and the consumer converts.</b> The meter's own knobs are
    ///         linear multipliers whose relation to EV is reciprocal — the <em>minimum</em> EV is the
    ///         <em>maximum</em> exposure — and a look authored in the meter's units would carry that
    ///         inversion into every document. Stops are also the space the pair blends in; see
    ///         <see cref="Ev100" />.
    ///     </para>
    /// </remarks>
    public float? MeterMinimumEv;

    /// <summary>And the brightest, so a dark level cannot be metered up to noon.</summary>
    public float? MeterMaximumEv;

    // --- Bloom ---------------------------------------------------------------

    /// <summary>How much of the pyramid is composited into the image.</summary>
    public float? BloomIntensity;

    /// <summary>Luminance above which a pixel contributes, in the source's units.</summary>
    public float? BloomThreshold;

    /// <summary>How soft the shoulder under the threshold is, 0 for a hard knee.</summary>
    public float? BloomKnee;

    /// <summary>What colour the glow is tinted.</summary>
    public Vector3? BloomTint;

    // --- Grade ---------------------------------------------------------------

    /// <summary>Contrast, applied around middle grey.</summary>
    public float? Contrast;

    /// <summary>Saturation, 0 for greyscale.</summary>
    public float? Saturation;

    /// <summary>A multiplier on the whole image before the curve.</summary>
    public Vector3? ColourFilter;

    /// <summary>The colour temperature the scene is graded against, in kelvin.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>It is a white balance, so the direction is the opposite of the intuition.</b> The
    ///         number names the light being corrected <em>for</em>, and correcting for a warm light
    ///         cools the picture: 4000 K reads cold and 7800 K reads warm. Writing a high number to
    ///         mean "cold" is the mistake, and it produces exactly the wrong frame.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The only field here whose zero is a sentinel rather than a value</b>, which is why
    ///         <c>TonemapRenderer</c> blends the resulting multiplier and never the kelvin. Zero means
    ///         "do not white balance" and no temperature means that, so interpolating toward it passes
    ///         through 7 K — which clamps to 1667 K, whose correction is an enormous blue gain. See
    ///         <c>TonemapRenderer.WhiteBalanceFor</c>; it shipped wrong once and looked like a hard
    ///         flip to blue at a volume's edge.
    ///     </para>
    /// </remarks>
    public float? Temperature;

    /// <summary>Green against magenta, perpendicular to the temperature.</summary>
    public float? Tint;

    /// <summary>Hue rotation, in degrees.</summary>
    public float? HueShift;

    /// <summary>The whole colour decision list, as one opinion.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>One field, not twenty-two, because one field is what the tonemap has.</b> Its
    ///         <c>Grading</c> is a single nullable block, so a settings layer that mirrors the node
    ///         mirrors that granularity — and a grade is authored as one artifact: a colourist's CDL
    ///         is a decision list, and letting two volumes each own half of one is a picture neither
    ///         of them graded. The compromise is the white balance's, made once for the same reason:
    ///         the pieces of one look travel together under one weight.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An opinion here reaches a node whose own grade is off.</b> The tonemap compiles
    ///         the CDL out entirely when its <c>Grading</c> is null, so the overlay's arrival is what
    ///         turns the permutation on, with <see cref="ColorGrading.Neutral" /> as the authored
    ///         base the blend starts from — never <c>default</c>, whose zeroed gain is a black frame.
    ///     </para>
    /// </remarks>
    public ColorGrading? Grading;

    // --- Fog -----------------------------------------------------------------

    /// <summary>How thick the fog is.</summary>
    public float? FogDensity;

    /// <summary>What colour it is.</summary>
    public Vector3? FogColour;

    /// <summary>Whether the fog thins with altitude, as an atmosphere does.</summary>
    /// <remarks>
    ///     ⚠ A switch rather than a number, which a weighted fold cannot crossfade — see
    ///     <see cref="BlendedToggle" /> for the rule. In the look profile, where the whole point is a
    ///     full-weight base layer, the weight is always 1 and the question never arises.
    /// </remarks>
    public bool? FogHeightFalloff;

    /// <summary>Whether looking toward the sun brightens the fog.</summary>
    public bool? FogSunScattering;

    /// <summary>How thick the froxel medium is here, per metre.</summary>
    /// <remarks>
    ///     ⚠ <b><c>null</c> and <c>0</c> are different answers and the difference is the whole
    ///     overlap rule.</b> Null is "this volume has no opinion about fog", so whatever is underneath
    ///     it stands; zero is "there is no fog <em>here</em>", which is an opinion, and a strong one —
    ///     it is how a designer clears the mist out of an interior that a level-wide volume filled.
    ///     Flattening the two would make every volume that cares only about the grade also silently
    ///     delete the fog, which is the failure the optional fields exist to prevent.
    /// </remarks>
    public float? VolumetricDensity;

    /// <summary>What fraction of that scatters rather than absorbs, per channel.</summary>
    /// <remarks>Near one for air. Lowering it is soot rather than mist.</remarks>
    public Vector3? VolumetricAlbedo;

    /// <summary>Henyey–Greenstein anisotropy: 0 an even glow, 0.9 a searchlight beam.</summary>
    public float? VolumetricPhaseG;

    // --- The lens's imperfections --------------------------------------------

    /// <summary>0 is no darkening at the corners, 1 is fully dark.</summary>
    public float? VignetteIntensity;

    /// <summary>How abrupt the vignette's falloff is.</summary>
    public float? VignetteSmoothness;

    /// <summary>How much film grain there is.</summary>
    public float? GrainIntensity;

    /// <summary>Channel offset at the screen edge, in UV units.</summary>
    public float? AberrationStrength;

    /// <summary>How bright the lens flare's ghosts are.</summary>
    public float? FlareIntensity;

    /// <summary>How wide the defocus may get, in pixels.</summary>
    /// <remarks>
    ///     ⚠ <b>A ceiling, not a focus distance.</b> The focus distance and the aperture are the
    ///     lens's and stay on <c>Camera</c>; what a place may reasonably say is "do not blur more
    ///     than this in here", which is a property of the room rather than of the optics.
    /// </remarks>
    public float? MaximumDefocus;

    /// <summary>A look with no opinions at all, which is what a fold starts from.</summary>
    /// <remarks>
    ///     The same as <c>default</c>, and named because "every field unset" is a meaningful state
    ///     with a name rather than an uninitialised struct.
    /// </remarks>
    public static PostProcessSettings None => default;

    /// <summary>Whether anything here has an opinion.</summary>
    /// <remarks>
    ///     What lets a volume nobody has authored cost nothing: a frame whose volumes are all empty
    ///     folds to an empty overlay, and every node then keeps exactly what its document gave it.
    /// </remarks>
    public readonly bool IsEmpty =>
        ExposureCompensation is null
        && Ev100 is null
        && MeterMinimumEv is null
        && MeterMaximumEv is null
        && LocalHighlightContrast is null
        && LocalShadowContrast is null
        && BloomIntensity is null
        && BloomThreshold is null
        && BloomKnee is null
        && BloomTint is null
        && Contrast is null
        && Saturation is null
        && ColourFilter is null
        && Temperature is null
        && Tint is null
        && HueShift is null
        && Grading is null
        && FogDensity is null
        && FogColour is null
        && FogHeightFalloff is null
        && FogSunScattering is null
        && VolumetricDensity is null
        && VolumetricAlbedo is null
        && VolumetricPhaseG is null
        && VignetteIntensity is null
        && VignetteSmoothness is null
        && GrainIntensity is null
        && AberrationStrength is null
        && FlareIntensity is null
        && MaximumDefocus is null;

    /// <summary>Adds the document name of every field that has an opinion.</summary>
    /// <param name="into">Where the names go. Not cleared first.</param>
    /// <exception cref="ArgumentNullException"><paramref name="into" /> is null.</exception>
    /// <remarks>
    ///     The names are the ones a scene file writes — <c>exposureCompensation</c>, <c>fogDensity</c>
    ///     — because the reader of this list is a person asking "which layer set what", and the answer
    ///     should be in the vocabulary they authored in. What consumes it is
    ///     <see cref="PostProcessVolumeSystem.Contributions" />, built on demand rather than per frame.
    /// </remarks>
    public readonly void Opinions(ICollection<string> into) {
        ArgumentNullException.ThrowIfNull(into);

        Note(into, ExposureCompensation is not null, "exposureCompensation");
        Note(into, Ev100 is not null, "ev100");
        Note(into, MeterMinimumEv is not null, "meterMinimumEv");
        Note(into, MeterMaximumEv is not null, "meterMaximumEv");
        Note(into, LocalHighlightContrast is not null, "localHighlightContrast");
        Note(into, LocalShadowContrast is not null, "localShadowContrast");
        Note(into, BloomIntensity is not null, "bloomIntensity");
        Note(into, BloomThreshold is not null, "bloomThreshold");
        Note(into, BloomKnee is not null, "bloomKnee");
        Note(into, BloomTint is not null, "bloomTint");
        Note(into, Contrast is not null, "contrast");
        Note(into, Saturation is not null, "saturation");
        Note(into, ColourFilter is not null, "colourFilter");
        Note(into, Temperature is not null, "temperature");
        Note(into, Tint is not null, "tint");
        Note(into, HueShift is not null, "hueShift");
        Note(into, Grading is not null, "grading");
        Note(into, FogDensity is not null, "fogDensity");
        Note(into, FogColour is not null, "fogColour");
        Note(into, FogHeightFalloff is not null, "fogHeightFalloff");
        Note(into, FogSunScattering is not null, "fogSunScattering");
        Note(into, VolumetricDensity is not null, "volumetricDensity");
        Note(into, VolumetricAlbedo is not null, "volumetricAlbedo");
        Note(into, VolumetricPhaseG is not null, "volumetricPhaseG");
        Note(into, VignetteIntensity is not null, "vignetteIntensity");
        Note(into, VignetteSmoothness is not null, "vignetteSmoothness");
        Note(into, GrainIntensity is not null, "grainIntensity");
        Note(into, AberrationStrength is not null, "aberrationStrength");
        Note(into, FlareIntensity is not null, "flareIntensity");
        Note(into, MaximumDefocus is not null, "maximumDefocus");

        static void Note(ICollection<string> into, bool held, string name) {
            if (held) {
                into.Add(name);
            }
        }
    }
}

/// <summary>A region a post-process volume applies inside.</summary>
/// <remarks>
///     <para>
///         <b>What replaced the axis-aligned box test</b>
///         ([35 § B2](../../../docs/plan/35-water.md#b2-doc-32s-volumes-are-boxes)). A volume used to
///         be a box and only a box, and the containment test was written into the fold; underwater is
///         a volume whose shape is <em>below this surface and inside this body</em>, which is not a
///         box, is not static, and moves with the waves.
///     </para>
///     <para>
///         ⚠ <b>A water body must not be the only non-box shape.</b> An interface with one built-in
///         implementation and one special case is an interface shaped like its special case — so a
///         sphere lands with it, and the two built-ins are what the fold is written against.
///     </para>
///     <para>
///         <b>The point is in world space and the distance is in the shape's own.</b> A shape is asked
///         about a world position because a water body's is a world-space query and cannot be
///         expressed as a transform of a canonical one; what comes back is compared against
///         <see cref="PostProcessVolume.BlendRadius" />, which has always been in the volume's own
///         units — a volume scaled by two reaches twice as far, and that is the documented behaviour
///         rather than a rounding of it.
///     </para>
///     <para>
///         <b>Implemented by readonly structs, and the fold calls the built-ins as concrete types.</b>
///         The interface is what makes a third shape possible; it is not what the two shapes every
///         frame goes through, because a per-volume interface dispatch is a per-volume allocation
///         waiting to be written.
///     </para>
/// </remarks>
public interface IPostProcessShape {
    /// <summary>Whether a world-space point is inside, and how far outside it is when it is not.</summary>
    /// <param name="world">The point, in world space.</param>
    /// <param name="distanceOutside">
    ///     Zero when the point is inside, and otherwise the distance to the shape's <em>surface</em> —
    ///     not to its centre. ⚠ Measuring from the centre makes a long thin volume fade in from much
    ///     further away at its ends than along its sides, which reads as a corridor whose grade starts
    ///     before the corridor does.
    /// </param>
    /// <returns>Whether the point is inside.</returns>
    bool Contains(Vector3 world, out float distanceOutside);
}

/// <summary>Which built-in shape a volume is.</summary>
/// <remarks>
///     ⚠ <b><see cref="Box" /> is zero, so a scene written before shapes existed loads as one.</b> The
///     field is absent from every volume authored against
///     [32](../../../docs/plan/32-post-process-volumes.md), and a default that was anything else would
///     silently change the shape of every one of them.
/// </remarks>
public enum PostProcessShapeKind {
    /// <summary>A box of <see cref="PostProcessVolume.Extents" />, in the entity's own space.</summary>
    Box,

    /// <summary>
    ///     An ellipsoid whose radii are <see cref="PostProcessVolume.Extents" />, in the entity's own
    ///     space. Uniform extents are a sphere.
    /// </summary>
    Sphere,

    /// <summary>
    ///     A shape something outside this assembly supplies, through
    ///     <see cref="PostProcessVolumeSystem.Shapes" />.
    /// </summary>
    /// <remarks>
    ///     What a water body is. ⚠ A volume marked <see cref="Custom" /> with nothing to resolve it
    ///     reaches nothing rather than everything, for the reason a singular transform does: the
    ///     failure that looks like the mistake it is, rather than the one that grades the whole level.
    /// </remarks>
    Custom
}

/// <summary>A box in an entity's own space, as a shape.</summary>
/// <remarks>
///     ⚠ <b>A singular transform contains nothing rather than everything.</b> A volume scaled to zero
///     on an axis cannot be inverted, and of the two available answers the one that looks like the
///     mistake it is beats the one that blacks out the level.
/// </remarks>
public readonly struct BoxPostProcessShape : IPostProcessShape {
    readonly Matrix4x4 inverse;
    readonly bool invertible;

    /// <summary>Creates one.</summary>
    /// <param name="transform">The volume's world transform. Inverted once, here, rather than per query.</param>
    /// <param name="extents">Half the box's size, in the entity's own space.</param>
    public BoxPostProcessShape(in Matrix4x4 transform, Vector3 extents) {
        invertible = Matrix4x4.Invert(transform, out inverse);
        Extents = extents;
    }

    /// <summary>Half the box's size, in the entity's own space.</summary>
    public Vector3 Extents { get; }

    /// <inheritdoc />
    public bool Contains(Vector3 world, out float distanceOutside) {
        if (!invertible) {
            distanceOutside = float.PositiveInfinity;
            return false;
        }

        distanceOutside = ExteriorDistance(Matrix4x4.TransformPosition(world, inverse), Extents);
        return distanceOutside <= 0f;
    }

    /// <summary>How far a point in the box's own space is outside its surface.</summary>
    /// <param name="local">The point, in the box's own space.</param>
    /// <param name="extents">Half the box's size.</param>
    /// <returns>Zero inside, and the distance to the nearest surface point outside.</returns>
    /// <remarks>
    ///     <c>length(max(|p| - e, 0))</c>, the standard exterior distance to a box — which is what
    ///     makes a corner fade at the same rate as a face.
    /// </remarks>
    public static float ExteriorDistance(Vector3 local, Vector3 extents) {
        var outside = new Vector3(
            MathF.Max(MathF.Abs(local.X) - MathF.Max(extents.X, 0f), 0f),
            MathF.Max(MathF.Abs(local.Y) - MathF.Max(extents.Y, 0f), 0f),
            MathF.Max(MathF.Abs(local.Z) - MathF.Max(extents.Z, 0f), 0f)
        );

        return outside.Length();
    }
}

/// <summary>An ellipsoid in an entity's own space, as a shape.</summary>
/// <remarks>
///     <para>
///         The second built-in shape, and it exists so that the first non-box case is not the water
///         body — see <see cref="IPostProcessShape" />. It is also the shape a designer actually wants
///         for a light shaft, a fire's warmth or a pool of damp: a box fading over a radius has
///         corners, and corners are visible in a grade.
///     </para>
///     <para>
///         ⚠ <b>The exterior distance is exact for a sphere and a bound for an ellipsoid.</b> There is
///         no closed form for the distance to an ellipsoid's surface, so non-uniform radii use
///         <c>(‖p ⁄ r‖ − 1) · min r</c>, which never overstates the distance and therefore never fades
///         a volume out earlier than its blend radius says. A volume authored with uniform radii — the
///         common one — gets the exact answer.
///     </para>
/// </remarks>
public readonly struct SpherePostProcessShape : IPostProcessShape {
    readonly Matrix4x4 inverse;
    readonly bool invertible;

    /// <summary>Creates one.</summary>
    /// <param name="transform">The volume's world transform.</param>
    /// <param name="radii">Its radii, in that space. Uniform radii are a sphere.</param>
    public SpherePostProcessShape(in Matrix4x4 transform, Vector3 radii) {
        invertible = Matrix4x4.Invert(transform, out inverse);
        Radii = radii;
    }

    /// <summary>The ellipsoid's radii, in the entity's own space.</summary>
    public Vector3 Radii { get; }

    /// <inheritdoc />
    public bool Contains(Vector3 world, out float distanceOutside) {
        if (!invertible) {
            distanceOutside = float.PositiveInfinity;
            return false;
        }

        distanceOutside = ExteriorDistance(Matrix4x4.TransformPosition(world, inverse), Radii);
        return distanceOutside <= 0f;
    }

    /// <summary>How far a point in the ellipsoid's own space is outside its surface.</summary>
    /// <param name="local">The point, in the ellipsoid's own space.</param>
    /// <param name="radii">Its radii.</param>
    /// <returns>Zero inside, and a lower bound on the distance to the surface outside.</returns>
    /// <remarks>
    ///     ⚠ <b>A radius of zero collapses the axis rather than dividing by it.</b> A point off a
    ///     collapsed axis is infinitely far outside, and a point on it is not constrained by that axis
    ///     at all — which is what makes a zeroed volume contain exactly its own centre, the same
    ///     degenerate-but-honest answer <see cref="BoxPostProcessShape" /> gives.
    /// </remarks>
    public static float ExteriorDistance(Vector3 local, Vector3 radii) {
        var scaled = 0f;
        var smallest = float.PositiveInfinity;

        Span<float> point = [local.X, local.Y, local.Z];
        Span<float> radius = [radii.X, radii.Y, radii.Z];

        for (var axis = 0; axis < 3; axis++) {
            if (radius[axis] > 0f) {
                var normalised = point[axis] / radius[axis];
                scaled += normalised * normalised;
                smallest = MathF.Min(smallest, radius[axis]);
            } else if (point[axis] != 0f) {
                return float.PositiveInfinity;
            }
        }

        var distance = MathF.Sqrt(scaled);

        if (distance <= 1f) {
            return 0f;
        }

        return float.IsPositiveInfinity(smallest) ? float.PositiveInfinity : (distance - 1f) * smallest;
    }
}

/// <summary>Where a volume's shape comes from when the built-ins are not enough.</summary>
/// <remarks>
///     <para>
///         The seam <see cref="PostProcessShapeKind.Custom" /> resolves through, and the whole of what
///         a water body needs from [32](../../../docs/plan/32-post-process-volumes.md): the priority,
///         the blend radius and the optional fields are unchanged, and being underwater costs a
///         containment test rather than a system.
///     </para>
///     <para>
///         ⚠ <b>Asked once per custom volume per fold, so the answer should be an object that lives
///         as long as the entity does.</b> A source that builds a shape per call is an allocation per
///         volume per frame, and this is called from the frame's own fold.
///     </para>
/// </remarks>
public interface IPostProcessShapeSource {
    /// <summary>The shape an entity's volume has, or <see langword="null" /> if this does not know.</summary>
    /// <param name="entity">The entity carrying the volume.</param>
    /// <returns>The shape, or <see langword="null" />.</returns>
    IPostProcessShape? ShapeFor(Entity entity);
}

/// <summary>A region that says how the frame looks inside it.</summary>
/// <remarks>
///     <para>
///         <b>Where a look applies, rather than which effects exist.</b> A compositor document names
///         the frame's passes and cannot know the player has walked into a cellar; this is how a level
///         says so, and a designer places one rather than writing code or a second document.
///     </para>
///     <para>
///         ⚠ <b>It blends parameters and cannot add a pass.</b> The frame's graph decides resource
///         lifetimes, pass ordering and transient aliasing, and rebuilding it as somebody crosses a
///         threshold would be a graph recompile per frame. So a volume that sets
///         <see cref="PostProcessSettings.MaximumDefocus" /> in a document with no <c>!DepthOfField</c>
///         node does nothing at all, and cannot say so at author time. Unreal has the same constraint
///         and hides it inside one uber-pass; here the passes are named in a file you can read, which
///         is what makes this statable — see [32](../../../docs/plan/32-post-process-volumes.md).
///     </para>
///     <para>
///         <b>Its bounds are in the entity's own space</b>, so a rotated entity is a rotated box and
///         the containment test runs in the volume's frame rather than in the world's. Scale counts:
///         a volume scaled by two reaches twice as far.
///     </para>
///     <para>
///         ⚠ <b>A zeroed component is inert rather than global.</b> Zero extents contain nothing and
///         a zero weight applies nothing, which is what an entity that has just been given the
///         component should do — the alternative, treating a zeroed volume as unbounded, would make
///         adding one in the inspector black out the level. <c>Default</c> is what a create menu uses.
///     </para>
/// </remarks>
[Component]
[DataContract]
public struct PostProcessVolume : IDefaultComponent<PostProcessVolume> {
    /// <summary>Half the shape's size, in the entity's own space.</summary>
    /// <remarks>
    ///     Half a box's extents, or an ellipsoid's radii — see <see cref="Shape" />. One field for both
    ///     rather than a second that is meaningless for one of them, which is what a discriminated
    ///     shape buys.
    /// </remarks>
    public Vector3 Extents;

    /// <summary>How far outside the shape it fades in, in metres.</summary>
    /// <remarks>
    ///     ⚠ <b>Outside, not inside.</b> A designer places the box around the region that should be
    ///     fully affected and the blend happens in the approach, so widening the falloff does not
    ///     shrink the room. Zero is a hard edge, which is what a volume changing something discrete
    ///     wants and a lighting volume does not.
    /// </remarks>
    public float BlendRadius;

    /// <summary>A master multiplier on everything it contributes, 0 to 1.</summary>
    public float Weight;

    /// <summary>Which volume wins where two overlap. Higher is on top.</summary>
    /// <remarks>
    ///     ⚠ Two volumes at one priority resolve in whatever order the world walks them, which is
    ///     arbitrary and deliberately undefined: two volumes both fully claiming one field at one
    ///     priority is a level-design mistake rather than a case worth inventing a tiebreak for.
    /// </remarks>
    public int Priority;

    /// <summary>Whether it applies everywhere, ignoring its bounds.</summary>
    /// <remarks>
    ///     The level's base look — Unreal's "Infinite Extent". One of these at a low priority is what
    ///     a scene grades itself with, and every other volume lays over it.
    /// </remarks>
    public bool Unbound;

    /// <summary>Which shape its <see cref="Extents" /> describe.</summary>
    /// <remarks>
    ///     ⚠ <b><see cref="PostProcessShapeKind.Box" /> is zero</b>, so a scene authored before shapes
    ///     existed loads as the box it was — see the enum.
    /// </remarks>
    public PostProcessShapeKind Shape;

    /// <summary>What it has an opinion about.</summary>
    public PostProcessSettings Settings;

    /// <summary>A five-metre cube that fades over one metre and applies fully inside.</summary>
    /// <remarks>
    ///     A property rather than <c>default</c>, for the reason the type's remarks give: a zeroed
    ///     volume contains nothing and contributes nothing, so an inspector that added one would show
    ///     a component that appears not to work.
    /// </remarks>
    public static PostProcessVolume Default => new() {
        Extents = new(5f, 5f, 5f),
        BlendRadius = 1f,
        Weight = 1f,
        Priority = 0,
        Unbound = false,
        Shape = PostProcessShapeKind.Box,
        Settings = PostProcessSettings.None
    };

    /// <inheritdoc />
    static PostProcessVolume IDefaultComponent<PostProcessVolume>.DefaultValue => Default;

    /// <summary>How much a volume applies at a given distance outside it.</summary>
    /// <param name="distanceOutside">What a shape reported. Zero or less is inside.</param>
    /// <param name="blendRadius">Over what distance it fades in.</param>
    /// <returns>1 inside, falling linearly to 0 at <paramref name="blendRadius" /> outside.</returns>
    /// <remarks>
    ///     Shared by every shape, and public so a custom one can be checked against the same curve
    ///     rather than reimplementing it. ⚠ A blend radius of zero is a hard edge, not a volume that
    ///     never applies — which is what a volume changing something discrete wants.
    /// </remarks>
    public static float FadeAt(float distanceOutside, float blendRadius) {
        if (distanceOutside <= 0f) {
            return 1f;
        }

        return blendRadius > 0f ? MathF.Max(1f - (distanceOutside / blendRadius), 0f) : 0f;
    }

    /// <summary>How much this volume applies at a point, before its <see cref="Weight" />.</summary>
    /// <param name="local">The point, already in the volume's own space.</param>
    /// <returns>1 inside, falling to 0 at <see cref="BlendRadius" /> outside.</returns>
    /// <remarks>
    ///     <para>
    ///         The distance is to the shape's <em>surface</em>, which is what makes a corner fade at
    ///         the same rate as a face; using the distance to the centre instead would make a long
    ///         thin volume fade in from much further away at its ends.
    ///     </para>
    ///     <para>
    ///         ⚠ <see cref="Unbound" /> answers 1 everywhere and never consults the point at all, and
    ///         so does <see cref="PostProcessShapeKind.Custom" /> — a shape only something outside this
    ///         assembly can evaluate is not a function of a local position, which is why the fold and
    ///         not this is what resolves one. See <see cref="PostProcessVolumeSystem.Shapes" />.
    ///     </para>
    /// </remarks>
    public readonly float Falloff(Vector3 local) {
        if (Unbound) {
            return 1f;
        }

        var distance = Shape == PostProcessShapeKind.Sphere
            ? SpherePostProcessShape.ExteriorDistance(local, Extents)
            : BoxPostProcessShape.ExteriorDistance(local, Extents);

        return FadeAt(distance, BlendRadius);
    }
}

