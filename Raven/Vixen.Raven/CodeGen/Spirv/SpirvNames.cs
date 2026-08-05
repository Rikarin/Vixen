// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Raven.CodeGen.Spirv;

/// <summary>
///     What a name in an <c>OpName</c> is allowed to be.
/// </summary>
/// <remarks>
///     <para>
///         <strong><c>OpName</c> is debug information that decides whether a shader runs.</strong>
///         SPIR-V itself does not care what anything is called — validation passes either way. But a
///         SPIR-V module is rarely the last step: MoltenVK cross-compiles it to Metal Shading
///         Language, D3D12 backends to HLSL, and both take variable names <em>from these very
///         strings</em>. A name that is an ordinary identifier in Raven and a keyword in the language
///         downstream produces source that will not compile, and the failure arrives as
///         <c>vkCreateComputePipelines … ErrorInitializationFailed</c> with no mention of a name.
///     </para>
///     <para>
///         The word that found this was <c>and</c>. Raven lowers <c>a &amp;&amp; b</c> into a local
///         so that <c>b</c> can be skipped, and called it that; C++ — and therefore MSL — spells
///         <c>&amp;&amp;</c> as <c>and</c> too, so <c>bool and = …;</c> is a syntax error, and every
///         shader with a short-circuiting operand that needs guarding failed on Apple hardware. The
///         GLSL backend never saw it, because GLSL has no alternative operator spellings and its own
///         sanitiser (<see cref="Glsl.GlslTypes.Identifier" />) covered the rest.
///     </para>
///     <para>
///         So the list here is deliberately <em>not</em> GLSL's. It is the words that are legal in
///         Raven and legal in GLSL and illegal in C++: the alternative operator tokens, and the
///         handful of C++ keywords GLSL does not reserve. Anything GLSL reserves is already handled
///         where it matters, and duplicating it here would be a second list to keep in step.
///     </para>
/// </remarks>
internal static class SpirvNames {
    /// <summary>What a suffixed name gains, so that two distinct names stay distinct.</summary>
    const string Suffix = "_";

    /// <summary>
    ///     Legal in Raven, legal in GLSL, and a keyword in the C++ dialects SPIR-V is cross-compiled
    ///     into.
    /// </summary>
    static readonly HashSet<string> Reserved = new(StringComparer.Ordinal) {
        // C++'s alternative operator spellings, which is the half nobody expects: these are not
        // identifiers in C++ at all, they *are* the operators.
        "and",
        "and_eq",
        "bitand",
        "bitor",
        "compl",
        "not",
        "not_eq",
        "or",
        "or_eq",
        "xor",
        "xor_eq",

        // C++ keywords that GLSL leaves alone. `constexpr` and `nullptr` are as ordinary a name in
        // Raven as `count` is.
        "alignas",
        "alignof",
        "constexpr",
        "decltype",
        "delete",
        "explicit",
        "export",
        "friend",
        "mutable",
        "namespace",
        "new",
        "noexcept",
        "nullptr",
        "operator",
        "private",
        "protected",
        "public",
        "register",
        "static_cast",
        "thread_local",
        "typeid",
        "typename",
        "using",
        "virtual",

        // MSL's own, which are not C++'s: an address space named as a variable is the same mistake
        // one step further out.
        "device",
        "constant",
        "threadgroup",
        "kernel",
        "vertex",
        "fragment"
    };

    /// <summary>The name to put in an <c>OpName</c>, which is the given one unless it is a hazard.</summary>
    /// <param name="name">What the IR calls it.</param>
    /// <returns>
    ///     The same string, or one with an underscore appended — the same shape
    ///     <see cref="Glsl.GlslTypes.Identifier" /> uses, so a reader who has seen one recognises the
    ///     other.
    /// </returns>
    /// <remarks>
    ///     Only exact matches are touched. A name is not a hazard for containing a keyword —
    ///     <c>andThen</c> is a perfectly good identifier in every language this reaches — and
    ///     mangling it would make the disassembly harder to read for nothing.
    /// </remarks>
    public static string Of(string name) =>
        string.IsNullOrEmpty(name) || !Reserved.Contains(name) ? name : name + Suffix;
}
