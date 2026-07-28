// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Silk.NET.Core.Contexts;
using Silk.NET.OpenGL;

namespace Vixen.Graphics.OpenGL;

/// <summary>The GL entry points, over <c>Silk.NET.OpenGL</c>.</summary>
/// <remarks>
///     <para>
///         <b>The only file in this assembly that names a binding type.</b> Everything above it talks
///         to <see cref="IGlApi" />, which is what lets the translation layer — the part ADR-001
///         wants validated, and the part that is actually difficult — be exercised on a CI runner
///         with no GL context. What is here is delegation: each method is one call, with the
///         enumerant cast from the value <see cref="GlConstants" /> holds to the enum Silk wants.
///     </para>
///     <para>
///         <c>GLEnum</c> rather than Silk's per-parameter enums throughout. Silk generates a distinct
///         enum for nearly every parameter — <c>BufferTargetARB</c>, <c>TextureTarget</c>,
///         <c>FramebufferAttachment</c> — and threading those through <see cref="IGlApi" /> would put
///         the binding in the interface, which is the one thing this seam exists to prevent. The
///         values are identical; <c>GLEnum</c> is the union of all of them.
///     </para>
///     <para>
///         <b>Not exercised by the test suite, deliberately and visibly.</b> There is nothing here to
///         get wrong except a transcription, and a transcription is what a compiler checks. The logic
///         that could be wrong is in the other twelve files, and all of it is under test. What this
///         file needs instead is a driver, which CI provides on the Mesa leg
///         (<c>docs/plan/05</c> § Cross-backend equivalence).
///     </para>
/// </remarks>
public sealed class SilkGlApi : IGlApi, IDisposable {
    readonly GL gl;
    readonly bool owned;

    /// <summary>Wraps entry points somebody else loaded.</summary>
    /// <param name="gl">The loaded GL.</param>
    /// <param name="profile">Which dialect the context was created for.</param>
    public SilkGlApi(GL gl, GlProfile profile) {
        this.gl = gl ?? throw new ArgumentNullException(nameof(gl));
        Profile = profile;
        owned = false;
    }

    /// <summary>Loads the entry points from a context.</summary>
    /// <param name="context">The current context, from the windowing layer.</param>
    /// <param name="profile">Which dialect it was created for.</param>
    public SilkGlApi(IGLContext context, GlProfile profile) {
        ArgumentNullException.ThrowIfNull(context);
        gl = GL.GetApi(context);
        Profile = profile;
        owned = true;
    }

    /// <inheritdoc />
    public GlProfile Profile { get; }

    /// <inheritdoc />
    public uint GenBuffer() => gl.GenBuffer();

    /// <inheritdoc />
    public void DeleteBuffer(uint buffer) => gl.DeleteBuffer(buffer);

    /// <inheritdoc />
    public void BindBuffer(uint target, uint buffer) => gl.BindBuffer((GLEnum)target, buffer);

    /// <inheritdoc />
    public void BindBufferRange(uint target, uint index, uint buffer, nint offset, nuint size) =>
        gl.BindBufferRange((GLEnum)target, index, buffer, offset, size);

    /// <inheritdoc />
    public unsafe void BufferData(uint target, nuint size, uint usage) =>
        gl.BufferData((GLEnum)target, size, null, (GLEnum)usage);

    /// <inheritdoc />
    public void BufferSubData(uint target, nint offset, ReadOnlySpan<byte> data) =>
        gl.BufferSubData((GLEnum)target, offset, data);

    /// <inheritdoc />
    public void CopyBufferSubData(
        uint readTarget,
        uint writeTarget,
        nint readOffset,
        nint writeOffset,
        nuint size
    ) => gl.CopyBufferSubData((GLEnum)readTarget, (GLEnum)writeTarget, readOffset, writeOffset, size);

    /// <inheritdoc />
    public void GetBufferSubData(uint target, nint offset, Span<byte> destination) =>
        gl.GetBufferSubData((GLEnum)target, offset, destination);

    /// <inheritdoc />
    public uint GenTexture() => gl.GenTexture();

    /// <inheritdoc />
    public void DeleteTexture(uint texture) => gl.DeleteTexture(texture);

    /// <inheritdoc />
    public void BindTexture(uint target, uint texture) => gl.BindTexture((GLEnum)target, texture);

    /// <inheritdoc />
    public void ActiveTexture(uint unit) => gl.ActiveTexture(GLEnum.Texture0 + (int)unit);

