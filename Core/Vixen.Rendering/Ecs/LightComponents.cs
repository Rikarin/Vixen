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

    /// <summary>A clear midday sun, in lux, which is what a new directional light is.</summary>
    /// <remarks>
    ///     <see cref="Lighting.PhysicalSky.SunIlluminance" /> works out about 95 klx overhead on a
    ///     clear day from a 128 klx solar constant, so this is that number rounded to the one every
    ///     daylight table quotes. It is deliberately not a sunset value: a sun somebody has to turn
    ///     <i>down</i> reads as a light that works, and a sun somebody has to turn up four decimal
    ///     orders reads as a broken renderer.
    /// </remarks>
    const float DaylightIlluminance = 100_000f;

    /// <summary>The luminous flux of the lamp every other kind's default is one of, in lumens.</summary>
    /// <remarks>
    ///     A 1600-lumen bulb — the 100 W equivalent on a supermarket shelf — because the four kinds
    ///     that have a position are all lamps and a lamp is sold in lumens. It is converted into each
    ///     kind's own unit below rather than stored as one, so <see cref="Light.Unit" /> never has to
    ///     be <see cref="LightUnit.Lumen" /> on a default and <c>Default(kind) with { Intensity = x }</c>
    ///     keeps meaning what it has always meant.
    /// </remarks>
    const float LampFlux = 1600f;

    /// <summary>A light of a kind, with values that light something.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>The light.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Not <c>default</c>, and the difference is whether the thing works.</b> A zeroed
    ///         light has no intensity and no range, so a scene lit by one is a black scene — the same
    ///         failure <c>Camera.Perspective</c> exists to avoid, where a zeroed camera has a zero far
    ///         plane and every matrix built from it is degenerate.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>"A light you can see by" is a different number for every kind, and that is the
    ///         correction this paragraph records.</b> This method used to hand back <c>Intensity = 1</c>
    ///         for all five with <see cref="Light.Unit" /> left at <see cref="LightUnit.Native" />, and
    ///         the claim was only true of the punctual ones — one candela is a dim candle, and one
    ///         <em>lux</em> is a directional light four to five decimal orders below the sky it is
    ///         standing under. A frame lit by that sun is pixel-identical to a frame with no sun in it,
    ///         so an author dragging one in saw nothing, turned it up to ten, and still saw nothing.
    ///         Each kind now gets a real lamp in its own unit:
    ///         <list type="bullet">
    ///             <item>Directional — <see cref="DaylightIlluminance" /> lux, a clear midday sun.</item>
    ///             <item>Point and Spot — <see cref="LampFlux" /> lumens as candela, so the spot is the
    ///             brighter of the two by exactly what its cone concentrates.</item>
    ///             <item>Rect and Tube — the same lamp as nits through the surface each one has.</item>
    ///         </list>
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The unit is stated rather than left at <see cref="LightUnit.Native" />, and it is
    ///         always the kind's own.</b> Native and the kind's own unit are the same arithmetic —
    ///         <see cref="Photometry.Intensity" /> only ever divides for <see cref="LightUnit.Lumen" />
    ///         — so writing it changes no pixel and labels the number, which is the whole difference
    ///         between an inspector row that reads "100000" and one that reads "100000 lux".
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Creation only.</b> Nothing here reaches a load: a compiled scene's component starts
    ///         at <c>default(T)</c> and is overwritten field by field, and the editor's YAML reader
    ///         builds its own zeroed <c>Light</c> — see <c>ISceneComponentBinder.CreateDefault</c>, whose
    ///         remark is the promise this relies on. Changing these numbers changes what an Add
    ///         Component and a Create Light hand over and changes no saved scene.
    ///     </para>
    /// </remarks>
    public static Light Default(LightKind kind) {
        var light = new Light {
            Kind = kind,
            Colour = new Color3(1f, 1f, 1f),
            Unit = NativeUnitOf(kind),
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

        // ⚠ After the switch, because three of the five conversions read the geometry it just set —
        // a spot's cone, a rectangle's extent, a tube's capsule. Doing it in the initialiser would
        // have divided the flux by a zero solid angle and by a zero area.
        //
        // Lumens mean nothing for a light with no position, so the sun is the one kind that is not
        // this lamp: `Photometry.Intensity` would hand a directional light its flux back unchanged
        // and call it lux, which is a number with a unit and no meaning.
        light.Intensity = kind is LightKind.Directional
            ? DaylightIlluminance
            : Photometry.Intensity(
                kind,
                LightUnit.Lumen,
                LampFlux,
                light.OuterAngle,
                light.Radius,
                light.HalfLength
            );

        return light;
    }

    /// <summary>The unit a kind's own numbers are already in — what <c>Native</c> means for it.</summary>
    /// <remarks>
    ///     ⚠ <b>Named after <see cref="LightUnit.Native" />'s own summary and has to keep agreeing with
    ///     it.</b> That enum member is documented as "candela for a point or a spot, lux for a
    ///     directional light, nits for an area one", and <see cref="Photometry.Intensity" /> is written
    ///     so that both readings coincide. A kind that disagreed here would be a default whose label
    ///     said one thing and whose arithmetic did another.
    /// </remarks>
    static LightUnit NativeUnitOf(LightKind kind) =>
        kind switch {
            LightKind.Directional => LightUnit.Lux,
            LightKind.Rect or LightKind.Tube => LightUnit.Nits,
            _ => LightUnit.Candela
        };

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
