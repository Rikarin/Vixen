// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Rendering.Compositor;

/// <summary>A node that laid state out against the frame's size and must lay it again when that changes.</summary>
/// <remarks>
///     <para>
///         <b>The seam a resize reaches the frame through.</b> A host writes the new size through
///         <see cref="GraphicsCompositor.Resize" />, which walks the frame and calls
///         <see cref="Reset" /> on every node that implements this. The shape is
///         <see cref="IPostProcessTarget" />'s exactly: the compositor owns the walk because it owns
///         the tree, and what a node keeps is the node's own business.
///     </para>
///     <para>
///         ⚠ <b>Most nodes need this and do not implement it.</b> A node that re-declares a graph
///         transient from <c>frame.Size</c> every build already resizes, and so does one that compares
///         a cached extent and reallocates — <c>TemporalAntialiasingRenderer</c>,
///         <c>ReflectionRenderer</c> and <c>HiZPyramid</c> all do, and destroying a texture inside
///         <c>Build</c> is safe because <see cref="Graphics.IGraphicsDevice.Destroy(Graphics.TextureHandle)" />
///         retires rather than frees. This interface is for the state that cannot be rebuilt that
///         cheaply: a lattice whose <em>shape</em> other objects were constructed against, a temporal
///         chain that a resize invalidates anyway.
///     </para>
///     <para>
///         ⚠ <b>The device is idle when <see cref="Reset" /> is called, and that is the whole reason
///         the walk exists rather than each node checking its own size.</b> A node that discovers the
///         mismatch inside <c>Build</c> is inside a frame, where the only honest thing left to do is
///         refuse. <see cref="GraphicsCompositor.Resize" /> is called between frames and idles once
///         before the first node is reset, so an implementation may release anything it owns.
///     </para>
///     <para>
///         ⚠ <b>A reset is a camera cut.</b> Everything accumulated starts over — history, placement,
///         readback rings — because reprojecting through a lattice that no longer exists is the
///         quieter of the two wrongnesses and the harder one to see.
///     </para>
/// </remarks>
public interface IResizeTarget {
    /// <summary>Forgets what was laid out against the old size, so the next build lays it again.</summary>
    /// <remarks>
    ///     Called with the device idle and outside any frame. Called on a node that has never built,
    ///     too — the first size a host writes is a change from the compositor's default — so an
    ///     implementation makes "nothing to forget" free rather than an error.
    /// </remarks>
    void Reset();
}