    /// <inheritdoc />
    public void TexStorage2D(uint target, int levels, uint internalFormat, int width, int height) =>
        gl.TexStorage2D((GLEnum)target, (uint)levels, (GLEnum)internalFormat, (uint)width, (uint)height);

    /// <inheritdoc />
    public void TexStorage3D(uint target, int levels, uint internalFormat, int width, int height, int depth) =>
        gl.TexStorage3D(
            (GLEnum)target,
            (uint)levels,
            (GLEnum)internalFormat,
            (uint)width,
            (uint)height,
            (uint)depth
        );

    /// <inheritdoc />
    /// <remarks>
    ///     Fixed sample locations, always. The alternative lets the driver choose per attachment, and
    ///     two attachments in one framebuffer whose patterns disagree is a framebuffer that is
    ///     incomplete for a reason nobody reads out of the status code.
    /// </remarks>
    public void TexStorage2DMultisample(uint target, int samples, uint internalFormat, int width, int height) =>
        gl.TexStorage2DMultisample(
            (GLEnum)target,
            (uint)samples,
            (GLEnum)internalFormat,
            (uint)width,
            (uint)height,
            true
        );

    /// <inheritdoc />
    public unsafe void TexSubImage2D(
        uint target,
        int level,
        int x,
        int y,
        int width,
        int height,
        uint format,
        uint type,
        nint bufferOffset
    ) => gl.TexSubImage2D(
        (GLEnum)target,
        level,
        x,
        y,
        (uint)width,
        (uint)height,
        (GLEnum)format,
        (GLEnum)type,
        (void*)bufferOffset
    );

    /// <inheritdoc />
    public unsafe void TexSubImage3D(
        uint target,
        int level,
        int x,
        int y,
        int z,
        int width,
        int height,
        int depth,
        uint format,
        uint type,
        nint bufferOffset
    ) => gl.TexSubImage3D(
        (GLEnum)target,
        level,
        x,
        y,
        z,
        (uint)width,
        (uint)height,
        (uint)depth,
        (GLEnum)format,
        (GLEnum)type,
        (void*)bufferOffset
    );

    /// <inheritdoc />
    public void TexParameter(uint target, uint parameter, int value) =>
        gl.TexParameter((GLEnum)target, (GLEnum)parameter, value);

    /// <inheritdoc />
    public void GenerateMipmap(uint target) => gl.GenerateMipmap((GLEnum)target);

    /// <inheritdoc />
    public void CopyImageSubData(
        uint sourceName,
        uint sourceTarget,
        int sourceLevel,
        int sourceX,
        int sourceY,
        int sourceZ,
        uint destinationName,
        uint destinationTarget,
        int destinationLevel,
        int destinationX,
        int destinationY,
        int destinationZ,
        int width,
        int height,
        int depth
    ) => gl.CopyImageSubData(
        sourceName,
        (GLEnum)sourceTarget,
        sourceLevel,
        sourceX,
        sourceY,
        sourceZ,
        destinationName,
        (GLEnum)destinationTarget,
        destinationLevel,
        destinationX,
        destinationY,
        destinationZ,
        (uint)width,
        (uint)height,
        (uint)depth
    );

    /// <inheritdoc />
    public uint GenSampler() => gl.GenSampler();

    /// <inheritdoc />
    public void DeleteSampler(uint sampler) => gl.DeleteSampler(sampler);

    /// <inheritdoc />
    public void BindSampler(uint unit, uint sampler) => gl.BindSampler(unit, sampler);

    /// <inheritdoc />
    public void SamplerParameter(uint sampler, uint parameter, int value) =>
        gl.SamplerParameter(sampler, (GLEnum)parameter, value);

    /// <inheritdoc />
    public void SamplerParameter(uint sampler, uint parameter, float value) =>
        gl.SamplerParameter(sampler, (GLEnum)parameter, value);

    /// <inheritdoc />
    public unsafe void SamplerParameter(uint sampler, uint parameter, ReadOnlySpan<float> values) {
        fixed (float* first = values) {
            gl.SamplerParameter(sampler, (GLEnum)parameter, first);
        }
    }

    /// <inheritdoc />
    public uint GenFramebuffer() => gl.GenFramebuffer();

    /// <inheritdoc />
    public void DeleteFramebuffer(uint framebuffer) => gl.DeleteFramebuffer(framebuffer);

