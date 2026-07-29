// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0


namespace Vixen.Raven.Symbols;

/// <summary>Which pipeline-supplied value a stage parameter carries.</summary>
public enum StageBuiltIn {
    /// <summary>Not a built-in: an ordinary located input.</summary>
    None,

    /// <summary>
    ///     This invocation's position in the whole dispatch — the workgroup index times the
    ///     workgroup size, plus the position within the group. The one nearly every compute
    ///     shader wants.
    /// </summary>
    DispatchThreadId,

    /// <summary>Which workgroup this invocation is in.</summary>
    GroupId,

    /// <summary>This invocation's position within its own workgroup.</summary>
    GroupThreadId,

    /// <summary>
    ///     This invocation's position within its workgroup, flattened to a single index — the
    ///     scalar form of <see cref="GroupThreadId" />, and what indexes shared storage.
    /// </summary>
    GroupIndex,

    /// <summary>
    ///     Which vertex is being shaded, counting from the draw's first index.
    /// </summary>
    /// <remarks>
    ///     What a shader with no vertex buffer at all is built on: a full-screen triangle derives
    ///     its position and UV from this and binds nothing, which is why every post-process pass
    ///     wants it.
    /// </remarks>
    VertexId,

    /// <summary>Which instance is being drawn, counting from the draw's first instance.</summary>
    InstanceId,

    /// <summary>
    ///     Whether the triangle this fragment came from faces the viewer.
    /// </summary>
    /// <remarks>
    ///     What a two-sided pipeline is shaded with: the inside of an open shape — a plane seen
    ///     from below, a cone with the camera inside it — arrives with its normal pointing away,
    ///     and flipping it there is the difference between a surface you can judge the shape of and
    ///     one that is flat black. There is nothing a shader could compute this from; the
    ///     rasterizer is the only thing that knows which winding it saw.
    /// </remarks>
    IsFrontFace
}

/// <summary>
///     One pipeline-supplied value, and how it is spelled everywhere it appears.
/// </summary>
/// <param name="Semantic">The HLSL-style name a <c>[Semantic("…")]</c> carries.</param>
/// <param name="BuiltIn">Which value it is.</param>
/// <param name="Stage">The stage that supplies it.</param>
/// <param name="Type">
///     The type the parameter has to be declared as. Refused rather than converted when it does not
///     match, so a conversion nobody wrote never appears between the built-in and its first use.
/// </param>
/// <param name="GlslName">
///     The GLSL built-in variable. The SPIR-V spelling is <c>SpirvBuiltIns.Of</c>'s, deliberately
///     kept there rather than here: neither target's name should be the one the other is derived
///     from, and both read <see cref="StageBuiltIn" />, so one added in either place cannot be
///     silently missing from the other.
/// </param>
public sealed record StageBuiltInInfo(
    string Semantic,
    StageBuiltIn BuiltIn,
    ShaderStage Stage,
    PrimitiveTypeSymbol Type,
    string GlslName
);

/// <summary>
///     The values a stage receives from the pipeline rather than from a vertex buffer or an earlier
///     stage.
/// </summary>
/// <remarks>
///     <para>
///         One table, shared by the binder, both backends and the reflection, for the reason
///         <c>BindingPlan</c> and <c>StreamPlan</c> exist: a built-in's name, its type and its
///         spelling in each target are one decision, and four copies of it would be four chances to
///         disagree. The binder rejects an unknown compute semantic here, so a backend never has to
///         decide what to do with one.
///     </para>
///     <para>
///         Reached through the <c>[Semantic("…")]</c> attribute that already carries
///         <c>SV_Position</c> and <c>SV_Target</c> — a built-in is the same mechanism rather than a
///         new one.
///     </para>
///     <para>
///         <strong>The stage is part of the identity.</strong> A vertex stage's parameters are
///         mostly attributes the host feeds and a compute stage's are all built-ins, so the same
///         name means different things about where a location comes from; keying on
///         (semantic, stage) is what lets the vertex table stay open — an unrecognised semantic
///         there is <c>POSITION</c> or <c>TEXCOORD0</c>, an ordinary attribute — while the compute
///         one stays closed.
///     </para>
/// </remarks>
public static class StageBuiltIns {
    static readonly StageBuiltInInfo[] Known = [
        new(
            "SV_DispatchThreadID",
            StageBuiltIn.DispatchThreadId,
            ShaderStage.Compute,
            BuiltInTypes.UInt3,
            "gl_GlobalInvocationID"
        ),
        new("SV_GroupID", StageBuiltIn.GroupId, ShaderStage.Compute, BuiltInTypes.UInt3, "gl_WorkGroupID"),
        new(
            "SV_GroupThreadID",
            StageBuiltIn.GroupThreadId,
            ShaderStage.Compute,
            BuiltInTypes.UInt3,
            "gl_LocalInvocationID"
        ),
        new(
            "SV_GroupIndex",
            StageBuiltIn.GroupIndex,
            ShaderStage.Compute,
            BuiltInTypes.UInt,
            "gl_LocalInvocationIndex"
        ),

        // Signed, unlike the dispatch ids and unlike HLSL, because that is what both targets give:
        // GLSL declares `in int gl_VertexIndex` and SPIR-V's VertexIndex is a signed 32-bit
        // integer under Vulkan. Declaring `uint` would put a conversion nobody wrote in front of
        // every use.
        new("SV_VertexID", StageBuiltIn.VertexId, ShaderStage.Vertex, BuiltInTypes.Int, "gl_VertexIndex"),
        new("SV_InstanceID", StageBuiltIn.InstanceId, ShaderStage.Vertex, BuiltInTypes.Int, "gl_InstanceIndex"),

        // The one built-in whose type is `bool`, and the one place a boolean reaches a stage
        // interface at all: `RVN2103` keeps a stream out of one because Vulkan has no boolean
        // interface *type*, and this is not one — `gl_FrontFacing` and SPIR-V's `FrontFacing` are
        // both declared bool by the target itself, so there is nothing here for a host to lay out.
        new("SV_IsFrontFace", StageBuiltIn.IsFrontFace, ShaderStage.Fragment, BuiltInTypes.Bool, "gl_FrontFacing")
    ];

    static readonly Dictionary<(string Semantic, ShaderStage Stage), StageBuiltInInfo> Table =
        Known.ToDictionary(
            b => (b.Semantic, b.Stage),
            EqualityComparer<(string, ShaderStage)>.Create(
                (a, b) => a.Item2 == b.Item2 && string.Equals(a.Item1, b.Item1, StringComparison.OrdinalIgnoreCase),
                v => HashCode.Combine(v.Item1.ToLowerInvariant(), v.Item2)
            )
        );

    /// <summary>
    ///     The recognised semantics for a stage, in the order a diagnostic should list them.
    /// </summary>
    /// <remarks>
    ///     Built from the ordered array rather than the dictionary's keys, because a
    ///     <see cref="Dictionary{K,V}" />'s enumeration order is an implementation detail and this
    ///     ends up in the text of <c>RVN2107</c> and <c>RVN2108</c> — a diagnostic message that
    ///     varies between runs is a golden test that fails for no reason.
    /// </remarks>
    public static string Names(ShaderStage stage) =>
        string.Join(", ", Known.Where(b => b.Stage == stage).Select(b => b.Semantic));

    /// <summary>What a semantic names on this stage, or null when it names no built-in.</summary>
    public static StageBuiltInInfo? Of(string? semantic, ShaderStage stage) =>
        semantic is not null && Table.TryGetValue((semantic, stage), out var found) ? found : null;
}
