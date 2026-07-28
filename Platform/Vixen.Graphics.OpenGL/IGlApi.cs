// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Graphics.OpenGL;

/// <summary>Every GL entry point this backend calls.</summary>
/// <remarks>
///     <para>
///         <b>The seam that makes the hard part testable.</b> ADR-001 gives this backend the job of
///         proving the RHI is API-neutral, and that proof lives in the translation — pipeline objects
///         becoming program-plus-state, descriptor sets becoming binding indices, barriers becoming
///         nothing at all. None of it needs a GL context to be wrong, and none of it would be checked
///         on a CI runner if checking it required one. So the calls go through an interface, the
///         binding lives behind <see cref="SilkGlApi" />, and the tests drive a recorder.
///     </para>
///     <para>
///         Deliberately non-DSA. GL 4.5 has <c>glCreateBuffers</c> and friends and they are nicer;
///         GLES and WebGL2 do not, and this backend is one code path across three profiles rather
///         than two paths and a flag. Bind-then-modify is what all three have.
///     </para>
///     <para>
///         Everything is single-threaded by contract. GL state is per-context and a context is
///         current on one thread — which is why <see cref="GlCommandList" /> records into managed
///         memory on whatever thread it likes and nothing here is called until submit.
///     </para>
/// </remarks>
public interface IGlApi {
    /// <summary>Which dialect this context speaks.</summary>
    GlProfile Profile { get; }

    // ── Buffers ─────────────────────────────────────────────────────────────────────────────

    /// <summary><c>glGenBuffers</c>, for one.</summary>
    uint GenBuffer();

    /// <summary><c>glDeleteBuffers</c>, for one.</summary>
    void DeleteBuffer(uint buffer);

    /// <summary><c>glBindBuffer</c>.</summary>
    void BindBuffer(uint target, uint buffer);

    /// <summary><c>glBindBufferRange</c>.</summary>
    void BindBufferRange(uint target, uint index, uint buffer, nint offset, nuint size);

    /// <summary><c>glBufferData</c> with no initial contents.</summary>
    void BufferData(uint target, nuint size, uint usage);

    /// <summary><c>glBufferSubData</c>.</summary>
    void BufferSubData(uint target, nint offset, ReadOnlySpan<byte> data);

    /// <summary><c>glCopyBufferSubData</c>.</summary>
    void CopyBufferSubData(uint readTarget, uint writeTarget, nint readOffset, nint writeOffset, nuint size);

    /// <summary><c>glGetBufferSubData</c>, or the profile's stand-in for it.</summary>
    /// <remarks>
    ///     Not one entry point on every profile: desktop GL has <c>glGetBufferSubData</c>, GLES has
    ///     <c>glMapBufferRange</c> plus a copy, and WebGL2 has <c>getBufferSubData</c> on the JS
    ///     side. One method here because the backend's question is the same in all three —
    ///     "give me these bytes" — and picking the mechanism is the binding's business.
    /// </remarks>
    void GetBufferSubData(uint target, nint offset, Span<byte> destination);

    // ── Textures ────────────────────────────────────────────────────────────────────────────

    /// <summary><c>glGenTextures</c>, for one.</summary>
    uint GenTexture();

    /// <summary><c>glDeleteTextures</c>, for one.</summary>
    void DeleteTexture(uint texture);

    /// <summary><c>glBindTexture</c>.</summary>
    void BindTexture(uint target, uint texture);

    /// <summary><c>glActiveTexture</c>, taking a unit index rather than <c>GL_TEXTURE0 + n</c>.</summary>
    void ActiveTexture(uint unit);

    /// <summary><c>glTexStorage2D</c>.</summary>
    void TexStorage2D(uint target, int levels, uint internalFormat, int width, int height);

    /// <summary><c>glTexStorage3D</c>.</summary>
    void TexStorage3D(uint target, int levels, uint internalFormat, int width, int height, int depth);

    /// <summary><c>glTexStorage2DMultisample</c>.</summary>
    void TexStorage2DMultisample(uint target, int samples, uint internalFormat, int width, int height);