    /// <inheritdoc />
    public void BindFramebuffer(uint target, uint framebuffer) =>
        gl.BindFramebuffer((GLEnum)target, framebuffer);

    /// <inheritdoc />
    public void FramebufferTexture2D(
        uint target,
        uint attachment,
        uint textureTarget,
        uint texture,
        int level
    ) => gl.FramebufferTexture2D((GLEnum)target, (GLEnum)attachment, (GLEnum)textureTarget, texture, level);

    /// <inheritdoc />
    public void FramebufferTextureLayer(uint target, uint attachment, uint texture, int level, int layer) =>
        gl.FramebufferTextureLayer((GLEnum)target, (GLEnum)attachment, texture, level, layer);

    /// <inheritdoc />
    public uint CheckFramebufferStatus(uint target) => (uint)gl.CheckFramebufferStatus((GLEnum)target);

    /// <inheritdoc />
    public unsafe void DrawBuffers(ReadOnlySpan<uint> attachments) {
        fixed (uint* first = attachments) {
            gl.DrawBuffers((uint)attachments.Length, (GLEnum*)first);
        }
    }

    /// <inheritdoc />
    public void ReadBuffer(uint attachment) => gl.ReadBuffer((GLEnum)attachment);

    /// <inheritdoc />
    public unsafe void InvalidateFramebuffer(uint target, ReadOnlySpan<uint> attachments) {
        fixed (uint* first = attachments) {
            gl.InvalidateFramebuffer((GLEnum)target, (uint)attachments.Length, (GLEnum*)first);
        }
    }

    /// <inheritdoc />
    public void BlitFramebuffer(
        int sourceX0,
        int sourceY0,
        int sourceX1,
        int sourceY1,
        int destinationX0,
        int destinationY0,
        int destinationX1,
        int destinationY1,
        uint mask,
        uint filter
    ) => gl.BlitFramebuffer(
        sourceX0,
        sourceY0,
        sourceX1,
        sourceY1,
        destinationX0,
        destinationY0,
        destinationX1,
        destinationY1,
        (ClearBufferMask)mask,
        (GLEnum)filter
    );

    /// <inheritdoc />
    public unsafe void ReadPixels(
        int x,
        int y,
        int width,
        int height,
        uint format,
        uint type,
        nint bufferOffset
    ) => gl.ReadPixels(x, y, (uint)width, (uint)height, (GLEnum)format, (GLEnum)type, (void*)bufferOffset);

    /// <inheritdoc />
    public unsafe void ClearBuffer(uint buffer, int drawBuffer, ReadOnlySpan<float> value) {
        fixed (float* first = value) {
            gl.ClearBuffer((GLEnum)buffer, drawBuffer, first);
        }
    }

    /// <inheritdoc />
    public unsafe void ClearBuffer(uint buffer, int drawBuffer, ReadOnlySpan<int> value) {
        fixed (int* first = value) {
            gl.ClearBuffer((GLEnum)buffer, drawBuffer, first);
        }
    }

    /// <inheritdoc />
    public void ClearBufferDepthStencil(int drawBuffer, float depth, int stencil) =>
        gl.ClearBuffer(GLEnum.DepthStencil, drawBuffer, depth, stencil);

    /// <inheritdoc />
    public uint GenVertexArray() => gl.GenVertexArray();

    /// <inheritdoc />
    public void DeleteVertexArray(uint array) => gl.DeleteVertexArray(array);

    /// <inheritdoc />
    public void BindVertexArray(uint array) => gl.BindVertexArray(array);

    /// <inheritdoc />
    public void EnableVertexAttribArray(uint index) => gl.EnableVertexAttribArray(index);

    /// <inheritdoc />
    public void DisableVertexAttribArray(uint index) => gl.DisableVertexAttribArray(index);

    /// <inheritdoc />
    public unsafe void VertexAttribPointer(
        uint index,
        int size,
        uint type,
        bool normalised,
        int stride,
        nint offset
    ) => gl.VertexAttribPointer(index, size, (GLEnum)type, normalised, (uint)stride, (void*)offset);

    /// <inheritdoc />
    public unsafe void VertexAttribIPointer(uint index, int size, uint type, int stride, nint offset) =>
        gl.VertexAttribIPointer(index, size, (GLEnum)type, (uint)stride, (void*)offset);

    /// <inheritdoc />
    public void VertexAttribDivisor(uint index, uint divisor) => gl.VertexAttribDivisor(index, divisor);

