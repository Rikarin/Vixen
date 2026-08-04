// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Terrain;

/// <summary>How a spline shapes the ground either side of it.</summary>
/// <remarks>
///     <para>
///         <b>[docs/plan/31 § T8]'s road profile.</b> A flat carriageway of
///         <see cref="HalfWidth" /> metres either side, then a cosine shoulder that blends into
///         whatever the ground was doing.
///     </para>
///     <para>
///         ⚠ <b>Left and right are independent, and that is not symmetry for its own sake.</b> A road
///         cut into a hillside has a cutting on the uphill side and an embankment on the downhill one,
///         and they are different widths. A single falloff makes every road look like it was laid on
///         a plain.
///     </para>
///     <para>
///         ⚠ <b>A cosine and not a linear ramp.</b> A linear shoulder meets the untouched ground at a
///         crease, which catches the light along the whole length of the road — a cosine's derivative
///         is zero at both ends, so the shoulder leaves and arrives flat.
///     </para>
/// </remarks>
public readonly record struct TerrainSplineProfile {
    /// <summary>How far the flat carriageway reaches either side of the curve, in metres.</summary>
    public float HalfWidth { get; init; }

    /// <summary>How far the shoulder reaches past that on the left, in metres.</summary>
    /// <remarks>Left is the curve's binormal side: <c>tangent × up</c>.</remarks>
    public float FalloffLeft { get; init; }

    /// <summary>And on the right.</summary>
    public float FalloffRight { get; init; }

    /// <summary>How much of the way to the curve's own height the ground is moved, 0…1.</summary>
    /// <remarks>
    ///     ⚠ <b>One is a road and less than one is a hint.</b> A river bed authored at 0.6 follows the
    ///     valley it was drawn in rather than flattening it, which is the difference between a
    ///     watercourse and a canal.
    /// </remarks>
    public float Strength { get; init; }

    /// <summary>How far below the curve the carriageway sits, in metres.</summary>
    /// <remarks>
    ///     Negative raises. What a river bed uses, and what a causeway uses with the sign flipped —
    ///     an author draws the centreline where they can see it and then sinks it.
    /// </remarks>
    public float Depth { get; init; }

    /// <summary>A road: three metres of carriageway and four of shoulder either side.</summary>
    public static TerrainSplineProfile Road =>
        new() {
            HalfWidth = 3f,
            FalloffLeft = 4f,
            FalloffRight = 4f,
            Strength = 1f,
            Depth = 0f
        };

    /// <summary>The furthest the profile reaches from the curve, in metres.</summary>
    public float Reach => MathF.Max(HalfWidth + FalloffLeft, HalfWidth + FalloffRight);

    /// <summary>How much the profile moves the ground at a signed distance from the curve.</summary>
    /// <param name="offset">
    ///     How far to the side, in metres. Positive is the curve's left — <c>tangent × up</c>.
    /// </param>
    /// <returns>The weight, 0…1.</returns>
    public float WeightAt(float offset) {
        var distance = MathF.Abs(offset);
        var half = MathF.Max(HalfWidth, 0f);

        if (distance <= half) {
            return 1f;
        }

        var falloff = offset >= 0f ? FalloffLeft : FalloffRight;

        if (!(falloff > 0f)) {
            return 0f;
        }

        var t = Math.Clamp((distance - half) / falloff, 0f, 1f);

        // A raised cosine: 1 at the carriageway edge, 0 at the shoulder's, flat at both.
        return 0.5f * (1f + MathF.Cos(t * MathF.PI));
    }

    /// <summary>Why this profile cannot be applied, or <see langword="null" /> if it can.</summary>
    public string? Validate() {
        if (HalfWidth < 0f || FalloffLeft < 0f || FalloffRight < 0f) {
            return $"A profile of {HalfWidth} m half-width with {FalloffLeft} / {FalloffRight} m "
                + "shoulders has a negative width somewhere.";
        }

        if (!(Reach > 0f)) {
            return "A profile with no width and no shoulders touches nothing.";
        }

        return null;
    }
}

