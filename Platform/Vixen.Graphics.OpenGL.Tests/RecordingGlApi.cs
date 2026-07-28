// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace Vixen.Graphics.OpenGL.Tests;

/// <summary>One recorded GL call.</summary>
/// <param name="Name">The entry point.</param>
/// <param name="Arguments">Its arguments, in order.</param>
public readonly record struct GlCall(string Name, object?[] Arguments) {
    /// <inheritdoc />
    public override string ToString() =>
        $"{Name}({string.Join(", ", Arguments.Select(argument => Convert.ToString(argument, CultureInfo.InvariantCulture)))})";
}

/// <summary>A GL that records what it was asked to do and does none of it.</summary>
/// <remarks>
///     <para>
///         <b>What makes the abstraction-validator claim checkable on a build agent.</b> ADR-001
///         gives this backend the job of proving the RHI is API-neutral, and the proof is in the
///         translation, not in the driver: whether a pipeline bind sends the right state, whether a
///         descriptor set resolves to the right binding indices, whether a barrier is correctly
///         nothing. All of that is decidable from the call stream, and requiring a GL context to
///         decide it would mean deciding it nowhere.
///     </para>
///     <para>
///         The same shape as <c>Vixen.Graphics.Null</c>'s recorder, one level down. That one asserts
///         what the engine asked the RHI for; this asserts what the RHI asked GL for.
///     </para>
/// </remarks>
public sealed class RecordingGlApi(GlProfile profile = GlProfile.Core45) : IGlApi {
    readonly List<GlCall> calls = [];
    readonly Dictionary<string, int> uniformLocations = [];

    uint next = 1;

    /// <inheritdoc />
    public GlProfile Profile => profile;

    /// <summary>Everything that has been asked for, in order.</summary>
    public IReadOnlyList<GlCall> Calls => calls;

    /// <summary>Uniform and block names the fake should report as absent.</summary>
    /// <remarks>
    ///     A driver strips a uniform nothing reads, and code that assumes every declared name links
    ///     is code that breaks on the first shader with an unused sampler.
    /// </remarks>
    public HashSet<string> Missing { get; } = [];

    /// <summary>What a shader compile should report, or <see langword="null" /> for success.</summary>
    public string? CompileLog { get; set; }

    /// <summary>What a program link should report, or <see langword="null" /> for success.</summary>
    public string? LinkLog { get; set; }

    /// <summary>What the next framebuffer completeness check should say.</summary>
    public uint FramebufferStatus { get; set; } = GlConstants.FramebufferComplete;

    /// <summary>Every call with a given name.</summary>
    public IReadOnlyList<GlCall> Named(string name) =>
        calls.Where(call => call.Name == name).ToList();

    /// <summary>How many calls with a given name were made.</summary>
    public int Count(string name) => calls.Count(call => call.Name == name);

    /// <summary>The only call with a given name, failing if there is not exactly one.</summary>
    public GlCall Single(string name) => calls.Single(call => call.Name == name);

    /// <summary>The names of every call, for an ordering assertion.</summary>
    public IReadOnlyList<string> Names => calls.Select(call => call.Name).ToList();

    /// <summary>Whether one call happened before another.</summary>
    /// <param name="first">The name that should come first.</param>
    /// <param name="second">The name that should come second.</param>
    public bool Precedes(string first, string second) {
        var a = calls.FindIndex(call => call.Name == first);
        var b = calls.FindIndex(call => call.Name == second);
        return a >= 0 && b >= 0 && a < b;
    }

    /// <summary>Throws away the record, keeping the object names already handed out.</summary>
    public void Clear() => calls.Clear();

    /// <inheritdoc />
    public override string ToString() => string.Join(Environment.NewLine, calls);

    // ── Everything below is one line of recording each ──────────────────────────────────────

    /// <inheritdoc />
    public uint GenBuffer() => Name("GenBuffer");

    /// <inheritdoc />
    public void DeleteBuffer(uint buffer) => Record("DeleteBuffer", buffer);

