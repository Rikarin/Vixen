// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Graphics.OpenGL;

/// <summary>What GL is currently set to, so that setting it again costs nothing.</summary>
/// <remarks>
///     <para>
///         <b>Half of what makes a GL backend viable.</b> The RHI's model is that a pipeline carries
///         its whole state and binding one applies all of it; GL's model is two dozen independent
///         setters, each a driver call, several of which flush pipeline state internally. A renderer
///         that binds the same pipeline for four hundred draws would make sixteen redundant calls per
///         draw without this, and those calls are not free — on several drivers a redundant
///         <c>glUseProgram</c> alone is measurable.
///     </para>
///     <para>
///         <b>The shadow must never be optimistic.</b> Every field starts at a value GL cannot be in,
///         so the first apply after <see cref="Invalidate" /> always writes. The failure mode of
///         guessing GL's defaults right is that it works until a driver disagrees, and then the
///         picture is wrong in a way that no capture explains — the call that would have fixed it is
///         the one that was elided.
///     </para>
///     <para>
///         The viewport and scissor flips live here as well, because they are the same conversion in
///         two places and doing it in one of them is the version of this bug that ships.
///     </para>
/// </remarks>
sealed class GlStateCache(IGlApi gl) {
    // Deliberately impossible starting values: 0 is a valid GL name and a valid enum, so "unset"
    // has to be something GL will never be in rather than something it merely usually is not.
    const uint Unset = uint.MaxValue;

    uint program = Unset;
    uint vertexArray = Unset;
    uint drawFramebuffer = Unset;
    uint readFramebuffer = Unset;
    uint indexBuffer = Unset;
    uint activeUnit = Unset;

    readonly Dictionary<uint, uint> boundBuffers = [];
    readonly Dictionary<uint, uint> boundTextures = [];
    readonly Dictionary<uint, uint> boundSamplers = [];
    readonly Dictionary<uint, (uint Texture, int Level, bool Layered, int Layer, uint Format)> boundImages = [];
    readonly Dictionary<uint, (uint Buffer, nint Offset, nuint Size)> boundRanges = [];
    readonly HashSet<uint> enabled = [];
    readonly HashSet<uint> disabled = [];

    (int X, int Y, int Width, int Height) viewport = (-1, -1, -1, -1);
    (float Near, float Far) depthRange = (-1f, -1f);
    (int X, int Y, int Width, int Height) scissor = (-1, -1, -1, -1);
    (float R, float G, float B, float A) blendConstant = (-1f, -1f, -1f, -1f);

    RasterizerState? rasterizer;
    DepthStencilState? depthStencil;
    BlendState? blend;
    uint stencilReference = Unset;
    uint stencilReadMask = Unset;

    /// <summary>The height of the framebuffer currently bound for drawing.</summary>
    /// <remarks>
    ///     Needed because the RHI's viewport and scissor are top-left-origin, following Vulkan, and
    ///     GL's are bottom-left-origin whatever <c>glClipControl</c> says — clip control changes
    ///     which way clip space maps into the viewport rectangle, not where the rectangle is
    ///     measured from. Two separate conversions, one shared misunderstanding.
    /// </remarks>
    public int TargetHeight { get; set; }

    /// <summary>Forgets everything, so the next apply writes every value.</summary>
    /// <remarks>
    ///     Called when something outside this cache could have changed GL state — after a swapchain
    ///     present, and at the start of a submission if the application shares the context with
    ///     other code. Cheap and always safe; the opposite is neither.
    /// </remarks>
    public void Invalidate() {
        program = vertexArray = drawFramebuffer = readFramebuffer = indexBuffer = activeUnit = Unset;
        boundBuffers.Clear();
        boundTextures.Clear();
        boundSamplers.Clear();
        boundImages.Clear();
        boundRanges.Clear();
        enabled.Clear();
        disabled.Clear();
        viewport = (-1, -1, -1, -1);
        depthRange = (-1f, -1f);
        scissor = (-1, -1, -1, -1);
        blendConstant = (-1f, -1f, -1f, -1f);
        rasterizer = null;
        depthStencil = null;
        blend = null;
        stencilReference = Unset;
        stencilReadMask = Unset;
    }

    /// <summary>Uses a program, if it is not already in use.</summary>
    public void UseProgram(uint value) {
        if (program == value) {
            return;
        }

        program = value;
        gl.UseProgram(value);
    }