/// <summary>What one mesh placed along a spline is.</summary>
/// <param name="Mesh">Which mesh, by asset name.</param>
/// <param name="Position">Where it stands, in world space.</param>
/// <param name="Rotation">Which way it faces: the curve's frame, with the point's roll.</param>
/// <param name="Distance">How far along the curve it is, in metres.</param>
public readonly record struct TerrainSplineMesh(
    string Mesh,
    Vector3 Position,
    Quaternion Rotation,
    float Distance
);

/// <summary>
///     Roads, rivers and paths: a spline that deforms the terrain, paints along its width and places
///     meshes along its length.
/// </summary>
/// <remarks>
///     <para>
///         <b>[docs/plan/31 § T8]'s terrain half.</b> Three operations over one curve, and all three
///         are re-runnable: the deformation goes into a reserved
///         <see cref="TerrainLayerKind.Splines" /> layer that is regenerated wholesale, the painting
///         goes into the weightmap through the same redistribution every brush uses, and the mesh
///         placement returns a list rather than writing one.
///     </para>
///     <para>
///         ⚠ <b>The deformation is non-destructive because of where it goes, not because of what it
///         does.</b> [§ D4]'s reserved layer is the whole mechanism: moving the road, changing its
///         width or deleting it re-runs into the same layer and the author's own sculpting underneath
///         is untouched. A road written into the base heightfield is a road that can never be moved.
///     </para>
///     <para>
///         ⚠ <b>Every sample within reach is visited once, from the curve's own bounding box.</b> The
///         alternative — walking the curve and stamping a brush at intervals — double-counts wherever
///         two stamps overlap, which on a tight bend is most of the inside of the corner. A road
///         stamped that way is deeper round its corners than along its straights.
///     </para>
/// </remarks>
public static class TerrainSpline {
    /// <summary>How many metres apart the deformation samples the curve when locating a point.</summary>
    /// <remarks>
    ///     <see cref="Spline.DistanceTo" /> already refines; this is only the granularity of the
    ///     initial rectangle, and it is one quad because that is the resolution the answer is written
    ///     at.
    /// </remarks>
    public const float LocateStep = 1f;

    /// <summary>Deforms the ground under a spline into an edit layer.</summary>
    /// <param name="terrain">The terrain.</param>
    /// <param name="layer">The layer to write. Cleared over the affected rect first.</param>
    /// <param name="spline">The curve, in the terrain's own space.</param>
    /// <param name="profile">How wide the road is and how it falls off.</param>
    /// <returns>The rect that changed.</returns>
    /// <exception cref="ArgumentNullException">Something was not supplied.</exception>
    /// <exception cref="ArgumentException">The profile touches nothing.</exception>
    /// <remarks>
    ///     ⚠ <b>Only this road's own rect is cleared, which is not enough when a road
    ///     <em>moves</em>.</b> A centreline dragged twenty metres leaves its old rect untouched,
    ///     because the new one no longer covers it — so what an editor calls is
    ///     <see cref="Regenerate" />, which empties the layer and lays every road down again. This is
    ///     the operation for adding one road to a layer that is otherwise already right.
    /// </remarks>
    public static TerrainRect Deform(
        Terrain terrain,
        TerrainEditLayer layer,
        Spline spline,
        in TerrainSplineProfile profile
    ) {
        ArgumentNullException.ThrowIfNull(terrain);
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentNullException.ThrowIfNull(spline);

        if (profile.Validate() is { } problem) {
            throw new ArgumentException(problem, nameof(profile));
        }

        var description = terrain.Description;
        var rect = RectOf(description, spline, profile.Reach);

        if (rect.IsEmpty) {
            return TerrainRect.Empty;
        }

        layer.Clear(rect);

        var scale = description.MetresPerQuad;
        var strength = Math.Clamp(profile.Strength, 0f, 1f);

        for (var z = rect.Z; z < rect.EndZ; z++) {
            for (var x = rect.X; x < rect.EndX; x++) {
                var ground = new Vector2(x * scale, z * scale);
                var distance = Nearest(spline, ground, out var parameter);

                if (distance > profile.Reach) {
                    continue;
                }

                var frame = spline.FrameAt(parameter, Vector3.UnitY);
                var offset = Offset(ground, frame);
                var weight = profile.WeightAt(offset) * strength;

                if (weight <= 0f) {
                    continue;
                }

                var wanted = frame.Position.Y - profile.Depth;

                // The composite *without* this layer, which is what clearing the rect first bought:
                // reading the base instead would put a road cut into a hillside back at sea level
                // wherever an author's own sculpting had raised the ground under it.
                var current = description.HeightOf(terrain.CompositeAt(x, z));
                var moved = float.Lerp(current, wanted, weight);

                // A delta in stored steps, which is the layer's own unit. Rounding rather than
                // truncating, so a road at exactly one step's height does not sink by one every time
                // the layer is regenerated.
                var delta = (moved - current) / description.MetresPerStep;

                layer.SetDelta(x, z, (short)Math.Clamp(MathF.Round(delta), short.MinValue, short.MaxValue));
            }
        }

        terrain.Invalidate(rect);

        return rect;
    }

