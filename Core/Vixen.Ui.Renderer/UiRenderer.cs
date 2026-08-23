// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Rendering;
using Vixen.Ui.Rendering;
using Vixen.Ui.Text.Rasterizing;

namespace Vixen.Ui.Renderer;

/// <summary>The four shader modules a user interface is drawn with.</summary>
/// <param name="Vertex">The one vertex stage all three pipelines share.</param>
/// <param name="Box">Rounded rectangles and borders, as a signed distance.</param>
/// <param name="Text">Glyphs, as a multi-channel distance field.</param>
/// <param name="Solid">Tessellated paths, flat.</param>
/// <remarks>
///     ⚠ <b>Supplied rather than compiled here, and that is the seam.</b> Turning shader source into
///     modules belongs to <c>Vixen.Shaders</c> and to Raven. A caller hands over whatever it has,
///     which is how the golden fixture drives this with hand-written GLSL and how
///     <c>Vixen.Editor.App</c> drives it from <c>Shaders/Ui.rvn</c>. What this must not do is grow a
///     compiler.
/// </remarks>
public readonly record struct UiShaders(
    ShaderHandle Vertex,
    ShaderHandle Box,
    ShaderHandle Text,
    ShaderHandle Solid
) {
    /// <summary>The stage that samples a texture: an image, a video frame, a viewport.</summary>
    /// <remarks>
    ///     ⚠ <b>An init property rather than a fifth positional parameter</b>, so that a host which
    ///     draws no images keeps the four-argument constructor it already had. Left unset, image
    ///     commands are skipped and everything else draws — which is the honest behaviour for a
    ///     renderer that was not given the shader for them.
    /// </remarks>
    public ShaderHandle Image { get; init; }

    /// <summary>One axis of the separable Gaussian a group's <c>filter: blur()</c> is made of.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Optional on the same terms as <see cref="Image" />, and the consequence of
    ///         leaving it out is a picture rather than a failure.</b> A host with no blur stage
    ///         composites every group unblurred: the surfaces are still rendered, the opacities are
    ///         still right, and a <c>filter: blur()</c> in a stylesheet draws sharp. That is the
    ///         honest behaviour, and it is a materially better one than <see cref="Image" />'s
    ///         absence gives — a missing image stage makes <c>Compose</c> do nothing at all, which
    ///         turns a faded panel opaque.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>One stage run twice, not two.</b> The axis is a push constant, so the horizontal
    ///         and vertical sweeps are the same module with a different kernel — which is what keeps
    ///         a host's shader table at five names and stops the two directions from drifting apart.
    ///     </para>
    /// </remarks>
    public ShaderHandle Blur { get; init; }

    /// <summary>The composite of a group whose <c>filter</c> carries a colour transform.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Optional on the same terms as <see cref="Blur" />, and the consequence is again a
    ///         picture rather than a failure.</b> A host with no colour stage composites every group
    ///         through <see cref="Image" /> instead, which draws it in full colour: the surfaces are
    ///         still rendered, the opacities are still right, and a <c>filter: grayscale(1)</c> in a
    ///         stylesheet draws untouched. <c>UiRenderer.Filtered</c> is what says so out loud, and it
    ///         is the reason that count exists.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A module of its own rather than a branch in <see cref="Image" />, and the reason
    ///         is what the branch would cost everything else.</b> <c>ui-image.frag</c> draws every
    ///         viewport, thumbnail and video frame the interface has; giving it a push-constant block
    ///         would put an identity matrix on the wire for all of them. A group with a filter is
    ///         rare enough that one pipeline switch for the one draw that has one is the cheaper
    ///         side.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>No second surface and no extra pass, which is the whole design and is worth
    ///         stating where a reader will meet it.</b> The matrix is per pixel, so unlike
    ///         <see cref="Blur" /> it has nothing to read from a neighbour and can be folded into the
    ///         composite draw the group was going to make anyway — see <c>UiRenderer.Compose</c>, whose
    ///         viewport-sized targets a filter does not add to.
    ///     </para>
    /// </remarks>
    public ShaderHandle Colour { get; init; }

    /// <summary>The composite of a group whose <c>mask-image</c> fades it out.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Optional on <see cref="Colour" />'s terms, and its absence looks like <c>mask-*</c>
    ///         doing nothing.</b> A host without this stage composites masked groups through
    ///         <see cref="Image" /> — the surfaces are still rendered and the opacities are still
    ///         right, and the mask simply is not applied. <c>UiRenderer.Masked</c> is what says so out
    ///         loud.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It carries the colour matrix as well as the mask, and that is forced rather than
    ///         chosen.</b> A pipeline is bound once per draw, so a group with both a <c>filter</c> and
    ///         a <c>mask-image</c> must be served by one module that does both — two modules would
    ///         silently drop whichever lost. The consequence for a host is worth stating: supplying
    ///         this and not <see cref="Colour" /> makes <c>grayscale</c> work on masked elements and
    ///         nowhere else, which is a stranger picture than either stage being absent.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>No second surface and no extra pass.</b> A mask is per pixel, so like the colour
    ///         matrix it rides the composite draw the group was going to make anyway. Where it
    ///         differs is that its <i>seam</i> is fixed rather than free: see <see cref="UiMask" />,
    ///         which is where the arithmetic is argued.
    ///     </para>
    /// </remarks>
    public ShaderHandle Mask { get; init; }

    /// <summary>
    ///     Where the vertex stage reads <c>UiVertex</c>'s four attributes: position, texture,
    ///     colour, then shape.
    /// </summary>
    /// <remarks>
    ///     Left unset for a stage compiled from hand-written GLSL, whose attributes are at 0 to 3. A
    ///     Raven stage declaring three streams has them at 3 to 6 — see <see cref="VertexLocations" />
    ///     for why the number belongs to the shader rather than to this renderer.
    /// </remarks>
    public VertexLocations Locations { get; init; }
}

/// <summary>Draws a frame of interface geometry.</summary>
/// <remarks>
///     <para>
///         Everything above this is a pure function of a draw list and is tested without a device.
///         This is where that stops: buffers, pipelines, an atlas texture and a scissor rectangle.
///         It is kept separate from <see cref="UiRenderFeature" /> so that the part that actually
///         touches a device can be driven by a golden image without a
///         <see cref="RenderSystem" /> — which is the only way to find out whether the shaders agree
///         with the geometry.
///     </para>
///     <para>
///         ⚠ <b>Three pipelines, one vertex layout.</b> They differ only in the fragment stage, so a
///         frame binds a different pipeline per batch kind and never a different buffer. That is what
///         makes a frame one upload however many kinds of thing it draws.
///     </para>
///     <para>
///         ⚠ <b>Host-visible buffers, rewritten every frame, and no staging copy.</b> The usual
///         advice is the opposite — upload through a staging buffer into device-local memory — and it
///         is the wrong advice here. That advice is about data the GPU reads many times; interface
///         geometry is read once, by one draw, and then thrown away. A staging copy would add a
///         transfer and a barrier to save nothing.
///     </para>
/// </remarks>
public sealed class UiRenderer : IDisposable {
    readonly IGraphicsDevice device;
    readonly UiShaders shaders;

    readonly DescriptorSetLayoutHandle atlasLayout;
    readonly PipelineLayoutHandle layout;
    readonly SamplerHandle sampler;

    readonly PipelineHandle boxPipeline;
    readonly PipelineHandle textPipeline;
    readonly PipelineHandle solidPipeline;

    BufferHandle vertices;
    BufferHandle indices;
    BufferHandle boxes;

    /// <summary>How many bytes one <see cref="UiShape" /> is.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Taken from the type rather than written down, and it was written down in three
    ///         places until the record grew.</b> <c>UiShape</c> went from eighty bytes to a hundred and
    ///         twelve when gradients gained a middle stop and two round shapes, and the literal
    ///         <c>80</c> here sized the storage buffer, strided the upload and bounded the descriptor
    ///         range. What that produced was not a validation error: the buffer was allocated at
    ///         seven-tenths of the size the writes needed, so boxes past the first few read whatever
    ///         was next in memory — which is a frame of plausible rounded rectangles with the wrong
    ///         radii, exactly the failure <c>UiShape</c>'s own remark warns about.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>This is the <i>fifth</i> file that has to agree about the layout</b>, and
    ///         <c>docs/plan/43</c> § A11 listed four. The other four all describe the record's shape;
    ///         this one only needs its size, which is why it was easy to miss and why deriving it means
    ///         it can never be missed again. <c>UiShapeLayoutTests</c> covers the shape; the size is
    ///         covered by not having a second opinion about it.
    ///     </para>
    /// </remarks>
    static readonly int ShapeBytes = Marshal.SizeOf<UiShape>();

    /// <summary>How many <c>mask-image</c> layers one frame may carry, across every masked group.</summary>
    /// <remarks>
    ///     ⚠ <b>A frame budget and not a per-group one, and a frame that exceeds it composites the
    ///     groups past the line <i>unmasked</i> rather than wrongly.</b> Eight layers is the most one
    ///     group can ask for — see <c>GradientReader.MostLayers</c> — so this is sixteen fully masked
    ///     groups in one frame, against an interface where a masked group at all is rare. The
    ///     fail-open answer is the one <c>DrawListBuilder</c> already gives an unreadable mask, and
    ///     for the same reason: a mask that failed closed would erase the group, which is
    ///     indistinguishable from a layout collapse.
    /// </remarks>
    const int MaskCapacity = 128;

    /// <summary>How many floats one entry of <see cref="maskEntries" /> is.</summary>
    /// <remarks>
    ///     Four <c>vec4</c>s, exactly as <c>ui-mask.frag</c>'s <c>MaskEntry</c> declares them. ⚠ The
    ///     packing is written out by hand in <see cref="SubmitDraw" />'s neighbour
    ///     <see cref="MaskBlock" /> rather than blitted from <see cref="UiMask" />, because that
    ///     struct's field order is C#'s business and std430's is the wire's.
    /// </remarks>
    const int MaskFloats = 16;

    /// <summary>How many bytes one frame's region of each buffer is.</summary>
    /// <remarks>Per region, not per buffer: each buffer is this many bytes times <see cref="slots" />.</remarks>
    int vertexCapacity;
    int indexCapacity;
    int boxCapacity;

    /// <summary>How many frames the device may have in flight, and so how many regions each buffer has.</summary>
    readonly int slots;

    /// <summary>Which region this frame writes and draws from.</summary>
    int slot;

    TextureHandle atlasTexture;
    TextureViewHandle atlasView;
    DescriptorSetHandle[] atlasDescriptors = [];

    /// <summary>Which frames' sets still point at a box buffer that has been replaced.</summary>
    /// <remarks>
    ///     ⚠ <b>A flag per frame rather than rewriting every set the moment the buffer grows</b>, and
    ///     the difference is a validation error. Growing replaces the buffer, which invalidates the
    ///     storage binding in all <see cref="slots" /> sets at once — but the other frames' sets are
    ///     the ones frames still running on the device are reading through, and a descriptor set may
    ///     not be updated while a submitted command buffer references it. Writing them all here is the
    ///     obvious fix for the stale pointer and it swaps one hazard for another:
    ///     <c>VUID-vkUpdateDescriptorSets-None-03047</c>, which is undefined behaviour on a driver
    ///     that does not report it. So the write is deferred to the frame that owns the set, where
    ///     <see cref="slot" />'s own invariant — the region moved on to is one the device has finished
    ///     with — already says nobody is reading it.
    /// </remarks>
    readonly bool[] staleBoxes;

    /// <summary>A registered texture: one set per frame in flight, and which of them are stale.</summary>
    /// <remarks>
    ///     ⚠ <b>A ring and not one set, for the same reason <see cref="staleBoxes" /> is a flag per
    ///     frame.</b> Re-registering a number — which is what a viewport does every time it is resized
    ///     — points the set at a new view, and one set shared by every frame in flight is one there is
    ///     no safe moment to write: <c>VUID-vkUpdateDescriptorSets-None-03047</c>. A set per frame has
    ///     one, and it is the moment <see cref="slot" /> comes round to it.
    /// </remarks>
    /// <param name="Sets">One per <see cref="slots" />, allocated once and never reallocated.</param>
    /// <param name="View">What the sets are to point at, which the stale ones do not yet.</param>
    /// <param name="Stale">Which frames' sets still point at the view this replaced.</param>
    sealed record ImageEntry(DescriptorSetHandle[] Sets, TextureViewHandle View, bool[] Stale);

    // ⚠ Allocated per registered texture, not per draw. A descriptor set is allocated from a pool
    // and updated on the device; doing either inside the record loop would put an allocation and a
    // driver call on every frame that shows an image. Registration is the host saying "this texture
    // will be drawn", which is a thing that happens when a viewport is resized, not per frame.
    readonly Dictionary<ulong, ImageEntry> imageDescriptors = [];

    readonly PipelineHandle imagePipeline;

    /// <summary>The composite pipeline a filtered group uses instead of <see cref="imagePipeline" />.</summary>
    readonly PipelineHandle colourPipeline;

    /// <summary>The composite pipeline a masked group uses instead of either of the other two.</summary>
    /// <remarks>
    ///     ⚠ It supersedes <see cref="colourPipeline" /> rather than joining it: a group with both a
    ///     mask and a matrix goes through this one, which does both. See <see cref="UiShaders.Mask" />.
    /// </remarks>
    readonly PipelineHandle maskPipeline;

    /// <summary>The colour matrix each composed group's composite draw has to be put through.</summary>
    /// <remarks>
    ///     ⚠ <b>Here rather than on <see cref="UiDraw" />, and the difference is fifty-two bytes on
    ///     every draw in every frame against a dictionary with as many entries as the frame has
    ///     filtered groups — which is almost always none.</b> A composite draw is an ordinary
    ///     <see cref="BatchKind.Image" /> naming its surface by number, so <see cref="SubmitDraw" />
    ///     already has the only key it needs; putting the matrix on the draw would widen a struct
    ///     the frame holds thousands of to carry a value a handful of them ever set.
    ///     ⚠ Rebuilt by <see cref="Compose" /> each frame rather than accumulated, so a group that
    ///     stopped being filtered cannot leave its matrix behind for the next frame to find.
    /// </remarks>
    readonly Dictionary<ulong, UiColorMatrix> layerFilters = [];

