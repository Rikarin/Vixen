// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Rendering;
using Vixen.Video.Gpu;

namespace Vixen.Video.Rendering;

/// <summary>The two shader modules a video is drawn with.</summary>
/// <param name="Vertex">Builds the quad from the vertex index and the push constants.</param>
/// <param name="Fragment">Samples three planes and multiplies by six numbers.</param>
/// <remarks>
///     ⚠ <b>Supplied rather than compiled here, and that is the seam.</b> The same call
///     <c>Vixen.Ui.Renderer</c>'s <c>UiShaders</c> makes for the same reason: turning shader source
///     into modules belongs to <c>Vixen.Shaders</c> and, once it lands, to Raven. Until then a caller
///     hands over whatever it has — <c>Samples/11-VideoPlayback</c> hands over hand-written GLSL —
///     and what this must not do is grow a compiler.
/// </remarks>
public readonly record struct VideoShaders(ShaderHandle Vertex, ShaderHandle Fragment);

/// <summary>One video, where it goes, and how much of it to show.</summary>
/// <param name="Texture">The planes.</param>
/// <param name="Target">Where to draw, in the surface's own units.</param>
/// <param name="TextureScale">What to multiply the texture coordinate by. One for the whole picture.</param>
/// <param name="TextureOffset">What to add afterwards. Zero for the whole picture.</param>
/// <param name="Tint">Multiplied into the colour. White is the picture untouched; the alpha fades it.</param>
/// <param name="Order">Where it sits among the surfaces, lowest drawn first.</param>
/// <remarks>
///     The scale and the offset are <see cref="VideoPlacement" />'s, which is where the aspect-ratio
///     arithmetic lives — in <c>Vixen.Video</c> rather than here, because a user interface needs the
///     same answer and the two must not disagree.
/// </remarks>
public readonly record struct VideoDraw(
    VideoTexture Texture,
    Rectangle Target,
    Vector2 TextureScale,
    Vector2 TextureOffset,
    Color4 Tint,
    uint Order = 0
) {
    /// <summary>A whole picture filling a rectangle.</summary>
    /// <param name="texture">The planes.</param>
    /// <param name="target">Where to draw.</param>
    /// <returns>The draw.</returns>
    public static VideoDraw Filling(VideoTexture texture, Rectangle target) =>
        new(texture, target, Vector2.One, Vector2.Zero, Color4.White);

    /// <summary>A picture placed by <see cref="VideoFit" />.</summary>
    /// <param name="texture">The planes.</param>
    /// <param name="placement">Where it landed and which part of it shows.</param>
    /// <returns>The draw.</returns>
    public static VideoDraw From(VideoTexture texture, in VideoPlacement placement) =>
        new(texture, placement.Target, placement.TextureScale, placement.TextureOffset, Color4.White);
}

/// <summary>Draws a video's planes. The device half.</summary>
/// <remarks>
///     <para>
///         <b>One pipeline, no vertex buffer, one descriptor set per texture.</b> A video is one quad
///         and the quad is built from <c>gl_VertexIndex</c>, so there is nothing to allocate per frame
///         and nothing to upload: everything that varies between two videos on one screen is in
///         sixty-four bytes of push constant and a descriptor set that was written once.
///     </para>
///     <para>
///         ⚠ <b>Kept separate from <see cref="VideoRenderFeature" /> for the reason
///         <c>UiRenderer</c> is kept separate from <c>UiRenderFeature</c>:</b> this is the part that
///         touches a device, so a golden image or a sample can drive it without a
///         <see cref="RenderSystem" />, a camera or a compositor — which is the only way to find out
///         whether the shader agrees with the six coefficients the module computed.
///     </para>
///     <para>
///         ⚠ <b>The picture is written premultiplied and the target must not be sRGB.</b> A decoded
///         video's RGB is already gamma-encoded — that is what the BT.709 transfer function is — so an
///         sRGB colour target encodes it a second time and shows as mid-tones that are far too
///         bright. A renderer lighting a video as a texture in a scene wants the opposite; a player
///         showing it wants the bytes to arrive as they are.
///     </para>
/// </remarks>
public sealed class VideoRenderer : IDisposable {
    /// <summary>How many bindings the layout has, whatever the picture's layout turns out to be.</summary>
    /// <remarks>
    ///     ⚠ Three plane bindings even for a picture with one plane, and every one of them written.
    ///     A descriptor a shader does not read is free; a descriptor pointing at nothing is undefined
    ///     behaviour that a driver is entitled to notice — so a grey or a BGRA picture binds its one
    ///     plane three times and the shader is told which case it is in.
    /// </remarks>
    const int PlaneBindings = 3;

