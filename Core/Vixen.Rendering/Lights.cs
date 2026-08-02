// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;

namespace Vixen.Rendering;

/// <summary>The five kinds of light one record can be.</summary>
/// <remarks>
///     The same five <c>LightKind</c> in <c>Raven/Library/Shading/Lighting.rvn</c>, and the values
///     must agree: the shader compares the record's <c>kind</c> against its own constants, so a
///     renumbering here that is not made there turns every spot light into a point light and nothing
///     reports it.
/// </remarks>
public enum LightKind {
    /// <summary>The sun: a direction and no position.</summary>
    Directional = 0,

    /// <summary>A position, radiating in every direction.</summary>
    Point = 1,

    /// <summary>A position and a cone.</summary>
    Spot = 2,

    /// <summary>A capsule: a segment with a radius. A fluorescent tube, a strip light.</summary>
    Tube = 3,

    /// <summary>A one-sided rectangle. A softbox, a window, a screen.</summary>
    Rect = 4
}

/// <summary>
///     One light as a host authors it: physical units, angles in radians, colour separate from
///     intensity.
/// </summary>
/// <remarks>
///     <para>
///         Deliberately not the layout the GPU reads. An author sets a colour and an intensity and a
///         cone half-angle; a shader wants a premultiplied radiance and the cosine of that angle.
///         Keeping the two apart means the trigonometry happens once per light per frame rather than
///         once per light per fragment, and it means changing the GPU record's packing does not
///         change what a scene file says.
///     </para>
///     <para>
///         <see cref="Radius" /> is a sphere-light radius rather than a falloff distance: it widens
///         the specular highlight instead of the reach. <see cref="Range" /> is the reach.
///     </para>
/// </remarks>
public struct RenderLight {
    /// <summary>Which kind of light this is.</summary>
    public LightKind Kind;

    /// <summary>Where it is. Unused by a directional light.</summary>
    public Vector3 Position;

    /// <summary>Which way it points, away from the light. Unused by a point light.</summary>
    public Vector3 Direction;

    /// <summary>Its colour, before intensity and before <see cref="Temperature" />.</summary>
    public Color3 Colour;

    /// <summary>How bright it is, in <see cref="Unit" />.</summary>
    /// <remarks>
    ///     No longer a bare multiplier: 1600 lumens is a bright bulb and 100000 lux is midday sun, so
    ///     the number only means something beside the unit. <see cref="Radiance" /> is where the two
    ///     become what the shader multiplies.
    /// </remarks>
    public float Intensity;

    /// <summary>What <see cref="Intensity" /> is measured in.</summary>
    public LightUnit Unit;

    /// <summary>Its colour temperature in kelvin, or zero to use <see cref="Colour" /> alone.</summary>
    /// <remarks>
    ///     <para>
    ///         Multiplied into <see cref="Colour" /> rather than replacing it, which is the same
    ///         arrangement HDRP's filter-and-temperature pair has: the temperature is what the light
    ///         physically is — 1900 K for a flame, 6500 K for daylight — and the colour is the gel in
    ///         front of it.
    ///     </para>
    ///     <para>
    ///         Zero rather than a separate flag, and it is not a value a real light could have.
    ///         <see cref="Photometry.FromTemperature" /> returns a colour of <em>unit luminance</em>,
    ///         so switching it on changes the tint and not the brightness.
    ///     </para>
    /// </remarks>
    public float Temperature;

    /// <summary>The distance at which its contribution reaches zero.</summary>
    public float Range;

    /// <summary>Its sphere radius. Zero for a punctual light.</summary>
    public float Radius;

    /// <summary>The inner cone half-angle in radians, inside which a spot is at full brightness.</summary>
    public float InnerAngle;

    /// <summary>The outer cone half-angle in radians, outside which a spot contributes nothing.</summary>
    public float OuterAngle;

    /// <summary>A shaped light's own axis: along a tube, or across a rectangle's width.</summary>
    /// <remarks>
    ///     Normalised on the way to the GPU rather than here, because an author setting an axis
    ///     component at a time would otherwise have to renormalise after each one.
    /// </remarks>
    public Vector3 Tangent;

    /// <summary>Half a tube's length, or half a rectangle's width. Zero for a punctual light.</summary>
    /// <remarks>
    ///     A rectangle's half-<em>height</em> is <see cref="Radius" />: every area shape here needs
    ///     exactly two extents, and reusing the field a sphere and a tube already have for their
    ///     radius is what keeps one record able to describe all five kinds.
    /// </remarks>
    public float HalfLength;