    /// <summary>Each masked group's mask list, keyed by surface number the way the filters are.</summary>
    /// <remarks>
    ///     ⚠ Rebuilt by <see cref="Compose" /> each frame for <see cref="layerFilters" />' reason, and
    ///     read by <see cref="SubmitDraw" /> at the same moment <c>SoftwareUiRasterizer</c> reads its
    ///     own copy — which is the seam <see cref="UiMask" /> requires both executors to share.
    ///     ⚠ A range into <see cref="maskEntries" /> and not the entries themselves: they are already
    ///     on the device by the time this is filled, written by <see cref="UploadGeometry" /> in one
    ///     copy beside the boxes.
    /// </remarks>
    readonly Dictionary<ulong, (int First, int Count)> layerMasks = [];

    /// <summary>What an image set's storage binding points at, and it is never the box buffer.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A buffer of its own precisely so that it never has to be rewritten, and it is
    ///         now the one buffer that <i>is</i> read through an image set.</b> The image shader
    ///         still ignores the binding — the layout only declares it — but <c>ui-mask.frag</c>
    ///         reads its mask list out of it, and a composite draw binds an image set. Pointing this
    ///         at the box buffer instead would mean rewriting every image set each time that buffer
    ///         grew; one allocation that outlives every frame buys the question away.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Fixed capacity, and that is what keeps the sets valid rather than a convenience.</b>
    ///         A buffer that grew would be a new handle, and every image set in the ring would be
    ///         pointing at freed memory on the frame it grew — the exact hazard <see cref="staleBoxes" />
    ///         exists to manage for the boxes, reintroduced on a path that has no marking. Sized once
    ///         for <see cref="MaskCapacity" /> entries per frame in flight, it is a few tens of
    ///         kilobytes and it is never replaced.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Allocated in the constructor rather than on first use, which the dummy it replaced
    ///         did not need to be.</b> <see cref="UploadGeometry" /> writes this frame's mask entries
    ///         and runs <i>before</i> <see cref="Compose" /> registers any layer surface, so the first
    ///         masked frame would otherwise write to a handle nothing had created yet.
    ///     </para>
    /// </remarks>
    readonly BufferHandle maskEntries;

    /// <summary>What a composited group's surface is rendered into and sampled back through.</summary>
    /// <param name="Texture">The surface, the size of the whole viewport.</param>
    /// <param name="View">What the composite's descriptor set points at.</param>
    /// <param name="Image">The number the composite draw names it by, from <c>UiGeometryBuilder</c>.</param>
    sealed record LayerSurface(TextureHandle Texture, TextureViewHandle View, ulong Image) {
        /// <summary>What the last barrier left it in.</summary>
        /// <remarks>
        ///     ⚠ <b>Per surface and not one flag for all of them</b>, for the reason
        ///     <c>UploadAtlas</c> gives: the state a barrier claims a texture was in has to be the
        ///     state it was in, and a surface allocated because this frame has one group more than
        ///     the last has never been written. A shared flag would say <c>ShaderRead</c> of it and
        ///     be a validation error on exactly the frame a group was added.
        /// </remarks>
        public ResourceState State { get; set; } = ResourceState.Undefined;
    }

    /// <summary>One surface per composited group, keyed by the number its composite draw names it by.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Keyed by <see cref="UiLayer.Image" /> and <i>not</i> by the group's position in
    ///         <see cref="UiGeometry.Layers" />, which are not the same number and look like they
    ///         are.</b> The list is sorted in pre-order — outermost group first — while the number is
    ///         handed out by a counter that ticks when a group <i>closes</i>, and groups close
    ///         innermost first. So in a frame with a group inside a group, the outer one is
    ///         <c>Layers[0]</c> and carries <c>LayerImage(1)</c>. Indexing the surfaces by position
    ///         hands each group the other one's texture, and what the device says about it is a layout
    ///         error rather than anything about compositing — the surface being sampled is the one
    ///         still open as a colour attachment.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Kept between frames rather than allocated per frame, and <i>one</i> each rather
    ///         than one per frame in flight.</b> A viewport-sized colour target is the most expensive
    ///         thing this renderer owns, so a ring of them would multiply the cost of a translucent
    ///         group by <see cref="slots" /> to solve a hazard a barrier already solves: the
    ///         transition back to <see cref="ResourceState.ColourTarget" /> at the top of
    ///         <see cref="Compose" /> names the shader stages as its source, which orders the previous
    ///         frame's sampling of this surface before this frame's writing of it. That is an
    ///         execution dependency across submissions on one queue, which is exactly what a
    ///         write-after-read needs and all it needs — the glyph atlas is a single texture
    ///         re-uploaded every frame on the same argument.
    ///     </para>
    /// </remarks>
    readonly Dictionary<ulong, LayerSurface> layerSurfaces = [];

    readonly PipelineHandle blurPipeline;

    /// <summary>Where the first axis of a blur lands, on its way back into the group's own surface.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>One for the whole frame, and not one per blurred group — which is the cost this
    ///         arrangement was expected to have and does not.</b> A separable blur cannot read and
    ///         write one attachment, so a blurred group does need a second target; but it needs it
    ///         only for the two passes between its own pass ending and its <c>ShaderRead</c> barrier,
    ///         and those are finished before the next group's begin. So the scratch is borrowed and
    ///         handed back, and a frame with twelve blurred groups costs thirteen viewport-sized
    ///         targets rather than twenty-four.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Allocated only when a frame actually asks for a blur.</b> It is the same size as
    ///         a layer surface, so paying for it on every frame that merely has a translucent panel
    ///         would be a whole extra viewport of memory for nothing — and <c>Composited</c> being
    ///         non-zero is much commoner than <see cref="Blurred" /> being non-zero.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Its descriptor set is written once and never rewritten</b>, which is why one set
    ///         is enough where a registered image needs <see cref="slots" />. The ring exists to stop
    ///         a set being <i>updated</i> while a submitted frame reads it; nothing updates this one.
    ///         The view behind it changes only when the viewport is resized, and that path destroys
    ///         the set along with the texture.
    ///     </para>
    /// </remarks>
    LayerSurface? scratch;

    DescriptorSetHandle scratchSet;

    /// <summary>The format a layer surface is, which is the format the pipelines were built for.</summary>
    /// <remarks>
    ///     ⚠ A surface in another format is a pipeline the device will not let the pass use. It is
    ///     read off the output rather than fixed, because the whole point of the surface is that the
    ///     same four pipelines draw into it as into the frame.
    /// </remarks>
    readonly PixelFormat layerFormat;

    int layerWidth;
    int layerHeight;

    /// <summary>How many of this frame's groups <see cref="Compose" /> actually rendered.</summary>
    /// <remarks>
    ///     ⚠ <b>Zero is the honest answer and not a failure, and it is what keeps a host that never
    ///     calls <see cref="Compose" /> from throwing or half-drawing.</b> <see cref="Record" /> skips
    ///     a group's own draws only when its surface exists; with none, the walk is the flat one over
    ///     every draw in order that this renderer has always done, and the composite quads are skipped
    ///     for want of a registered texture.
    ///     <para>
    ///         ⚠ <b><s>and the frame is the un-isolated approximation
    ///         <c>DrawListBuilder.Compositing</c> being off produces anyway.</s> It is not, and that
    ///         sentence was the reason turning the gate on looked like one change.</b> The
    ///         approximation fades each of a group's children by the group's alpha; what this produces
    ///         instead is those children at <i>full</i> strength, because the builder emits a group's
    ///         contents with alpha one precisely so the surface can carry the fade — see
    ///         <c>DrawListBuilder.Emit</c>, and
    ///         <c>DrawListTests.Opacity_brackets_a_group_rather_than_multiplying_down_the_tree</c>,
    ///         which asserts the one. So a host that leaves this at zero with the gate on does not
    ///         approximate a faded panel, it draws an opaque one. That is worse than either model and
    ///         is why <c>Compose</c> is not optional, and why an absent <c>UiShaders.Image</c> — which
    ///         makes <c>Compose</c> return having done nothing — is the same fault wearing a different
    ///         hat.
    ///     </para>
    /// </remarks>
    int composed;

    /// <summary>How many of those groups <see cref="Compose" /> also ran a blur over.</summary>
    int blurred;

    /// <summary>How many of those groups <see cref="Compose" /> also cast a drop shadow from.</summary>
    /// <remarks>
    ///     ⚠ <b>Counted where the work happens and not where the quad is drawn, which is the
    ///     opposite of <see cref="filtered" /> and is right for the opposite reason.</b> A tinted
    ///     composite is a draw and can be counted as one; a shadow is two render passes into a
    ///     surface, and the quad that samples it is an ordinary image draw that no amount of
    ///     inspection at <see cref="SubmitDraw" /> distinguishes from a nested group's. So this
    ///     counts surfaces filled — and <see cref="filtered" /> counts the tints that reached them,
    ///     which is the second half a test needs: a shadow surface composited through the image
    ///     pipeline is a full-colour copy of the element and increments this and not that.
    /// </remarks>
    int shadowed;

    /// <summary>How many composite draws the frame put a colour matrix through.</summary>
    /// <remarks>
    ///     ⚠ <b>Reset by <see cref="Compose" /> and incremented by <see cref="SubmitDraw" />, which
    ///     is the one arrangement that counts each of a frame's filtered composites exactly once.</b>
    ///     A group's composite is submitted by whichever pass contains it — the frame's, recorded in
    ///     <see cref="Record" />, or a parent group's, recorded inside <see cref="Compose" /> — so
    ///     resetting in <see cref="Record" /> would throw away every nested one, and resetting in
    ///     <see cref="SubmitDraw" /> is not a thing. The pair are documented as called together and
    ///     in that order; a host that calls neither leaves <see cref="layerFilters" /> empty and this
    ///     at zero, which is the honest answer for a frame with no composited group in it.
    /// </remarks>
    int filtered;

    /// <summary>How many composite draws the last <see cref="Record" /> put a mask through.</summary>
    /// <remarks>
    ///     ⚠ <b>The observer a silently-skipped mask has, and it needs one for a reason the colour
    ///     matrix's fourth case only hints at.</b> All four of <see cref="filtered" />'s ways to be
    ///     skipped apply — no <c>UiShaders.Mask</c>, no <c>UiLayer.Mask</c>, a group collapsed before
    ///     it became a layer, and contents the ramp happens not to touch. The comparison of the two
    ///     executors cannot see any of them either, because both read the same plan. This can.
    /// </remarks>
    int masked;

    BufferHandle atlasStaging;
    int atlasWidth;
    int atlasHeight;
    int atlasRevision = -1;
    ResourceState atlasState = ResourceState.Undefined;

    byte[] atlasBytes = [];

    /// <summary>Builds the pipelines a UI pass needs.</summary>
    /// <param name="device">Where they live.</param>
    /// <param name="shaders">The modules to build them from.</param>
    /// <param name="output">The formats of the pass this will be drawn in.</param>
    public UiRenderer(IGraphicsDevice device, UiShaders shaders, RenderOutput output) {
        ArgumentNullException.ThrowIfNull(device);

        this.device = device;
        this.shaders = shaders;

        shaders.Locations.Require(4, nameof(UiRenderer));

        // ⚠ A region per frame in flight, and it is the whole of what stops the interface tearing.
        // `IGraphicsDevice.Write` is a memcpy into persistently-mapped host-visible memory: writing
        // this frame's vertices at offset zero overwrites the ones the GPU is still reading for a
        // frame that has not finished, and the symptom is an interface that is briefly a blend of
        // two frames. `Vixen.Rendering.UploadBuffer` says the same thing about skinning matrices and
        // rings its buffer for the same reason.
        //
        // What made it visible is what makes it confusing: hovering a row inserts one rectangle
        // *before* everything drawn after it, so every later element's vertices move to a different
        // offset — and a panel on the other side of the window flickers while the list under the
        // pointer looks perfectly still.
        slots = Math.Max(1, device.FramesInFlight);
        staleBoxes = new bool[slots];

        atlasLayout = device.CreateDescriptorSetLayout(
            new(
                // ⚠ Set 0, which by the convention in `DescriptorSetSlot` is the per-frame set. It is
                // not a misuse: a UI pass has no per-frame or per-view set at all — the projection is
                // four floats in a push constant — so the atlas is the only set there is, and a
                // layout with two empty sets in front of it would cost two bind points to honour a
                // naming convention.
                DescriptorSetSlot.PerFrame,
                [
                    new(0, DescriptorKind.SampledTexture, ShaderStage.Fragment),
                    new(1, DescriptorKind.Sampler, ShaderStage.Fragment),

                    // ⚠ In the same set as the atlas rather than one of its own, for the reason the
                    // layouts are shared at all: a set the box pipeline has and the text pipeline
                    // does not is a set a pipeline change disturbs.
                    new(2, DescriptorKind.StorageBuffer, ShaderStage.Fragment)
                ],
                "ui atlas"
            )
        );

        // ⚠ <b>One pipeline layout for all three pipelines, including the two whose shaders never
        // sample the atlas.</b> The obvious arrangement is a layout each — why would a box declare a
        // texture it does not read — and it is the one that has to be got right per draw: Vulkan
        // disturbs every descriptor set from the first one two layouts disagree about, so a box drawn
        // between two runs of text unbinds the atlas, and the second run reads whatever is left. That
        // is undefined behaviour rather than an error, and this machine's driver happens to keep the
        // binding, so a golden image cannot see it. Making the layouts identical makes the question
        // not arise: the set is bound once a frame and no pipeline change can disturb it. Declaring a
        // binding a shader ignores costs nothing.
        // ⚠ <b>A second range, for the fragment stage, that only two of the pipelines' shaders
        // declare — and they declare different amounts of it.</b> A pipeline layout may promise more
        // push constants than a shader reads — the reverse is the error — so the four pipelines that
        // never look at it are unaffected, `ui-blur.frag` reads the first sixteen bytes as its
        // kernel, and `ui-colour.frag` reads all forty-eight as three matrix rows. The alternative is
        // a layout of its own per stage, which is the very thing the paragraph above refuses: two
        // layouts in one pass is a descriptor set disturbed at the first slot they disagree about.
        // Disjoint offsets rather than one range spanning both stages, so that `ui.vert`'s block
        // stays exactly the sixteen bytes it declares today and no committed module has to be
        // recompiled to keep matching it.
        // ⚠ Sixty-four bytes in total, which is half the 128 every Vulkan implementation is required
        // to offer — so widening the fragment range costs nothing anybody has to check for.
        layout = device.CreatePipelineLayout(
            // ⚠ <b>112 fragment bytes rather than 48, and 16 + 112 = 128 is not a coincidence.</b>
            // `ui-mask.frag` is the widest consumer — a colour matrix at 48 plus a mask at 64 — and
            // 128 is exactly the push-constant size the Vulkan specification guarantees on every
            // device. The number is a floor that was reached rather than a budget that was chosen, so
            // the next thing to want a push constant here cannot simply be added: it either shares
            // these bytes or moves to the storage buffer `UiShape` already uses. A shader is free to
            // read fewer than the layout promises — the reverse is the error — which is what lets one
            // layout keep serving `ui-blur.frag`'s 16 bytes and `ui-colour.frag`'s 48.
            new([atlasLayout], [new(ShaderStage.Vertex, 0, 16), new(ShaderStage.Fragment, 16, 112)], "ui")
        );

        // ⚠ Linear filtering and clamped, and never an sRGB view. The atlas holds distances, not
        // light: decoding them as colour is the classic mistake and shows as text that is too thin.
        // Clamped because a glyph at the atlas edge sampled with repeat picks up whichever glyph was
        // packed on the far side.
        sampler = device.CreateSampler(SamplerDescription.LinearClamp with { Name = "ui atlas" });

        boxPipeline = Pipeline(shaders.Box, output, "ui box");
        textPipeline = Pipeline(shaders.Text, output, "ui text");
        solidPipeline = Pipeline(shaders.Solid, output, "ui solid");

        // Only when there is a stage for it. A host that draws no images does not pay for a pipeline,
        // and one that hands over no image shader gets image draws skipped rather than a throw at
        // construction — see UiShaders.Image.
        if (shaders.Image.IsValid) {
            imagePipeline = Pipeline(shaders.Image, output, "ui image");
        }

        // The same bargain one line up: a host that hands over no blur stage draws its groups sharp
        // rather than throwing here — see `UiShaders.Blur`.
        if (shaders.Blur.IsValid) {
            blurPipeline = Pipeline(shaders.Blur, output, "ui blur");
        }

        // And again for the colour matrix — see `UiShaders.Colour`. A host without it composites its
        // filtered groups through `imagePipeline`, in full colour, which is a picture rather than a
        // fault.
        if (shaders.Colour.IsValid) {
            colourPipeline = Pipeline(shaders.Colour, output, "ui colour");
        }

        // And again for the mask — see `UiShaders.Mask`. A host without it composites masked groups
        // unmasked, which is a picture rather than a failure, and `Masked` is what reports it.
        if (shaders.Mask.IsValid) {
            maskPipeline = Pipeline(shaders.Mask, output, "ui mask");
        }

        // ⚠ Once, at full size, and never again — see the field. Every image descriptor set in the
        // ring points here for the whole life of the renderer, so a replacement would be a set
        // pointing at freed memory on a frame nothing marked stale.
        maskEntries = device.CreateBuffer(
            new(MaskFloats * 4 * MaskCapacity * slots, BufferUsage.Storage, MemoryAccess.HostUpload, "ui masks")
        );

        layerFormat = output.ColourCount > 0 ? output.ColourFormats[0] : PixelFormat.Rgba8UNorm;
    }