    readonly Dictionary<VideoTexture, Bound> bound = [];
    readonly DescriptorSetLayoutHandle planeLayout;
    readonly IGraphicsDevice device;
    readonly PipelineHandle pipeline;
    readonly PipelineLayoutHandle layout;

    bool disposed;

    /// <summary>Builds the pipeline a video pass needs.</summary>
    /// <param name="device">Where it lives.</param>
    /// <param name="shaders">The modules to build it from.</param>
    /// <param name="output">The formats of the pass this will be drawn in.</param>
    /// <param name="name">A name for the debugger and captures.</param>
    /// <exception cref="ArgumentNullException"><paramref name="device" /> is null.</exception>
    public VideoRenderer(
        IGraphicsDevice device,
        VideoShaders shaders,
        RenderOutput output,
        string name = "video"
    ) {
        ArgumentNullException.ThrowIfNull(device);

        this.device = device;

        planeLayout = device.CreateDescriptorSetLayout(
            new DescriptorSetLayoutDescription(
                // Set 0, which by the convention in `DescriptorSetSlot` is the per-frame set, and it
                // is the same deliberate misuse `UiRenderer` makes: a video pass has no per-frame and
                // no per-view set — the placement is sixteen floats in a push constant — so the
                // planes are the only set there is, and a layout with two empty sets in front of them
                // would cost two bind points to honour a naming convention.
                DescriptorSetSlot.PerFrame,
                [
                    new DescriptorBinding(0, DescriptorKind.SampledTexture, ShaderStage.Fragment),
                    new DescriptorBinding(1, DescriptorKind.SampledTexture, ShaderStage.Fragment),
                    new DescriptorBinding(2, DescriptorKind.SampledTexture, ShaderStage.Fragment),
                    new DescriptorBinding(3, DescriptorKind.Sampler, ShaderStage.Fragment)
                ],
                $"{name} planes"
            )
        );

        layout = device.CreatePipelineLayout(
            new PipelineLayoutDescription(
                [planeLayout],
                // Visible to both stages because the placement is read by the vertex stage and the
                // crop and the coefficients by the fragment one, and a range each would be two ranges
                // over one struct that has to stay in step with itself.
                [new PushConstantRange(ShaderStage.Vertex | ShaderStage.Fragment, 0, VideoConstants.Size)],
                name
            )
        );

        pipeline = device.CreateGraphicsPipeline(
            new GraphicsPipelineDescription(
                shaders.Vertex,
                shaders.Fragment,
                layout,
                [
                    new ColourTargetState(
                        output.ColourCount > 0 ? output.ColourFormats[0] : PixelFormat.Bgra8UNorm,
                        BlendState.PremultipliedAlpha
                    )
                ],
                // No vertex buffers at all: six vertices, and the quad is arithmetic on the index.
                // A buffer would be ninety-six bytes to say what `gl_VertexIndex` already says.
                [],
                Rasterizer: RasterizerState.TwoSided,
                DepthStencil: DepthStencilState.Disabled,
                Name: name
            )
        );
    }

    /// <summary>How many draws the last run of <see cref="Record" /> submitted.</summary>
    public int Draws { get; private set; }

    /// <summary>How many descriptor sets have been written, which is once per texture and once per resize.</summary>
    /// <remarks>
    ///     Exposed for the reason <c>UiRenderer.AtlasUploads</c> is: the claim is that drawing the
    ///     same video every frame writes no descriptors, and a claim about work avoided that cannot
    ///     be measured is one nobody can check.
    /// </remarks>
    public int DescriptorWrites { get; private set; }

    /// <summary>Forgets a texture's descriptor set, for a video that has gone away.</summary>
    /// <param name="texture">The texture.</param>
    /// <returns>Whether there was one.</returns>
    /// <remarks>
    ///     Worth calling and not required. The set is destroyed with this renderer either way; what
    ///     this avoids is a long-running game that opens a hundred cutscenes keeping a hundred sets
    ///     alive for the one it is playing.
    /// </remarks>
    public bool Forget(VideoTexture texture) {
        ArgumentNullException.ThrowIfNull(texture);

        if (!bound.Remove(texture, out var stale)) {
            return false;
        }

        device.Destroy(stale.Descriptors);

        return true;
    }

    /// <summary>Starts a run of draws. Called inside a render pass, before the first <see cref="Record" />.</summary>
    /// <remarks>
    ///     Separate from <see cref="Record" /> so that several videos on one screen bind one pipeline
    ///     between them, and so that <see cref="Draws" /> counts a run rather than a frame.
    /// </remarks>
    public void Begin() => Draws = 0;