    /// <summary><c>glTexSubImage2D</c>, reading from whatever is bound to <c>GL_PIXEL_UNPACK_BUFFER</c>.</summary>
    void TexSubImage2D(
        uint target,
        int level,
        int x,
        int y,
        int width,
        int height,
        uint format,
        uint type,
        nint bufferOffset
    );

    /// <summary><c>glTexSubImage3D</c>, reading from whatever is bound to <c>GL_PIXEL_UNPACK_BUFFER</c>.</summary>
    void TexSubImage3D(
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
    );

    /// <summary><c>glTexParameteri</c>.</summary>
    void TexParameter(uint target, uint parameter, int value);

    /// <summary><c>glGenerateMipmap</c>.</summary>
    void GenerateMipmap(uint target);

    /// <summary><c>glCopyImageSubData</c>.</summary>
    /// <remarks>
    ///     GL 4.3 and GLES 3.2. Below that a texture-to-texture copy is a blit through a framebuffer
    ///     pair, which is what <see cref="BlitFramebuffer" /> is also here for.
    /// </remarks>
    void CopyImageSubData(
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
    );

    // ── Samplers ────────────────────────────────────────────────────────────────────────────

    /// <summary><c>glGenSamplers</c>, for one.</summary>
    uint GenSampler();

    /// <summary><c>glDeleteSamplers</c>, for one.</summary>
    void DeleteSampler(uint sampler);

    /// <summary><c>glBindSampler</c>.</summary>
    void BindSampler(uint unit, uint sampler);

    /// <summary><c>glSamplerParameteri</c>.</summary>
    void SamplerParameter(uint sampler, uint parameter, int value);

    /// <summary><c>glSamplerParameterf</c>.</summary>
    void SamplerParameter(uint sampler, uint parameter, float value);

    /// <summary><c>glSamplerParameterfv</c>, for the four-component border colour.</summary>
    void SamplerParameter(uint sampler, uint parameter, ReadOnlySpan<float> values);

    // ── Framebuffers ────────────────────────────────────────────────────────────────────────

    /// <summary><c>glGenFramebuffers</c>, for one.</summary>
    uint GenFramebuffer();

    /// <summary><c>glDeleteFramebuffers</c>, for one.</summary>
    void DeleteFramebuffer(uint framebuffer);

    /// <summary><c>glBindFramebuffer</c>.</summary>
    void BindFramebuffer(uint target, uint framebuffer);

    /// <summary><c>glFramebufferTexture2D</c>.</summary>
    void FramebufferTexture2D(uint target, uint attachment, uint textureTarget, uint texture, int level);

    /// <summary><c>glFramebufferTextureLayer</c>.</summary>
    void FramebufferTextureLayer(uint target, uint attachment, uint texture, int level, int layer);

    /// <summary><c>glCheckFramebufferStatus</c>.</summary>
    uint CheckFramebufferStatus(uint target);

    /// <summary><c>glDrawBuffers</c>.</summary>
    void DrawBuffers(ReadOnlySpan<uint> attachments);

    /// <summary><c>glReadBuffer</c>.</summary>
    void ReadBuffer(uint attachment);

    /// <summary><c>glInvalidateFramebuffer</c>, which is how <see cref="StoreAction.DontCare" /> is
    /// said.</summary>
    /// <remarks>
    ///     Not decoration on a tiled GPU: it is the difference between the driver writing a
    ///     4K attachment back to main memory at the end of every pass and not.
    /// </remarks>
    void InvalidateFramebuffer(uint target, ReadOnlySpan<uint> attachments);

    /// <summary><c>glBlitFramebuffer</c>.</summary>
    void BlitFramebuffer(
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
    );

    /// <summary><c>glReadPixels</c> into whatever is bound to <c>GL_PIXEL_PACK_BUFFER</c>.</summary>
    void ReadPixels(int x, int y, int width, int height, uint format, uint type, nint bufferOffset);

    // ── Clearing ────────────────────────────────────────────────────────────────────────────

    /// <summary><c>glClearBufferfv</c>.</summary>
    void ClearBuffer(uint buffer, int drawBuffer, ReadOnlySpan<float> value);

    /// <summary><c>glClearBufferiv</c>.</summary>
    void ClearBuffer(uint buffer, int drawBuffer, ReadOnlySpan<int> value);