    /// <summary>How many draws the last <see cref="Record" /> submitted.</summary>
    /// <remarks>
    ///     Exposed for the same reason <c>DrawList.Batched</c> is: a claim about how little a frame
    ///     costs that cannot be measured is one nobody can check.
    /// </remarks>
    public int Draws { get; private set; }

    /// <summary>How many groups the last <see cref="Compose" /> rendered a surface for.</summary>
    /// <remarks>
    ///     ⚠ <b>The only observer the whole arrangement has, and a test that does not read it can
    ///     pass without a single group having been composited.</b> A frame whose groups were left
    ///     uncomposed still draws — it draws the un-isolated approximation, which on anything opaque
    ///     is the same picture. So "the two renderers agree" is a claim about nothing unless this is
    ///     also non-zero. It is the count of surfaces and so also the count of extra render passes
    ///     the frame cost, which is the other thing worth being able to check.
    ///     <para>
    ///         ⚠ <b><s>It is the count of surfaces and so also the count of extra render passes.</s>
    ///         It is the count of surfaces and no longer the count of passes</b>, because a blurred
    ///         group costs two more of the second and none of the first — see <see cref="Blurred" />,
    ///         which is the other half of the arithmetic. The whole frame's extra passes are
    ///         <c>Composited + 2 × Blurred</c>; its extra viewport-sized targets are
    ///         <c>Composited + (Blurred &gt; 0 ? 1 : 0)</c>.
    ///     </para>
    /// </remarks>
    public int Composited => composed;

    /// <summary>How many of those groups the last <see cref="Compose" /> also ran a blur over.</summary>
    /// <remarks>
    ///     ⚠ <b>The observer a silently-skipped blur has, and there are three ways to skip one.</b> A
    ///     host that handed over no <c>UiShaders.Blur</c>, a geometry whose groups carry no
    ///     <c>UiLayer.Blur</c>, and a builder that collapsed the group away before it ever became a
    ///     layer all produce the same frame: composited, correct, and sharp. None of them is an error
    ///     and none of them says anything, so a test that asserts a blurred picture without asserting
    ///     this is a test that passes when the blur does not run — the same hole
    ///     <see cref="Composited" /> exists to plug one level up.
    /// </remarks>
    public int Blurred => blurred;

    /// <summary>How many of those groups the last <see cref="Compose" /> also cast a drop shadow from.</summary>
    /// <remarks>
    ///     ⚠ <b>The observer a silently-skipped drop shadow has, and it needs one more than a blur
    ///     because a shadow can be skipped by a stage it does not obviously depend on.</b> All of
    ///     <see cref="Blurred" />'s ways apply — no <c>UiShaders.Blur</c>, no <c>UiLayer.Shadow</c>, a
    ///     group collapsed before it became a layer — and so does a fourth: a host with a blur stage
    ///     and no <c>UiShaders.Colour</c> gets no shadow at all, because
    ///     <see cref="EnsureSurfaces" /> declines to allocate a surface whose tint nothing could
    ///     apply. Every one of those leaves a correct, shadowless picture, which is exactly the frame
    ///     a comparison of the two executors would still pass on if the software path had made the
    ///     same call.
    ///     <para>
    ///         ⚠ It is the count of surfaces and two-thirds of the count of passes: a blurred shadow
    ///         is two passes and an unblurred one is a single copy. A frame's extra passes are
    ///         <c>Composited + 2 × Blurred + (1 or 2) × Shadowed</c>.
    ///     </para>
    /// </remarks>
    public int Shadowed => shadowed;

    /// <summary>How many composite draws the last <see cref="Record" /> put a colour matrix through.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The observer a silently-skipped colour filter has, and it has one more way of
    ///         being skipped than a blur does.</b> The three <see cref="Blurred" /> lists all apply —
    ///         no <c>UiShaders.Colour</c>, no <c>UiLayer.Filter</c>, a group collapsed before it
    ///         became a layer — and a fourth is peculiar to this: the frame draws <i>identically</i>
    ///         with the filter missing whenever the group's contents happen not to exercise the
    ///         matrix. A <c>grayscale</c> over a panel that is already grey is a correct picture
    ///         either way, so a screenshot cannot tell, and neither can a comparison of the two
    ///         executors. This can.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Per <i>draw</i> and not per group, and it therefore spans both halves of the
    ///         frame.</b> A group's composite is submitted once, but it is submitted by whichever
    ///         pass contains it — the frame's, in <see cref="Record" />, or a parent group's, inside
    ///         <see cref="Compose" /> — so the count is reset once, by <see cref="Compose" />, and
    ///         then accumulated across both. A composite that was scissored away contributes nothing
    ///         here for the same reason it contributes nothing to <see cref="Draws" />.
    ///     </para>
    /// </remarks>
    public int Filtered => filtered;

    /// <summary>How many composite draws the last <see cref="Record" /> put a mask through.</summary>
    /// <remarks>
    ///     ⚠ <b>Counted separately from <see cref="Filtered" /> even though one pipeline serves
    ///     both.</b> A group with a mask and a matrix increments both, because both were applied; a
    ///     single count would make "the mask ran" and "the matrix ran" indistinguishable on exactly
    ///     the draw where the two-in-one shader is most likely to have dropped one of them.
    /// </remarks>
    public int Masked => masked;

    /// <summary>How many descriptor sets registering images has ever allocated.</summary>
    /// <remarks>
    ///     ⚠ <b>The only observer a leak here has.</b> A backend allocates these from pools created
    ///     without <c>FreeDescriptorSetBit</c>, so a set handed back is memory nothing reclaims until
    ///     the device goes away — and neither the picture nor the validation layers say a word about
    ///     it. The claim worth checking is that re-registering a number allocates nothing, because a
    ///     viewport does exactly that once a frame for as long as a splitter is being dragged.
    /// </remarks>
    public int ImageSets { get; private set; }

    /// <summary>How many times the atlas has been copied to the GPU.</summary>
    /// <remarks>
    ///     ⚠ The claim this exists to make checkable is that a frame drawing text it has drawn before
    ///     uploads nothing. An atlas re-uploaded every frame is a megabyte of transfer per frame to
    ///     move bytes that did not change, and it is invisible in the picture.
    /// </remarks>
    public int AtlasUploads { get; private set; }

    /// <summary>How many frames' worth of geometry the buffers hold at once.</summary>
    /// <remarks>
    ///     The device's frames in flight, and the reason the buffers are that many times bigger than
    ///     one frame needs. Exposed for the same reason the two above are: the claim it makes — that
    ///     a frame never writes over geometry an unfinished frame is reading — is otherwise
    ///     unobservable from outside, and its failure mode is a picture that is briefly a blend of
    ///     two frames rather than an error anything reports.
    /// </remarks>
    public int Regions => slots;

    /// <summary>Which of them the last <see cref="Upload" /> wrote.</summary>
    /// <remarks>
    ///     ⚠ <b>It advances per upload, which assumes one upload per frame.</b> That is what a
    ///     renderer per surface gives, and it is the only arrangement there is today. A caller that
    ///     drew two surfaces through one renderer would consume two regions a frame and could come
    ///     back round to one an unfinished frame is still reading — the fix for which is a renderer
    ///     each, not a bigger ring, because the two surfaces have different geometry anyway.
    /// </remarks>
    public int Region => slot;

    /// <summary>
    ///     Puts this frame's geometry where the GPU can read it. Called outside a render pass.
    /// </summary>
    /// <param name="commands">A list that is not inside a pass, for the atlas copy.</param>
    /// <param name="geometry">The frame's geometry.</param>
    /// <param name="atlas">The glyph atlas the text draws from.</param>
    /// <remarks>
    ///     ⚠ <b>Separate from <see cref="Record" /> because a texture copy cannot happen inside a
    ///     render pass.</b> Both halves take a command list and it would be tempting to merge them;
    ///     the validation layers would reject the result, and only when a glyph the frame had not
    ///     drawn before appeared.
    /// </remarks>
    public void Upload(ICommandList commands, in UiGeometry geometry, GlyphAtlas atlas) {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(atlas);

        UploadGeometry(geometry);
        UploadAtlas(commands, atlas);
    }

    /// <summary>Records the frame. Called inside a render pass.</summary>
    /// <param name="commands">Where to record.</param>
    /// <param name="geometry">The frame's geometry, already uploaded.</param>
    /// <param name="surface">
    ///     The size of the target <b>in the geometry's own units</b> — the same rectangle the
    ///     geometry was built against, not the framebuffer.
    /// </param>
    /// <param name="scale">
    ///     How many framebuffer pixels one of those units is. One when the interface is laid out in
    ///     physical pixels; the display's DPI scale when it is laid out in device-independent ones.
    /// </param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Two spaces, and they are only the same at 1:1.</b> The projection maps geometry
    ///         units onto clip space and needs the geometry's extent; a scissor is submitted in
    ///         framebuffer pixels and needs the framebuffer's. Handing the framebuffer size to a
    ///         frame whose geometry is in device-independent units draws the whole interface into
    ///         the top-left <c>1/scale</c> of the window — which reads as a renderer that is
    ///         mysteriously small rather than as a unit mismatch, and takes the pointer with it,
    ///         because hit testing is done against the layout and the layout is right.
    ///     </para>
    ///     <para>
    ///         <b>The alternative was to scale the geometry instead</b>, and it is worse: the vertex
    ///         positions are not the only thing in a geometry unit — the shape parameters a rounded
    ///         box's signed distance is evaluated against are too — so scaling after the fact means
    ///         scaling several things that must agree, in a builder that is otherwise a pure
    ///         function of a draw list. Two numbers here cost one multiply per draw.
    ///     </para>
    /// </remarks>
    public void Record(ICommandList commands, in UiGeometry geometry, Int2 surface, float scale = 1f) {
        ArgumentNullException.ThrowIfNull(commands);

        Draws = 0;

        if (geometry.Indices.Count == 0 || surface.X <= 0 || surface.Y <= 0 || scale <= 0f) {
            return;
        }

        var bound = default(Bindings);
        Submit(commands, geometry, self: -1, surface, scale, ref bound);
    }

