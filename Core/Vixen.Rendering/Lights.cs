// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;

namespace Vixen.Rendering;

/// <summary>The three kinds of light a punctual record can be.</summary>
/// <remarks>
///     The same three <c>LightKind</c> in <c>Raven/Library/Shading/Lighting.rvn</c>, and the values
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
    Spot = 2
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

    /// <summary>Its colour, before intensity.</summary>
    public Color3 Colour;

    /// <summary>How bright it is, as a multiplier on <see cref="Colour" />.</summary>
    public float Intensity;

    /// <summary>The distance at which its contribution reaches zero.</summary>
    public float Range;

    /// <summary>Its sphere radius. Zero for a punctual light.</summary>
    public float Radius;

    /// <summary>The inner cone half-angle in radians, inside which a spot is at full brightness.</summary>
    public float InnerAngle;

    /// <summary>The outer cone half-angle in radians, outside which a spot contributes nothing.</summary>
    public float OuterAngle;

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

    /// <summary>The radiance this light emits — colour times intensity.</summary>
    public readonly Vector3 Radiance => new(Colour.R * Intensity, Colour.G * Intensity, Colour.B * Intensity);

    /// <summary>This light in the layout the shader reads.</summary>
    public readonly PunctualLightData ToGpu() =>
        new() {
            Position = Position,
            Kind = (float)Kind,
            Colour = Radiance,
            Range = Range,
            Direction = Direction,
            CosInner = MathF.Cos(InnerAngle),
            Radius = Radius,
            CosOuter = MathF.Cos(OuterAngle)
        };
}

/// <summary>
///     One light exactly as <c>PunctualLight</c> in <c>Lighting.rvn</c> declares it.
/// </summary>
/// <remarks>
///     <para>
///         Sixty-four bytes with no padding anywhere, and that is the whole reason the field order is
///         what it is: each <c>float3</c> is followed by a <c>float</c>, so every member lands on its
///         natural std140 sixteen-byte boundary. Reordered so that two vectors are adjacent, the same
///         eight values cost ninety-six bytes and every offset moves.
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
    ///     Declared rather than left to the compiler, so that <c>sizeof</c> is sixty-four on every
    ///     runtime rather than sixty-four on the ones that happen to round up.
    /// </remarks>
    public Vector2 Padding;
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