    /// <inheritdoc />
    public uint CreateShader(uint type) => gl.CreateShader((GLEnum)type);

    /// <inheritdoc />
    public string? CompileShader(uint shader, string source) {
        gl.ShaderSource(shader, source);
        gl.CompileShader(shader);

        // GL_COMPILE_STATUS. Asked every time and not only in debug: a shader that failed to compile
        // still links into a program that binds and draws nothing, and finding that later means
        // looking at a black screen with no error anywhere.
        return gl.GetShader(shader, GLEnum.CompileStatus) != 0 ? null : gl.GetShaderInfoLog(shader);
    }

    /// <inheritdoc />
    public void DeleteShader(uint shader) => gl.DeleteShader(shader);

    /// <inheritdoc />
    public uint CreateProgram() => gl.CreateProgram();

    /// <inheritdoc />
    public void AttachShader(uint program, uint shader) => gl.AttachShader(program, shader);

    /// <inheritdoc />
    public string? LinkProgram(uint program) {
        gl.LinkProgram(program);
        return gl.GetProgram(program, GLEnum.LinkStatus) != 0 ? null : gl.GetProgramInfoLog(program);
    }

    /// <inheritdoc />
    public void DeleteProgram(uint program) => gl.DeleteProgram(program);

    /// <inheritdoc />
    public void UseProgram(uint program) => gl.UseProgram(program);

    /// <inheritdoc />
    public uint GetUniformBlockIndex(uint program, string name) => gl.GetUniformBlockIndex(program, name);

    /// <inheritdoc />
    public void UniformBlockBinding(uint program, uint blockIndex, uint binding) =>
        gl.UniformBlockBinding(program, blockIndex, binding);

    /// <inheritdoc />
    public uint GetStorageBlockIndex(uint program, string name) =>
        gl.GetProgramResourceIndex(program, GLEnum.ShaderStorageBlock, name);

    /// <inheritdoc />
    public void StorageBlockBinding(uint program, uint blockIndex, uint binding) =>
        gl.ShaderStorageBlockBinding(program, blockIndex, binding);

    /// <inheritdoc />
    public int GetUniformLocation(uint program, string name) => gl.GetUniformLocation(program, name);

    /// <inheritdoc />
    public void Uniform(int location, int value) => gl.Uniform1(location, value);

    /// <inheritdoc />
    public void Uniform4(int location, ReadOnlySpan<float> values) => gl.Uniform4(location, values);

    /// <inheritdoc />
    public void Enable(uint capability) => gl.Enable((GLEnum)capability);

    /// <inheritdoc />
    public void Disable(uint capability) => gl.Disable((GLEnum)capability);

    /// <inheritdoc />
    public void DepthFunc(uint comparison) => gl.DepthFunc((GLEnum)comparison);

    /// <inheritdoc />
    public void DepthMask(bool write) => gl.DepthMask(write);

    /// <inheritdoc />
    public void ColourMask(bool red, bool green, bool blue, bool alpha) =>
        gl.ColorMask(red, green, blue, alpha);

    /// <inheritdoc />
    public void CullFace(uint face) => gl.CullFace((GLEnum)face);

    /// <inheritdoc />
    public void FrontFace(uint winding) => gl.FrontFace((GLEnum)winding);

    /// <inheritdoc />
    public void PolygonMode(uint face, uint mode) => gl.PolygonMode((GLEnum)face, (GLEnum)mode);

    /// <inheritdoc />
    public void PolygonOffset(float factor, float units) => gl.PolygonOffset(factor, units);

    /// <inheritdoc />
    public void BlendEquationSeparate(uint colour, uint alpha) =>
        gl.BlendEquationSeparate((GLEnum)colour, (GLEnum)alpha);

    /// <inheritdoc />
    public void BlendFuncSeparate(
        uint sourceColour,
        uint destinationColour,
        uint sourceAlpha,
        uint destinationAlpha
    ) => gl.BlendFuncSeparate(
        (GLEnum)sourceColour,
        (GLEnum)destinationColour,
        (GLEnum)sourceAlpha,
        (GLEnum)destinationAlpha
    );

    /// <inheritdoc />
    public void BlendColour(float red, float green, float blue, float alpha) =>
        gl.BlendColor(red, green, blue, alpha);

    /// <inheritdoc />
    public void StencilFuncSeparate(uint face, uint comparison, int reference, uint mask) =>
        gl.StencilFuncSeparate((GLEnum)face, (GLEnum)comparison, reference, mask);