    /// <summary>Records one video. Called inside a render pass.</summary>
    /// <param name="commands">Where to record.</param>
    /// <param name="draw">What to draw and where.</param>
    /// <param name="surface">
    ///     The size of the target <b>in the draw's own units</b> — the same rectangle
    ///     <see cref="VideoDraw.Target" /> was measured against, not the framebuffer.
    /// </param>
    /// <returns>Whether anything was drawn.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="commands" /> is null.</exception>
    /// <remarks>
    ///     ⚠ <b>Two spaces, and they are only the same at 1:1</b> — the same trap
    ///     <c>UiRenderer.Record</c> documents. The projection maps the draw's units onto clip space
    ///     and needs the surface's extent in those units; handing it the framebuffer's size for an
    ///     interface laid out in device-independent ones draws the video into the top-left corner of
    ///     the window, which reads as a renderer that is mysteriously small rather than as a unit
    ///     mismatch.
    /// </remarks>
    public bool Record(ICommandList commands, in VideoDraw draw, Int2 surface) {
        ArgumentNullException.ThrowIfNull(commands);
        ObjectDisposedException.ThrowIf(disposed, this);

        if (draw.Texture is null
            || draw.Texture.PlaneCount == 0
            || draw.Target.Width <= 0
            || draw.Target.Height <= 0
            || surface.X <= 0
            || surface.Y <= 0) {
            return false;
        }

        var descriptors = Descriptors(draw.Texture);

        commands.BindPipeline(pipeline);
        commands.BindDescriptorSet(DescriptorSetSlot.PerFrame, descriptors);

        var constants = VideoConstants.For(in draw, surface);

        commands.PushConstants(
            ShaderStage.Vertex | ShaderStage.Fragment,
            0,
            MemoryMarshal.AsBytes(new ReadOnlySpan<VideoConstants>(in constants))
        );

        // Two triangles as six vertices rather than a strip, because the RHI's draw takes a count and
        // the topology is the pipeline's — and a full-screen triangle, which is what the sample used
        // to do, cannot express a rectangle that is not the whole screen.
        commands.Draw(6);
        Draws++;

        return true;
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        foreach (var entry in bound.Values) {
            device.Destroy(entry.Descriptors);
        }

        bound.Clear();

        device.Destroy(pipeline);
        device.Destroy(layout);
        device.Destroy(planeLayout);
    }

    /// <summary>Finds or writes the descriptor set for a texture's current planes.</summary>
    /// <remarks>
    ///     ⚠ <b>Keyed on the views rather than on the texture alone.</b> <c>VideoTexture</c> destroys
    ///     and recreates its planes when the picture changes shape — which a WebM is allowed to do
    ///     mid-stream — and a set still pointing at the old views is a set pointing at destroyed
    ///     resources. Comparing the handles is three integers and catches every case a version
    ///     counter would, without <c>VideoTexture</c> having to grow one.
    /// </remarks>
    DescriptorSetHandle Descriptors(VideoTexture texture) {
        Span<TextureViewHandle> views = stackalloc TextureViewHandle[PlaneBindings];

        for (var plane = 0; plane < PlaneBindings; plane++) {
            // A one-plane picture binds its one plane three times: the shader is told which case it
            // is in and reads only what it should, and nothing points at nothing.
            views[plane] = texture.PlaneView(plane < texture.PlaneCount ? plane : 0);
        }

        if (bound.TryGetValue(texture, out var existing) && existing.Matches(views)) {
            return existing.Descriptors;
        }

        var set = existing.Descriptors.IsValid
            ? existing.Descriptors
            : device.CreateDescriptorSet(planeLayout, "video planes");

        device.UpdateDescriptorSet(
            set,
            [
                DescriptorWrite.Texture(0, views[0]),
                DescriptorWrite.Texture(1, views[1]),
                DescriptorWrite.Texture(2, views[2]),
                DescriptorWrite.SamplerAt(3, texture.Sampler)
            ]
        );

        bound[texture] = new Bound(set, views[0], views[1], views[2]);
        DescriptorWrites++;

        return set;
    }

    readonly record struct Bound(
        DescriptorSetHandle Descriptors,
        TextureViewHandle Luma,
        TextureViewHandle Blue,
        TextureViewHandle Red
    ) {
        public bool Matches(ReadOnlySpan<TextureViewHandle> views) =>
            Luma == views[0] && Blue == views[1] && Red == views[2];
    }
}
