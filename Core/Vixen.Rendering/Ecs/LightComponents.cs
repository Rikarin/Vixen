// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;

namespace Vixen.Rendering.Ecs;

/// <summary>An entity that lights the scene.</summary>
/// <remarks>
///     <para>
///         <b>Everything a light is except where it is.</b> Position, direction and the axis a tube
///         or a rectangle is oriented along all come from the transform, the same way a mesh's do —
///         which is what makes a spot light something you aim with the rotate gizmo rather than by
///         typing a vector, and what stops a file saying two different things about where a light
///         points.
///     </para>
///     <para>
///         <b>The authored light, against <see cref="RenderLight" />'s per-frame one.</b> This is what
///         a <c>.vxscene</c> holds and an inspector edits; that is what the lighting feature reads,
///         with the transform already folded in. The fields line up so that
///         <see cref="LightExtractionSystem" /> is a copy rather than a translation.
///     </para>
///     <para>
///         ⚠ <b>It lives here because this is the assembly that knows what a light is.</b>
///         <c>Vixen.Engine</c> references no graphics API and cannot name <see cref="LightKind" />, so
///         this component spent a while in the editor's scene view — authored, saved, and invisible to
///         any build. The arrangement that fixes it is the one <c>Vixen.Physics</c> and
///         <c>Vixen.Audio</c> already use: the subsystem references the ECS and the engine, and owns
///         both its components and the system that bridges them.
///     </para>
///     <para>
///         ⚠ <b><c>[Component]</c> and <c>[DataContract]</c>, which together are what declares it to
///         <c>SceneComponentRegistry</c>.</b> The contract gives it a serializer and a member
///         description — the rows an inspector draws — and the pair is the claim that a compiled scene
///         may name it. Nothing calls a registration method; the engine's component generator emits
///         one declaration per assembly from these two attributes.
///     </para>
/// </remarks>
[Component]
[DataContract]
public struct Light : IDefaultComponent<Light> {
    /// <summary>Which of the five kinds it is.</summary>
    public LightKind Kind;

    /// <summary>Its colour, before intensity and before <see cref="Temperature" />.</summary>
    public Color3 Colour;

    /// <summary>How bright it is, in <see cref="Unit" />.</summary>
    public float Intensity;

    /// <summary>What <see cref="Intensity" /> is measured in.</summary>
    /// <remarks>
    ///     <see cref="LightUnit.Native" /> — what a zeroed component has, and what a scene saved
    ///     before this field existed reads as — takes the number as written.
    /// </remarks>
    public LightUnit Unit;

    /// <summary>Its colour temperature in kelvin, or zero for none.</summary>
    /// <remarks>
    ///     A tint of unit luminance multiplied into <see cref="Colour" />, so switching it on changes
    ///     what the light looks like and not how much of it there is. 1850 K is a candle, 2700 K a
    ///     warm bulb, 5500 K noon, 7500 K an overcast sky.
    /// </remarks>
    public float Temperature;

    /// <summary>The distance at which its contribution reaches zero. Unused by a directional light.</summary>
    public float Range;

    /// <summary>Its sphere radius, or a rectangle's half-height. Zero for a punctual light.</summary>
    public float Radius;

    /// <summary>The inner cone half-angle in radians, inside which a spot is at full brightness.</summary>
    public float InnerAngle;

    /// <summary>The outer cone half-angle in radians, outside which a spot contributes nothing.</summary>
    public float OuterAngle;

    /// <summary>Half a tube's length, or half a rectangle's width. Zero for a punctual light.</summary>
    public float HalfLength;

    /// <summary>A point light you can see by, which is what an Add Component hands over.</summary>
    /// <remarks>
    ///     ⚠ <b>Explicit, so <c>Light.Default</c> does not become a second spelling of
    ///     <see cref="Lights.Default(LightKind)" />.</b> That one takes the kind and this one has to
    ///     pick, and a point light is the pick: it is the only kind that shows every field doing
    ///     something — a directional light does not use the range, and a spot needs two angles
    ///     explained before it looks like anything.
    /// </remarks>
    static Light IDefaultComponent<Light>.DefaultValue => Lights.Default(LightKind.Point);
}

/// <summary>Reading and writing an entity's light, and what the kinds are called.</summary>
public static class Lights {
    /// <summary>Every kind, in the order a menu should offer them.</summary>
    /// <remarks>
    ///     ⚠ <b>Written out rather than taken from <c>Enum.GetValues</c></b>, for the reason
    ///     <see cref="PrimitiveShapes.All" /> gives: the enum's order is a wire format shared with
    ///     <c>Raven/Library/Shading/Lighting.rvn</c> and must not change, and a menu's order is what
    ///     somebody reaches for most. Directional and Point are first because they are what a scene
    ///     gets lit with; the two area kinds are last because they are what it gets finished with.
    /// </remarks>
    public static IReadOnlyList<LightKind> All { get; } = [
        LightKind.Directional,
        LightKind.Point,
        LightKind.Spot,
        LightKind.Rect,
        LightKind.Tube
    ];