    /// <inheritdoc />
    public void StencilOpSeparate(uint face, uint fail, uint depthFail, uint pass) =>
        gl.StencilOpSeparate((GLEnum)face, (GLEnum)fail, (GLEnum)depthFail, (GLEnum)pass);

    /// <inheritdoc />
    public void StencilMaskSeparate(uint face, uint mask) => gl.StencilMaskSeparate((GLEnum)face, mask);

    /// <inheritdoc />
    public void Viewport(int x, int y, int width, int height) =>
        gl.Viewport(x, y, (uint)width, (uint)height);

    /// <inheritdoc />
    public void DepthRange(float near, float far) => gl.DepthRange(near, far);

    /// <inheritdoc />
    public void Scissor(int x, int y, int width, int height) =>
        gl.Scissor(x, y, (uint)width, (uint)height);

    /// <inheritdoc />
    public void ClipControl(uint origin, uint depth) => gl.ClipControl((GLEnum)origin, (GLEnum)depth);

    /// <inheritdoc />
    public void DrawArraysInstanced(uint topology, int first, int count, int instanceCount) =>
        gl.DrawArraysInstanced((GLEnum)topology, first, (uint)count, (uint)instanceCount);

    /// <inheritdoc />
    public void DrawArraysInstancedBaseInstance(
        uint topology,
        int first,
        int count,
        int instanceCount,
        uint baseInstance
    ) => gl.DrawArraysInstancedBaseInstance(
        (GLEnum)topology,
        first,
        (uint)count,
        (uint)instanceCount,
        baseInstance
    );

    /// <inheritdoc />
    public unsafe void DrawElementsInstancedBaseVertex(
        uint topology,
        int count,
        uint indexType,
        nint offset,
        int instanceCount,
        int baseVertex
    ) => gl.DrawElementsInstancedBaseVertex(
        (GLEnum)topology,
        (uint)count,
        (GLEnum)indexType,
        (void*)offset,
        (uint)instanceCount,
        baseVertex
    );

    /// <inheritdoc />
    public unsafe void DrawElementsInstancedBaseVertexBaseInstance(
        uint topology,
        int count,
        uint indexType,
        nint offset,
        int instanceCount,
        int baseVertex,
        uint baseInstance
    ) => gl.DrawElementsInstancedBaseVertexBaseInstance(
        (GLEnum)topology,
        (uint)count,
        (GLEnum)indexType,
        new ReadOnlySpan<byte>((void*)offset, 0),
        (uint)instanceCount,
        baseVertex,
        baseInstance
    );

    /// <inheritdoc />
    public unsafe void MultiDrawElementsIndirect(
        uint topology,
        uint indexType,
        nint offset,
        int drawCount,
        int stride
    ) => gl.MultiDrawElementsIndirect(
        (GLEnum)topology,
        (GLEnum)indexType,
        (void*)offset,
        (uint)drawCount,
        (uint)stride
    );

    /// <inheritdoc />
    public void DispatchCompute(uint x, uint y, uint z) => gl.DispatchCompute(x, y, z);

    /// <inheritdoc />
    public void DispatchComputeIndirect(nint offset) => gl.DispatchComputeIndirect(offset);

    /// <inheritdoc />
    public void MemoryBarrier(uint bits) => gl.MemoryBarrier(bits);

    /// <inheritdoc />
    public void Finish() => gl.Finish();

    /// <inheritdoc />
    public void Flush() => gl.Flush();

    /// <inheritdoc />
    public void PushDebugGroup(string name) =>
        gl.PushDebugGroup(GLEnum.DebugSourceApplication, 0, (uint)name.Length, name);

    /// <inheritdoc />
    public void PopDebugGroup() => gl.PopDebugGroup();

    /// <inheritdoc />
    public void DebugMarker(string name) => gl.DebugMessageInsert(
        GLEnum.DebugSourceApplication,
        GLEnum.DebugTypeMarker,
        0,
        GLEnum.DebugSeverityNotification,
        (uint)name.Length,
        name
    );

    /// <inheritdoc />
    public void ObjectLabel(uint identifier, uint name, string label) =>
        gl.ObjectLabel((GLEnum)identifier, name, (uint)label.Length, label);

    /// <inheritdoc />
    public uint GetError() => (uint)gl.GetError();

    /// <inheritdoc />
    public void Dispose() {
        if (owned) {
            gl.Dispose();
        }
    }
}
