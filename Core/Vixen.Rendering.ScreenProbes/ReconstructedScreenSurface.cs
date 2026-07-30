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

    /// <summary>Encoded normals per pixel, row-major — <c>xyz</c> in 0..1, as the G-buffer stores them.</summary>
    /// <remarks>
    ///     Kept encoded rather than decoded on the way in, so the decode below is the shader's line —
    ///     <c>SafeNormalize(xyz * 2 - 1)</c> — run on the shader's input. A buffer of already-decoded
    ///     normals would be a second convention for what a stored normal means.
    /// </remarks>
    public Span<Vector4> Normals => normals;

    /// <inheritdoc />
    /// <exception cref="ArgumentOutOfRangeException">The pixel is outside the viewport.</exception>
    /// <remarks>
    ///     False for the sky, and false again for a written depth whose normal decodes to nothing —
    ///     the encoded mid-grey a cleared normal target holds. A probe standing on a surface whose
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

        var encoded = normals[index];

        normal = SafeNormalize((new Vector3(encoded.X, encoded.Y, encoded.Z) * 2f) - Vector3.One);

        if (normal == Vector3.Zero) {
            return false;
        }

        // A pixel's own centre, then `Transform.UvDepthToWorld` verbatim: UV to NDC with no y
        // negation — the engine's UV and Vulkan's NDC both point y down — and the clip divide.
        var uv = new Vector2((pixel.X + 0.5f) / Viewport.X, (pixel.Y + 0.5f) / Viewport.Y);
        var ndc = (uv * 2f) - Vector2.One;
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
