// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Silk.NET.Core.Contexts;
using Silk.NET.OpenGLES;

namespace Vixen.Graphics.OpenGL;

/// <summary>The GL entry points, over <c>Silk.NET.OpenGLES</c>.</summary>
/// <remarks>
///     <para>
///         <b>The second binding, and the reason there are two.</b> <c>libGL</c> and
///         <c>libGLESv2</c> are different libraries with different entry-point tables, and
///         <c>Silk.NET.OpenGL</c> and <c>Silk.NET.OpenGLES</c> are the two generated bindings that
///         say so — the type named <c>GL</c> in each is a distinct class over a distinct symbol set.
///         A single binding covering both would have to declare entry points that do not exist on
///         one of them, which is exactly the mistake this file exists to avoid making silently.
///     </para>
///     <para>
///         Everything above <see cref="IGlApi" /> is unchanged, which is the claim the seam was built
///         to make good on: <see cref="GlDevice" />, <see cref="GlStateCache" />,
///         <see cref="GlBindingPlan" /> and <see cref="GlslTranslator" /> already differ by
///         <see cref="GlProfile" />, and none of them names a binding type. Adding GLES is this file
///         and the context that loads it, and nothing else.
///     </para>
///     <para>
///         <b>Three profiles use it, not two.</b> <see cref="GlProfile.Es30" /> and
///         <see cref="GlProfile.Es32" /> reach it through <see cref="EglContext" />;
///         <see cref="GlProfile.WebGl2" /> reaches it through a browser context, which is the same
///         binding over Emscripten's GL layer (<c>docs/plan/05</c> § Web). Where the three differ is
///         inside the methods, and every difference is named in the remarks on the method rather
///         than branched on here.
///     </para>
///     <para>
///         <b>Three methods here are stand-ins rather than transcription</b>, and each is a place
///         GLES has no entry point but does have an answer:
///     </para>
///     <list type="bullet">
///         <item>
///             <see cref="GetBufferSubData" /> — GLES has no <c>glGetBufferSubData</c> at any
///             version. It maps the range, copies and unmaps, which is what the
///             <see cref="IGlApi" /> contract asks for: "give me these bytes", by whatever mechanism
///             the profile has.
///         </item>
///         <item>
///             <see cref="MultiDrawElementsIndirect" /> — GLES 3.1 has <c>glDrawElementsIndirect</c>
///             for one draw and no multi-draw at all, so this loops. That is why
///             <c>GraphicsDeviceFeatures.HasMultiDrawIndirect</c> is false on every GLES profile:
///             the calls happen, and they happen one at a time.
///         </item>
///         <item>
///             <see cref="StorageBlockBinding" /> — GLES has no
///             <c>glShaderStorageBlockBinding</c>. A storage block's binding has to come from
///             <c>layout(binding = …)</c> in the source, which is what
///             <see cref="GlslTranslator" /> emits on every profile that has storage buffers at
///             all — <see cref="GlProfiles.HasStorageBuffers" /> and
///             <see cref="GlProfiles.HasExplicitBindings" /> are true together and start at the same
///             profile. So there is nothing left to assign after the link, and this is a no-op
///             rather than a refusal.
///         </item>
///     </list>
///     <para>
///         <b>Four more throw</b>, and none of them is reachable from a device that asked its
///         profile first: <see cref="ClipControl" />, <see cref="PolygonMode" /> and the two
///         base-instance draws. <see cref="GlDevice" /> gates them on
///         <see cref="GlProfiles.HasClipControl" />, <see cref="GlProfiles.HasWireframe" /> and
///         <see cref="GlProfiles.HasBaseInstance" /> respectively, all of which are desktop-only —
///         so reaching one of these means a gate was removed, and a throw naming the entry point is
///         a better report of that than a silent no-op that draws the wrong thing.
///     </para>
///     <para>
///         <b>Not exercised by the test suite, deliberately and visibly</b> — the same position
///         <see cref="SilkGlApi" /> takes, for the same reason. What it needs is a driver, which CI
///         provides on the Mesa leg (<c>docs/plan/05</c> § Cross-backend equivalence) and an Android
///         device provides for the rest.
///     </para>
/// </remarks>
public sealed class SilkGlesApi : IGlApi, IDisposable {
    readonly GL gl;
    readonly bool owned;