    /// <summary><c>glClearBufferfi</c>, the only way to clear packed depth-stencil.</summary>
    void ClearBufferDepthStencil(int drawBuffer, float depth, int stencil);

    // ── Vertex arrays ───────────────────────────────────────────────────────────────────────

    /// <summary><c>glGenVertexArrays</c>, for one.</summary>
    uint GenVertexArray();

    /// <summary><c>glDeleteVertexArrays</c>, for one.</summary>
    void DeleteVertexArray(uint array);

    /// <summary><c>glBindVertexArray</c>.</summary>
    void BindVertexArray(uint array);

    /// <summary><c>glEnableVertexAttribArray</c>.</summary>
    void EnableVertexAttribArray(uint index);

    /// <summary><c>glDisableVertexAttribArray</c>.</summary>
    void DisableVertexAttribArray(uint index);

    /// <summary><c>glVertexAttribPointer</c>.</summary>
    void VertexAttribPointer(
        uint index,
        int size,
        uint type,
        bool normalised,
        int stride,
        nint offset
    );

    /// <summary><c>glVertexAttribIPointer</c>.</summary>
    void VertexAttribIPointer(uint index, int size, uint type, int stride, nint offset);

    /// <summary><c>glVertexAttribDivisor</c>.</summary>
    void VertexAttribDivisor(uint index, uint divisor);

    // ── Programs ────────────────────────────────────────────────────────────────────────────

    /// <summary><c>glCreateShader</c>.</summary>
    uint CreateShader(uint type);

    /// <summary><c>glShaderSource</c> plus <c>glCompileShader</c>, reporting the log on failure.</summary>
    /// <returns><see langword="null" /> on success, or the driver's compile log.</returns>
    string? CompileShader(uint shader, string source);

    /// <summary><c>glDeleteShader</c>.</summary>
    void DeleteShader(uint shader);

    /// <summary><c>glCreateProgram</c>.</summary>
    uint CreateProgram();

    /// <summary><c>glAttachShader</c>.</summary>
    void AttachShader(uint program, uint shader);

    /// <summary><c>glLinkProgram</c>, reporting the log on failure.</summary>
    /// <returns><see langword="null" /> on success, or the driver's link log.</returns>
    string? LinkProgram(uint program);

    /// <summary><c>glDeleteProgram</c>.</summary>
    void DeleteProgram(uint program);

    /// <summary><c>glUseProgram</c>.</summary>
    void UseProgram(uint program);

    /// <summary><c>glGetUniformBlockIndex</c>.</summary>
    uint GetUniformBlockIndex(uint program, string name);

    /// <summary><c>glUniformBlockBinding</c>.</summary>
    void UniformBlockBinding(uint program, uint blockIndex, uint binding);

    /// <summary><c>glGetProgramResourceIndex</c> for a shader storage block.</summary>
    uint GetStorageBlockIndex(uint program, string name);

    /// <summary><c>glShaderStorageBlockBinding</c>.</summary>
    void StorageBlockBinding(uint program, uint blockIndex, uint binding);

    /// <summary><c>glGetUniformLocation</c>.</summary>
    int GetUniformLocation(uint program, string name);

    /// <summary><c>glUniform1i</c>.</summary>
    void Uniform(int location, int value);

    /// <summary><c>glUniform4fv</c> over an array of <c>vec4</c>.</summary>
    /// <remarks>How push constants are delivered — see <see cref="GlPipelineLayout" />.</remarks>
    void Uniform4(int location, ReadOnlySpan<float> values);

    // ── Fixed function ──────────────────────────────────────────────────────────────────────

    /// <summary><c>glEnable</c>.</summary>
    void Enable(uint capability);

    /// <summary><c>glDisable</c>.</summary>
    void Disable(uint capability);

    /// <summary><c>glDepthFunc</c>.</summary>
    void DepthFunc(uint comparison);

    /// <summary><c>glDepthMask</c>.</summary>
    void DepthMask(bool write);

    /// <summary><c>glColorMask</c>.</summary>
    void ColourMask(bool red, bool green, bool blue, bool alpha);

    /// <summary><c>glCullFace</c>.</summary>
    void CullFace(uint face);

