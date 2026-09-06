// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Graphics;

namespace Vixen.Editor.Plugin;

/// <summary>A picture a plugin made, put where the interface can draw it.</summary>
/// <remarks>
///     <para>
///         <b>The number, and the way to give it back, in one object.</b>
///         <c>ImageView.Image</c> and <c>Viewport</c>'s own handle are numbers the interface
///         resolves against the renderer, and a plugin that was handed a bare <see cref="Image" />
///         would have to remember to release it by calling something else with the same number —
///         which is <see cref="PluginContext.OnUnload" />'s rule with an extra chance to get it
///         wrong. This is the shape <c>IEditorRegistry.Add</c> already uses: what you get back is
///         the undo, so <c>context.Owns(…)</c> is the whole of the bookkeeping.
///     </para>
///     <para>
///         ⚠ <b>Disposing it does not destroy the texture on the spot</b>, and must not. The frame
///         that drew the picture may still be in flight, so the host retires the image between
///         frames — the same deferral <c>ThumbnailSurface.Retire</c> makes for a thumbnail scrolled
///         off screen, and for the same reason.
///     </para>
/// </remarks>
public interface IEditorImage : IDisposable {
    /// <summary>The number <c>ImageView.Image</c> takes. Never zero for a live image.</summary>
    ulong Image { get; }

    /// <summary>How wide it is, in texels.</summary>
    int Width { get; }

    /// <summary>How tall it is, in texels.</summary>
    int Height { get; }
}

/// <summary>The editor's graphics, lent to a plugin: a device to work on, and a way to be seen.</summary>
/// <remarks>
///     <para>
///         <b>Doc 36 § F2's gap, found by the first plugin that draws.</b> Until this,
///         <see cref="PluginServices" /> published the project, the scene, the registries and the
///         plugin host — and nothing device-shaped — so a third party could add a panel and could
///         not put a picture in it. <c>Editor/Vixen.Editor.Texturing</c> is what found it and
///         <a href="https://github.com/Rikarin/Vixen/issues/737">#737</a> is where it was reported.
///     </para>
///     <para>
///         ⚠ <b>Why this and not <c>IGraphicsDevice</c> straight into <see cref="PluginServices" />,
///         which is what #737 called "the smallest honest fix" — and why that one line could not
///         have worked.</b> <c>EditorApplication</c> builds its <see cref="PluginHost" /> in its
///         constructor and does not have a device then: the host hands one over afterwards, through
///         a settable property, and hands over <see langword="null" /> again on the way down.
///         <c>PluginServices.Add</c> throws on a second publish of a type, so there is no moment at
///         which the device could have been added. What a plugin can be handed is therefore a
///         <i>live view</i> of whether there is one — the same shape <c>IActiveScene</c> and
///         <c>IActiveView</c> already take, and for the same reason.
///     </para>
///     <para>
///         ⚠ <b>And why the device is still handed over whole, rather than a narrower "allocate me a
///         surface and run this on it".</b> That was the intended answer and the evaluator refutes
///         it: <c>TexturePlanEvaluator</c> caches a compiled pipeline per kernel and output format
///         across evaluations, so a contract that lent the device for the duration of one call would
///         make every preview recompile forty-five kernels. A plugin that dispatches its own work
///         needs a device it can <i>hold</i>, and nothing narrower expresses that.
///     </para>
///     <para>
///         ⚠ <b>What the plugin is promising by taking it.</b> The device is the host's: it outlives
///         every project, it is what the editor's own frame is recorded into, and
///         <see cref="IGraphicsDevice" /> is <see cref="IDisposable" /> — a plugin that disposed it,
///         or that called <c>BeginFrame</c>, <c>EndFrame</c> or <c>CreateSwapChain</c> on it, would
///         take the editor down with it. Nothing here can prevent that; what this type does is make
///         the loan the thing a plugin asks for by name, so the terms are written where the author
///         reads them rather than inferred from a service that is simply a device.
///     </para>
///     <para>
///         ⚠ <b><see cref="Upload" /> takes pixels rather than a texture view, and that is the
///         deliberate half.</b> A plugin's own image is created for whatever it dispatches into —
///         <c>TextureUsage.Storage</c>, typically — and the interface renderer samples what it is
///         given; a view registered straight from a storage image is missing
///         <c>TextureUsage.Sampled</c> and is in the wrong layout, which MoltenVK forgives and a
///         discrete card does not. The host already owns the three steps that make an upload
///         correct — a staging buffer, a copy, and the two barriers — and doing them once, in the
///         host, is what keeps that class of defect out of every plugin that draws.
///     </para>
/// </remarks>
public interface IEditorGraphics {
    /// <summary>The device to allocate on and dispatch over, or <c>null</c> while this host has none.</summary>
    /// <remarks>
    ///     ⚠ <b>Asked each time rather than kept.</b> Null is an ordinary state — a headless host, a
    ///     test, the moments before the window has a surface and after it has lost one — and a plugin
    ///     that read this once at activation would hold a device the editor has since released.
    /// </remarks>
    IGraphicsDevice? Device { get; }

