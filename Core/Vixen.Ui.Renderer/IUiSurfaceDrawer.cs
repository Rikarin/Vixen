// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;

namespace Vixen.Ui.Renderer;

/// <summary>One external picture, and everything a drawer needs to put it on the screen.</summary>
/// <param name="Source">Whatever the element put in <c>DrawList.Surfaces</c>.</param>
/// <param name="Rectangle">Where it goes, in the geometry's own units.</param>
/// <param name="Tint">Multiplied into it. The alpha carries the element's <c>opacity</c>.</param>
/// <param name="Clip">The scissor in force, in the geometry's units.</param>
/// <param name="Surface">The target's extent, in those same units.</param>
/// <param name="Scale">How many framebuffer pixels one of those units is.</param>
/// <remarks>
///     ⚠ <b>The rectangle is in document units and so is the surface, which is what lets a drawer
///     build its own projection.</b> Handing over a clip-space rectangle instead would have baked in
///     this renderer's y-flip and its DPI handling, and a drawer that also draws outside a user
///     interface — which the video one does — would then need two ways of being told where to draw.
/// </remarks>
public readonly record struct UiSurfaceDraw(
    object Source,
    Rectangle Rectangle,
    Color4 Tint,
    Rectangle Clip,
    Int2 Surface,
    float Scale
);

/// <summary>Draws the pictures a user interface names but does not understand.</summary>
/// <remarks>
///     <para>
///         <b>The seam that lets a video appear inside an interface without either side learning
///         about the other.</b> <c>Vixen.Ui</c> holds no texture and no device; <c>Vixen.Video</c>
///         holds no element tree. What connects them is an index into a list of
///         <see langword="object" /> and this interface — so a game that draws video in its UI
///         implements it in about ten lines over <c>VideoRenderer.Record</c>, and a game that does
///         not links neither.
///     </para>
///     <para>
///         ⚠ <b>An implementation binds its own pipeline, and <see cref="UiRenderer" /> re-binds
///         everything afterwards.</b> That is not politeness: Vulkan disturbs every descriptor set
///         from the first one two pipeline layouts disagree about, so an interface that carried on
///         after a foreign draw would sample whatever the video left bound. The re-bind is
///         unconditional and costs one pipeline bind and one descriptor bind per surface.
///     </para>
///     <para>
///         <b>Whatever it draws must be uploaded already.</b> This is called inside a render pass,
///         where no API permits a copy or a layout transition — so a drawer that owns a texture does
///         its transfers where <c>UiRenderer.Upload</c> does its own.
///     </para>
/// </remarks>
public interface IUiSurfaceDrawer {
    /// <summary>Draws one surface.</summary>
    /// <param name="commands">Where to record. Inside a render pass.</param>
    /// <param name="draw">What to draw and where.</param>
    /// <returns>
    ///     Whether anything was drawn. <see langword="false" /> for a source this drawer does not
    ///     recognise, which is how several drawers are chained.
    /// </returns>
    bool Draw(ICommandList commands, in UiSurfaceDraw draw);
}