    /// <summary>Binds a vertex array.</summary>
    public void BindVertexArray(uint value) {
        if (vertexArray == value) {
            return;
        }

        vertexArray = value;
        gl.BindVertexArray(value);
    }

    /// <summary>Binds a framebuffer for drawing.</summary>
    public void BindDrawFramebuffer(uint value) {
        if (drawFramebuffer == value) {
            return;
        }

        drawFramebuffer = value;
        gl.BindFramebuffer(GlConstants.DrawFramebuffer, value);
    }

    /// <summary>Binds a framebuffer for reading.</summary>
    public void BindReadFramebuffer(uint value) {
        if (readFramebuffer == value) {
            return;
        }

        readFramebuffer = value;
        gl.BindFramebuffer(GlConstants.ReadFramebuffer, value);
    }

    /// <summary>Binds a buffer to a target.</summary>
    /// <remarks>
    ///     ⚠ <c>GL_ELEMENT_ARRAY_BUFFER</c> is not tracked here, because it is not context state — it
    ///     is <em>vertex array object</em> state, and the same target holds a different buffer
    ///     depending on which VAO is bound. Caching it against the context would elide exactly the
    ///     binds that a pipeline change makes necessary.
    /// </remarks>
    public void BindBuffer(uint target, uint value) {
        if (target == GlConstants.ElementArrayBuffer) {
            BindIndexBuffer(value);
            return;
        }

        if (boundBuffers.TryGetValue(target, out var current) && current == value) {
            return;
        }

        boundBuffers[target] = value;
        gl.BindBuffer(target, value);
    }

    /// <summary>Binds the index buffer into the current vertex array.</summary>
    public void BindIndexBuffer(uint value) {
        if (indexBuffer == value) {
            return;
        }

        indexBuffer = value;
        gl.BindBuffer(GlConstants.ElementArrayBuffer, value);
    }

    /// <summary>Binds a range of a buffer to an indexed target.</summary>
    public void BindBufferRange(uint target, uint index, uint buffer, nint offset, nuint size) {
        var key = (target << 8) | index;

        if (boundRanges.TryGetValue(key, out var current)
            && current == (buffer, offset, size)) {
            return;
        }

        boundRanges[key] = (buffer, offset, size);

        // The indexed binding also sets the general one on every driver, so the shadow of the
        // general target has to be dropped or a later BindBuffer would be elided wrongly. This is
        // the sort of thing a state cache gets wrong once and then never again.
        boundBuffers.Remove(target);
        gl.BindBufferRange(target, index, buffer, offset, size);
    }

    /// <summary>Binds a texture and its sampler to a unit.</summary>
    public void BindTextureUnit(uint unit, uint target, uint texture, uint sampler) {
        var key = (unit << 16) | (target & 0xFFFF);

        if (!boundTextures.TryGetValue(key, out var currentTexture) || currentTexture != texture) {
            ActiveTexture(unit);
            boundTextures[key] = texture;
            gl.BindTexture(target, texture);
        }

        if (boundSamplers.TryGetValue(unit, out var currentSampler) && currentSampler == sampler) {
            return;
        }

        boundSamplers[unit] = sampler;
        gl.BindSampler(unit, sampler);
    }

    /// <summary>Binds a storage image to an image unit, if it is not already there.</summary>
    /// <param name="unit">The image unit. ⚠ Its own namespace, not a texture unit.</param>
    /// <param name="texture">The texture name.</param>
    /// <param name="level">The mip level.</param>
    /// <param name="layered">Whether the whole array or volume is bound.</param>
    /// <param name="layer">The layer, when it is not.</param>
    /// <param name="format">The sized internal format the shader sees.</param>
    /// <remarks>
    ///     ⚠ <b>Every argument is part of the key, which is why this is a tuple and the texture
    ///     cache above is a name.</b> Two views of one texture at different mip levels are the
    ///     ordinary case for a compute chain that writes a pyramid, and a cache keyed on the texture
    ///     alone would bind level 0 for all of them and produce a mip chain whose every level is the
    ///     first — a picture that is blurry rather than absent.
    /// </remarks>
    public void BindImageTexture(uint unit, uint texture, int level, bool layered, int layer, uint format) {
        var wanted = (texture, level, layered, layer, format);

        if (boundImages.TryGetValue(unit, out var current) && current == wanted) {
            return;
        }

        boundImages[unit] = wanted;
        gl.BindImageTexture(unit, texture, level, layered, layer, GlConstants.ReadWrite, format);
    }