    /// <summary>A directional light, which needs no position and no range.</summary>
    public static RenderLight Directional(Vector3 direction, Color3 colour, float intensity = 1f) =>
        new() {
            Kind = LightKind.Directional,
            Direction = Vector3.Normalize(direction),
            Colour = colour,
            Intensity = intensity
        };

    /// <summary>A point light.</summary>
    public static RenderLight Point(Vector3 position, float range, Color3 colour, float intensity = 1f) =>
        new() {
            Kind = LightKind.Point,
            Position = position,
            Range = range,
            Colour = colour,
            Intensity = intensity
        };

    /// <summary>A spot light, with half-angles in radians.</summary>
    public static RenderLight Spot(
        Vector3 position,
        Vector3 direction,
        float range,
        float innerAngle,
        float outerAngle,
        Color3 colour,
        float intensity = 1f
    ) =>
        new() {
            Kind = LightKind.Spot,
            Position = position,
            Direction = Vector3.Normalize(direction),
            Range = range,
            InnerAngle = innerAngle,
            OuterAngle = outerAngle,
            Colour = colour,
            Intensity = intensity
        };

    /// <summary>A tube light: a segment of a given radius, lit along its whole length.</summary>
    /// <param name="position">The middle of the tube.</param>
    /// <param name="axis">Which way it runs. Normalised on the way to the GPU.</param>
    /// <param name="halfLength">Half its length, so the ends are at position ± axis × this.</param>
    /// <param name="radius">How thick it is.</param>
    /// <param name="range">Where its contribution reaches zero, measured from its surface.</param>
    /// <param name="colour">Its colour.</param>
    /// <param name="intensity">What that colour is multiplied by.</param>
    public static RenderLight Tube(
        Vector3 position,
        Vector3 axis,
        float halfLength,
        float radius,
        float range,
        Color3 colour,
        float intensity = 1f
    ) =>
        new() {
            Kind = LightKind.Tube,
            Position = position,
            Tangent = axis,
            HalfLength = halfLength,
            Radius = radius,
            Range = range,
            Colour = colour,
            Intensity = intensity
        };

    /// <summary>A rectangular panel, emitting from one face.</summary>
    /// <param name="position">The middle of the panel.</param>
    /// <param name="normal">The face it emits from.</param>
    /// <param name="axis">Its width axis. Made perpendicular to the normal on the way to the GPU.</param>
    /// <param name="halfWidth">Half its width, along the axis.</param>
    /// <param name="halfHeight">Half its height, across the axis.</param>
    /// <param name="range">Where its contribution reaches zero.</param>
    /// <param name="colour">Its colour.</param>
    /// <param name="intensity">What that colour is multiplied by.</param>
    public static RenderLight Rect(
        Vector3 position,
        Vector3 normal,
        Vector3 axis,
        float halfWidth,
        float halfHeight,
        float range,
        Color3 colour,
        float intensity = 1f
    ) =>
        new() {
            Kind = LightKind.Rect,
            Position = position,
            Direction = Vector3.Normalize(normal),
            Tangent = axis,
            HalfLength = halfWidth,
            Radius = halfHeight,
            Range = range,
            Colour = colour,
            Intensity = intensity
        };

    /// <summary>What the shader multiplies: the tinted colour times the converted intensity.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Photometric, and which quantity it is depends on the kind.</b> Illuminance in lux
    ///         for a directional light, luminous intensity in candela for a point or a spot, luminance
    ///         in nits for an area one — <see cref="Photometry.Intensity" /> says why each kind
    ///         converts to the one it does. The name is the one the record has always had; the values
    ///         in it now have units.
    ///     </para>
    ///     <para>
    ///         The temperature is a tint of unit luminance, so it multiplies in without changing what
    ///         the light emits. A light with no temperature is exactly its colour, which is what every
    ///         scene authored before there were units still gets.
    ///     </para>
    /// </remarks>
    public readonly Vector3 Radiance {
        get {
            var scale = Photometry.Intensity(Kind, Unit, Intensity, OuterAngle, Radius, HalfLength);
            var tint = Temperature > 0f ? Photometry.FromTemperature(Temperature) : new Color3(1f, 1f, 1f);

            return new(Colour.R * tint.R * scale, Colour.G * tint.G * scale, Colour.B * tint.B * scale);
        }
    }

    /// <summary>This light in the layout the shader reads.</summary>
    /// <remarks>
    ///     The tangent is orthogonalised against the direction here rather than trusted, because a
    ///     rectangle's two axes and its normal have to be an orthonormal basis for the closest-point
    ///     search to mean anything — and an author who typed an axis that is a little off would
    ///     otherwise get a panel that is subtly sheared, which is not a thing anyone would think to
    ///     look for.
    /// </remarks>
    public readonly PunctualLightData ToGpu() =>
        new() {
            Position = Position,
            Kind = (float)Kind,
            Colour = Radiance,
            Range = Range,
            Direction = Direction,
            CosInner = MathF.Cos(InnerAngle),
            Radius = Radius,
            CosOuter = MathF.Cos(OuterAngle),
            Tangent = Axis(),
            HalfLength = HalfLength
        };