    /// <inheritdoc />
    public void BindBuffer(uint target, uint buffer) => Record("BindBuffer", target, buffer);

    /// <inheritdoc />
    public void BindBufferRange(uint target, uint index, uint buffer, nint offset, nuint size) =>
        Record("BindBufferRange", target, index, buffer, (long)offset, (ulong)size);

    /// <inheritdoc />
    public void BufferData(uint target, nuint size, uint usage) =>
        Record("BufferData", target, (ulong)size, usage);

    /// <inheritdoc />
    public void BufferSubData(uint target, nint offset, ReadOnlySpan<byte> data) =>
        Record("BufferSubData", target, (long)offset, data.Length);

    /// <inheritdoc />
    public void CopyBufferSubData(
        uint readTarget,
        uint writeTarget,
        nint readOffset,
        nint writeOffset,
        nuint size
    ) => Record("CopyBufferSubData", readTarget, writeTarget, (long)readOffset, (long)writeOffset, (ulong)size);

    /// <inheritdoc />
    public void GetBufferSubData(uint target, nint offset, Span<byte> destination) =>
        Record("GetBufferSubData", target, (long)offset, destination.Length);

    /// <inheritdoc />
    public uint GenTexture() => Name("GenTexture");

    /// <inheritdoc />
    public void DeleteTexture(uint texture) => Record("DeleteTexture", texture);

    /// <inheritdoc />
    public void BindTexture(uint target, uint texture) => Record("BindTexture", target, texture);

    /// <inheritdoc />
    public void ActiveTexture(uint unit) => Record("ActiveTexture", unit);

    /// <inheritdoc />
    public void TexStorage2D(uint target, int levels, uint internalFormat, int width, int height) =>
        Record("TexStorage2D", target, levels, internalFormat, width, height);

    /// <inheritdoc />
    public void TexStorage3D(uint target, int levels, uint internalFormat, int width, int height, int depth) =>
        Record("TexStorage3D", target, levels, internalFormat, width, height, depth);

    /// <inheritdoc />
    public void TexStorage2DMultisample(uint target, int samples, uint internalFormat, int width, int height) =>
        Record("TexStorage2DMultisample", target, samples, internalFormat, width, height);

    /// <inheritdoc />
    public void TexSubImage2D(
        uint target,
        int level,
        int x,
        int y,
        int width,
        int height,
        uint format,
        uint type,
        nint bufferOffset
    ) => Record("TexSubImage2D", target, level, x, y, width, height, format, type, (long)bufferOffset);

    /// <inheritdoc />
    public void TexSubImage3D(
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
    ) => Record("TexSubImage3D", target, level, x, y, z, width, height, depth, format, type, (long)bufferOffset);

    /// <inheritdoc />
    public void TexParameter(uint target, uint parameter, int value) =>
        Record("TexParameter", target, parameter, value);