    /// <summary>
    ///     Renders this frame's composited groups into surfaces of their own. Called outside a render
    ///     pass, after <see cref="Upload" /> and before <see cref="Record" />.
    /// </summary>
    /// <param name="commands">A list that is not inside a pass, because each group opens one.</param>
    /// <param name="geometry">The frame's geometry, already uploaded.</param>
    /// <param name="surface">The size of the target, in the geometry's own units. As <see cref="Record" />.</param>
    /// <param name="scale">How many framebuffer pixels one of those units is. As <see cref="Record" />.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>An entry point of its own, in the shape of <see cref="Upload" />, because a render
    ///         pass cannot be opened inside one.</b> A group is a pass — it clears a surface, draws a
    ///         subtree into it and stores the result — and <see cref="Record" /> is called by a host
    ///         that has already begun the frame's pass. Recursing there would be
    ///         <c>"'…' began while '…' already had a pass open. Passes do not nest."</c> from the
    ///         Vulkan backend, or the same rule unenforced and undefined on a backend that does not
    ///         check. So the passes are recorded first, before the caller's own begins, and
    ///         <see cref="Record" /> meets their results as ordinary registered textures.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Optional, and a host that never calls it draws what it drew before.</b> See
    ///         <see cref="composed" />: the skip in <c>Submit</c> is conditional on a surface
    ///         existing, so the flat walk is still what a frame with no composited group gets — and
    ///         still what a frame with one gets from a host that has not adopted this. Nothing throws
    ///         and no picture is left half-drawn.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Innermost first, which is what reverse pre-order buys.</b>
    ///         <see cref="UiGeometry.Layers" /> is sorted so that a group comes before every group
    ///         nested in it; walking it backwards therefore renders every group's children before the
    ///         group itself, which is the order the dependency runs in — an outer group's pass
    ///         <i>samples</i> its children's surfaces.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A colour filter costs this method nothing, and that is a claim about the two
    ///         paragraphs above rather than about the matrix.</b> <see cref="UiLayer.Filter" /> is not
    ///         read here at all beyond being copied into <see cref="layerFilters" />: it adds no pass,
    ///         no surface, no barrier and no widening of <see cref="Confine" />'s region, because a
    ///         per-pixel transform has nothing to read from a neighbour and can therefore be folded
    ///         into the composite draw the group was going to make anyway — see
    ///         <see cref="SubmitDraw" />. A blurred group costs a shared scratch and two more passes
    ///         and needs its bounds outset; a filtered one costs a pipeline switch and forty-eight
    ///         bytes. Anything that made the second look like the first would be paying a
    ///         neighbourhood operation's price for something that is not one.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And this is where <c>backdrop-filter</c> stops, which is worth writing down here
    ///         rather than in a plan document, because the reason is the shape of this method.</b> A
    ///         backdrop filter reads what is <i>behind</i> the layer — the destination the group is
    ///         about to composite into — and at the moment a group's surface is rendered that
    ///         destination has not been drawn. It cannot have been: the paragraph two up says these
    ///         passes are recorded <i>before</i> the caller's own begins, because a pass cannot be
    ///         opened inside one, so nothing painted below the group exists yet. Nor is the composite
    ///         a way round it — by then the destination is the colour attachment being written, which
    ///         is not sampleable without an input attachment or a copy out of the pass, and neither
    ///         exists in this layer's command list.
    ///         <para>
    ///             ⚠ <b>The trap it sets is that <c>SoftwareUiRasterizer</c> could do it in three
    ///             lines</b> — its recursion already holds the parent's buffer while it runs the
    ///             group's — so a backdrop filter written there alone would look implemented and would
    ///             fail <c>UiCompositingTests</c> as a compositing divergence rather than as a missing
    ///             feature. What closing it actually needs is the backdrop captured into a real
    ///             texture before the groups that read it are composed: a second walk over the layer
    ///             tree to find which groups have one, an ordering constraint saying everything behind
    ///             each of them is finished first, and a copy per backdrop group. That is a different
    ///             feature with a different price, not a slot in the matrix.
    ///         </para>
    ///     </para>
    /// </remarks>
    public void Compose(ICommandList commands, in UiGeometry geometry, Int2 surface, float scale = 1f) {
        ArgumentNullException.ThrowIfNull(commands);

        composed = 0;
        blurred = 0;
        shadowed = 0;
        filtered = 0;
        masked = 0;

        // ⚠ Cleared here and not where it is filled, so that every early return below leaves the map
        // empty rather than holding the *previous* frame's answers keyed by numbers this frame's
        // layers may well reuse — `UiGeometryBuilder.LayerImage` counts down from the top for every
        // frame, so a group in the same position gets the same number.
        layerFilters.Clear();
        layerMasks.Clear();

        if (geometry.Layers.Count == 0 || geometry.Indices.Count == 0) {
            return;
        }

        if (surface.X <= 0 || surface.Y <= 0 || scale <= 0f || !imagePipeline.IsValid) {
            // ⚠ No image pipeline means no shader to composite a surface back with, so rendering the
            // surfaces would cost a pass each and put nothing on screen. Left uncomposed, which draws
            // the groups' contents in place — see `composed`.
            return;
        }

        var width = (int) MathF.Ceiling(surface.X * scale);
        var height = (int) MathF.Ceiling(surface.Y * scale);

        if (width <= 0 || height <= 0) {
            return;
        }

        EnsureSurfaces(geometry, width, height);
        composed = geometry.Layers.Count;

        // ⚠ In a loop of its own and before the passes, because it is read by `Record` rather than
        // here — a group's composite draw is submitted by whichever pass contains it, and that pass
        // may be another group's, recorded earlier in the walk below. Filling the map as each group's
        // own pass came round would mean a nested group's composite was submitted before the entry
        // that describes it existed.
        // ⚠ The identity is not stored. A `grayscale(0)` is a real filter as far as the draw list is
        // concerned — CSS isolates for it — but putting it through the colour pipeline would cost a
        // pipeline switch and a push to compute the sample back, and `Filtered` would then count a
        // draw nothing happened to.
        if (colourPipeline.IsValid || maskPipeline.IsValid) {
            foreach (var layer in geometry.Layers) {
                if (layer.Filter is { } matrix && !matrix.IsIdentity) {
                    layerFilters[layer.Image] = matrix;
                }

                // ⚠ <b>The shadow's tint goes in the same map, keyed by the shadow's own number, and
                // that is the whole of what makes a drop shadow a drop shadow rather than a second
                // copy of the element.</b> <see cref="UiDropShadow.Tint" /> has zero coefficients and
                // the colour in its offsets, so <c>ui-colour.frag</c> — unchanged, and not told it is
                // drawing a shadow — evaluates <c>0·c + colour·a</c> and writes the silhouette.
                //
                // ⚠ Conditional on the surface existing rather than on the layer having a shadow,
                // because <see cref="EnsureSurfaces" /> declines to allocate one where the host has
                // no colour stage. An entry here for a surface that was never made would put the
                // quad through the colour pipeline to sample an unregistered image — which
                // <see cref="SubmitDraw" /> skips, after counting nothing, so the two would simply
                // disagree about how many draws the frame had.
                if (layer.Shadow is { } cast && layerSurfaces.ContainsKey(layer.ShadowImage)) {
                    layerFilters[layer.ShadowImage] = cast.Tint;
                }
            }
        }

        // ⚠ In the same shape and for the same reason, and separately because the two pipelines are
        // not the same optional stage. A host with a colour stage and no mask stage must still get
        // its matrices; a host with the reverse must still get its masks — and a group carrying both
        // needs `maskPipeline`, which is the only module that does both. The identity is dropped one
        // level up, in `DrawListBuilder`, rather than here: an opaque mask never becomes a
        // `UiLayer.Mask` at all, because unlike a `grayscale(0)` it is not worth a group.
        if (maskPipeline.IsValid) {
            foreach (var layer in geometry.Layers) {
                // ⚠ The ceiling is checked here rather than at the upload, because this is the map
                // the draw reads: a group whose entries did not fit has to composite *unmasked*, and
                // leaving it in the map with a range past the written region would have it composite
                // by whatever the previous frame left in the buffer. See `MaskCapacity`.
                if (layer.MaskCount > 0 && layer.MaskFirst + layer.MaskCount <= MaskCapacity) {
                    layerMasks[layer.Image] = (layer.MaskFirst, layer.MaskCount);

                    // ⚠ <b>The shadow is masked too, and it is masked in its own frame rather than
                    // where it lands — which is a stated divergence and not an oversight.</b> CSS
                    // applies `filter` first and `mask` to the result, so a browser cuts the shadow
                    // by the mask at the shadow's real position. Both executors recover a mask's
                    // point as `uv × size`, and the shadow quad's UV is deliberately the
                    // <i>un-displaced</i> one — that is how the offset is expressed at all — so what
                    // this gives is the mask travelling with the silhouette. Exact when the offset is
                    // zero, and when it is not, a `mask-b-from-*` fades the shadow out at the bottom
                    // of the shadow instead of at the bottom of the box.
                    //
                    // ⚠ The alternative was leaving the shadow unmasked, which is worse and not
                    // merely differently wrong: a faded element casting a hard-edged shadow is ink
                    // escaping a mask, which is the one thing a mask is for. Closing it properly
                    // needs the displacement in a push constant so the fragment can evaluate the fold
                    // at `uv × size + offset`, which is a shader change for a combination — a mask
                    // and a drop shadow on one element — that nothing in the engine writes yet.
                    if (layer.Shadow is not null && layerSurfaces.ContainsKey(layer.ShadowImage)) {
                        layerMasks[layer.ShadowImage] = (layer.MaskFirst, layer.MaskCount);
                    }
                }
            }
        }

        for (var index = geometry.Layers.Count - 1; index >= 0; index--) {
            var layer = geometry.Layers[index];
            var target = layerSurfaces[layer.Image];

            // ⚠ The source half names the shader stages, and that is what makes one surface per group
            // enough rather than one per frame in flight. See `layerSurfaces`: the hazard is this
            // frame's colour writes overtaking the *previous* frame's sampling of the same texture,
            // and an execution dependency from the shader stages to the colour-attachment stage is
            // exactly the ordering a write-after-read wants. `Undefined` on the first frame, because a
            // texture nothing has written has no contents to preserve and claiming otherwise is a
            // validation error on exactly one frame.
            commands.Barrier(new([], [new(target.Texture, target.State, ResourceState.ColourTarget)]));

            // ⚠ The reach is computed here rather than inside `BlurSurface`, because it widens the
            // region *this* pass has to leave defined — see `Confine`. `BlurSurface` derives it again
            // from the same method, so the two cannot drift.
            // ⚠ The wider of the two, because the shadow's kernel reads this pass's target — see
            // `UiGeometryBuilder.Layer`, which outsets the bounds by the same maximum. A region
            // widened only by the group's own blur would leave the shadow's outermost taps reading
            // texels this pass never wrote, which on a reused surface is the *previous* group's
            // contents smeared along the shadow's edge.
            var reach = blurPipeline.IsValid
                ? Math.Max(
                    UiLayer.KernelRadius(layer.Blur, scale),
                    UiLayer.KernelRadius(layer.Shadow?.Blur ?? 0f, scale)
                )
                : 0;

            var region = Confine(layer.Bounds, surface, scale, reach);

            // Transparent black, not the frame's background: a group composites *over* whatever is
            // already on the target, so starting its surface from that would blend it in twice.
            commands.BeginRenderPass(
                new(
                    [new(target.View, LoadAction.Clear, StoreAction.Store, new Color4(0f, 0f, 0f, 0f))],
                    name: "ui layer " + index.ToString(CultureInfo.InvariantCulture),
                    renderArea: region
                )
            );

            // ⚠ Nothing carries over a pass boundary — not the pipeline, not the descriptor set, not
            // the push constants — so each group's pass starts from an empty binding state. Sharing
            // one across the passes is the optimisation that looks free and is a frame drawn with
            // whatever the last pass left bound.
            var bound = default(Bindings);
            Submit(commands, geometry, index, surface, scale, ref bound);

            commands.EndRenderPass();

            // ⚠ <b>Between the group's pass and its <c>ShaderRead</c> barrier, which is the only
            // window where the surface is finished and nothing downstream has been told it is
            // readable yet.</b> Two more passes go in here and the surface comes back out in
            // <c>ColourTarget</c>, so the barrier below is the same barrier either way — the blur is
            // an interpolation into this loop rather than a second shape of it.
            // ⚠ The count comes back from the work rather than being incremented beside it, because
            // every early return in `BlurSurface` is a blur that did not happen — and `Blurred`
            // exists precisely so that a blur which did not happen cannot look like one that did.
            if (layer.Blur > 0f
                && blurPipeline.IsValid
                && Scratch(width, height) is { } through
                && BlurSurface(commands, geometry, layer, target, through, surface, scale, region)) {
                blurred++;
            }

            // ⚠ <b>After the group's own blur and in the same window, because a drop shadow is cast
            // from the <i>finished</i> surface.</b> The two Gaussians do not commute — see
            // <see cref="UiLayer.Shadow" /> — so the order is fixed rather than chosen, and this is
            // the only place in the loop where the group's contents are complete and nothing has
            // sampled them. `SoftwareUiRasterizer` casts its shadow at the same seam, from the same
            // surface, which is what makes the two comparable at all.
            if (layer.Shadow is not null
                && blurPipeline.IsValid
                && layerSurfaces.TryGetValue(layer.ShadowImage, out var cast)
                && Scratch(width, height) is { } bounce
                && ShadowSurface(commands, geometry, layer, target, cast, bounce, surface, scale, region)) {
                shadowed++;
            }

            commands.Barrier(
                new([], [new(target.Texture, ResourceState.ColourTarget, ResourceState.ShaderRead)])
            );

            target.State = ResourceState.ShaderRead;
        }
    }