    /// <summary>Wraps entry points somebody else loaded.</summary>
    /// <param name="gl">The loaded GLES.</param>
    /// <param name="profile">Which dialect the context was created for.</param>
    public SilkGlesApi(GL gl, GlProfile profile) {
        this.gl = gl ?? throw new ArgumentNullException(nameof(gl));
        Profile = Checked(profile);
        owned = false;
    }

    /// <summary>Loads the entry points from a context.</summary>
    /// <param name="context">The current context — <see cref="EglContext" />, or a windowing layer's.</param>
    /// <param name="profile">Which dialect it was created for.</param>
    public SilkGlesApi(IGLContext context, GlProfile profile) {
        ArgumentNullException.ThrowIfNull(context);
        gl = GL.GetApi(context);
        Profile = Checked(profile);
        owned = true;
    }

    /// <summary>Loads the entry points from a function that resolves them by name.</summary>
    /// <param name="getProcAddress">What turns <c>glDrawArrays</c> into an address.</param>
    /// <param name="profile">Which dialect the current context was created for.</param>
    /// <returns>The entry points.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="getProcAddress" /> is null.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The overload a platform can reach, and its absence is why nothing outside this
    ///         assembly's own tests ever built a GLES binding.</b> <see cref="SilkGlApi" /> has had
    ///         this since the desktop path was wired and this did not, so a windowing layer holding
    ///         an <c>IGlContext</c> — which names no Silk type, deliberately, because
    ///         <c>Vixen.Platform</c> is a Core assembly — had exactly one binding it could
    ///         construct, and it was the desktop one. An embedded context loaded through it gets
    ///         <c>libGL</c>'s table, which on a phone is not a wrong version of the right library
    ///         but a library that is not installed.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A context has to be current on this thread already</b>, for the reason
    ///         <see cref="SilkGlApi.FromProcAddress" /> states: a table resolved with none current
    ///         is a table of nulls that fails at the first draw rather than here.
    ///     </para>
    /// </remarks>
    public static SilkGlesApi FromProcAddress(Func<string, nint> getProcAddress, GlProfile profile) {
        ArgumentNullException.ThrowIfNull(getProcAddress);

        // Owned, as in SilkGlApi: the GL this builds wraps a context loaded here, so nothing else
        // can dispose it.
        return new(GL.GetApi(getProcAddress), profile, owned: true);
    }