    /// <summary><c>glFrontFace</c>.</summary>
    void FrontFace(uint winding);

    /// <summary><c>glPolygonMode</c>. Desktop only.</summary>
    void PolygonMode(uint face, uint mode);

    /// <summary><c>glPolygonOffset</c>.</summary>
    void PolygonOffset(float factor, float units);

    /// <summary><c>glBlendEquationSeparate</c>.</summary>
    void BlendEquationSeparate(uint colour, uint alpha);

    /// <summary><c>glBlendFuncSeparate</c>.</summary>
    void BlendFuncSeparate(
        uint sourceColour,
        uint destinationColour,
        uint sourceAlpha,
        uint destinationAlpha
    );

    /// <summary><c>glBlendColor</c>.</summary>
    void BlendColour(float red, float green, float blue, float alpha);

    /// <summary><c>glStencilFuncSeparate</c>.</summary>
    void StencilFuncSeparate(uint face, uint comparison, int reference, uint mask);

    /// <summary><c>glStencilOpSeparate</c>.</summary>
    void StencilOpSeparate(uint face, uint fail, uint depthFail, uint pass);

    /// <summary><c>glStencilMaskSeparate</c>.</summary>
    void StencilMaskSeparate(uint face, uint mask);

    /// <summary><c>glViewport</c>.</summary>
    void Viewport(int x, int y, int width, int height);

    /// <summary><c>glDepthRangef</c>.</summary>
    void DepthRange(float near, float far);

    /// <summary><c>glScissor</c>.</summary>
    void Scissor(int x, int y, int width, int height);

    /// <summary><c>glClipControl</c>. GL 4.5 only.</summary>
    void ClipControl(uint origin, uint depth);

    // ── Drawing ─────────────────────────────────────────────────────────────────────────────

    /// <summary><c>glDrawArraysInstanced</c>.</summary>
    void DrawArraysInstanced(uint topology, int first, int count, int instanceCount);

    /// <summary><c>glDrawArraysInstancedBaseInstance</c>. GL 4.2 only.</summary>
    void DrawArraysInstancedBaseInstance(
        uint topology,
        int first,
        int count,
        int instanceCount,
        uint baseInstance
    );

    /// <summary><c>glDrawElementsInstancedBaseVertex</c>.</summary>
    void DrawElementsInstancedBaseVertex(
        uint topology,
        int count,
        uint indexType,
        nint offset,
        int instanceCount,
        int baseVertex
    );

    /// <summary><c>glDrawElementsInstancedBaseVertexBaseInstance</c>. GL 4.2 only.</summary>
    void DrawElementsInstancedBaseVertexBaseInstance(
        uint topology,
        int count,
        uint indexType,
        nint offset,
        int instanceCount,
        int baseVertex,
        uint baseInstance
    );

    /// <summary><c>glMultiDrawElementsIndirect</c>, reading from the bound indirect buffer.</summary>
    void MultiDrawElementsIndirect(uint topology, uint indexType, nint offset, int drawCount, int stride);

    /// <summary><c>glDispatchCompute</c>.</summary>
    void DispatchCompute(uint x, uint y, uint z);

    /// <summary><c>glDispatchComputeIndirect</c>.</summary>
    void DispatchComputeIndirect(nint offset);

    /// <summary><c>glMemoryBarrier</c>.</summary>
    void MemoryBarrier(uint bits);

    /// <summary><c>glFinish</c>.</summary>
    void Finish();

    /// <summary><c>glFlush</c>.</summary>
    void Flush();

    // ── Debugging ───────────────────────────────────────────────────────────────────────────

    /// <summary><c>glPushDebugGroup</c>.</summary>
    void PushDebugGroup(string name);

    /// <summary><c>glPopDebugGroup</c>.</summary>
    void PopDebugGroup();

    /// <summary><c>glDebugMessageInsert</c> with a marker severity.</summary>
    void DebugMarker(string name);

    /// <summary><c>glObjectLabel</c>.</summary>
    void ObjectLabel(uint identifier, uint name, string label);

    /// <summary><c>glGetError</c>, drained.</summary>
    /// <returns>The first error since the last call, or <c>0</c>.</returns>
    uint GetError();
}
