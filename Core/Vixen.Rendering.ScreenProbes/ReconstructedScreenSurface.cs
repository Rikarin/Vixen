// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Rendering.ScreenProbes;

/// <summary>The screen's own surfaces, reconstructed from a frame's depth and normal buffers.</summary>
/// <remarks>
///     <para>
///         <b>What "probe placement from the real depth buffer" is.</b> The gather's fixtures answer
///         <see cref="IScreenSurface" /> from analytic geometry; a frame answers it from what it drew.
///         This holds one frame's depth and encoded normals on the host and reconstructs a world
///         position per pixel by exactly the arithmetic every screen-space shader here uses —
///         <c>Transform.UvDepthToWorld</c>, the UV-to-NDC map with no y negation, the clip divide
///         guarded by the same epsilon. The shader and this must be one function evaluated twice,
///         because a probe placed by this arithmetic is upsampled by that one.
///     </para>
///     <para>
///         <b>Zero depth is the sky, because depth is reversed.</b> Near is one and far is zero
///         (<c>Matrix4x4.PerspectiveFieldOfView</c> says why), so a pixel nothing drew reads the depth
///         clear of zero and has no surface — the test that keeps the comparison the right way round
///         is the same <c>&lt;= 0</c> the upsample pass runs.
///     </para>
///     <para>
///         <b>The buffers are owned here and refilled in place.</b> A caller reading a frame back
///         copies into <see cref="Depth" /> and <see cref="Normals" /> and sets
///         <see cref="InverseViewProjection" /> to the matrix of the camera <i>that drew them</i> —
///         the two are one snapshot, and pairing this frame's matrix with last frame's depth
///         reconstructs surfaces that exist nowhere. Nothing here reads a device: the readback is the
///         caller's, which is what keeps this checkable against closed forms.
///     </para>
/// </remarks>
public sealed class ReconstructedScreenSurface : IScreenSurface {
    /// <summary>The clip-divide guard — the Raven library's <c>Const.Epsilon</c>, by value.</summary>
    const float Epsilon = 0.0001f;

    readonly float[] depth;
    readonly Vector4[] normals;

    /// <summary>Builds a surface over one viewport. The buffers start empty — all sky.</summary>
    /// <param name="viewport">The viewport, in pixels.</param>
    /// <exception cref="ArgumentOutOfRangeException">An empty viewport.</exception>
    public ReconstructedScreenSurface(Int2 viewport) {
        ArgumentOutOfRangeException.ThrowIfLessThan(viewport.X, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(viewport.Y, 1);

        Viewport = viewport;
        depth = new float[viewport.X * viewport.Y];
        normals = new Vector4[viewport.X * viewport.Y];
    }

    /// <summary>The viewport the buffers cover, in pixels.</summary>
    public Int2 Viewport { get; }

    /// <summary>The inverse of the view-projection that drew the buffers.</summary>
    /// <remarks>
    ///     The host's row-vector convention, exactly as <c>IndirectDiffuseRenderer</c> receives it —
    ///     the same matrix a frame writes into the shaders' <c>inverseViewProjection</c>, applied here
    ///     as <c>v · M</c>, which is what the shader's <c>M · v</c> reads back off the uploaded bytes.
    /// </remarks>
    public Matrix4x4 InverseViewProjection { get; set; } = Matrix4x4.Identity;

    /// <summary>Device depth per pixel, row-major over the viewport. Zero is the sky.</summary>
    public Span<float> Depth => depth;

    /// <summary>Stored normals per pixel, row-major — <c>xyz</c> raw and signed, as the G-buffer
    ///     stores them in Rgba16Float.</summary>
    /// <remarks>
    ///     Kept as stored rather than re-encoded on the way in, so the decode below is the shader's
    ///     line — <c>SafeNormalize(xyz)</c> — run on the shader's input. A buffer holding a second
    ///     convention for what a stored normal means is how the old <c>* 2 − 1</c> remap survived
    ///     here after every producer had switched to writing raw.
    /// </remarks>
    public Span<Vector4> Normals => normals;

    /// <inheritdoc />
    /// <exception cref="ArgumentOutOfRangeException">The pixel is outside the viewport.</exception>
    /// <remarks>
    ///     False for the sky, and false again for a written depth whose normal decodes to nothing —
    ///     the zero vector a cleared normal target holds. A probe standing on a surface whose
    ///     facing is unknown cannot be biased off it, so it does not stand at all.
    /// </remarks>
    public bool TrySurface(Int2 pixel, out Vector3 position, out Vector3 normal) {
        ArgumentOutOfRangeException.ThrowIfNegative(pixel.X);
        ArgumentOutOfRangeException.ThrowIfNegative(pixel.Y);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pixel.X, Viewport.X);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pixel.Y, Viewport.Y);

        position = default;
        normal = default;

        var index = (pixel.Y * Viewport.X) + pixel.X;
        var deviceDepth = depth[index];

        // Nothing was drawn here. ZERO is the sky, because depth is reversed — the upsample pass's
        // own line, and `IndirectDiffuse`'s before it.
        if (deviceDepth <= 0f) {
            return false;
        }

        var stored = normals[index];

        // Raw, not unorm: the G-buffer's normal plane is Rgba16Float and carries its sign natively.
        // The old `* 2 − 1` remap tilted every flat surface onto a fixed world diagonal, which broke
        // the upsample's plane test on open floors — and made a cleared texel decode to a plausible
        // diagonal instead of the zero the sky test below wants.
        normal = SafeNormalize(new Vector3(stored.X, stored.Y, stored.Z));

        if (normal == Vector3.Zero) {
            return false;
        }

        // A pixel's own centre, then `Transform.UvDepthToWorld` verbatim: UV to NDC and the clip
        // divide.
        //
        // ⚠ y *is* negated, and the comment that stood here said it was not — on the grounds that the
        // engine's UV and Vulkan's NDC both point y down. A shader never sees Vulkan-native NDC: the
        // projection is built y-up and the backend lands it with a negative-height viewport, so clip
        // y = +1 is the top of the screen while a UV's v = 0 is the top row. See
        // `Transform.UvToNdc`, which this mirrors and which gained the same sign.
        var uv = new Vector2((pixel.X + 0.5f) / Viewport.X, (pixel.Y + 0.5f) / Viewport.Y);
        var ndc = new Vector2((uv.X * 2f) - 1f, ((1f - uv.Y) * 2f) - 1f);
        var clip = Matrix4x4.TransformVector4(new Vector4(ndc.X, ndc.Y, deviceDepth, 1f), InverseViewProjection);

        position = new Vector3(clip.X, clip.Y, clip.Z) / MathF.Max(clip.W, Epsilon);

        return true;
    }

    /// <summary>The shader's <c>Math.SafeNormalize</c>, by value: zero for a degenerate vector.</summary>
    static Vector3 SafeNormalize(Vector3 value) {
        var lengthSquared = Vector3.Dot(value, value);

        return lengthSquared > Epsilon * Epsilon
            ? value * (1f / MathF.Sqrt(lengthSquared))
            : Vector3.Zero;
    }
}