    /// <summary>The shaped axis, unit length and square to the direction where that matters.</summary>
    readonly Vector3 Axis() {
        if (Tangent.LengthSquared() < 1e-12f) {
            return Vector3.Zero;
        }

        var axis = Vector3.Normalize(Tangent);

        // Only a rectangle has a normal for its axis to be square to. A tube's axis *is* its
        // direction, and projecting it against the unused `Direction` field would zero it.
        if (Kind != LightKind.Rect) {
            return axis;
        }

        var squared = axis - (Direction * Vector3.Dot(axis, Direction));
        return squared.LengthSquared() < 1e-12f ? Orthogonal(Direction) : Vector3.Normalize(squared);
    }

    /// <summary>Any unit vector square to <paramref name="normal" />, for an axis that was parallel to it.</summary>
    static Vector3 Orthogonal(Vector3 normal) {
        var candidate = MathF.Abs(normal.Z) < 0.999f ? Vector3.UnitZ : Vector3.UnitX;
        return Vector3.Normalize(Vector3.Cross(candidate, normal));
    }
}

/// <summary>
///     One light exactly as <c>PunctualLight</c> in <c>Lighting.rvn</c> declares it.
/// </summary>
/// <remarks>
///     <para>
///         Eighty bytes with no padding anywhere, and that is the whole reason the field order is
///         what it is: each <c>float3</c> is followed by a <c>float</c>, so every member lands on its
///         natural std140 sixteen-byte boundary. Reordered so that two vectors are adjacent, the same
///         values cost far more and every offset moves.
///     </para>
///     <para>
///         <strong>Because it matches, an upload is a blit.</strong> Writing this array to a buffer is
///         one <c>MemoryMarshal.AsBytes</c> and no per-field packing — which is the only reason a
///         per-object light list is affordable at all. A test asserts the size and the offsets rather
///         than trusting the comment, because the failure is silent: the shader reads whatever bytes
///         are there and shades with them.
///     </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct PunctualLightData {
    /// <summary>Where the light is.</summary>
    public Vector3 Position;

    /// <summary>Which <see cref="LightKind" /> it is, as a float.</summary>
    /// <remarks>
    ///     A float rather than an int because the record is host-written memory read as a uniform
    ///     block, and a block with no integer members has no integer-versus-float ambiguity to get
    ///     wrong on any backend.
    /// </remarks>
    public float Kind;

    /// <summary>Its radiance — colour already multiplied by intensity.</summary>
    public Vector3 Colour;

    /// <summary>Where its contribution reaches zero.</summary>
    public float Range;

    /// <summary>Which way it points.</summary>
    public Vector3 Direction;

    /// <summary>The cosine of the inner cone half-angle.</summary>
    public float CosInner;

    /// <summary>Its sphere radius.</summary>
    public float Radius;

    /// <summary>The cosine of the outer cone half-angle.</summary>
    public float CosOuter;

    /// <summary>Two floats of tail padding the shader declares and never reads.</summary>
    /// <remarks>
    ///     Declared rather than left to the compiler, so that <c>sizeof</c> is the same on every
    ///     runtime rather than on the ones that happen to round up.
    /// </remarks>
    public Vector2 Padding;

    /// <summary>A shaped light's axis: along a tube, or across a rectangle's width.</summary>
    public Vector3 Tangent;

    /// <summary>Half a tube's length, or half a rectangle's width.</summary>
    public float HalfLength;
}

/// <summary>Which slice of the light buffer holds one object's list.</summary>
/// <param name="Offset">Where the object's block starts, in bytes.</param>
/// <param name="Count">How many lights the block holds.</param>
/// <remarks>
///     Per-object data in the sense <see cref="RenderDataHolder" /> means: eight bytes in an array
///     the lighting feature registered, which nothing else reads and which costs an object with no
///     lighting one unused slot.
/// </remarks>
public readonly record struct LightAssignment(int Offset, int Count);

/// <summary>Something that knows which light is the scene's sun.</summary>
/// <remarks>
///     An interface rather than a reference to the lighting feature, because a shadow renderer needs
///     one fact and should not depend on the machinery that happens to produce it — a scene with a
///     scripted sun, or a cinematic overriding one, supplies this and nothing else changes.
/// </remarks>
public interface ISunSource {
    /// <summary>The directional light casting the scene's shadows, or null when there is none.</summary>
    RenderLight? Sun { get; }
}