    /// <summary>Selects a texture unit.</summary>
    public void ActiveTexture(uint unit) {
        if (activeUnit == unit) {
            return;
        }

        activeUnit = unit;
        gl.ActiveTexture(unit);
    }

    /// <summary>Enables or disables a capability.</summary>
    public void Set(uint capability, bool on) {
        var set = on ? enabled : disabled;
        var other = on ? disabled : enabled;

        if (!set.Add(capability)) {
            return;
        }

        other.Remove(capability);

        if (on) {
            gl.Enable(capability);
        } else {
            gl.Disable(capability);
        }
    }

    /// <summary>Sets the viewport, converting from the RHI's top-left origin.</summary>
    public void SetViewport(float x, float y, float width, float height, float near, float far) {
        var flipped = TargetHeight - (int)(y + height);
        var value = ((int)x, flipped, (int)width, (int)height);

        if (viewport != value) {
            viewport = value;
            gl.Viewport(value.Item1, value.Item2, value.Item3, value.Item4);
        }

        if (depthRange == (near, far)) {
            return;
        }

        depthRange = (near, far);
        gl.DepthRange(near, far);
    }

    /// <summary>Sets the scissor rectangle, converting from the RHI's top-left origin.</summary>
    public void SetScissor(int x, int y, int width, int height) {
        var value = (x, TargetHeight - (y + height), width, height);

        if (scissor == value) {
            return;
        }

        scissor = value;
        gl.Scissor(value.Item1, value.Item2, value.Item3, value.Item4);
    }

    /// <summary>Sets the blend constant.</summary>
    public void SetBlendConstant(float r, float g, float b, float a) {
        if (blendConstant == (r, g, b, a)) {
            return;
        }

        blendConstant = (r, g, b, a);
        gl.BlendColour(r, g, b, a);
    }

    /// <summary>Sets the stencil reference, keeping the comparison the pipeline chose.</summary>
    /// <remarks>
    ///     GL has no separate "set reference" call: the reference, the comparison and the read mask
    ///     go in together through <c>glStencilFuncSeparate</c>. So a dynamic reference change has to
    ///     re-send the pipeline's comparison, which means the cache has to remember it — one of the
    ///     places where GL's state model and the RHI's do not divide the same way.
    /// </remarks>
    public void SetStencilReference(uint reference) {
        if (stencilReference == reference) {
            return;
        }

        stencilReference = reference;

        if (depthStencil is not { StencilTest: true } state) {
            return;
        }

        gl.StencilFuncSeparate(
            GlConstants.Front,
            GlEnums.Compare(state.Front.Compare),
            (int)reference,
            stencilReadMask
        );

        gl.StencilFuncSeparate(
            GlConstants.Back,
            GlEnums.Compare(state.Back.Compare),
            (int)reference,
            stencilReadMask
        );
    }

    /// <summary>Opens every write mask so that a clear clears everything.</summary>
    /// <remarks>
    ///     <para>
    ///         A GL clear is not exempt from the write masks or the scissor — it goes through the
    ///         same fixed-function path a draw does. So a pass that clears while the previous
    ///         pipeline's depth mask is off clears no depth at all, and a pass that clears while a
    ///         colour mask excludes alpha leaves the previous frame's alpha in place. Neither
    ///         produces an error and both look like something else entirely.
    ///     </para>
    ///     <para>
    ///         The pipeline's own state is then forgotten rather than restored, so the next
    ///         <see cref="ApplyPipeline" /> re-sends it. Conservative on purpose: restoring by hand
    ///         means remembering which of five masks were touched, and getting that list wrong is the
    ///         same bug one level deeper.
    ///     </para>
    /// </remarks>
    public void PrepareClear() {
        gl.DepthMask(true);
        gl.ColourMask(true, true, true, true);
        gl.StencilMaskSeparate(GlConstants.FrontAndBack, 0xFF);
        depthStencil = null;
        blend = null;
    }

    /// <summary>Applies a pipeline's whole state block, writing only what differs.</summary>
    public void ApplyPipeline(GlPipeline pipeline) {
        UseProgram(pipeline.Program);

        if (pipeline.IsCompute) {
            return;
        }

        BindVertexArray(pipeline.VertexArray);
        ApplyRasterizer(pipeline.Rasterizer);
        ApplyDepthStencil(pipeline.DepthStencil);
        ApplyBlend(pipeline.Blend);
    }