    /// <summary>Runs the two axes of a group's blur, and leaves its surface a colour target again.</summary>
    /// <returns>Whether the blur ran. False leaves every resource in the state it was handed.</returns>
    /// <param name="commands">A list that is not inside a pass. Each axis opens one.</param>
    /// <param name="geometry">The frame's geometry, for the quad to draw.</param>
    /// <param name="layer">The group.</param>
    /// <param name="target">Its surface, holding its finished contents and about to hold the blur.</param>
    /// <param name="through">The scratch the first axis lands in.</param>
    /// <param name="surface">The target size in geometry units.</param>
    /// <param name="scale">Framebuffer pixels per geometry unit.</param>
    /// <param name="region">
    ///     The part of both targets these passes are confined to — <see cref="Confine" />'s answer for
    ///     this group, already widened by the kernel.
    /// </param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The quad is the group's own composite quad, drawn twice with a different
    ///         pipeline.</b> It already covers exactly the rectangle the blur has to fill — that is
    ///         what <c>UiGeometryBuilder.Layer</c>'s outset is for — and its texture coordinates
    ///         already map that rectangle onto the viewport-sized surface. Emitting a quad here
    ///         instead would mean a vertex buffer of this renderer's own, uploaded outside the one
    ///         upload a frame is supposed to be, to describe geometry that is already in the one that
    ///         was.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Both axes clear rather than load, and that is what makes a pass <i>replace</i>
    ///         instead of blending over what was there.</b> The second axis writes back into the
    ///         surface the group's unblurred contents are still sitting in; premultiplied source-over
    ///         onto a cleared attachment is the identity, so the clear is the whole of the
    ///         difference between a blurred group and a blurred group composited over a sharp copy of
    ///         itself. The first axis clears for a different reason: the scratch is shared, so
    ///         whatever the previous blurred group left outside this one's bounds would otherwise be
    ///         read by this one's vertical taps.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Nothing is bound across the two passes.</b> A pass boundary drops the pipeline,
    ///         the set and the push constants, which is the same rule <see cref="Compose" />'s own
    ///         loop opens a fresh <c>Bindings</c> for.
    ///     </para>
    /// </remarks>
    bool BlurSurface(
        ICommandList commands,
        in UiGeometry geometry,
        in UiLayer layer,
        LayerSurface target,
        LayerSurface through,
        Int2 surface,
        float scale,
        ScissorRect region
    ) {
        // The composite quad sits just after the group's own draws, past the shadow's quad where
        // there is one — see <see cref="UiLayer.Composite" />, which is where that arithmetic lives
        // now that it has two cases. A geometry that has been truncated between being built and being
        // composed would index past the end here rather than draw the wrong thing, so it is checked.
        var composite = layer.Composite;

        if (composite >= geometry.Draws.Count) {
            return false;
        }

        var quad = geometry.Draws[composite];
        var scissor = Scissor(quad.Clip, surface, scale);

        if (scissor.Width <= 0 || scissor.Height <= 0) {
            return false;
        }

        var reach = UiLayer.KernelRadius(layer.Blur, scale);

        if (reach <= 0) {
            return false;
        }

        var sigma = layer.Blur * scale;
        var source = imageDescriptors.TryGetValue(layer.Image, out var registered) ? registered.Sets[slot] : default;

        if (!source.IsValid || !scratchSet.IsValid) {
            return false;
        }

        // Across, into the scratch: the surface is read, so it has to stop being a colour target
        // first, and the scratch has to start being one.
        commands.Barrier(
            new(
                [],
                [
                    new(target.Texture, ResourceState.ColourTarget, ResourceState.ShaderRead),
                    new(through.Texture, through.State, ResourceState.ColourTarget)
                ]
            )
        );

        Sweep(commands, through, quad, scissor, region, source, surface, 1f / layerWidth, 0f, sigma, reach);

        // And back down, into the surface. The two textures swap roles, which is why the scratch
        // cannot simply stay a colour target: this is the pass that samples it.
        commands.Barrier(
            new(
                [],
                [
                    new(through.Texture, ResourceState.ColourTarget, ResourceState.ShaderRead),
                    new(target.Texture, ResourceState.ShaderRead, ResourceState.ColourTarget)
                ]
            )
        );

        through.State = ResourceState.ShaderRead;

        Sweep(commands, target, quad, scissor, region, scratchSet, surface, 0f, 1f / layerHeight, sigma, reach);

        return true;
    }

    /// <summary>Casts a group's <c>drop-shadow</c> into a surface of its own.</summary>
    /// <returns>Whether it ran. False leaves every resource in the state it was handed.</returns>
    /// <param name="commands">A list that is not inside a pass. Each axis opens one.</param>
    /// <param name="geometry">The frame's geometry, for the quad to draw.</param>
    /// <param name="layer">The group.</param>
    /// <param name="target">Its finished surface — the source, and left untouched.</param>
    /// <param name="cast">The shadow's own surface, which this fills.</param>
    /// <param name="through">The scratch the first axis lands in.</param>
    /// <param name="surface">The target size in geometry units.</param>
    /// <param name="scale">Framebuffer pixels per geometry unit.</param>
    /// <param name="region">The part of every target these passes are confined to, already widened.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The group's own composite quad, not the shadow's, and picking the wrong one is a
    ///         picture rather than an error.</b> These passes fill the region of the shadow's surface
    ///         that the shadow's quad will <i>sample</i>, and that region is where the group is —
    ///         un-displaced. The offset lives entirely in the shadow quad's vertices, whose texture
    ///         coordinates are taken from where it would have been without one; see
    ///         <c>UiGeometryBuilder.Layer</c>. Sweeping through the displaced quad would move the
    ///         silhouette twice.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Two passes and not three, and it is a different ping-pong from
    ///         <see cref="BlurSurface" />'s.</b> That one has to land back in the surface it started
    ///         from, so it goes target → scratch → target. This one has somewhere else to be, so the
    ///         second axis writes straight into the shadow's surface and the group's own is only ever
    ///         read. A drop shadow therefore costs exactly what a blur costs — two passes and no
    ///         copy — plus the surface.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A shadow with no blur is one pass and a sigma of one, which is not the lie it
    ///         looks like.</b> <c>drop-shadow(2px 2px)</c> is a legal, offset, hard-edged silhouette,
    ///         and <c>KernelRadius</c> answers zero for it. <c>ui-blur.frag</c> with a reach of zero
    ///         takes exactly one tap, whose weight is <c>exp(0/2σ²)/1</c> — one, for every σ that is
    ///         not itself zero. Passing the real zero would make that <c>exp(-0/0)</c>, which is a
    ///         NaN and a surface of them. So the one pass is an exact copy and the number beside it
    ///         is chosen to be any positive one; the alternative was a <c>max</c> in a shipped shader
    ///         for a case the shader cannot otherwise reach.
    ///     </para>
    /// </remarks>
    bool ShadowSurface(
        ICommandList commands,
        in UiGeometry geometry,
        in UiLayer layer,
        LayerSurface target,
        LayerSurface cast,
        LayerSurface through,
        Int2 surface,
        float scale,
        ScissorRect region
    ) {
        if (layer.Shadow is not { } shadow || layer.Composite >= geometry.Draws.Count) {
            return false;
        }

        var quad = geometry.Draws[layer.Composite];
        var scissor = Scissor(quad.Clip, surface, scale);

        if (scissor.Width <= 0 || scissor.Height <= 0) {
            return false;
        }

        var source = imageDescriptors.TryGetValue(layer.Image, out var registered) ? registered.Sets[slot] : default;

        if (!source.IsValid || !scratchSet.IsValid) {
            return false;
        }

        var reach = UiLayer.KernelRadius(shadow.Blur, scale);
        var sigma = reach > 0 ? shadow.Blur * scale : 1f;

        // ⚠ <b>The scissor is the composite quad's clip and <i>not</i> the shadow quad's, which is
        // the same choice the quad above is.</b> What is being filled is the un-displaced footprint;
        // narrowing it by the clip the displaced quad will be cut to would cut the silhouette twice,
        // once in the wrong place. The shadow's own clip is applied where it belongs, on its own
        // draw, by `SubmitDraw`.
        if (reach <= 0) {
            commands.Barrier(
                new(
                    [],
                    [
                        new(target.Texture, ResourceState.ColourTarget, ResourceState.ShaderRead),
                        new(cast.Texture, cast.State, ResourceState.ColourTarget)
                    ]
                )
            );

            Sweep(commands, cast, quad, scissor, region, source, surface, 0f, 0f, sigma, 0);

            commands.Barrier(
                new(
                    [],
                    [
                        new(cast.Texture, ResourceState.ColourTarget, ResourceState.ShaderRead),
                        new(target.Texture, ResourceState.ShaderRead, ResourceState.ColourTarget)
                    ]
                )
            );

            cast.State = ResourceState.ShaderRead;
            return true;
        }

        // Across, into the scratch. The group's surface is read, so it stops being a colour target
        // for the duration and is handed back as one below.
        commands.Barrier(
            new(
                [],
                [
                    new(target.Texture, ResourceState.ColourTarget, ResourceState.ShaderRead),
                    new(through.Texture, through.State, ResourceState.ColourTarget)
                ]
            )
        );

        Sweep(commands, through, quad, scissor, region, source, surface, 1f / layerWidth, 0f, sigma, reach);

        // And down, into the shadow's own surface — which is where this differs from a blur, whose
        // second axis lands back where it started.
        commands.Barrier(
            new(
                [],
                [
                    new(through.Texture, ResourceState.ColourTarget, ResourceState.ShaderRead),
                    new(cast.Texture, cast.State, ResourceState.ColourTarget)
                ]
            )
        );

        through.State = ResourceState.ShaderRead;

        Sweep(commands, cast, quad, scissor, region, scratchSet, surface, 0f, 1f / layerHeight, sigma, reach);

        // ⚠ Both in one barrier, and the group's surface is put *back* to being a colour target
        // rather than left readable. The loop that called this ends with one barrier taking it from
        // `ColourTarget` to `ShaderRead`, and it is the same barrier whether a blur ran, a shadow
        // ran, or neither — see `Compose`. A method that left the state changed would make that one
        // line wrong for exactly the frames this feature appears in.
        commands.Barrier(
            new(
                [],
                [
                    new(cast.Texture, ResourceState.ColourTarget, ResourceState.ShaderRead),
                    new(target.Texture, ResourceState.ShaderRead, ResourceState.ColourTarget)
                ]
            )
        );

        cast.State = ResourceState.ShaderRead;
        return true;
    }

    /// <summary>One axis: a pass, the composite quad, and the kernel in a push constant.</summary>
    void Sweep(
        ICommandList commands,
        LayerSurface into,
        in UiDraw quad,
        ScissorRect scissor,
        ScissorRect region,
        DescriptorSetHandle source,
        Int2 surface,
        float stepX,
        float stepY,
        float sigma,
        int reach
    ) {
        commands.BeginRenderPass(
            new(
                [new(into.View, LoadAction.Clear, StoreAction.Store, new Color4(0f, 0f, 0f, 0f))],
                name: "ui blur",
                renderArea: region
            )
        );

        commands.BindVertexBuffer(0, vertices, (long) slot * vertexCapacity);
        commands.BindIndexBuffer(indices, IndexFormat.UInt32, (long) slot * indexCapacity);
        commands.BindPipeline(blurPipeline);

        Span<float> projection = [2f / surface.X, -2f / surface.Y, -1f, 1f];
        commands.PushConstants(ShaderStage.Vertex, 0, MemoryMarshal.AsBytes(projection));

        Span<float> kernel = [stepX, stepY, sigma, reach];
        commands.PushConstants(ShaderStage.Fragment, 16, MemoryMarshal.AsBytes(kernel));

        commands.BindDescriptorSet(DescriptorSetSlot.PerFrame, source);
        commands.SetScissor(scissor);
        commands.DrawIndexed(quad.Count, firstIndex: quad.First);

        commands.EndRenderPass();
    }

    /// <summary>What is bound within one pass, so that a run of draws rebinds only what changes.</summary>
    struct Bindings {
        public PipelineHandle Pipeline;
        public DescriptorSetHandle Set;
        public bool Pushed;
        public bool Geometry;
    }

    /// <summary>Submits one group's draws, or the whole frame's when <paramref name="self" /> is -1.</summary>
    /// <param name="commands">Where to record. Already inside the pass these draws belong to.</param>
    /// <param name="geometry">The frame's geometry.</param>
    /// <param name="self">Which layer's range to submit, or -1 for the frame's top level.</param>
    /// <param name="surface">The target size in geometry units.</param>
    /// <param name="scale">Framebuffer pixels per geometry unit.</param>
    /// <param name="bound">What this pass has bound so far.</param>
    /// <remarks>
    ///     ⚠ <b>A group already rendered into a surface contributes its <i>composite</i> and not its
    ///     contents, and drawing both is the failure this walk exists to prevent.</b> The composite
    ///     quad sits immediately after the group's own draws — see <c>UiGeometryBuilder.Layer</c> — so
    ///     jumping to the end of the range puts the group on screen exactly once. Drawing the contents
    ///     as well would paint the subtree twice, at full opacity underneath and faded on top, which
    ///     is a picture that looks nearly right on anything opaque and obviously wrong on text.
    /// </remarks>
    void Submit(
        ICommandList commands,
        in UiGeometry geometry,
        int self,
        Int2 surface,
        float scale,
        ref Bindings bound
    ) {
        var layers = geometry.Layers;
        var from = self < 0 ? 0 : layers[self].First;
        var to = self < 0 ? geometry.Draws.Count : layers[self].First + layers[self].Count;

        // ⚠ Clamped rather than trusted, because <see cref="composed" /> is a count taken from the
        // *last* geometry <see cref="Compose" /> saw. A host that composed one frame and recorded a
        // different one is misusing the pair — the contract is that they are called together — but
        // the shape of that mistake should be a group drawn uncomposited, not an index out of range.
        var groups = Math.Min(composed, layers.Count);

        // Document pixels to clip space, and ⚠ <b>y is flipped</b> — which is the opposite of what
        // the reasoning "the interface's y runs down and so does Vulkan's" arrives at, and that
        // reasoning was written into this file before the picture was looked at. Vulkan's raw clip
        // space does have +y down, but nothing here ever sees it: `VulkanCommandList.SetViewport`
        // submits a negative-height viewport so that the engine's convention of +y up holds
        // everywhere (Core/Vixen.Core.Mathematics/Conventions.md). A frame that agreed with the API
        // instead of with the engine draws upside down, and every unit test in `Vixen.Ui` passes
        // while it does.
        //
        // ⚠ The same numbers in a group's pass as in the frame's, and that is the whole of why a
        // layer surface is the size of the viewport rather than of the group. See `UiLayer`: there is
        // no origin to subtract, so the two paths cannot subtract it differently.
        Span<float> projection = [2f / surface.X, -2f / surface.Y, -1f, 1f];

        var draw = from;

        // The first group nested inside this one, in pre-order. -1 + 1 is zero, which is the frame's
        // first group, so the top level needs no special case.
        var child = self + 1;

        while (draw < to) {
            if (child < groups && layers[child].First == draw) {
                draw = layers[child].First + layers[child].Count;

                // Past the skipped group's own descendants, which lie inside the range just jumped
                // over. Without this the grandchildren would be skipped again against the composite
                // draws that follow.
                while (child < groups && layers[child].First < draw) {
                    child++;
                }

                continue;
            }

            SubmitDraw(commands, geometry.Draws[draw], surface, scale, projection, ref bound);
            draw++;
        }
    }

