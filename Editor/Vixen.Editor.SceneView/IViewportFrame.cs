// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Rendering.Compositor;

namespace Vixen.Editor.SceneView;

/// <summary>An open document the viewport can draw through: an authored frame, followed as it changes.</summary>
/// <remarks>
///     <para>
///         <b>The one document kind the viewport is a view of</b>, said as an interface so the
///         application points its panes at one without naming the document type (#1297). Opening a
///         document that implements this makes the panes draw through <see cref="Frame" />, and
///         <see cref="FrameChanged" /> keeps them up with it — the inspector's writes and a reload
///         from disk alike — with no restart.
///     </para>
///     <para>
///         ⚠ <b><see cref="Frame" /> is the expanded form</b>, the compositor a builder would build, not
///         the authored document: the presets are a document rewrite, and handing over the un-expanded
///         form would be a second opinion of it.
///     </para>
/// </remarks>
public interface IViewportFrame {
    /// <summary>What the frame is called, for the line that says the viewport is drawing it.</summary>
    string FrameName { get; }

    /// <summary>The compositor to build, expanded.</summary>
    GraphicsCompositorAsset Frame { get; }

    /// <summary>Raised after <see cref="Frame" /> changed.</summary>
    event Action<IViewportFrame>? FrameChanged;
}
