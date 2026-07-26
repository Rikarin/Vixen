// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Raven.Symbols;

/// <summary>Which of the dispatch built-ins a compute parameter carries.</summary>
public enum ComputeBuiltIn {
    /// <summary>Not a dispatch built-in.</summary>
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
    GroupIndex
}

/// <summary>
///     The dispatch built-ins a compute entry point can take as parameters, and the type each
///     one has.
/// </summary>
/// <remarks>
///     <para>
///         One table, shared by the binder and both backends, for the reason
///         <c>BindingPlan</c> and <c>StreamPlan</c> exist: a built-in's name, its type and its
///         spelling in each target are one decision, and three copies of it would be three
///         chances to disagree. The binder rejects an unknown semantic here, so a backend never
///         has to decide what to do with one.
///     </para>
///     <para>
///         Semantics are the HLSL names, reached through the <c>[Semantic("…")]</c> attribute
///         that already carries <c>SV_Position</c> and <c>SV_Target</c> — a compute built-in is
///         the same mechanism, not a new one. <c>[Semantic]</c> on a parameter already binds
///         (<c>SourceParameterSymbol.SemanticName</c>), so nothing in the front end changes.
///     </para>
/// </remarks>
public static class ComputeBuiltIns {
    /// <summary>The recognised semantics, in the order a diagnostic should list them.</summary>
    /// <remarks>
    ///     An array rather than the dictionary's keys, because a <see cref="Dictionary{K,V}" />'s
    ///     enumeration order is an implementation detail and this ends up in the text of
    ///     <c>RVN2107</c> and <c>RVN2108</c> — a diagnostic message that varies between runs is a
    ///     golden test that fails for no reason.
    /// </remarks>
    static readonly string[] Semantics = [
        "SV_DispatchThreadID", "SV_GroupID", "SV_GroupThreadID", "SV_GroupIndex"
    ];

    /// <summary>The built-in each semantic names.</summary>
    /// <remarks>
    ///     Case-insensitive, matching how <c>[Semantic]</c> is compared elsewhere: HLSL semantics
    ///     are conventionally spelled <c>SV_DispatchThreadID</c> but nobody should lose an
    ///     afternoon to <c>SV_DispatchThreadId</c>.
    /// </remarks>
    static readonly Dictionary<string, ComputeBuiltIn> Table = new(StringComparer.OrdinalIgnoreCase) {
        ["SV_DispatchThreadID"] = ComputeBuiltIn.DispatchThreadId,
        ["SV_GroupID"] = ComputeBuiltIn.GroupId,
        ["SV_GroupThreadID"] = ComputeBuiltIn.GroupThreadId,
        ["SV_GroupIndex"] = ComputeBuiltIn.GroupIndex
    };

    /// <summary>The recognised semantics, for a diagnostic that lists what was expected.</summary>
    public static string Names => string.Join(", ", Semantics);

    /// <summary>The built-in a semantic names, or <see cref="ComputeBuiltIn.None" />.</summary>
    public static ComputeBuiltIn Of(string? semantic) =>
        semantic is not null && Table.TryGetValue(semantic, out var found) ? found : ComputeBuiltIn.None;

    /// <summary>The type a built-in must be declared as.</summary>
    /// <remarks>
    ///     Unsigned in both targets — a dispatch coordinate is a count, and GLSL's
    ///     <c>gl_GlobalInvocationID</c> is a <c>uvec3</c> — so a signed declaration is refused
    ///     rather than silently converted, which would put a conversion nobody wrote between the
    ///     built-in and its first use. The symbol rather than its name, because
    ///     <see cref="BuiltInTypes" /> are singletons and reference equality is then the check.
    /// </remarks>
    public static PrimitiveTypeSymbol TypeOf(ComputeBuiltIn builtIn) =>
        builtIn == ComputeBuiltIn.GroupIndex ? BuiltInTypes.UInt : BuiltInTypes.UInt3;

    /// <summary>The GLSL built-in variable this maps to.</summary>
    public static string GlslName(ComputeBuiltIn builtIn) =>
        builtIn switch {
            ComputeBuiltIn.DispatchThreadId => "gl_GlobalInvocationID",
            ComputeBuiltIn.GroupId => "gl_WorkGroupID",
            ComputeBuiltIn.GroupThreadId => "gl_LocalInvocationID",
            ComputeBuiltIn.GroupIndex => "gl_LocalInvocationIndex",
            _ => throw new ArgumentOutOfRangeException(nameof(builtIn), builtIn, "Not a dispatch built-in.")
        };
}