    /// <summary>Submits one draw, binding whatever it needs that is not bound.</summary>
    void SubmitDraw(
        ICommandList commands,
        in UiDraw draw,
        Int2 surface,
        float scale,
        ReadOnlySpan<float> projection,
        ref Bindings bound
    ) {
        // ⚠ <b>Looked up before the pipeline is chosen, because it <i>is</i> the choice.</b> A
        // composite draw whose group carries a colour matrix goes through `ui-colour.frag` instead of
        // `ui-image.frag`; everything else, including a composite with no filter and an ordinary
        // image that happens to share a number with nothing, goes through the image pipeline. The
        // map is empty on almost every frame, so the common path is one `Count` test.
        var matrix = layerFilters.Count > 0
            && draw.Kind == BatchKind.Image
            && layerFilters.TryGetValue(draw.Image, out var entry)
                ? entry
                : default(UiColorMatrix?);

        var mask = layerMasks.Count > 0
            && draw.Kind == BatchKind.Image
            && layerMasks.TryGetValue(draw.Image, out var range)
                ? range
                : default((int First, int Count)?);

        // ⚠ <b>The mask wins, because it is the only one of the three modules that does both jobs.</b>
        // Choosing `colourPipeline` for a group that has a matrix *and* a mask would drop the mask
        // silently — the picture would be a correctly filtered, entirely unmasked group, which looks
        // like the mask never parsed. That precedence is also what makes the matrix unconditional in
        // the push below: it goes out as the identity when there is no filter.
        var pipeline = mask is not null
            ? maskPipeline
            : matrix is null
                ? PipelineFor(draw.Kind)
                : colourPipeline;

        if (!pipeline.IsValid) {
            // An image with no image shader. Skipped rather than drawn with another pipeline,
            // which would put the font atlas in the hole and read as a rendering fault.
            return;
        }

        if (!bound.Geometry) {
            // ⚠ At this frame's region, and once per pass. The indices stay zero-based because the
            // vertex binding moves with them — an index is relative to the bound vertex offset, which
            // is exactly why the ring costs nothing downstream.
            commands.BindVertexBuffer(0, vertices, (long) slot * vertexCapacity);
            commands.BindIndexBuffer(indices, IndexFormat.UInt32, (long) slot * indexCapacity);
            bound.Geometry = true;
        }

        if (pipeline != bound.Pipeline) {
            commands.BindPipeline(pipeline);
            bound.Pipeline = pipeline;
        }

        if (!bound.Pushed) {
            // ⚠ After the first pipeline and then never again. It is written through the bound
            // pipeline's layout, so there is nothing to write it through until one is bound — and
            // because every pipeline shares one layout, a later pipeline change cannot disturb it.
            commands.PushConstants(ShaderStage.Vertex, 0, MemoryMarshal.AsBytes(projection));
            bound.Pushed = true;
        }

        if (mask is { } list) {
            // ⚠ <b>The matrix goes out here too, as the identity when the group has no filter.</b>
            // `ui-mask.frag` reads its matrix rows unconditionally, so a mask-only group that pushed
            // only its list would leave them holding whatever the last filtered draw put there — the
            // group would come out tinted by its predecessor. That is a defect which needs two groups
            // in one frame to appear, and so survives every single-group test.
            var identity = matrix ?? UiColorMatrix.Identity;

            // The packing `ui-mask.frag` declares: three matrix rows, then the index and the count.
            // ⚠ <b>The index is absolute within the buffer and carries this frame's own region with
            // it.</b> The binding covers the whole allocation rather than one frame's slice — a
            // ring of offsets would be a descriptor rewrite per frame on sets that are shared with
            // every image in the interface — so the arithmetic that picks the frame happens here,
            // once per masked draw, and `UploadGeometry` writes at the matching offset.
            Span<float> block = [
                identity.Red.X, identity.Red.Y, identity.Red.Z, identity.Red.W,
                identity.Green.X, identity.Green.Y, identity.Green.Z, identity.Green.W,
                identity.Blue.X, identity.Blue.Y, identity.Blue.Z, identity.Blue.W,
                (slot * MaskCapacity) + list.First, list.Count, 0f, 0f
            ];

            commands.PushConstants(ShaderStage.Fragment, 16, MemoryMarshal.AsBytes(block));
        } else if (matrix is { } filter) {
            // ⚠ Every time, rather than tracked in `Bindings` the way the projection is. Two filtered
            // groups in one pass carry two different matrices and the second one's is not the first's
            // — so a `Pushed` flag here would draw the second group with the first's filter, which is
            // a picture that looks like a filter working. Push constants are the cheapest thing in
            // the API and there are at most a handful of these draws in a frame.
            Span<float> rows = [
                filter.Red.X, filter.Red.Y, filter.Red.Z, filter.Red.W,
                filter.Green.X, filter.Green.Y, filter.Green.Z, filter.Green.W,
                filter.Blue.X, filter.Blue.Y, filter.Blue.Z, filter.Blue.W
            ];

            commands.PushConstants(ShaderStage.Fragment, 16, MemoryMarshal.AsBytes(rows));
        }

        // ⚠ Per draw rather than once, now that a draw can carry its own texture. The set is the
        // atlas for everything except an image, so a frame with no images binds exactly once —
        // the comparison is what keeps the old behaviour rather than a flag that used to.
        var registered = draw.Kind == BatchKind.Image
            ? imageDescriptors.TryGetValue(draw.Image, out var found) ? found.Sets[slot] : default
            : atlasDescriptors.Length > slot ? atlasDescriptors[slot] : default;

        if (!registered.IsValid) {
            // An image nobody registered. Drawing it would sample the atlas through the image
            // shader, which is a rectangle of scrambled glyphs where the picture should be.
            return;
        }

        if (registered != bound.Set) {
            commands.BindDescriptorSet(DescriptorSetSlot.PerFrame, registered);
            bound.Set = registered;
        }

        var scissor = Scissor(draw.Clip, surface, scale);

        if (scissor.Width <= 0 || scissor.Height <= 0) {
            // ⚠ Wholly clipped away — a panel scrolled off the edge, a tooltip for a window that
            // moved. Skipped, and the honest claim for the skip is that it saves a draw call and
            // not that it prevents anything: a zero-extent scissor is legal and draws nothing, so
            // submitting it would produce the same picture. `Draws` is what makes the saving
            // visible, because nothing else can be.
            return;
        }

        commands.SetScissor(scissor);
        commands.DrawIndexed(draw.Count, firstIndex: draw.First);
        Draws++;

        // ⚠ After the draw and after every early return above it, for the reason `Blurred` gives:
        // a filter that did not happen must not be able to look like one that did, and three of the
        // returns between here and the top are reachable for a filtered composite — no registered
        // set, a scissor that clipped it away, a colour pipeline the host never handed over.
        if (matrix is not null) {
            filtered++;
        }

        if (mask is not null) {
            masked++;
        }
    }

    /// <summary>Makes sure there is a surface per group, at the size the frame is being drawn at.</summary>
    /// <remarks>
    ///     ⚠ <b>Recreating one re-registers its number, and this frame's set is written here rather
    ///     than left for the next.</b> <see cref="RegisterImage" /> marks every frame's set stale and
    ///     leaves the writing to <see cref="UploadGeometry" />, because a host registers <i>between</i>
    ///     frames — before <see cref="slot" /> has advanced — and writing the set the just-submitted
    ///     frame is reading is the hazard the ring exists to avoid. Here the opposite holds:
    ///     <see cref="Compose" /> runs after <see cref="Upload" />, so <see cref="slot" /> already
    ///     names the frame about to draw and its set is the one nobody is reading. Leaving it for the
    ///     next frame would bind a set pointing at the view this one just destroyed.
    /// </remarks>
    /// <remarks>
    ///     ⚠ <b>A size change destroys the old surfaces immediately, and a frame in flight may still
    ///     be sampling them.</b> That is the same hazard <c>DestroyAtlas</c> takes when the glyph
    ///     atlas is repacked to a new size, and it is taken here for the same reason: the alternative
    ///     is a deferred-destruction queue for a case that happens when a window is resized, which is
    ///     already the frame every other resource in the host is being rebuilt on. It is written down
    ///     rather than argued away — a caller that resizes without letting the device drain is relying
    ///     on it, and it is not free.
    /// </remarks>
    void EnsureSurfaces(in UiGeometry geometry, int width, int height) {
        if (width != layerWidth || height != layerHeight) {
            DestroySurfaces();
            layerWidth = width;
            layerHeight = height;
        }

        foreach (var layer in geometry.Layers) {
            Ensure(layer.Image);

            // ⚠ <b>Only where the shadow can actually be drawn, and the alternative is worse than
            // no shadow.</b> A shadow's quad composites through <c>colourPipeline</c>, because
            // <see cref="UiDropShadow.Tint" /> is what turns the blurred surface into a silhouette; a
            // host that handed over no colour stage would fall through to the image pipeline and draw
            // an unfiltered, full-colour copy of the element under itself, offset. Left unallocated,
            // the quad names an image nobody registered and <see cref="SubmitDraw" /> skips it — the
            // same degradation every other optional stage gets, and the reason that early return
            // exists.
            if (layer.Shadow is not null && blurPipeline.IsValid && Tintable(layer)) {
                Ensure(layer.ShadowImage);
            }
        }

        // ⚠ <b>Which module the shadow's quad will actually reach, and the two answers are not
        // interchangeable.</b> A shadow is a tint over a silhouette, so the quad must go through a
        // stage that applies `UiColorMatrix` — <c>ui-colour.frag</c>, or <c>ui-mask.frag</c> where the
        // group also carries a mask, since <see cref="SubmitDraw" /> lets the mask win. Asking only
        // whether <i>either</i> exists would allocate a surface for a masked shadow on a host with a
        // colour stage and no mask stage, and the quad would then fall through to the image pipeline
        // and draw a full-colour copy of the element under itself. The mask-capacity test is the same
        // one the map below applies: a group whose entries did not fit composites unmasked, so its
        // shadow needs the colour stage rather than the mask one.
        bool Tintable(in UiLayer layer) =>
            layer.MaskCount > 0 && layer.MaskFirst + layer.MaskCount <= MaskCapacity
                ? maskPipeline.IsValid
                : colourPipeline.IsValid;

        void Ensure(ulong image) {
            if (layerSurfaces.ContainsKey(image)) {
                return;
            }

            var name = "ui layer " + layerSurfaces.Count.ToString(CultureInfo.InvariantCulture);

            var texture = device.CreateTexture(
                new(layerFormat, width, height, TextureUsage.ColourTarget | TextureUsage.Sampled, Name: name)
            );

            var surface = new LayerSurface(texture, device.CreateTextureView(texture), image);
            layerSurfaces[image] = surface;

            RegisterImage(surface.Image, surface.View);
            RebindImage(imageDescriptors[surface.Image], slot);
        }
    }

    /// <summary>The shared blur scratch, made on the first frame that asks for one.</summary>
    /// <remarks>
    ///     ⚠ <b>Not registered through <see cref="RegisterImage" />, and deliberately outside the
    ///     number space <c>UiGeometryBuilder.LayerImage</c> hands out.</b> That space is counted down
    ///     from the top and this would have to be given a number in it that no frame could ever reach
    ///     — an argument about how many groups a frame can have, in place of no argument at all. It is
    ///     never named by a draw, only bound by <see cref="BlurSurface" />, so it needs a set and not
    ///     a name.
    /// </remarks>
    LayerSurface? Scratch(int width, int height) {
        if (scratch is not null) {
            return scratch;
        }

        var texture = device.CreateTexture(
            new(layerFormat, width, height, TextureUsage.ColourTarget | TextureUsage.Sampled, Name: "ui blur scratch")
        );

        scratch = new LayerSurface(texture, device.CreateTextureView(texture), Image: 0);

        scratchSet = device.CreateDescriptorSet(atlasLayout, "ui blur scratch");
        Write(scratchSet, scratch.View);

        return scratch;
    }

    void DestroySurfaces() {
        foreach (var surface in layerSurfaces.Values) {
            UnregisterImage(surface.Image);
            device.Destroy(surface.View);
            device.Destroy(surface.Texture);
        }

        layerSurfaces.Clear();

        if (scratch is null) {
            return;
        }

        // ⚠ Its set goes with it, which is the whole of why one set is enough: the only thing that
        // can invalidate the view behind it destroys the set in the same breath, so there is never a
        // set pointing at a replaced view for a later frame to have to notice.
        if (scratchSet.IsValid) {
            device.Destroy(scratchSet);
            scratchSet = default;
        }

        device.Destroy(scratch.View);
        device.Destroy(scratch.Texture);
        scratch = null;
    }