    /// <summary>What a kind is called in a scene file and in a command id.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>Its name.</returns>
    public static string NameOf(LightKind kind) => kind.ToString();

    /// <summary>What a kind is called in a menu.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>Its title.</returns>
    /// <remarks>
    ///     ⚠ <b>Not the same string as <see cref="NameOf" />, which is a departure from how shapes are
    ///     named and is forced.</b> "Cube" is a complete answer to what a menu line makes and "Point"
    ///     is not — every one of these lines makes a <i>light</i>, and a Create menu that read
    ///     "Directional, Point, Spot" leaves the reader to supply the noun. <c>Rect</c> is the shader's
    ///     name for the shape and "Area Light" is what every author calls it, so that one differs by
    ///     more than a suffix.
    /// </remarks>
    public static string TitleOf(LightKind kind) =>
        kind switch {
            LightKind.Rect => "Area Light",
            _ => NameOf(kind) + " Light"
        };

    /// <summary>Reads a kind's name back, tolerating one that is not a kind.</summary>
    /// <param name="text">The name, or null or empty for "not a light".</param>
    /// <param name="kind">The kind.</param>
    /// <returns>Whether it named one.</returns>
    /// <remarks>
    ///     An unrecognised name is <see langword="false" /> rather than an exception, which is
    ///     <see cref="PrimitiveShapes.TryParse" />'s argument exactly: a scene written by a newer editor
    ///     that knows a sixth kind should open, minus that entity's light.
    /// </remarks>
    public static bool TryParse(string? text, out LightKind kind) {
        if (!string.IsNullOrWhiteSpace(text)) {
            return Enum.TryParse(text.Trim(), ignoreCase: true, out kind) && All.Contains(kind);
        }

        kind = default;
        return false;
    }

    /// <summary>A light of a kind, with values that light something.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>The light.</returns>
    /// <remarks>
    ///     ⚠ <b>Not <c>default</c>, and the difference is whether the thing works.</b> A zeroed light
    ///     has no intensity and no range, so a scene lit by one is a black scene — the same failure
    ///     <c>Camera.Perspective</c> exists to avoid, where a zeroed camera has a zero far plane and
    ///     every matrix built from it is degenerate. A new light is a light you can see by.
    /// </remarks>
    public static Light Default(LightKind kind) {
        var light = new Light {
            Kind = kind,
            Colour = new Color3(1f, 1f, 1f),
            Intensity = 1f,
            Range = 10f
        };

        switch (kind) {
            case LightKind.Directional:
                // No position and no reach: the sun does not fall off, and a range on it would be a
                // number that looks like it does something.
                light.Range = 0f;
                break;

            case LightKind.Spot:
                light.InnerAngle = MathUtil.DegreesToRadians(20f);
                light.OuterAngle = MathUtil.DegreesToRadians(30f);
                break;

            case LightKind.Rect:
                // Half-extents, so this is a one-by-one metre softbox.
                light.Radius = 0.5f;
                light.HalfLength = 0.5f;
                break;

            case LightKind.Tube:
                light.Radius = 0.05f;
                light.HalfLength = 0.5f;
                break;

            default:
                break;
        }

        return light;
    }

    /// <summary>Gives an entity a light, or replaces the one it has.</summary>
    /// <param name="world">The world it lives in.</param>
    /// <param name="entity">The entity.</param>
    /// <param name="light">The light.</param>
    public static void Attach(World world, Entity entity, Light light) {
        ArgumentNullException.ThrowIfNull(world);

        if (world.Has<Light>(entity)) {
            world.Set(entity, in light);
            return;
        }

        world.Add(entity, in light);
    }

    /// <summary>Gives an entity a light of a kind, with that kind's defaults.</summary>
    /// <param name="world">The world it lives in.</param>
    /// <param name="entity">The entity.</param>
    /// <param name="kind">The kind.</param>
    public static void Attach(World world, Entity entity, LightKind kind) => Attach(world, entity, Default(kind));

    /// <summary>What light an entity is, if it is one.</summary>
    /// <param name="world">The world.</param>
    /// <param name="entity">The entity.</param>
    /// <param name="light">The light.</param>
    /// <returns>Whether it has one.</returns>
    public static bool TryGet(World world, Entity entity, out Light light) {
        ArgumentNullException.ThrowIfNull(world);

        if (world.IsAlive(entity) && world.Has<Light>(entity)) {
            light = world.Read<Light>(entity);
            return true;
        }

        light = default;
        return false;
    }
}