    void ApplyRasterizer(in RasterizerState state) {
        if (rasterizer == state) {
            return;
        }

        var previous = rasterizer;
        rasterizer = state;

        if (previous?.Cull != state.Cull) {
            var face = GlEnums.Cull(state.Cull);
            Set(GlConstants.CullFace, face != 0);

            if (face != 0) {
                gl.CullFace(face);
            }
        }

        if (previous?.FrontFace != state.FrontFace) {
            gl.FrontFace(GlEnums.Winding(state.FrontFace));
        }

        if (previous?.Fill != state.Fill && gl.Profile.HasWireframe()) {
            gl.PolygonMode(GlConstants.FrontAndBack, GlEnums.Fill(state.Fill));
        }

        if (previous?.DepthClamp != state.DepthClamp && gl.Profile >= GlProfile.Core45) {
            Set(GlConstants.DepthClamp, state.DepthClamp);
        }

        if (previous?.DepthBias == state.DepthBias && previous?.DepthBiasSlope == state.DepthBiasSlope) {
            return;
        }

        var biased = state.DepthBias != 0f || state.DepthBiasSlope != 0f;
        Set(GlConstants.PolygonOffsetFill, biased);

        if (biased) {
            gl.PolygonOffset(state.DepthBiasSlope, state.DepthBias);
        }
    }

    void ApplyDepthStencil(in DepthStencilState state) {
        if (depthStencil == state) {
            return;
        }

        var previous = depthStencil;
        depthStencil = state;

        Set(GlConstants.DepthTest, state.DepthTest);

        if (state.DepthTest && previous?.DepthCompare != state.DepthCompare) {
            gl.DepthFunc(GlEnums.Compare(state.DepthCompare));
        }

        if (previous?.DepthWrite != state.DepthWrite) {
            gl.DepthMask(state.DepthWrite);
        }

        Set(GlConstants.StencilTest, state.StencilTest);

        if (!state.StencilTest) {
            return;
        }

        stencilReadMask = state.StencilReadMask;
        var reference = stencilReference == Unset ? 0 : (int)stencilReference;

        gl.StencilFuncSeparate(
            GlConstants.Front,
            GlEnums.Compare(state.Front.Compare),
            reference,
            state.StencilReadMask
        );

        gl.StencilFuncSeparate(
            GlConstants.Back,
            GlEnums.Compare(state.Back.Compare),
            reference,
            state.StencilReadMask
        );

        gl.StencilOpSeparate(
            GlConstants.Front,
            GlEnums.Stencil(state.Front.Fail),
            GlEnums.Stencil(state.Front.DepthFail),
            GlEnums.Stencil(state.Front.Pass)
        );

        gl.StencilOpSeparate(
            GlConstants.Back,
            GlEnums.Stencil(state.Back.Fail),
            GlEnums.Stencil(state.Back.DepthFail),
            GlEnums.Stencil(state.Back.Pass)
        );

        gl.StencilMaskSeparate(GlConstants.FrontAndBack, state.StencilWriteMask);
    }

    void ApplyBlend(in BlendState state) {
        if (blend == state) {
            return;
        }

        var previous = blend;
        blend = state;

        Set(GlConstants.Blend, state.Enabled);

        if (state.Enabled
            && (previous?.SourceColour != state.SourceColour
                || previous?.DestinationColour != state.DestinationColour
                || previous?.SourceAlpha != state.SourceAlpha
                || previous?.DestinationAlpha != state.DestinationAlpha)) {
            gl.BlendFuncSeparate(
                GlEnums.Blend(state.SourceColour),
                GlEnums.Blend(state.DestinationColour),
                GlEnums.Blend(state.SourceAlpha),
                GlEnums.Blend(state.DestinationAlpha)
            );
        }

        if (state.Enabled
            && (previous?.ColourOperation != state.ColourOperation
                || previous?.AlphaOperation != state.AlphaOperation)) {
            gl.BlendEquationSeparate(
                GlEnums.BlendOp(state.ColourOperation),
                GlEnums.BlendOp(state.AlphaOperation)
            );
        }

        if (previous?.WriteMask == state.WriteMask) {
            return;
        }

        gl.ColourMask(
            (state.WriteMask & ColourWriteMask.Red) != 0,
            (state.WriteMask & ColourWriteMask.Green) != 0,
            (state.WriteMask & ColourWriteMask.Blue) != 0,
            (state.WriteMask & ColourWriteMask.Alpha) != 0
        );
    }
}