    /// <summary>Names a texture, so a draw list can ask for it.</summary>
    /// <param name="image">The number the draw commands will carry. Must not be zero.</param>
    /// <param name="view">The texture to draw. Owned by the caller.</param>
    /// <remarks>
    ///     <para>
    ///         <b>The other half of the bargain <c>DrawCommand.Image</c> makes.</b> <c>Vixen.Ui</c>
    ///         describes what to draw and cannot name a texture view; this is where a number becomes
    ///         one. What the number <i>is</i> is the host's business — a viewport uses its render
    ///         target's handle, an image control an asset's — and nothing checks it beyond being
    ///         non-zero, because zero is what every non-image command carries.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The texture is not owned.</b> A registered view that the host destroys leaves a
    ///         descriptor set pointing at freed memory, and the first frame that draws it is
    ///         undefined behaviour rather than an error. Unregister before destroying, which for a
    ///         viewport means on resize — the same moment the target is recreated.
    ///     </para>
    ///     <para>
    ///         Registering a number twice replaces what it points at, which is what a viewport that
    ///         was resized wants: the number stays the same and the texture behind it does not. ⚠ It
    ///         allocates nothing the second time — a viewport resized by a dragged splitter registers
    ///         once a frame for as long as the drag lasts, and a set per registration is a leak the
    ///         backend cannot reclaim, because its pools are created without
    ///         <c>FreeDescriptorSetBit</c> on purpose. The sets are made once and rewritten.
    ///     </para>
    /// </remarks>
    public void RegisterImage(ulong image, TextureViewHandle view) {
        ArgumentOutOfRangeException.ThrowIfZero(image);

        if (imageDescriptors.TryGetValue(image, out var existing)) {
            // ⚠ Marked and not one of them written, this one included. A host registers between
            // frames, which is *before* `UploadGeometry` advances `slot` — so at this moment `slot`
            // still names the frame that has just been submitted, and writing its set is the very
            // hazard the ring exists to avoid. Every write happens after the advance, in
            // `UploadGeometry`, which still runs before this frame records anything.
            imageDescriptors[image] = existing with { View = view };
            Array.Fill(existing.Stale, true);
            return;
        }

        var sets = new DescriptorSetHandle[slots];

        for (var index = 0; index < slots; index++) {
            sets[index] = device.CreateDescriptorSet(
                atlasLayout,
                "ui image " + index.ToString(CultureInfo.InvariantCulture)
            );

            ImageSets++;
        }

        var entry = new ImageEntry(sets, view, new bool[slots]);
        imageDescriptors[image] = entry;

        // ⚠ All of them written here, which is the one place it is safe: they were allocated a line
        // ago, so no submitted frame can be reading one. Once they are in the ring the rule is the
        // opposite — the same distinction `CreateAtlas` draws.
        for (var index = 0; index < slots; index++) {
            Write(sets[index], view);
        }
    }

    /// <summary>Points one frame's set at the texture the number now names.</summary>
    /// <param name="entry">The registration.</param>
    /// <param name="index">The frame, which must be the one about to draw.</param>
    /// <remarks>
    ///     ⚠ <b>The caller has to be the frame that owns the set</b>, for the reason
    ///     <see cref="RebindBoxes" /> spells out: this is a device-side write to a set nothing may be
    ///     reading, and <see cref="slot" /> having come round is the only thing that establishes it.
    /// </remarks>
    void RebindImage(ImageEntry entry, int index) {
        if (!entry.Stale[index]) {
            return;
        }

        Write(entry.Sets[index], entry.View);
        entry.Stale[index] = false;
    }

    /// <summary>Writes the three bindings an image set declares.</summary>
    /// <remarks>
    ///     ⚠ <b>Binding 2 is written for two reasons now, and only one of them is the old one.</b>
    ///     The image shader still never reads it and the layout still declares it — a set that leaves
    ///     a declared binding unwritten is a validation error on the frame it is bound, not on the
    ///     frame it was made. But a composite draw binds one of these sets, and <c>ui-mask.frag</c>
    ///     reads its mask list through exactly this binding, so what it points at is load-bearing
    ///     rather than merely present.
    ///     ⚠ The whole allocation and not one frame's slice: see <see cref="maskEntries" />, whose
    ///     fixed size is what lets a set written once stay correct for every frame after it.
    /// </remarks>
    void Write(DescriptorSetHandle set, TextureViewHandle view) =>
        device.UpdateDescriptorSet(
            set,
            [
                DescriptorWrite.Texture(0, view),
                DescriptorWrite.SamplerAt(1, sampler),
                DescriptorWrite.Storage(2, maskEntries, 0, MaskFloats * 4 * MaskCapacity * slots)
            ]
        );

    /// <summary>Forgets a texture.</summary>
    /// <param name="image">The number it was registered under.</param>
    /// <returns>Whether it was registered.</returns>
    /// <remarks>
    ///     ⚠ <b>Call it before destroying the texture, not after.</b> A draw list built before this
    ///     may still name the number; an unregistered number is skipped, which is a hole in the
    ///     interface, and a registered one whose texture is gone is undefined behaviour.
    /// </remarks>
    public bool UnregisterImage(ulong image) {
        if (!imageDescriptors.Remove(image, out var entry)) {
            return false;
        }

        foreach (var set in entry.Sets) {
            device.Destroy(set);
        }

        return true;
    }

    /// <inheritdoc />
    public void Dispose() {
        // ⚠ Before the sets are swept, because this unregisters each surface's number — and after it
        // the sweep below has nothing of theirs left to destroy twice.
        DestroySurfaces();

        foreach (var set in imageDescriptors.Values.SelectMany(entry => entry.Sets)) {
            device.Destroy(set);
        }

        imageDescriptors.Clear();

        if (imagePipeline.IsValid) {
            device.Destroy(imagePipeline);
        }

        if (blurPipeline.IsValid) {
            device.Destroy(blurPipeline);
        }

        // ⚠ Both of these were leaking until the mask landed: `colourPipeline` was created beside
        // `blurPipeline` and never destroyed beside it. Optional stages are the easy ones to forget
        // here, because the guard that creates them has no counterpart down here unless someone
        // writes one.
        if (colourPipeline.IsValid) {
            device.Destroy(colourPipeline);
        }

        if (maskPipeline.IsValid) {
            device.Destroy(maskPipeline);
        }

        device.Destroy(boxPipeline);
        device.Destroy(textPipeline);
        device.Destroy(solidPipeline);
        device.Destroy(layout);
        device.Destroy(atlasLayout);
        device.Destroy(sampler);

        if (vertices.IsValid) {
            device.Destroy(vertices);
        }

        if (indices.IsValid) {
            device.Destroy(indices);
        }

        if (boxes.IsValid) {
            device.Destroy(boxes);
        }

        // ⚠ Destroyed beside the buffers it was allocated with, which is the discipline
        // `colourPipeline` was found missing yesterday: created beside `blurPipeline` and never
        // destroyed beside it. The guard stays even though the constructor always allocates, because
        // a throw part-way through construction is the one path that reaches here without it.
        if (maskEntries.IsValid) {
            device.Destroy(maskEntries);
        }

        DestroyAtlas();
    }

    PipelineHandle PipelineFor(BatchKind kind) =>
        kind switch {
            BatchKind.Text => textPipeline,
            BatchKind.Image => imagePipeline,
            BatchKind.PathFill or BatchKind.PathStroke => solidPipeline,
            _ => boxPipeline
        };

    PipelineHandle Pipeline(ShaderHandle fragment, RenderOutput output, string name) =>
        device.CreateGraphicsPipeline(
            new(
                shaders.Vertex,
                fragment,
                layout,
                [new(output.ColourCount > 0 ? output.ColourFormats[0] : PixelFormat.Rgba8UNorm, BlendState.PremultipliedAlpha)],
                [
                    new(
                        // Four attributes in the order `UiVertex` declares them: position, texture,
                        // colour, shape.
                        48,
                        [
                            new(shaders.Locations[0], VertexFormat.Float32X2, 0),
                            new(shaders.Locations[1], VertexFormat.Float32X2, 8),
                            new(shaders.Locations[2], VertexFormat.Float32X4, 16),
                            new(shaders.Locations[3], VertexFormat.Float32X4, 32)
                        ]
                    )
                ],
                // ⚠ Two-sided. A tessellated path's winding follows the path the caller drew, and a
                // fill emitted from a sweep has no consistent one at all — so culling would drop
                // roughly half the triangles of any shape somebody happened to draw anticlockwise.
                Rasterizer: RasterizerState.TwoSided,
                DepthStencil: DepthStencilState.Disabled,
                Name: name
            )
        );

    void UploadGeometry(in UiGeometry geometry) {
        if (geometry.Indices.Count == 0) {
            return;
        }

        // ⚠ Advanced here rather than in Record, because this is the call that writes: the region
        // moved on to is the one used `slots` frames ago, which the device has finished with. It is
        // the same invariant `UploadBuffer.Begin` and `DescriptorAllocator.BeginFrame` keep.
        slot = (slot + 1) % slots;

        // The return is ignored for these two: they are bound by handle in `Record`, every frame, so
        // a replacement is picked up without anything having to be rewritten. Only the storage
        // buffer is reached through a descriptor set.
        Grow(ref vertices, ref vertexCapacity, geometry.Vertices.Count * 48, BufferUsage.Vertex, "ui vertices");
        Grow(ref indices, ref indexCapacity, geometry.Indices.Count * 4, BufferUsage.Index, "ui indices");

        // ⚠ At least one record, even in a frame with no boxes in it. A storage buffer with no
        // allocation behind it is a descriptor pointing at nothing, and a frame of pure text would
        // bind it and never read it — which is undefined rather than harmless, and would be
        // undefined only on the frames where nothing looked wrong.
        var boxBytes = Math.Max(geometry.Shapes.Count, 1) * ShapeBytes;

        if (Grow(ref boxes, ref boxCapacity, boxBytes, BufferUsage.Storage, "ui boxes")) {
            // ⚠ Growing the buffer replaced it, so every frame's descriptor set points at a buffer
            // that no longer exists. Noting it is the whole of the fix and forgetting it is a frame
            // that draws every box with whatever memory the allocator handed out next.
            Array.Fill(staleBoxes, true);
        }

        // This frame's set, and only this frame's — see `staleBoxes` for why the other frames' sets
        // are left stale. Before anything binds it and after the region it points into is settled, so
        // a frame never binds a set pointing at a destroyed buffer.
        RebindBoxes(slot);

        // The same, for every number a host has re-registered since this frame last came round. A
        // resized viewport marks all of them and this is where the marking is paid off.
        foreach (var entry in imageDescriptors.Values) {
            RebindImage(entry, slot);
        }

        if (geometry.Shapes.Count > 0) {
            var shapeBytes = new UiShape[geometry.Shapes.Count];
            geometry.Shapes.CopyToArray(shapeBytes);
            device.Write(boxes, (long) slot * boxCapacity, MemoryMarshal.AsBytes<UiShape>(shapeBytes));
        }

        // ⚠ Here rather than in `Compose`, beside the boxes and for their reason: this is the call
        // that has just advanced `slot`, so this frame's region is the one no submitted frame is
        // still reading. `Compose` runs after it and only records which range each group owns.
        var maskCount = Math.Min(geometry.Masks.Count, MaskCapacity);

        if (maskCount > 0) {
            var block = new float[maskCount * MaskFloats];

            for (var index = 0; index < maskCount; index++) {
                MaskBlock(geometry.Masks[index], block.AsSpan(index * MaskFloats, MaskFloats));
            }

            device.Write(
                maskEntries,
                (long) slot * MaskCapacity * MaskFloats * 4,
                MemoryMarshal.AsBytes<float>(block)
            );
        }

        // Copied through arrays because the geometry is exposed as `IReadOnlyList`, which is what
        // lets the builder hand out its own buffers without a defensive copy per frame. A `List<T>`
        // the renderer could take a span over is the improvement, and it is owed.
        var vertexBytes = new UiVertex[geometry.Vertices.Count];
        geometry.Vertices.CopyToArray(vertexBytes);

        var indexBytes = new uint[geometry.Indices.Count];
        geometry.Indices.CopyToArray(indexBytes);

        device.Write(vertices, (long) slot * vertexCapacity, MemoryMarshal.AsBytes<UiVertex>(vertexBytes));
        device.Write(indices, (long) slot * indexCapacity, MemoryMarshal.AsBytes<uint>(indexBytes));
    }

    /// <summary>Writes one mask into the sixteen floats <c>ui-mask.frag</c>'s <c>MaskEntry</c> is.</summary>
    /// <param name="mask">The mask.</param>
    /// <param name="into">Exactly <see cref="MaskFloats" /> floats.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Written out rather than blitted, because <see cref="UiMask" />'s field order is
    ///         C#'s business and std430's is the wire's.</b> The record has a <c>bool</c> and two
    ///         enums in it; a <c>MemoryMarshal.Cast</c> over it would compile, produce a block of the
    ///         right length on this machine, and be a mask drawn from the wrong bytes the first time
    ///         a field was reordered or the layout rules differed.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The shape goes out as <see cref="GradientShape" />'s own number and the operator
    ///         as <see cref="MaskComposite" />'s.</b> Neither is renumbered here and the shader
    ///         branches on the same values. Renumbering one of them to something "cleaner" on the way
    ///         out is the afternoon <c>ui-mask.frag</c>'s <c>ramp</c> comment records: a linear mask
    ///         arriving as a radial one draws a plausible round fade that nothing but
    ///         <c>UiCompositingTests</c> can see is wrong.
    ///     </para>
    /// </remarks>
    static void MaskBlock(in UiMask mask, Span<float> into) {
        into[0] = mask.Centre.X;
        into[1] = mask.Centre.Y;
        into[2] = mask.Half.X;
        into[3] = mask.Half.Y;

        into[4] = mask.Axis.X;
        into[5] = mask.Axis.Y;
        into[6] = (float) mask.Shape;
        into[7] = mask.Via ? 1f : 0f;

        into[8] = mask.Alphas.X;
        into[9] = mask.Alphas.Y;
        into[10] = mask.Alphas.Z;
        into[11] = mask.Stops.From;

        into[12] = mask.Stops.Via;
        into[13] = mask.Stops.To;
        into[14] = (float) mask.Composite;
        into[15] = 0f;
    }

    /// <summary>Points one frame's descriptor set at that frame's region of the box buffer.</summary>
    /// <param name="index">The frame, which must be the one about to draw.</param>
    /// <remarks>
    ///     ⚠ <b>The caller has to be the frame that owns the set.</b> This is a device-side write to a
    ///     set nothing may be reading, and the only thing that establishes that is <see cref="slot" />
    ///     having come round to a region the device has finished with. Calling it for a frame other
    ///     than the current one is the error this whole arrangement exists to avoid.
    /// </remarks>
    void RebindBoxes(int index) {
        if (!staleBoxes[index] || !boxes.IsValid || index >= atlasDescriptors.Length) {
            return;
        }

        if (!atlasDescriptors[index].IsValid) {
            // No atlas yet, so no set to write. Left stale, which is what makes `CreateAtlas` write
            // the binding when it makes the set — a frame that binds a set with a declared binding
            // never written is a validation error, and leaving the flag up is what prevents it.
            return;
        }

        device.UpdateDescriptorSet(
            atlasDescriptors[index],
            [DescriptorWrite.Storage(2, boxes, (long) index * boxCapacity, boxCapacity)]
        );

        staleBoxes[index] = false;
    }