    /// <inheritdoc />
    public void GenerateMipmap(uint target) => Record("GenerateMipmap", target);

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
    ) => Record(
        "CopyImageSubData",
        sourceName,
        sourceTarget,
        sourceLevel,
        sourceX,
        sourceY,
        sourceZ,
        destinationName,
        destinationTarget,
        destinationLevel,
        destinationX,
        destinationY,
        destinationZ,
        width,
        height,
        depth
    );

    /// <inheritdoc />
    public uint GenSampler() => Name("GenSampler");

    /// <inheritdoc />
    public void DeleteSampler(uint sampler) => Record("DeleteSampler", sampler);

    /// <inheritdoc />
    public void BindSampler(uint unit, uint sampler) => Record("BindSampler", unit, sampler);

    /// <inheritdoc />
    public void SamplerParameter(uint sampler, uint parameter, int value) =>
        Record("SamplerParameterI", sampler, parameter, value);

    /// <inheritdoc />
    public void SamplerParameter(uint sampler, uint parameter, float value) =>
        Record("SamplerParameterF", sampler, parameter, value);

    /// <inheritdoc />
    public void SamplerParameter(uint sampler, uint parameter, ReadOnlySpan<float> values) =>
        Record("SamplerParameterV", sampler, parameter, values.Length);

    /// <inheritdoc />
    public uint GenFramebuffer() => Name("GenFramebuffer");

    /// <inheritdoc />
    public void DeleteFramebuffer(uint framebuffer) => Record("DeleteFramebuffer", framebuffer);

    /// <inheritdoc />
    public void BindFramebuffer(uint target, uint framebuffer) =>
        Record("BindFramebuffer", target, framebuffer);

    /// <inheritdoc />
    public void FramebufferTexture2D(
        uint target,
        uint attachment,
        uint textureTarget,
        uint texture,
        int level
    ) => Record("FramebufferTexture2D", target, attachment, textureTarget, texture, level);

    /// <inheritdoc />
    public void FramebufferTextureLayer(uint target, uint attachment, uint texture, int level, int layer) =>
        Record("FramebufferTextureLayer", target, attachment, texture, level, layer);

    /// <inheritdoc />
    public uint CheckFramebufferStatus(uint target) {
        Record("CheckFramebufferStatus", target);
        return FramebufferStatus;
    }

    /// <inheritdoc />
    public void DrawBuffers(ReadOnlySpan<uint> attachments) =>
        Record("DrawBuffers", attachments.ToArray().Cast<object?>().ToArray());

    /// <inheritdoc />
    public void ReadBuffer(uint attachment) => Record("ReadBuffer", attachment);

    /// <inheritdoc />
    public void InvalidateFramebuffer(uint target, ReadOnlySpan<uint> attachments) =>
        Record("InvalidateFramebuffer", [target, .. attachments.ToArray().Cast<object?>()]);

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
    ) => Record(
        "BlitFramebuffer",
        sourceX0,
        sourceY0,
        sourceX1,
        sourceY1,
        destinationX0,
        destinationY0,
        destinationX1,
        destinationY1,
        mask,
        filter
    );

    /// <inheritdoc />
    public void ReadPixels(int x, int y, int width, int height, uint format, uint type, nint bufferOffset) =>
        Record("ReadPixels", x, y, width, height, format, type, (long)bufferOffset);

    /// <inheritdoc />
    public void ClearBuffer(uint buffer, int drawBuffer, ReadOnlySpan<float> value) =>
        Record("ClearBufferF", [buffer, drawBuffer, .. value.ToArray().Cast<object?>()]);

    /// <inheritdoc />
    public void ClearBuffer(uint buffer, int drawBuffer, ReadOnlySpan<int> value) =>
        Record("ClearBufferI", [buffer, drawBuffer, .. value.ToArray().Cast<object?>()]);

    /// <inheritdoc />
    public void ClearBufferDepthStencil(int drawBuffer, float depth, int stencil) =>
        Record("ClearBufferDepthStencil", drawBuffer, depth, stencil);

    /// <inheritdoc />
    public uint GenVertexArray() => Name("GenVertexArray");

    /// <inheritdoc />
    public void DeleteVertexArray(uint array) => Record("DeleteVertexArray", array);

    /// <inheritdoc />
    public void BindVertexArray(uint array) => Record("BindVertexArray", array);

    /// <inheritdoc />
    public void EnableVertexAttribArray(uint index) => Record("EnableVertexAttribArray", index);

    /// <inheritdoc />
    public void DisableVertexAttribArray(uint index) => Record("DisableVertexAttribArray", index);

    /// <inheritdoc />
    public void VertexAttribPointer(
        uint index,
        int size,
        uint type,
        bool normalised,
        int stride,
        nint offset
    ) => Record("VertexAttribPointer", index, size, type, normalised, stride, (long)offset);

    /// <inheritdoc />
    public void VertexAttribIPointer(uint index, int size, uint type, int stride, nint offset) =>
        Record("VertexAttribIPointer", index, size, type, stride, (long)offset);

    /// <inheritdoc />
    public void VertexAttribDivisor(uint index, uint divisor) =>
        Record("VertexAttribDivisor", index, divisor);

    /// <inheritdoc />
    public uint CreateShader(uint type) {
        Record("CreateShader", type);
        return next++;
    }

    /// <inheritdoc />
    public string? CompileShader(uint shader, string source) {
        Record("CompileShader", shader, source);
        return CompileLog;
    }

    /// <inheritdoc />
    public void DeleteShader(uint shader) => Record("DeleteShader", shader);

    /// <inheritdoc />
    public uint CreateProgram() => Name("CreateProgram");

    /// <inheritdoc />
    public void AttachShader(uint program, uint shader) => Record("AttachShader", program, shader);

    /// <inheritdoc />
    public string? LinkProgram(uint program) {
        Record("LinkProgram", program);
        return LinkLog;
    }

    /// <inheritdoc />
    public void DeleteProgram(uint program) => Record("DeleteProgram", program);

    /// <inheritdoc />
    public void UseProgram(uint program) => Record("UseProgram", program);

    /// <inheritdoc />
    public uint GetUniformBlockIndex(uint program, string name) {
        Record("GetUniformBlockIndex", program, name);
        return Missing.Contains(name) ? uint.MaxValue : Location(name);
    }

    /// <inheritdoc />
    public void UniformBlockBinding(uint program, uint blockIndex, uint binding) =>
        Record("UniformBlockBinding", program, blockIndex, binding);

    /// <inheritdoc />
    public uint GetStorageBlockIndex(uint program, string name) {
        Record("GetStorageBlockIndex", program, name);
        return Missing.Contains(name) ? uint.MaxValue : Location(name);
    }

    /// <inheritdoc />
    public void StorageBlockBinding(uint program, uint blockIndex, uint binding) =>
        Record("StorageBlockBinding", program, blockIndex, binding);

    /// <inheritdoc />
    public int GetUniformLocation(uint program, string name) {
        Record("GetUniformLocation", program, name);
        return Missing.Contains(name) ? -1 : (int)Location(name);
    }

    /// <inheritdoc />
    public void Uniform(int location, int value) => Record("Uniform1i", location, value);

    /// <inheritdoc />
    public void Uniform4(int location, ReadOnlySpan<float> values) =>
        Record("Uniform4fv", [location, .. values.ToArray().Cast<object?>()]);

    /// <inheritdoc />
    public void Enable(uint capability) => Record("Enable", capability);

    /// <inheritdoc />
    public void Disable(uint capability) => Record("Disable", capability);

    /// <inheritdoc />
    public void DepthFunc(uint comparison) => Record("DepthFunc", comparison);

    /// <inheritdoc />
    public void DepthMask(bool write) => Record("DepthMask", write);

    /// <inheritdoc />
    public void ColourMask(bool red, bool green, bool blue, bool alpha) =>
        Record("ColorMask", red, green, blue, alpha);

    /// <inheritdoc />
    public void CullFace(uint face) => Record("CullFace", face);

    /// <inheritdoc />
    public void FrontFace(uint winding) => Record("FrontFace", winding);

    /// <inheritdoc />
    public void PolygonMode(uint face, uint mode) => Record("PolygonMode", face, mode);

    /// <inheritdoc />
    public void PolygonOffset(float factor, float units) => Record("PolygonOffset", factor, units);

    /// <inheritdoc />
    public void BlendEquationSeparate(uint colour, uint alpha) =>
        Record("BlendEquationSeparate", colour, alpha);

    /// <inheritdoc />
    public void BlendFuncSeparate(
        uint sourceColour,
        uint destinationColour,
        uint sourceAlpha,
        uint destinationAlpha
    ) => Record("BlendFuncSeparate", sourceColour, destinationColour, sourceAlpha, destinationAlpha);

    /// <inheritdoc />
    public void BlendColour(float red, float green, float blue, float alpha) =>
        Record("BlendColor", red, green, blue, alpha);

    /// <inheritdoc />
    public void StencilFuncSeparate(uint face, uint comparison, int reference, uint mask) =>
        Record("StencilFuncSeparate", face, comparison, reference, mask);

    /// <inheritdoc />
    public void StencilOpSeparate(uint face, uint fail, uint depthFail, uint pass) =>
        Record("StencilOpSeparate", face, fail, depthFail, pass);

    /// <inheritdoc />
    public void StencilMaskSeparate(uint face, uint mask) => Record("StencilMaskSeparate", face, mask);

    /// <inheritdoc />
    public void Viewport(int x, int y, int width, int height) => Record("Viewport", x, y, width, height);

    /// <inheritdoc />
    public void DepthRange(float near, float far) => Record("DepthRange", near, far);

    /// <inheritdoc />
    public void Scissor(int x, int y, int width, int height) => Record("Scissor", x, y, width, height);

    /// <inheritdoc />
    public void ClipControl(uint origin, uint depth) => Record("ClipControl", origin, depth);

    /// <inheritdoc />
    public void DrawArraysInstanced(uint topology, int first, int count, int instanceCount) =>
        Record("DrawArraysInstanced", topology, first, count, instanceCount);

    /// <inheritdoc />
    public void DrawArraysInstancedBaseInstance(
        uint topology,
        int first,
        int count,
        int instanceCount,
        uint baseInstance
    ) => Record("DrawArraysInstancedBaseInstance", topology, first, count, instanceCount, baseInstance);

    /// <inheritdoc />
    public void DrawElementsInstancedBaseVertex(
        uint topology,
        int count,
        uint indexType,
        nint offset,
        int instanceCount,
        int baseVertex
    ) => Record(
        "DrawElementsInstancedBaseVertex",
        topology,
        count,
        indexType,
        (long)offset,
        instanceCount,
        baseVertex
    );

    /// <inheritdoc />
    public void DrawElementsInstancedBaseVertexBaseInstance(
        uint topology,
        int count,
        uint indexType,
        nint offset,
        int instanceCount,
        int baseVertex,
        uint baseInstance
    ) => Record(
        "DrawElementsInstancedBaseVertexBaseInstance",
        topology,
        count,
        indexType,
        (long)offset,
        instanceCount,
        baseVertex,
        baseInstance
    );

    /// <inheritdoc />
    public void MultiDrawElementsIndirect(
        uint topology,
        uint indexType,
        nint offset,
        int drawCount,
        int stride
    ) => Record("MultiDrawElementsIndirect", topology, indexType, (long)offset, drawCount, stride);

    /// <inheritdoc />
    public void DispatchCompute(uint x, uint y, uint z) => Record("DispatchCompute", x, y, z);

    /// <inheritdoc />
    public void DispatchComputeIndirect(nint offset) => Record("DispatchComputeIndirect", (long)offset);

    /// <inheritdoc />
    public void MemoryBarrier(uint bits) => Record("MemoryBarrier", bits);

    /// <inheritdoc />
    public void Finish() => Record("Finish");

    /// <inheritdoc />
    public void Flush() => Record("Flush");

    /// <inheritdoc />
    public void PushDebugGroup(string name) => Record("PushDebugGroup", name);

    /// <inheritdoc />
    public void PopDebugGroup() => Record("PopDebugGroup");

    /// <inheritdoc />
    public void DebugMarker(string name) => Record("DebugMarker", name);

    /// <inheritdoc />
    public void ObjectLabel(uint identifier, uint name, string label) =>
        Record("ObjectLabel", identifier, name, label);

    /// <inheritdoc />
    public uint GetError() => 0;

    uint Name(string call) {
        var name = next++;
        Record(call, name);
        return name;
    }

    uint Location(string name) {
        if (uniformLocations.TryGetValue(name, out var existing)) {
            return (uint)existing;
        }

        var location = uniformLocations.Count + 100;
        uniformLocations[name] = location;
        return (uint)location;
    }

    void Record(string name, params object?[] arguments) => calls.Add(new(name, arguments));
}
