// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

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
);

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
    int vertexCapacity;
    int indexCapacity;

    TextureHandle atlasTexture;
    TextureViewHandle atlasView;
    DescriptorSetHandle atlasDescriptors;
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
                    new(1, DescriptorKind.Sampler, ShaderStage.Fragment)
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
    }

    /// <summary>How many draws the last <see cref="Record" /> submitted.</summary>
    /// <remarks>
    ///     Exposed for the same reason <c>DrawList.Batched</c> is: a claim about how little a frame
    ///     costs that cannot be measured is one nobody can check.
    /// </remarks>
    public int Draws { get; private set; }

    /// <summary>How many times the atlas has been copied to the GPU.</summary>
    /// <remarks>
    ///     ⚠ The claim this exists to make checkable is that a frame drawing text it has drawn before
    ///     uploads nothing. An atlas re-uploaded every frame is a megabyte of transfer per frame to
    ///     move bytes that did not change, and it is invisible in the picture.
    /// </remarks>
    public int AtlasUploads { get; private set; }

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
    /// <param name="surface">The size of the target, in document pixels.</param>
    public void Record(ICommandList commands, in UiGeometry geometry, Int2 surface) {
        ArgumentNullException.ThrowIfNull(commands);

        Draws = 0;

        if (geometry.Indices.Count == 0 || surface.X <= 0 || surface.Y <= 0) {
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

        commands.BindVertexBuffer(0, vertices);
        commands.BindIndexBuffer(indices, IndexFormat.UInt32);

        var bound = default(PipelineHandle);
        var shared = false;

        foreach (var draw in geometry.Draws) {
            var pipeline = PipelineFor(draw.Kind);

            if (pipeline != bound) {
                commands.BindPipeline(pipeline);
                bound = pipeline;
            }

            if (!shared) {
                // ⚠ After the first pipeline and then never again. Both of these are written through
                // the bound pipeline's layout, so there is nothing to write them through until one is
                // bound — and because all three pipelines share one layout, a later pipeline change
                // cannot disturb either of them.
                commands.PushConstants(ShaderStage.Vertex, 0, MemoryMarshal.AsBytes(projection));
                commands.BindDescriptorSet(DescriptorSetSlot.PerFrame, atlasDescriptors);
                shared = true;
            }

            var scissor = Scissor(draw.Clip, surface);

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

    /// <inheritdoc />
    public void Dispose() {
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

        DestroyAtlas();
    }

    PipelineHandle PipelineFor(BatchKind kind) =>
        kind switch {
            BatchKind.Text => textPipeline,
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

        Grow(ref vertices, ref vertexCapacity, geometry.Vertices.Count * 48, BufferUsage.Vertex, "ui vertices");
        Grow(ref indices, ref indexCapacity, geometry.Indices.Count * 4, BufferUsage.Index, "ui indices");

        // Copied through arrays because the geometry is exposed as `IReadOnlyList`, which is what
        // lets the builder hand out its own buffers without a defensive copy per frame. A `List<T>`
        // the renderer could take a span over is the improvement, and it is owed.
        var vertexBytes = new UiVertex[geometry.Vertices.Count];
        geometry.Vertices.CopyToArray(vertexBytes);

        var indexBytes = new uint[geometry.Indices.Count];
        geometry.Indices.CopyToArray(indexBytes);

        device.Write(vertices, 0, MemoryMarshal.AsBytes<UiVertex>(vertexBytes));
        device.Write(indices, 0, MemoryMarshal.AsBytes<uint>(indexBytes));
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

        atlasDescriptors = device.CreateDescriptorSet(atlasLayout, "ui atlas");

        device.UpdateDescriptorSet(
            atlasDescriptors,
            [DescriptorWrite.Texture(0, atlasView), DescriptorWrite.SamplerAt(1, sampler)]
        );

        // A version no atlas has, so the first frame always uploads — including a frame that draws no
        // text at all, which still has to leave the texture in a state the shader can read.
        atlasVersion = -1;
        atlasState = ResourceState.Undefined;
    }

    void DestroyAtlas() {
        if (!atlasTexture.IsValid) {
            return;
        }

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

    void Grow(ref BufferHandle buffer, ref int capacity, int bytes, BufferUsage usage, string name) {
        if (buffer.IsValid && capacity >= bytes) {
            return;
        }

        if (buffer.IsValid) {
            device.Destroy(buffer);
        }

        // Doubling, and never shrinking. A frame that grew is usually followed by another that grew,
        // and a renderer that reallocated on every size change would reallocate on every keystroke
        // in a text box.
        capacity = Math.Max(bytes, capacity * 2);
        buffer = device.CreateBuffer(new(capacity, usage, MemoryAccess.HostUpload, name));
    }

    /// <summary>A clip rectangle as a scissor, clamped to the surface.</summary>
    /// <remarks>
    ///     ⚠ Clamped rather than trusted. A clip is in document pixels and may legitimately extend
    ///     past the surface — a panel scrolled half off the edge — and a scissor that does is a
    ///     validation error rather than a clamp on most drivers.
    /// </remarks>
    static ScissorRect Scissor(Rectangle clip, Int2 surface) {
        var left = Math.Clamp((int)MathF.Floor(clip.X), 0, surface.X);
        var top = Math.Clamp((int)MathF.Floor(clip.Y), 0, surface.Y);
        var right = Math.Clamp((int)MathF.Ceiling(clip.X + clip.Width), 0, surface.X);
        var bottom = Math.Clamp((int)MathF.Ceiling(clip.Y + clip.Height), 0, surface.Y);

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