    /// <summary>Paints a layer along the width of a spline.</summary>
    /// <param name="terrain">The terrain.</param>
    /// <param name="spline">The curve, in the terrain's own space.</param>
    /// <param name="layer">Which paint layer, by index.</param>
    /// <param name="profile">How wide the painting is and how it falls off.</param>
    /// <param name="strength">How much of the layer to lay down at full weight, 0…1.</param>
    /// <returns>The rect that changed.</returns>
    /// <exception cref="ArgumentNullException">Something was not supplied.</exception>
    /// <exception cref="ArgumentOutOfRangeException">There is no such layer.</exception>
    /// <remarks>
    ///     ⚠ <b>Through <see cref="TerrainWeights.Paint" />, so the sum-to-one invariant is maintained
    ///     in one place.</b> Writing the channel directly would leave the other layers where they
    ///     were, and a sample whose weights sum to more than one draws as a brighter patch that no
    ///     tool can find.
    /// </remarks>
    public static TerrainRect PaintAlong(
        Terrain terrain,
        Spline spline,
        int layer,
        in TerrainSplineProfile profile,
        float strength = 1f
    ) {
        ArgumentNullException.ThrowIfNull(terrain);
        ArgumentNullException.ThrowIfNull(spline);
        ArgumentOutOfRangeException.ThrowIfNegative(layer);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(layer, terrain.Weights.LayerCount);

        var description = terrain.Description;
        var rect = RectOf(description, spline, profile.Reach);

        if (rect.IsEmpty) {
            return TerrainRect.Empty;
        }

        var scale = description.MetresPerQuad;
        var amount = Math.Clamp(strength, 0f, 1f);

        for (var z = rect.Z; z < rect.EndZ; z++) {
            for (var x = rect.X; x < rect.EndX; x++) {
                var ground = new Vector2(x * scale, z * scale);
                var distance = Nearest(spline, ground, out var parameter);

                if (distance > profile.Reach) {
                    continue;
                }

                var frame = spline.FrameAt(parameter, Vector3.UnitY);
                var offset = Offset(ground, frame);
                var weight = profile.WeightAt(offset) * amount;

                if (weight <= 0f) {
                    continue;
                }

                var wanted = (int)MathF.Round(weight * TerrainWeights.Total);
                var already = terrain.Weights.WeightAt(layer, x, z);

                if (wanted > already) {
                    terrain.Weights.Paint(layer, x, z, wanted - already);
                }
            }
        }

        return rect;
    }