    void UploadAtlas(ICommandList commands, GlyphAtlas atlas) {
        if (atlas.Width != atlasWidth || atlas.Height != atlasHeight) {
            DestroyAtlas();
            CreateAtlas(atlas);
        }

        // ⚠ <b>The revision and not the version</b>, and the difference is the whole of whether text
        // that was not on screen when the window opened ever appears. A version moves when the
        // packing changes, which only compaction does; a glyph merely added leaves every existing
        // region where it was and so does not move it. Gating on the version therefore uploads the
        // texture on the first frame and never again — every glyph met after that, which is every
        // menu the user opens and every name in a tree they expand, samples whatever was at its
        // coordinate before it was allocated: an empty texture reads as characters missing out of
        // the middle of words, and a reused hole reads as another glyph in their place.
        //
        // A number rather than the atlas's own dirty flag, because the flag belongs to whoever
        // clears it and there may be more than one renderer over one atlas — two windows sharing a
        // font cache is the ordinary case. A revision each reader remembers for itself cannot be
        // cleared out from under another one.
        if (atlas.Revision == atlasRevision) {
            return;
        }

        atlasRevision = atlas.Revision;

        // Eight bits a channel, which is what every MSDF implementation ships: the field is a
        // distance in [0, 1] over a range of a few pixels, so a 256th of that range is far finer
        // than the edge it describes.
        for (var i = 0; i < atlasBytes.Length; i++) {
            var pixel = i / 4;
            var channel = i % 4;

            atlasBytes[i] = channel == 3
                ? byte.MaxValue
                : (byte)Math.Clamp((int)((atlas.Pixels[(pixel * 3) + channel] * 255f) + 0.5f), 0, 255);
        }

        device.Write(atlasStaging, 0, atlasBytes);

        // ⚠ The state a barrier claims the texture was in has to be the state it was in. A fresh
        // texture is `Undefined` and every later frame's is `ShaderResource`, so the first transition
        // is not the same as the rest — writing `ShaderResource` here unconditionally is a validation
        // error on exactly one frame, which is the kind that reaches a release build.
        commands.Barrier(new([], [new(atlasTexture, atlasState, ResourceState.CopyDestination)]));

        commands.CopyBufferToTexture(atlasStaging, 0, new(atlasTexture), new(atlas.Width, atlas.Height, 1));

        commands.Barrier(
            new([], [new(atlasTexture, ResourceState.CopyDestination, ResourceState.ShaderRead)])
        );

        atlasState = ResourceState.ShaderRead;
        AtlasUploads++;
    }

    void CreateAtlas(GlyphAtlas atlas) {
        atlasWidth = atlas.Width;
        atlasHeight = atlas.Height;
        atlasBytes = new byte[atlas.Width * atlas.Height * 4];

        atlasTexture = device.CreateTexture(
            new(
                PixelFormat.Rgba8UNorm,
                atlas.Width,
                atlas.Height,
                TextureUsage.Sampled | TextureUsage.CopyDestination,
                Name: "ui glyph atlas"
            )
        );

        atlasView = device.CreateTextureView(atlasTexture);

        atlasStaging = device.CreateBuffer(
            new(atlasBytes.Length, BufferUsage.CopySource, MemoryAccess.HostUpload, "ui atlas staging")
        );

        // ⚠ One set per frame in flight, because the storage binding carries an offset and the
        // offset is what makes the ring work. A single set rewritten each frame would be a
        // descriptor update on a set an unfinished frame is still using, which is undefined and
        // which validation reports somewhere else entirely.
        atlasDescriptors = new DescriptorSetHandle[slots];

        for (var index = 0; index < slots; index++) {
            atlasDescriptors[index] = device.CreateDescriptorSet(atlasLayout, "ui atlas " + index.ToString(CultureInfo.InvariantCulture));

            // ⚠ Every set written here and not only the current frame's, which is the one place that
            // is safe: these sets were allocated a line ago, so no submitted frame can be reading one.
            // Once they are in the ring the rule is the opposite — see `staleBoxes`.
            device.UpdateDescriptorSet(
                atlasDescriptors[index],
                boxes.IsValid
                    ? [
                        DescriptorWrite.Texture(0, atlasView),
                        DescriptorWrite.SamplerAt(1, sampler),
                        DescriptorWrite.Storage(2, boxes, (long) index * boxCapacity, boxCapacity)
                    ]
                    : [DescriptorWrite.Texture(0, atlasView), DescriptorWrite.SamplerAt(1, sampler)]
            );

            // Left stale when there is no box buffer to point at yet, so the first frame that makes
            // one writes the binding before it binds the set.
            staleBoxes[index] = !boxes.IsValid;
        }

        // A version no atlas has, so the first frame always uploads — including a frame that draws no
        // text at all, which still has to leave the texture in a state the shader can read.
        atlasRevision = -1;
        atlasState = ResourceState.Undefined;
    }

    void DestroyAtlas() {
        if (!atlasTexture.IsValid) {
            return;
        }

        foreach (var set in atlasDescriptors) {
            if (set.IsValid) {
                device.Destroy(set);
            }
        }

        atlasDescriptors = [];

        device.Destroy(atlasView);
        device.Destroy(atlasTexture);
        device.Destroy(atlasStaging);
        atlasTexture = default;
        atlasView = default;
        atlasStaging = default;
        atlasWidth = 0;
        atlasHeight = 0;
        atlasState = ResourceState.Undefined;
    }

    /// <summary>Makes sure a buffer has room for <paramref name="bytes" /> in every region.</summary>
    /// <returns>Whether the buffer was replaced, which is what invalidates a descriptor pointing at it.</returns>
    /// <remarks>
    ///     ⚠ <b><paramref name="capacity" /> is one region, and the allocation is that many times
    ///     <see cref="slots" />.</b> Rounded up to <see cref="Alignment" /> so that region <c>n</c>
    ///     starts at an offset a storage-buffer descriptor may legally be bound at — a
    ///     <c>minStorageBufferOffsetAlignment</c> of 256 is common, and an unaligned binding is a
    ///     validation error rather than a slow path.
    /// </remarks>
    bool Grow(ref BufferHandle buffer, ref int capacity, int bytes, BufferUsage usage, string name) {
        if (buffer.IsValid && capacity >= bytes) {
            return false;
        }

        if (buffer.IsValid) {
            device.Destroy(buffer);
        }

        // Doubling, and never shrinking. A frame that grew is usually followed by another that grew,
        // and a renderer that reallocated on every size change would reallocate on every keystroke
        // in a text box.
        capacity = Math.Max(bytes, capacity * 2);
        capacity = (capacity + Alignment - 1) / Alignment * Alignment;

        buffer = device.CreateBuffer(new((long) capacity * slots, usage, MemoryAccess.HostUpload, name));
        return true;
    }

    /// <summary>What a region's offset is rounded up to.</summary>
    const int Alignment = 256;

    /// <summary>The part of a group's surface its passes are confined to, in framebuffer pixels.</summary>
    /// <param name="bounds">The group's <see cref="UiLayer.Bounds" />, in geometry units.</param>
    /// <param name="surface">The geometry's extent, in its own units.</param>
    /// <param name="scale">How many framebuffer pixels one of those units is.</param>
    /// <param name="reach">The blur's kernel radius in texels, or zero for an unblurred group.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A render area and not a scissor, and the difference is the whole point.</b> A
    ///         scissor confines <i>draws</i>; the clear happens when the pass begins, before any draw,
    ///         and obeys the render area alone. Confining the surfaces with a scissor would leave
    ///         twelve viewport-sized clears exactly where they were and buy nothing — which is not a
    ///         hypothetical, it is what the virtual shadow atlas did before it lost every page but the
    ///         last drawn. See <see cref="RenderPassDescription.RenderArea" />.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The <i>allocation</i> is untouched and stays viewport-sized.</b> This confines
    ///         what a frame clears and stores, not what it reserves — so <see cref="UiLayer" />'s
    ///         argument survives intact: a group's contents are still drawn at exactly the
    ///         coordinates they would have had, there is still no origin to translate, and the two
    ///         executors still composite through the same rectangle. What changes is only how much of
    ///         a target the passes touch.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Everything outside this rectangle is left holding the <i>previous</i> frame's
    ///         pixels, not transparent black — so it has to be a superset of everything anything
    ///         samples.</b> Three readers, and the widest of them decides:
    ///         <list type="bullet">
    ///             <item>
    ///                 the composite quad, which covers <paramref name="bounds" /> exactly — plus one
    ///                 texel, because the shared sampler is linear and a fractional
    ///                 <paramref name="scale" /> puts a fragment centre between texels;
    ///             </item>
    ///             <item>
    ///                 the first blur axis, which reads the group's own surface
    ///                 <paramref name="reach" /> texels each side along x;
    ///             </item>
    ///             <item>
    ///                 the second, which reads the scratch <paramref name="reach" /> texels each side
    ///                 along y.
    ///             </item>
    ///         </list>
    ///         One rectangle outset by <c>reach + 1</c> covers all three, which is why the group's
    ///         three passes are handed the same one rather than a rectangle each. Getting it too
    ///         small does not draw black: it draws whatever that group's surface held last frame,
    ///         which on a still frame is the right answer and on a moving one is a ghost.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Which half of that outset is policed, checked by breaking each.</b> Unlike the
    ///         bounds outset — which <c>UiCompositingTests</c> provably cannot see, because both
    ///         executors composite through the same <see cref="UiLayer.Bounds" /> — a render area is
    ///         a device-side concept the software path has no counterpart for, so it <i>is</i> visible
    ///         there: dropping <paramref name="reach" /> from this margin makes the two frames differ
    ///         on 8.1% of pixels by up to 48 levels. The <c>+ 1</c> is <i>not</i> covered, and cannot
    ///         be by that fixture: it draws at a scale of one, where the composite quad's fragment
    ///         centres land on texel centres and the linear sampler never reaches a neighbour. It is
    ///         kept for the fractional scale a DPI-scaled window draws at, where they do.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><paramref name="reach" /> is recomputed at <paramref name="scale" /> rather than
    ///         read off the bounds.</b> <c>UiGeometryBuilder.Layer</c> outsets the bounds by the
    ///         kernel at a scale of one, in document pixels; the executor's kernel is
    ///         <see cref="UiLayer.KernelRadius" /> at the scale it is drawing at. Those are the same
    ///         number only at 1:1, and the halo is the thing that goes missing when they are not.
    ///     </para>
    /// </remarks>
    static ScissorRect Confine(Rectangle bounds, Int2 surface, float scale, int reach) {
        var width = (int)MathF.Ceiling(surface.X * scale);
        var height = (int)MathF.Ceiling(surface.Y * scale);

        var area = Scissor(bounds, surface, scale);
        var margin = reach + 1;

        var left = Math.Clamp(area.X - margin, 0, width);
        var top = Math.Clamp(area.Y - margin, 0, height);
        var right = Math.Clamp(area.X + area.Width + margin, 0, width);
        var bottom = Math.Clamp(area.Y + area.Height + margin, 0, height);

        // ⚠ The whole attachment rather than nothing. An empty render area is a pass that clears
        // nothing and draws nothing, and the composite would then sample a surface no frame has ever
        // written — so a degenerate rectangle falls back to the behaviour this method replaced
        // instead of to a hole. `UiGeometryBuilder.Layer` drops an empty group before it becomes a
        // layer, so this is a guard and not a case.
        return right > left && bottom > top
            ? new(left, top, right - left, bottom - top)
            : new(0, 0, width, height);
    }

    /// <summary>A clip rectangle as a scissor, clamped to the surface.</summary>
    /// <remarks>
    ///     ⚠ Clamped rather than trusted. A clip is in document pixels and may legitimately extend
    ///     past the surface — a panel scrolled half off the edge — and a scissor that does is a
    ///     validation error rather than a clamp on most drivers.
    /// </remarks>
    /// <summary>Turns a clip in geometry units into a scissor in framebuffer pixels.</summary>
    /// <param name="clip">The clip, in the same units as the geometry.</param>
    /// <param name="surface">The geometry's extent, in its own units.</param>
    /// <param name="scale">How many framebuffer pixels one of those units is.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This is the one place the two spaces are not the same, which is why the scale is
    ///         a parameter and not an assumption.</b> The projection above is a pure mapping from
    ///         geometry units to clip space and does not care how big a pixel is; a scissor is
    ///         submitted to Vulkan in <i>framebuffer</i> pixels and cares about nothing else. They
    ///         are equal at 1:1 and only at 1:1 — so an interface laid out in device-independent
    ///         units on a display that has two pixels per unit draws at the right size and clips to
    ///         a quarter of the right rectangle, which looks like scroll views eating their content
    ///         rather than like a DPI mistake.
    ///     </para>
    ///     <para>
    ///         Outward, deliberately: floor the near edges and ceil the far ones <i>after</i>
    ///         scaling. A scissor rounded inward on a fractional scale loses a pixel of the very
    ///         edge it was asked to keep, and a UI is full of one-pixel borders sitting exactly on
    ///         a clip boundary.
    ///     </para>
    /// </remarks>
    static ScissorRect Scissor(Rectangle clip, Int2 surface, float scale) {
        var width = (int)MathF.Ceiling(surface.X * scale);
        var height = (int)MathF.Ceiling(surface.Y * scale);

        var left = Math.Clamp((int)MathF.Floor(clip.X * scale), 0, width);
        var top = Math.Clamp((int)MathF.Floor(clip.Y * scale), 0, height);
        var right = Math.Clamp((int)MathF.Ceiling((clip.X + clip.Width) * scale), 0, width);
        var bottom = Math.Clamp((int)MathF.Ceiling((clip.Y + clip.Height) * scale), 0, height);

        return new(left, top, right - left, bottom - top);
    }
}

/// <summary>Copying an <see cref="IReadOnlyList{T}" /> without allocating an enumerator per frame.</summary>
static class ReadOnlyListExtensions {
    public static void CopyToArray<T>(this IReadOnlyList<T> source, T[] destination) {
        // The common case by far: the geometry builder hands out its own `List<T>`, which copies as a
        // block. The loop is the fallback for anything else, and is why this is not just a cast.
        if (source is List<T> list) {
            list.CopyTo(destination);
            return;
        }

        for (var i = 0; i < source.Count; i++) {
            destination[i] = source[i];
        }
    }
}
