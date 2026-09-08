// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Editor.Texturing.Painting;

/// <summary>
///     The three things <c>PaintSession</c> says a 3D surface owes, done: a ray to texels, a screen
///     radius to texels, and the mirrors.
/// </summary>
/// <remarks>
///     <para>
///         <b>Doc 48 § D13's first front end, as the object a viewport holds for the length of a
///         drag.</b> <c>PaintSession</c> owns spacing, jitter, smoothing, the cached composite, the
///         dilation and the single undo entry, and its remarks name exactly what it cannot do
///         because it has no geometry. This is that, and nothing else: it does not open a canvas, it
///         does not know what a layer is, and it never touches the atlas.
///     </para>
///     <para>
///         ⚠ <b>The radius is decided at pointer-down and cannot move afterwards, because
///         <c>PaintSession.Begin</c> takes the brush.</b> That is a consequence rather than a
///         simplification: a radius recomputed per stamp would be a brush that changed size as the
///         artist dragged across a chart boundary, which reads as a broken brush rather than as a
///         correct density. <see cref="Begin" /> is therefore where the conversion happens, and the
///         number it returns is what a caller puts on <c>PaintBrush.Radius</c> before beginning the
///         session.
///     </para>
///     <para>
///         ⚠ <b>The number of paths is fixed at pointer-down too, and <c>PaintSession.MoveAll</c>
///         throws if it is not.</b> A mirrored ray that misses the mesh cannot simply be skipped for
///         one move — that would change the path count mid-drag, which the session refuses on
///         purpose, because a mirror with no record leaves the undo entry restoring half of what the
///         drag painted. So a path that misses <em>holds its last position</em>, and holding is free:
///         <c>BrushStroke.MoveTo</c> lays a stamp only for a movement with a length, so a repeated
///         position deposits nothing at all. A mirror that misses at pointer-down has no first
///         position and is dropped for the whole drag, which is the one lossy case and is the same
///         rule stated once.
///     </para>
///     <para>
///         ⚠ <b>Nothing here evaluates the stack, and that is what makes exit criterion 8
///         reachable.</b> The composite is built inside <c>PaintSession.Begin</c> and resolved per
///         dirty rectangle; this type adds one raycast per path per pointer move, which is a
///         branch-and-bound descent of a BVH and is not a function of the layer count or of the
///         atlas size. <c>PaintProjectionTests</c> asserts the composite is evaluated once across a
///         whole projected drag rather than trusting that sentence.
///     </para>
/// </remarks>
sealed class PaintProjector {
    readonly PaintProjection projection;
    readonly int width;
    readonly int height;

    /// <summary>Where each path was last seen, so a ray that misses holds rather than vanishes.</summary>
    /// <remarks>Two at most — the hit and one mirror — so it is an array and never grows.</remarks>
    readonly Vector2[] held = new Vector2[2];

    int paths;
    bool started;

    /// <summary>Aims a brush at a mesh, for one atlas size.</summary>
    /// <param name="projection">The mesh.</param>
    /// <param name="width">The atlas width in texels.</param>
    /// <param name="height">Its height.</param>
    /// <exception cref="ArgumentNullException"><paramref name="projection" /> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The atlas has no area.</exception>
    public PaintProjector(PaintProjection projection, int width, int height) {
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        this.projection = projection;
        this.width = width;
        this.height = height;
    }

    /// <summary>The plane a stroke is mirrored through, or null for no symmetry.</summary>
    /// <remarks>
    ///     ⚠ <b>Read at pointer-down and not per move.</b> Turning symmetry on halfway through a
    ///     drag is precisely what <c>PaintSession.MoveAll</c> refuses, so this is latched by
    ///     <see cref="Begin" /> and a change during a drag takes effect on the next one.
    /// </remarks>
    public PaintSymmetry? Symmetry { get; set; }

    /// <summary>How many paths the drag has: one, plus one per mirror that found the mesh.</summary>
    public int Paths => paths;

    /// <summary>Where the primary ray last landed, in texels.</summary>
    public PaintHit Hit { get; private set; } = PaintHit.None;

    /// <summary>Pointer-down: the hit, the mirrors, and the radius in texels the brush should use.</summary>
    /// <param name="eye">The camera the ray came from.</param>
    /// <param name="ray">The ray under the pointer, in the mesh's own space.</param>
    /// <param name="screenRadius">How wide the brush is, in render pixels.</param>
    /// <param name="radius">The radius in texels, or zero when there is nothing to paint.</param>
    /// <returns>Whether the ray found the mesh.</returns>
    /// <remarks>
    ///     ⚠ <b>The radius comes off the <em>primary</em> hit and both paths get it.</b> A mirror
    ///     landing on a chart of a different density would otherwise paint a different size on the
    ///     two sides of a symmetric model, which is the one thing a symmetric stroke exists not to
    ///     do — and the session could not express it anyway, since a brush belongs to a session and
    ///     not to a stroke.
    /// </remarks>
    public bool Begin(PaintEye eye, Ray ray, float screenRadius, out float radius) {
        radius = 0f;
        started = false;
        paths = 0;
        Hit = PaintHit.None;

        if (!projection.TryHit(ray, out var hit)) {
            return false;
        }

        Hit = hit;
        radius = PaintFootprint.Radius(eye, hit, projection.Density(hit.Triangle, width, height), screenRadius);
        held[paths++] = PaintProjection.Texel(hit.Coordinate, width, height);

        if (Symmetry is { } plane && projection.TryHit(plane.Mirror(ray), out var mirrored)) {
            held[paths++] = PaintProjection.Texel(mirrored.Coordinate, width, height);
        }

        return true;
    }

    /// <summary>Pointer-move: where every path is now, in texels, for <c>PaintSession.MoveAll</c>.</summary>
    /// <param name="ray">The ray under the pointer, in the mesh's own space.</param>
    /// <returns>
    ///     One position per path, the primary first — the exact span
    ///     <c>PaintSession.MoveAll(ReadOnlySpan{Vector2})</c> takes. Empty before
    ///     <see cref="Begin" /> has found the mesh.
    /// </returns>
    /// <remarks>
    ///     ⚠ <b>The first move after <see cref="Begin" /> returns the positions <see cref="Begin" />
    ///     found, whatever this ray does.</b> Pointer-down and the first pointer-move are the same
    ///     gesture and a viewport may deliver them as one event or two; a first move that recast
    ///     would put the stroke's first stamp at a position the radius was not measured for, and a
    ///     click that never moves would paint nothing.
    /// </remarks>
    public ReadOnlySpan<Vector2> Resolve(Ray ray) {
        if (paths == 0) {
            return [];
        }

        if (!started) {
            started = true;
        } else {
            Advance(0, ray);

            if (paths > 1 && Symmetry is { } plane) {
                Advance(1, plane.Mirror(ray));
            }
        }

        return held.AsSpan(0, paths);
    }

    void Advance(int path, Ray ray) {
        if (!projection.TryHit(ray, out var hit)) {
            return;
        }

        if (path == 0) {
            Hit = hit;
        }

        held[path] = PaintProjection.Texel(hit.Coordinate, width, height);
    }
}