    SilkGlesApi(GL gl, GlProfile profile, bool owned) {
        this.gl = gl;
        Profile = Checked(profile);
        this.owned = owned;
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
    /// <remarks>
    ///     <para>
    ///         Mapped, copied and unmapped, because GLES has no <c>glGetBufferSubData</c> at any
    ///         version. <c>GL_MAP_READ_BIT</c> alone: asking for the write bit as well would let the
    ///         driver believe the range is dirty on unmap and write it back over itself.
    ///     </para>
    ///     <para>
    ///         A failed map returns null and is reported rather than copied from, which is the
    ///         difference between a readback that says it did not happen and a segmentation fault
    ///         inside <c>CopyBlock</c>.
    ///     </para>
    ///     <para>
    ///         <b>WebGL2 has no <c>glMapBufferRange</c> either</b>, which
    ///         <see cref="GlProfiles.HasBufferMapping" /> reports and this cannot work around: the
    ///         browser's own <c>getBufferSubData</c> is on the JS side. Emscripten emulates the map
    ///         under <c>FULL_ES3</c>, so the call lands somewhere — but a browser head that wants a
    ///         readback it can rely on should implement <see cref="IGlApi" />'s one method over the
    ///         JS call rather than through this.
    ///     </para>
    /// </remarks>
    public unsafe void GetBufferSubData(uint target, nint offset, Span<byte> destination) {
        if (destination.IsEmpty) {
            return;
        }

        var mapped = gl.MapBufferRange(
            (GLEnum)target,
            offset,
            (nuint)destination.Length,
            GlConstants.MapReadBit
        );

        if (mapped == null) {
            throw new InvalidOperationException(
                $"A {destination.Length}-byte readback at offset {offset} could not be mapped. GLES has "
                + "no glGetBufferSubData, so a readback here is glMapBufferRange plus a copy, and the "
                + "map is what failed — check that the buffer was created with MemoryAccess.HostReadback."
            );
        }

        try {
            new ReadOnlySpan<byte>(mapped, destination.Length).CopyTo(destination);
        } finally {
            gl.UnmapBuffer((GLEnum)target);
        }
    }

    /// <inheritdoc />
    public uint GenTexture() => gl.GenTexture();

    /// <inheritdoc />
    public void DeleteTexture(uint texture) => gl.DeleteTexture(texture);

    /// <inheritdoc />
    public void BindTexture(uint target, uint texture) => gl.BindTexture((GLEnum)target, texture);

    /// <inheritdoc />
    public void ActiveTexture(uint unit) => gl.ActiveTexture(GLEnum.Texture0 + (int)unit);

    /// <inheritdoc />
    public void BindImageTexture(
        uint unit,
        uint texture,
        int level,
        bool layered,
        int layer,
        uint access,
        uint format
    ) => gl.BindImageTexture(unit, texture, level, layered, layer, (GLEnum)access, (GLEnum)format);

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
    ///     Fixed sample locations, for the reason <see cref="SilkGlApi" /> gives: two attachments in
    ///     one framebuffer whose patterns disagree is a framebuffer that is incomplete for a reason
    ///     nobody reads out of the status code. GLES 3.1 and up only — below that
    ///     <c>GraphicsDeviceFeatures.SupportedSampleCounts</c> is one, so nothing asks.
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
    /// <remarks>
    ///     The border colour, which is <c>EXT_texture_border_clamp</c> on GLES rather than core.
    ///     <see cref="GlProfiles.HasBorderClamp" /> is false on every profile here, so
    ///     <c>GlEnums</c> has already turned a border address mode into clamp-to-edge and nothing
    ///     reaches this — it is transcribed rather than refused because the entry point does exist
    ///     once the extension is present, and a profile that gains it should not have to change this
    ///     file.
    /// </remarks>
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

        // Asked every time and not only in debug, for the reason SilkGlApi gives: a shader that
        // failed to compile still links into a program that binds and draws nothing.
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
    /// <remarks>
    ///     Nothing, and deliberately. GLES has no <c>glShaderStorageBlockBinding</c> — a storage
    ///     block's binding comes from <c>layout(binding = …)</c> in the source and nowhere else. Every
    ///     profile with storage buffers has explicit bindings, so
    ///     <see cref="GlslTranslator" /> has already written it there and
    ///     <see cref="GlProgramCache" /> has nothing left to assign.
    /// </remarks>
    public void StorageBlockBinding(uint program, uint blockIndex, uint binding) { }

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
    /// <remarks>
    ///     Refused. <c>glPolygonMode</c> is in no version of GLES, which is what
    ///     <see cref="GlProfiles.HasWireframe" /> reports and what <see cref="GlStateCache" /> asks
    ///     before it gets here.
    /// </remarks>
    public void PolygonMode(uint face, uint mode) => throw Absent(nameof(PolygonMode), "glPolygonMode");

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
    /// <remarks>
    ///     <c>glDepthRangef</c> underneath — the <c>f</c> suffix is the only spelling GLES has, and
    ///     it is the one Silk binds as <c>DepthRange</c>. Desktop GL's <c>double</c> form takes the
    ///     same two numbers.
    /// </remarks>
    public void DepthRange(float near, float far) => gl.DepthRange(near, far);

    /// <inheritdoc />
    public void Scissor(int x, int y, int width, int height) =>
        gl.Scissor(x, y, (uint)width, (uint)height);

    /// <inheritdoc />
    /// <remarks>
    ///     Refused. <c>glClipControl</c> is GL 4.5 and has no GLES equivalent, which is the whole
    ///     reason <see cref="GlslTranslator" /> flips <c>y</c> and remaps <c>z</c> in the vertex
    ///     shader on every other profile. <see cref="GlDevice" /> asks
    ///     <see cref="GlProfiles.HasClipControl" /> before calling this.
    /// </remarks>
    public void ClipControl(uint origin, uint depth) => throw Absent(nameof(ClipControl), "glClipControl");

    /// <inheritdoc />
    public void DrawArraysInstanced(uint topology, int first, int count, int instanceCount) =>
        gl.DrawArraysInstanced((GLEnum)topology, first, (uint)count, (uint)instanceCount);

    /// <inheritdoc />
    /// <remarks>Refused — see <see cref="GlProfiles.HasBaseInstance" />, which says why at length.</remarks>
    public void DrawArraysInstancedBaseInstance(
        uint topology,
        int first,
        int count,
        int instanceCount,
        uint baseInstance
    ) => throw Absent(nameof(DrawArraysInstancedBaseInstance), "glDrawArraysInstancedBaseInstance");

    /// <inheritdoc />
    /// <remarks>
    ///     Core in GLES 3.2 and <c>EXT_draw_elements_base_vertex</c> below it. The base vertex is
    ///     the half of the pair GLES does have — it is the base <em>instance</em> that exists
    ///     nowhere.
    /// </remarks>
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
    /// <remarks>Refused — see <see cref="GlProfiles.HasBaseInstance" />.</remarks>
    public void DrawElementsInstancedBaseVertexBaseInstance(
        uint topology,
        int count,
        uint indexType,
        nint offset,
        int instanceCount,
        int baseVertex,
        uint baseInstance
    ) => throw Absent(
        nameof(DrawElementsInstancedBaseVertexBaseInstance),
        "glDrawElementsInstancedBaseVertexBaseInstance"
    );

    /// <inheritdoc />
    /// <remarks>
    ///     <para>
    ///         A loop, because GLES 3.1 has <c>glDrawElementsIndirect</c> for exactly one draw and no
    ///         multi-draw at any version. The buffer, the layout of a command inside it and the
    ///         stride between commands are identical, so what desktop GL does in one call this does
    ///         in <c>drawCount</c> of them, walking the offset forward by the stride.
    ///     </para>
    ///     <para>
    ///         A zero stride means tightly packed, which the desktop entry point defines and this has
    ///         to reproduce: five <c>uint</c>s per <c>DrawElementsIndirectCommand</c>, twenty bytes.
    ///         Reading it as literally zero would issue the same draw <c>drawCount</c> times.
    ///     </para>
    ///     <para>
    ///         This is why <c>GraphicsDeviceFeatures.HasMultiDrawIndirect</c> is false on every GLES
    ///         profile. The draws happen; what is absent is the single call, and a renderer that
    ///         batches on the strength of that flag should keep its batches small here.
    ///     </para>
    /// </remarks>
    public unsafe void MultiDrawElementsIndirect(
        uint topology,
        uint indexType,
        nint offset,
        int drawCount,
        int stride
    ) {
        var step = stride > 0 ? stride : IndirectCommandSize;

        for (var draw = 0; draw < drawCount; draw++) {
            gl.DrawElementsIndirect((GLEnum)topology, (GLEnum)indexType, (void*)(offset + (draw * step)));
        }
    }

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

    /// <summary>The size of one <c>DrawElementsIndirectCommand</c>: five <c>uint</c>s.</summary>
    const int IndirectCommandSize = 20;

    /// <summary>Refuses the one profile this binding cannot speak for.</summary>
    /// <remarks>
    ///     <para>
    ///         <c>libGLESv2</c> is not <c>libGL</c>. A <see cref="GlProfile.Core45" /> device over
    ///         this binding would report desktop capabilities — <c>glClipControl</c>, wireframe, a
    ///         base instance — and then reach the methods above that have no entry point to call.
    ///         Construction is the only place that can be said before something draws.
    ///     </para>
    ///     <para>
    ///         <see cref="GlProfile.WebGl2" /> is allowed, and that is not a concession. The browser
    ///         profile runs on this same binding — <c>docs/plan/05</c> § Web, and the spike in
    ///         <c>docs/plan/spikes/web-webgl2</c>, which drove a real WebGL2 context from
    ///         <c>browser-wasm</c> through it. What differs there is the context, which comes from
    ///         Emscripten rather than from EGL, and this class takes one it is handed.
    ///     </para>
    /// </remarks>
    static GlProfile Checked(GlProfile profile) => profile is not GlProfile.Core45
        ? profile
        : throw new ArgumentOutOfRangeException(
            nameof(profile),
            profile,
            "GlProfile.Core45 is desktop GL. Silk.NET.OpenGLES binds libGLESv2, which has none of "
            + "its entry points — use SilkGlApi, which binds libGL, for a desktop context."
        );

    static NotSupportedException Absent(string method, string entryPoint) => new(
        $"{nameof(SilkGlesApi)}.{method} was called, and GLES has no {entryPoint} at any version. The "
        + "profile reports this absent through GraphicsDeviceFeatures and GlDevice asks before "
        + "recording, so reaching this means a gate above it was removed rather than that a driver is "
        + "old."
    );
}
