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
///     modules belongs to <c>Vixen.Shaders</c> and, once it lands, to Raven — which already carries
///     <c>Ui/Msdf.rvn</c> and <c>Ui/RoundedRect.rvn</c> for exactly this. Until then a caller hands
///     over whatever it has, which is how the golden fixture drives this with hand-written GLSL and
///     how a game will drive it from an effect. What this must not do is grow a compiler.
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

    /// <summary>What an image set's storage binding points at, and it is never the box buffer.</summary>
    /// <remarks>
    ///     ⚠ <b>A buffer of its own precisely so that it never has to be rewritten.</b> The image
    ///     shader never reads the binding; the layout only declares it, so what it points at has to
    ///     exist and nothing more. Pointing it at the box buffer would mean rewriting every image set
    ///     each time that buffer grew, for a binding nothing reads — one allocation that outlives
    ///     every frame buys the question away.
    /// </remarks>
    BufferHandle imageBoxes;

    BufferHandle atlasStaging;
    int atlasWidth;
    int atlasHeight;
    int atlasVersion = -1;
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
        layout = device.CreatePipelineLayout(
            new([atlasLayout], [new(ShaderStage.Vertex, 0, 16)], "ui")
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
    }

    /// <summary>How many draws the last <see cref="Record" /> submitted.</summary>
    /// <remarks>
    ///     Exposed for the same reason <c>DrawList.Batched</c> is: a claim about how little a frame
    ///     costs that cannot be measured is one nobody can check.
    /// </remarks>
    public int Draws { get; private set; }

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

        // Document pixels to clip space, and ⚠ <b>y is flipped</b> — which is the opposite of what
        // the reasoning "the interface's y runs down and so does Vulkan's" arrives at, and that
        // reasoning was written into this file before the picture was looked at. Vulkan's raw clip
        // space does have +y down, but nothing here ever sees it: `VulkanCommandList.SetViewport`
        // submits a negative-height viewport so that the engine's convention of +y up holds
        // everywhere (Core/Vixen.Core.Mathematics/Conventions.md). A frame that agreed with the API
        // instead of with the engine draws upside down, and every unit test in `Vixen.Ui` passes
        // while it does.
        Span<float> projection = [2f / surface.X, -2f / surface.Y, -1f, 1f];

        // ⚠ At this frame's region. The indices stay zero-based because the vertex binding moves
        // with them — an index is relative to the bound vertex offset, which is exactly why the ring
        // costs nothing downstream.
        commands.BindVertexBuffer(0, vertices, (long) slot * vertexCapacity);
        commands.BindIndexBuffer(indices, IndexFormat.UInt32, (long) slot * indexCapacity);

        var bound = default(PipelineHandle);
        var boundSet = default(DescriptorSetHandle);
        var pushed = false;

        foreach (var draw in geometry.Draws) {
            var pipeline = PipelineFor(draw.Kind);

            if (!pipeline.IsValid) {
                // An image with no image shader. Skipped rather than drawn with another pipeline,
                // which would put the font atlas in the hole and read as a rendering fault.
                continue;
            }

            if (pipeline != bound) {
                commands.BindPipeline(pipeline);
                bound = pipeline;
            }

            if (!pushed) {
                // ⚠ After the first pipeline and then never again. It is written through the bound
                // pipeline's layout, so there is nothing to write it through until one is bound — and
                // because every pipeline shares one layout, a later pipeline change cannot disturb it.
                commands.PushConstants(ShaderStage.Vertex, 0, MemoryMarshal.AsBytes(projection));
                pushed = true;
            }

            // ⚠ Per draw rather than once, now that a draw can carry its own texture. The set is the
            // atlas for everything except an image, so a frame with no images binds exactly once —
            // the comparison is what keeps the old behaviour rather than a flag that used to.
            var registered = draw.Kind == BatchKind.Image
                ? imageDescriptors.TryGetValue(draw.Image, out var found) ? found.Sets[slot] : default
                : atlasDescriptors[slot];

            if (!registered.IsValid) {
                // An image nobody registered. Drawing it would sample the atlas through the image
                // shader, which is a rectangle of scrambled glyphs where the picture should be.
                continue;
            }

            var set = registered;

            if (set != boundSet) {
                commands.BindDescriptorSet(DescriptorSetSlot.PerFrame, set);
                boundSet = set;
            }

            var scissor = Scissor(draw.Clip, surface, scale);

            if (scissor.Width <= 0 || scissor.Height <= 0) {
                // ⚠ Wholly clipped away — a panel scrolled off the edge, a tooltip for a window that
                // moved. Skipped, and the honest claim for the skip is that it saves a draw call and
                // not that it prevents anything: a zero-extent scissor is legal and draws nothing, so
                // submitting it would produce the same picture. `Draws` is what makes the saving
                // visible, because nothing else can be.
                continue;
            }

            commands.SetScissor(scissor);
            commands.DrawIndexed(draw.Count, firstIndex: draw.First);
            Draws++;
        }
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

        if (!imageBoxes.IsValid) {
            // One shape's worth, because the size is what a storage binding needs and nothing reads
            // past it. See the field for why it is not the box buffer.
            imageBoxes = device.CreateBuffer(
                new(80, BufferUsage.Storage, MemoryAccess.HostUpload, "ui image boxes")
            );
        }

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
    ///     ⚠ <b>Binding 2 is written even though the image shader never reads it.</b> The layout is
    ///     shared with the box pipeline — see the pipeline layout's own remarks for why all of them
    ///     are one — and a set that leaves a declared binding unwritten is a validation error on the
    ///     frame it is bound, not on the frame it was made.
    /// </remarks>
    void Write(DescriptorSetHandle set, TextureViewHandle view) =>
        device.UpdateDescriptorSet(
            set,
            [
                DescriptorWrite.Texture(0, view),
                DescriptorWrite.SamplerAt(1, sampler),
                DescriptorWrite.Storage(2, imageBoxes, 0, 80)
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
        foreach (var set in imageDescriptors.Values.SelectMany(entry => entry.Sets)) {
            device.Destroy(set);
        }

        imageDescriptors.Clear();

        if (imagePipeline.IsValid) {
            device.Destroy(imagePipeline);
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

        if (imageBoxes.IsValid) {
            device.Destroy(imageBoxes);
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
                            new(0, VertexFormat.Float32X2, 0),
                            new(1, VertexFormat.Float32X2, 8),
                            new(2, VertexFormat.Float32X4, 16),
                            new(3, VertexFormat.Float32X4, 32)
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
        var boxBytes = Math.Max(geometry.Shapes.Count, 1) * 80;

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

        // ⚠ Version rather than the atlas's own dirty flag, because the flag belongs to whoever
        // clears it and there may be more than one renderer over one atlas — two windows sharing a
        // font cache is the ordinary case. A version each reader remembers for itself cannot be
        // cleared out from under another one.
        if (atlas.Version == atlasVersion) {
            return;
        }

        atlasVersion = atlas.Version;

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
        atlasVersion = -1;
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
