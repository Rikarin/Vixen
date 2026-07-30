// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Core.Mathematics;

namespace Vixen.Rendering.ScreenProbes;

/// <summary>Screen probes accumulated across frames — the denoiser's opening move.</summary>
/// <remarks>
///     <para>
///         <b>Why this exists before any spatial filter.</b> Doc 19 § L3's own warning is that the
///         un-denoised gather looks worse than § L2 alone: sixty-four rays per probe is a noisy
///         estimate, and the cheapest variance reduction is the frames already paid for. A probe's
///         resolved projection is blended with its own history — <c>(history · w + current) / (w + 1)</c>,
///         the running mean, with <see cref="MaxFrames" /> capping <c>w</c> so the oldest frames age
///         out and a moved light converges instead of lingering.
///     </para>
///     <para>
///         <b>History follows the surface, not the tile.</b> A probe is anchored to a screen tile,
///         but what it measured is the light at a <i>surface</i> — so this frame's surface is
///         projected through <i>last frame's</i> camera to find the probe that stood on it then. The
///         lattice ran a frame behind the camera from the day the gather node existed; this is where
///         that staleness gets its name and its answer.
///     </para>
///     <para>
///         <b>Disocclusion is rejected by the plane test placement already trusts.</b> A reprojected
///         probe whose stored surface plane the current surface does not lie on — farther than
///         <see cref="Tolerance" />, <c>ScreenProbeAtlas</c>'s own mismatch — measured a different
///         surface, and blending it in is ghosting: last frame's wall smeared over this frame's
///         doorway. Rejected history starts over at weight one, which is noisy and honest; the
///         spatial filter that will hide the restart is the denoiser's next move, not this one.
///     </para>
///     <para>
///         The buffers are double-buffered internally: reprojection reads neighbouring slots while
///         writing others, and a camera pan would otherwise blend half-updated history into itself.
///     </para>
/// </remarks>
public sealed class ScreenProbeHistory {
    SphericalHarmonicsL1[] sh;
    SphericalHarmonicsL1[] nextSh;
    Vector3[] positions;
    Vector3[] nextPositions;
    Vector3[] normals;
    Vector3[] nextNormals;
    float[] weights;
    float[] nextWeights;

    /// <summary>Builds an empty history over a lattice. The first accumulation keeps nothing.</summary>
    /// <param name="layout">Where the probes stand.</param>
    public ScreenProbeHistory(ScreenProbeLayout layout) {
        Layout = layout;
        sh = new SphericalHarmonicsL1[layout.ProbeCount];
        nextSh = new SphericalHarmonicsL1[layout.ProbeCount];
        positions = new Vector3[layout.ProbeCount];
        nextPositions = new Vector3[layout.ProbeCount];
        normals = new Vector3[layout.ProbeCount];
        nextNormals = new Vector3[layout.ProbeCount];
        weights = new float[layout.ProbeCount];
        nextWeights = new float[layout.ProbeCount];
    }

    /// <summary>The lattice the history covers.</summary>
    public ScreenProbeLayout Layout { get; }

    /// <summary>The camera of the frames already inside — what reprojection projects through.</summary>
    public Matrix4x4 ViewProjection { get; private set; } = Matrix4x4.Identity;

    /// <summary>How many frames a probe's history may weigh at most.</summary>
    /// <remarks>
    ///     The lag-versus-noise dial: the running mean's oldest frames age out at rate
    ///     <c>1 / MaxFrames</c>, so a moved light converges in about this many frames while the
    ///     noise floor drops by its square root.
    /// </remarks>
    public int MaxFrames { get; set; } = 16;

    /// <summary>How far off a history probe's plane this frame's surface may stand, in world units.</summary>
    public float Tolerance { get; set; } = 0.05f;

    /// <summary>How many accumulations have run.</summary>
    public int Frames { get; private set; }

    /// <summary>How many probes reused their history in the last accumulation.</summary>
    public int Reprojected { get; private set; }

    /// <summary>How many probes rejected a reprojected history as a different surface.</summary>
    /// <remarks>
    ///     What makes disocclusion observable: a camera cut shows here as a whole screen of
    ///     rejections, and a static frame as none — either of those inverted is a bug with a name.
    /// </remarks>
    public int Rejected { get; private set; }

    /// <summary>A probe's accumulated projection.</summary>
    /// <param name="probe">The probe.</param>
    public SphericalHarmonicsL1 Resolved(Int2 probe) => sh[Layout.ProbeIndex(probe)];

