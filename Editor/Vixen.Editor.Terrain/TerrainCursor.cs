// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Editor.SceneView;
using Vixen.Terrain;
using TerrainMap = Vixen.Terrain.Terrain;

namespace Vixen.Editor.Terrain;

/// <summary>
///     The brush's footprint, drawn on the ground under the pointer.
/// </summary>
/// <remarks>
///     <para>
///         <b>Two rings and not one.</b> A single circle says how far the brush reaches and says
///         nothing about the soft edge, which is the number that decides what a stroke actually looks
///         like — a radius of eight at falloff 0 and the same radius at falloff 1 are a disc and a
///         smudge. The outer ring is <see cref="TerrainBrush.Radius" /> and the inner one is where the
///         plateau ends, at <c>radius × (1 − falloff)</c>, which is <see cref="TerrainBrush.WeightAt" />'s
///         own boundary rather than a decorative second circle.
///     </para>
///     <para>
///         ⚠ <b>Conformed to the ground rather than drawn flat at the hit's height.</b> A flat disc on
///         a hillside is half buried and half in the air, and where it is buried it is invisible —
///         which is the state the tool was in before this existed, on exactly the terrain somebody is
///         most likely to be sculpting. Every point of the ring is its own
///         <see cref="TerrainPick.HeightAt" />, which is the same bilinear surface the pick that
///         placed it used, so the ring lands where the stamp will.
///     </para>
///     <para>
///         ⚠ <b>The segment count follows the radius, not the ring.</b> A fixed thirty-two segments is
///         a smooth circle at any size and a *liar* about the ground at a large one: chords a hundred
///         metres long fly over the valleys between their ends. Sampling about twice a quad is what
///         makes the ring follow the surface it is describing, and it is clamped at both ends so a
///         tiny brush is still round and a four-kilometre one is still cheap.
///     </para>
///     <para>
///         ⚠ <b>It draws into the overlay channel — see <see cref="SceneViewport.Cursor" />.</b> A
///         ring lying on the ground is coplanar with the ground; depth-tested it would z-fight in and
///         out along its length as the camera moved.
///     </para>
/// </remarks>
static class TerrainCursor {
    /// <summary>The fewest segments a ring is drawn with, however small it is.</summary>
    /// <remarks>Forty-eight, which is round at the size a brush is aimed from.</remarks>
    public const int MinimumSegments = 48;

    /// <summary>And the most, however large.</summary>
    /// <remarks>
    ///     A four-kilometre brush on a one-metre grid would want fifty thousand segments to sample
    ///     every quad, which is a megabyte of line vertices for a cursor. Five hundred and twelve is
    ///     past the point where a further segment is a subpixel change on screen.
    /// </remarks>
    public const int MaximumSegments = 512;

    /// <summary>How many samples of the ring there are per quad it crosses.</summary>
    public const float SamplesPerQuad = 2f;

    /// <summary>What the outer ring — the brush's reach — is drawn in.</summary>
    /// <remarks>
    ///     ⚠ <b>Not <c>SceneLines.SelectedColour</c>'s amber, which is a metre away from it on the
    ///     wheel.</b> A brush ring and a selection are on screen together constantly and mean opposite
    ///     things — one is what is about to change and the other is what already did — so the cursor
    ///     is a cyan nothing else in the viewport uses.
    /// </remarks>
    public static Color4 Outer { get; } = new(0.35f, 0.9f, 1f, 0.95f);

    /// <summary>And the inner one, where the falloff begins.</summary>
    /// <remarks>The same hue at less than half the alpha: it is the same brush, said quieter.</remarks>
    public static Color4 Inner { get; } = new(0.35f, 0.9f, 1f, 0.4f);

    /// <summary>Draws a brush footprint on the ground.</summary>
    /// <param name="draw">Where the lines go.</param>
    /// <param name="terrain">The terrain the ring is conformed to.</param>
    /// <param name="origin">Where the terrain's samples sit, in world space.</param>
    /// <param name="ground">The brush's centre, in the terrain's own XZ.</param>
    /// <param name="radius">How far it reaches, in metres.</param>
    /// <param name="falloff">What fraction of that is soft edge, 0…1.</param>
    public static void Draw(
        GizmoDraw draw,
        TerrainMap terrain,
        Vector3 origin,
        Vector2 ground,
        float radius,
        float falloff
    ) {
        ArgumentNullException.ThrowIfNull(draw);
        ArgumentNullException.ThrowIfNull(terrain);

        if (!(radius > 0f) || !float.IsFinite(radius)) {
            return;
        }

        Ring(draw, terrain, origin, ground, radius, Outer);

        // ⚠ Only when it is a ring of its own. At falloff 1 the plateau is a point and at falloff 0
        // the inner ring is exactly the outer one — drawing it there is a second set of lines over
        // the first, which reads on screen as the cursor being brighter for no reason a user can
        // name.
        var plateau = radius * (1f - Math.Clamp(falloff, 0f, 1f));

        if (plateau > radius * 0.02f && plateau < radius * 0.98f) {
            Ring(draw, terrain, origin, ground, plateau, Inner);
        }
    }

    /// <summary>How many segments a ring of a radius is drawn with on a terrain.</summary>
    /// <param name="terrain">The terrain, whose quad size sets the sampling rate.</param>
    /// <param name="radius">The radius, in metres.</param>
    /// <returns>The count, between <see cref="MinimumSegments" /> and <see cref="MaximumSegments" />.</returns>
    public static int SegmentsFor(TerrainMap terrain, float radius) {
        ArgumentNullException.ThrowIfNull(terrain);

        var quad = terrain.Description.MetresPerQuad;

        if (!(quad > 0f)) {
            return MinimumSegments;
        }

        var wanted = MathF.Tau * radius / quad * SamplesPerQuad;

        return wanted <= MinimumSegments
            ? MinimumSegments
            : wanted >= MaximumSegments
                ? MaximumSegments
                : (int)wanted;
    }

    /// <summary>One closed ring, every vertex at its own ground height.</summary>
    static void Ring(
        GizmoDraw draw,
        TerrainMap terrain,
        Vector3 origin,
        Vector2 ground,
        float radius,
        Color4 colour
    ) {
        var segments = SegmentsFor(terrain, radius);
        var previous = On(terrain, origin, ground, radius, 0f);

        for (var i = 1; i <= segments; i++) {
            var next = On(terrain, origin, ground, radius, (float)i / segments);

            draw.Line(previous, next, colour);
            previous = next;
        }
    }

    /// <summary>A point of the ring, in world space, sitting on the ground.</summary>
    static Vector3 On(TerrainMap terrain, Vector3 origin, Vector2 ground, float radius, float turn) {
        var angle = turn * MathF.Tau;
        var x = ground.X + (MathF.Cos(angle) * radius);
        var z = ground.Y + (MathF.Sin(angle) * radius);

        return origin + new Vector3(x, TerrainPick.HeightAt(terrain, x, z), z);
    }
}