    /// <summary>Places meshes along the length of a spline.</summary>
    /// <param name="spline">The curve.</param>
    /// <param name="meshes">Which meshes to choose between, by asset name.</param>
    /// <param name="spacing">How far apart, in metres along the curve.</param>
    /// <param name="seed">What the choice and the jitter derive from.</param>
    /// <param name="jitter">How far along the curve a placement may wander, 0…1 of the spacing.</param>
    /// <returns>The placements, in order along the curve.</returns>
    /// <exception cref="ArgumentNullException">There is no curve or no mesh list.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The spacing is not positive.</exception>
    /// <remarks>
    ///     <para>
    ///         <b>Fence posts, lamp-posts, rocks along a riverbank.</b> Spaced by <em>distance</em>
    ///         rather than by parameter, which is what <see cref="Spline.EvaluateAtDistance" /> is for
    ///         — spacing by parameter bunches everything up in the tight segments and strings it out
    ///         in the wide ones, which is exactly wrong for a fence.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The choice is hashed from the index, not drawn from a generator.</b> Re-running
    ///         after moving one control point must not re-roll the whole fence, and a sequence would.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It returns placements rather than writing them anywhere.</b> This assembly has no
    ///         scene and no asset database; what a caller does with the list — spawn entities, fill a
    ///         foliage volume, hand it to an instancing batch — is the caller's.
    ///     </para>
    /// </remarks>
    public static IReadOnlyList<TerrainSplineMesh> PlaceAlong(
        Spline spline,
        IReadOnlyList<string> meshes,
        float spacing,
        uint seed = 0x9E3779B9u,
        float jitter = 0f
    ) {
        ArgumentNullException.ThrowIfNull(spline);
        ArgumentNullException.ThrowIfNull(meshes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(spacing);

        if (meshes.Count == 0 || !(spline.Length > 0f)) {
            return [];
        }

        var placements = new List<TerrainSplineMesh>();
        var count = (int)MathF.Floor(spline.Length / spacing);
        var wander = Math.Clamp(jitter, 0f, 1f) * spacing * 0.5f;

        for (var index = 0; index <= count; index++) {
            var hash = Hash(seed, index);
            var at = (index * spacing) + (((Unit(hash, 1) * 2f) - 1f) * wander);

            at = Math.Clamp(at, 0f, spline.Length);

            var parameter = spline.ParameterAtDistance(at);
            var frame = spline.FrameAt(parameter, Vector3.UnitY);
            var mesh = meshes[(int)(hash % (uint)meshes.Count)];

            placements.Add(new(mesh, frame.Position, Facing(frame), at));
        }

        return placements;
    }

    /// <summary>Empties the spline layer and lays every road down again.</summary>
    /// <param name="terrain">The terrain.</param>
    /// <param name="layer">The reserved layer.</param>
    /// <param name="roads">Every spline that deforms the ground, with its profile.</param>
    /// <returns>The rect that changed, which covers where the roads were as well as where they are.</returns>
    /// <exception cref="ArgumentNullException">Something was not supplied.</exception>
    /// <remarks>
    ///     <para>
    ///         <b>[§ D4]'s reserved layer, regenerated wholesale — and wholesale is the word.</b>
    ///         Moving a road, narrowing it or deleting it are all the same operation from here: empty
    ///         the layer, lay down what there now is. <see cref="Deform" /> clears only its own rect,
    ///         which is right for adding a road and wrong for moving one.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The invalidated rect covers where the roads <em>were</em>, from the chunks the
    ///         layer had already allocated.</b> Invalidating only the new rects would leave the cached
    ///         composite holding the old road wherever the two do not overlap — which draws as a road
    ///         that has been moved and also has not.
    ///     </para>
    /// </remarks>
    public static TerrainRect Regenerate(
        Terrain terrain,
        TerrainEditLayer layer,
        IEnumerable<(Spline Spline, TerrainSplineProfile Profile)> roads
    ) {
        ArgumentNullException.ThrowIfNull(terrain);
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentNullException.ThrowIfNull(roads);

        var dirty = Occupied(layer);

        layer.Clear();

        foreach (var (spline, profile) in roads) {
            var rect = Deform(terrain, layer, spline, profile);

            dirty = dirty.IsEmpty ? rect : rect.IsEmpty ? dirty : dirty.Union(rect);
        }

        if (!dirty.IsEmpty) {
            terrain.Invalidate(dirty);
        }

        return dirty;
    }

    /// <summary>The samples a layer's allocated chunks cover.</summary>
    static TerrainRect Occupied(TerrainEditLayer layer) {
        var rect = TerrainRect.Empty;

        foreach (var (x, z) in layer.OccupiedChunks()) {
            var chunk = new TerrainRect(
                x * TerrainEditLayer.ChunkSize,
                z * TerrainEditLayer.ChunkSize,
                TerrainEditLayer.ChunkSize,
                TerrainEditLayer.ChunkSize
            );

            rect = rect.IsEmpty ? chunk : rect.Union(chunk);
        }

        return rect;
    }

    /// <summary>The layer a terrain reserves for its splines, adding it if there is not one.</summary>
    /// <param name="terrain">The terrain.</param>
    /// <param name="name">What to call it.</param>
    /// <returns>The layer.</returns>
    /// <remarks>
    ///     ⚠ <b>One layer for every spline, not one per spline.</b> Two roads crossing have to agree
    ///     about the height at the junction, and two layers would give the answer to whichever
    ///     composited last. It is also what makes "regenerate the splines" one operation.
    /// </remarks>
    public static TerrainEditLayer LayerOf(Terrain terrain, string name = "Splines") {
        ArgumentNullException.ThrowIfNull(terrain);
        return terrain.ReservedLayer(TerrainLayerKind.Splines, name);
    }

    /// <summary>The samples a curve of this reach could touch, clipped to the terrain.</summary>
    static TerrainRect RectOf(in TerrainDescription description, Spline spline, float reach) {
        var minimum = new Vector3(float.PositiveInfinity);
        var maximum = new Vector3(float.NegativeInfinity);

        // The control points *and* the curve between them: a Hermite segment leaves the hull of its
        // two endpoints whenever the tangents are long, and a road whose bounding rect stopped at its
        // control points would have its bends cut off.
        var steps = Math.Max(8, (int)MathF.Ceiling(spline.Length / LocateStep));

        for (var step = 0; step <= steps; step++) {
            var point = spline.Evaluate(spline.MaxParameter * (step / (float)steps));

            minimum = Vector3.Min(minimum, point);
            maximum = Vector3.Max(maximum, point);
        }

        var scale = description.MetresPerQuad;
        var low = new Vector2(minimum.X - reach, minimum.Z - reach) / scale;
        var high = new Vector2(maximum.X + reach, maximum.Z + reach) / scale;

        var x0 = (int)MathF.Floor(low.X);
        var z0 = (int)MathF.Floor(low.Y);
        var x1 = (int)MathF.Ceiling(high.X);
        var z1 = (int)MathF.Ceiling(high.Y);

        return new TerrainRect(x0, z0, x1 - x0 + 1, z1 - z0 + 1)
            .Clip(new(0, 0, description.SamplesX, description.SamplesZ));
    }

    /// <summary>The nearest point of a curve to a place on the ground, measured horizontally.</summary>
    /// <param name="spline">The curve.</param>
    /// <param name="ground">Where, in world XZ.</param>
    /// <param name="parameter">The parameter there.</param>
    /// <returns>The horizontal distance, in metres.</returns>
    /// <remarks>
    ///     ⚠ <b>Horizontal, and not <see cref="Spline.DistanceTo" />, because a road's width is
    ///     measured across the ground rather than through the air.</b> The 3D distance is the one a
    ///     camera wants; used here it means a road can only deform ground it is already level with —
    ///     so a centreline drawn twenty metres above a valley floor, which is exactly how an author
    ///     draws a causeway, touches nothing at all. Cutting and filling is the whole point of a
    ///     spline that deforms.
    ///     <para>
    ///         A scan of the same table <see cref="Spline.DistanceTo" /> uses, then a ternary search
    ///         over the span it localised to. Accurate to well inside a half-width.
    ///     </para>
    /// </remarks>
    public static float Nearest(Spline spline, Vector2 ground, out float parameter) {
        ArgumentNullException.ThrowIfNull(spline);

        var steps = Math.Max(8, (int)MathF.Ceiling(spline.Length / LocateStep));
        var best = float.PositiveInfinity;
        var bestAt = 0f;

        for (var step = 0; step <= steps; step++) {
            var at = spline.MaxParameter * (step / (float)steps);
            var distance = Flat(spline.Evaluate(at), ground);

            if (distance < best) {
                best = distance;
                bestAt = at;
            }
        }

        var span = spline.MaxParameter / steps;
        var low = MathF.Max(0f, bestAt - span);
        var high = MathF.Min(spline.MaxParameter, bestAt + span);

        for (var iteration = 0; iteration < 32 && high - low > 1e-6f; iteration++) {
            var third = (high - low) / 3f;
            var a = low + third;
            var b = high - third;

            if (Flat(spline.Evaluate(a), ground) < Flat(spline.Evaluate(b), ground)) {
                high = b;
            } else {
                low = a;
            }
        }

        parameter = (low + high) * 0.5f;

        return MathF.Sqrt(Flat(spline.Evaluate(parameter), ground));
    }

    /// <summary>How far to the curve's left a place on the ground is, in metres.</summary>
    static float Offset(Vector2 ground, in SplineFrame frame) {
        var side = Side(frame);
        var away = new Vector3(ground.X - frame.Position.X, 0f, ground.Y - frame.Position.Z);

        return Vector3.Dot(away, side);
    }

    /// <summary>Squared horizontal distance, which is what the searches compare.</summary>
    static float Flat(Vector3 point, Vector2 ground) {
        var dx = point.X - ground.X;
        var dz = point.Z - ground.Y;

        return (dx * dx) + (dz * dz);
    }

    /// <summary>The curve's left, which is what a signed offset is measured along.</summary>
    /// <remarks>
    ///     The frame's binormal, flattened onto the horizontal plane and renormalised: a road is
    ///     measured across the ground rather than across its own banking, or a banked corner would be
    ///     narrower than the straight it joins.
    /// </remarks>
    static Vector3 Side(in SplineFrame frame) {
        var side = new Vector3(frame.Binormal.X, 0f, frame.Binormal.Z);

        return side.LengthSquared() > 1e-8f ? Vector3.Normalize(side) : Vector3.UnitX;
    }

    /// <summary>The rotation that stands a mesh on the curve, facing along it.</summary>
    /// <remarks>
    ///     Up onto the frame's normal, then twisted about it until the mesh's forward lies along the
    ///     tangent. Two well-defined rotations rather than a basis matrix converted to a quaternion,
    ///     because a basis has a handedness convention to get wrong and these do not.
    /// </remarks>
    static Quaternion Facing(in SplineFrame frame) {
        var upright = Quaternion.FromToRotation(Vector3.UnitY, frame.Normal);
        var forward = Quaternion.Transform(Vector3.Forward, upright);
        var twist = Quaternion.FromToRotation(forward, frame.Tangent);

        return Quaternion.Normalize(upright * twist);
    }

    static uint Hash(uint seed, int index) {
        var hash = seed ^ (uint)index;

        hash ^= hash >> 16;
        hash *= 0x7FEB352Du;
        hash ^= hash >> 15;
        hash *= 0x846CA68Bu;
        hash ^= hash >> 16;

        return hash;
    }

    static float Unit(uint hash, int stream) => Hash(hash ^ (uint)(stream * 0x27D4EB2), stream) / 4294967296f;
}