    /// <summary>How many frames stand behind a probe's answer — zero for a probe with none.</summary>
    /// <param name="probe">The probe.</param>
    public float Weight(Int2 probe) => weights[Layout.ProbeIndex(probe)];

    /// <summary>Folds one frame's resolved probes into the history.</summary>
    /// <param name="atlas">The frame's atlas, already resolved.</param>
    /// <param name="viewProjection">The camera that frame stood under — next frame reprojects through it.</param>
    /// <exception cref="ArgumentNullException">There is no atlas.</exception>
    /// <exception cref="ArgumentException">The atlas covers a different lattice.</exception>
    public void Accumulate(ScreenProbeAtlas atlas, Matrix4x4 viewProjection) {
        ArgumentNullException.ThrowIfNull(atlas);

        if (atlas.Layout.GridSize != Layout.GridSize || atlas.Layout.Viewport != Layout.Viewport) {
            throw new ArgumentException(
                $"The atlas covers {atlas.Layout.GridSize} probes over {atlas.Layout.Viewport} and this history "
                + $"{Layout.GridSize} over {Layout.Viewport}. History is per-lattice; a resized frame starts a new one.",
                nameof(atlas)
            );
        }

        Reprojected = 0;
        Rejected = 0;

        for (var y = 0; y < Layout.GridSize.Y; y++) {
            for (var x = 0; x < Layout.GridSize.X; x++) {
                var probe = new Int2(x, y);
                var index = Layout.ProbeIndex(probe);

                if (!atlas.TrySurface(probe, out var position, out var normal)) {
                    nextSh[index] = SphericalHarmonicsL1.Zero;
                    nextPositions[index] = default;
                    nextNormals[index] = default;
                    nextWeights[index] = 0f;

                    continue;
                }

                var current = atlas.Resolved(probe);
                var accumulated = current;
                var weight = 1f;

                if (Frames > 0 && TryReproject(position, out var previous)) {
                    var from = Layout.ProbeIndex(previous);

                    if (weights[from] > 0f) {
                        if (ScreenProbeAtlas.Mismatch(position, positions[from], normals[from]) <= Tolerance) {
                            // The running mean, capped: lerp(current, history, w / (w + 1)) is
                            // (history · w + current) / (w + 1) with one multiply fewer.
                            var w = MathF.Min(weights[from], MaxFrames - 1);

                            accumulated = SphericalHarmonicsL1.Lerp(current, sh[from], w / (w + 1f));
                            weight = w + 1f;
                            Reprojected++;
                        } else {
                            Rejected++;
                        }
                    }
                }

                nextSh[index] = accumulated;
                nextPositions[index] = position;
                nextNormals[index] = normal;
                nextWeights[index] = weight;
            }
        }

        (sh, nextSh) = (nextSh, sh);
        (positions, nextPositions) = (nextPositions, positions);
        (normals, nextNormals) = (nextNormals, normals);
        (weights, nextWeights) = (nextWeights, weights);

        ViewProjection = viewProjection;
        Frames++;
    }

    /// <summary>The probe that stood on a surface last frame, if the camera could see it.</summary>
    /// <remarks>
    ///     Point reprojection into the tile that contained the surface — the nearest single probe.
    ///     Bilinear history, blending the four around it, is an owed refinement with this as its
    ///     baseline; it softens reprojection at the cost of smearing across the very edges the
    ///     plane test guards.
    /// </remarks>
    bool TryReproject(Vector3 position, out Int2 probe) {
        probe = default;

        var clip = Matrix4x4.TransformVector4(new(position, 1f), ViewProjection);

        if (clip.W <= 1e-4f) {
            return false;
        }

        var ndc = new Vector3(clip.X, clip.Y, clip.Z) / clip.W;

        if (ndc.Z <= 0f || ndc.Z >= 1f) {
            return false;
        }

        var x = (int)MathF.Floor(((ndc.X * 0.5f) + 0.5f) * Layout.Viewport.X);
        var y = (int)MathF.Floor(((ndc.Y * 0.5f) + 0.5f) * Layout.Viewport.Y);

        if (x < 0 || y < 0 || x >= Layout.Viewport.X || y >= Layout.Viewport.Y) {
            return false;
        }

        probe = new(
            Math.Min(x / Layout.TileSize, Layout.GridSize.X - 1),
            Math.Min(y / Layout.TileSize, Layout.GridSize.Y - 1)
        );

        return true;
    }
}
