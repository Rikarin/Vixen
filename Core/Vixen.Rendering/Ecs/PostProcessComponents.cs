// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;

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

    // --- Bloom ---------------------------------------------------------------

    /// <summary>How much of the pyramid is composited into the image.</summary>
    public float? BloomIntensity;

    /// <summary>Luminance above which a pixel contributes, in the source's units.</summary>
    public float? BloomThreshold;

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
    public float? Temperature;

    /// <summary>Green against magenta, perpendicular to the temperature.</summary>
    public float? Tint;

    /// <summary>Hue rotation, in degrees.</summary>
    public float? HueShift;

    // --- Fog -----------------------------------------------------------------

    /// <summary>How thick the fog is.</summary>
    public float? FogDensity;

    /// <summary>What colour it is.</summary>
    public Vector3? FogColour;

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
        && LocalHighlightContrast is null
        && LocalShadowContrast is null
        && BloomIntensity is null
        && BloomThreshold is null
        && BloomTint is null
        && Contrast is null
        && Saturation is null
        && ColourFilter is null
        && Temperature is null
        && Tint is null
        && HueShift is null
        && FogDensity is null
        && FogColour is null
        && VignetteIntensity is null
        && VignetteSmoothness is null
        && GrainIntensity is null
        && AberrationStrength is null
        && FlareIntensity is null
        && MaximumDefocus is null;
}

/// <summary>A box that says how the frame looks inside it.</summary>
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
public struct PostProcessVolume {
    /// <summary>Half the box's size, in the entity's own space.</summary>
    public Vector3 Extents;

    /// <summary>How far outside the box it fades in, in metres.</summary>
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
        Settings = PostProcessSettings.None
    };

    /// <summary>How much this volume applies at a point, before its <see cref="Weight" />.</summary>
    /// <param name="local">The point, already in the volume's own space.</param>
    /// <returns>1 inside, falling to 0 at <see cref="BlendRadius" /> outside.</returns>
    /// <remarks>
    ///     <para>
    ///         The distance is to the box's <em>surface</em>, which is what makes a corner fade at the
    ///         same rate as a face: <c>length(max(|p| - e, 0))</c> is the standard exterior distance
    ///         to a box, and using the distance to the centre instead would make a long thin volume
    ///         fade in from much further away at its ends.
    ///     </para>
    ///     <para>
    ///         ⚠ <see cref="Unbound" /> answers 1 everywhere and never consults the point at all.
    ///     </para>
    /// </remarks>
    public readonly float Falloff(Vector3 local) {
        if (Unbound) {
            return 1f;
        }

        var outside = new Vector3(
            MathF.Max(MathF.Abs(local.X) - MathF.Max(Extents.X, 0f), 0f),
            MathF.Max(MathF.Abs(local.Y) - MathF.Max(Extents.Y, 0f), 0f),
            MathF.Max(MathF.Abs(local.Z) - MathF.Max(Extents.Z, 0f), 0f)
        );

        var distance = outside.Length();

        if (distance <= 0f) {
            return 1f;
        }

        return BlendRadius > 0f ? MathF.Max(1f - (distance / BlendRadius), 0f) : 0f;
    }
}