    /// <summary>Puts pixels where the interface can draw them.</summary>
    /// <param name="width">How wide, in texels.</param>
    /// <param name="height">How tall.</param>
    /// <param name="rgba">The pixels, four bytes each, top row first.</param>
    /// <returns>The image, or <c>null</c> when this host cannot show one.</returns>
    /// <remarks>
    ///     Hand the result to <see cref="PluginContext.Owns{T}" /> unless the plugin releases it
    ///     itself sooner: an image left behind is a texture and a descriptor set the editor holds
    ///     for the rest of the session.
    /// </remarks>
    IEditorImage? Upload(int width, int height, ReadOnlySpan<byte> rgba);

    /// <summary>Rewrites one rectangle of a picture that is already on the screen.</summary>
    /// <param name="image">What <see cref="Upload" /> returned, and this host made.</param>
    /// <param name="x">The rectangle's low column, in texels of the image.</param>
    /// <param name="y">Its low row.</param>
    /// <param name="width">How many columns.</param>
    /// <param name="height">How many rows.</param>
    /// <param name="rgba">
    ///     The rectangle's pixels, four bytes each, its own rows tightly packed — <c>width × height ×
    ///     4</c> bytes and not a window onto the whole picture.
    /// </param>
    /// <returns>
    ///     Whether it happened. <c>false</c> for an image this host did not make, a rectangle outside
    ///     it, too few bytes, or a host with nothing to draw on — and a caller that gets it must fall
    ///     back to <see cref="Upload" /> rather than leave the screen showing the old pixels.
    /// </returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Without this an editing tool pays the whole picture per pointer event</b> —
    ///         <a href="https://github.com/Rikarin/Vixen/issues/912">#912</a>. A plugin that changed a
    ///         96-texel disc of a 4096² atlas had to hand back 67 MB, take a <i>new</i>
    ///         <see cref="IEditorImage" /> and drop the old one, which is a texture creation and a
    ///         descriptor-set write per frame of a drag. The rectangle is what a paint stroke already
    ///         knows.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The image is not replaced and its number does not change</b>, which is the whole
    ///         difference from <see cref="Upload" />: the pane goes on drawing the same handle and the
    ///         plugin goes on owning it. Nothing is returned to <see cref="PluginContext.Owns{T}" />
    ///         here because nothing new was made.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The write is deferred to the host's own frame, and that — rather than a second
    ///         copy of the texture — is what makes it safe.</b> <see cref="IEditorImage" />'s remarks
    ///         say a disposed image is retired between frames because the frame that drew it may be in
    ///         flight, and a partial write has the same hazard with none of the deferral: the texels
    ///         are going into a texture the last frame may still be sampling. So the host copies the
    ///         pixels into a staging buffer now and records the copy on the frame that draws next,
    ///         behind a barrier out of <c>ShaderRead</c> — which orders against everything already
    ///         submitted to that queue, the frame in flight included. A caller may therefore call this
    ///         as often as the pointer moves, and what it must not do is expect the picture to have
    ///         changed before the next frame.
    ///     </para>
    /// </remarks>
    bool Update(IEditorImage image, int x, int y, int width, int height, ReadOnlySpan<byte> rgba);
}
