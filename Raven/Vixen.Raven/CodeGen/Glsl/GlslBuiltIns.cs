// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Raven.CodeGen.Glsl;

/// <summary>
///     The names GLSL's own built-in functions occupy, across every dialect the engine targets.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This is a GLSL <em>ES</em> rule and desktop GLSL does not have it</b>, which is
///         exactly why nothing saw it until the library met an ES front end. Measured rather than
///         read: <c>uniform float dot;</c> and <c>uniform readonly image2D average;</c> both compile
///         at <c>#version 450 core</c> and are both <c>'…' : redefinition</c> at
///         <c>#version 320 es</c>. So does a <c>struct average</c>, and a <c>float dot(float)</c> is
///         <c>'dot' : function name is redeclaration of existing name</c>.
///     </para>
///     <para>
///         ⚠ <b>And the ESSL table is larger than the desktop one.</b>
///         <c>GL_EXT_shader_integer_functions2</c> — <c>countLeadingZeros</c>,
///         <c>countTrailingZeros</c>, <c>absoluteDifference</c>, <c>addSaturate</c>,
///         <c>subtractSaturate</c>, <c>average</c>, <c>averageRounded</c>, <c>multiply32x16</c> —
///         is in glslang's ESSL 3.10-and-above symbol table whether or not the extension is
///         enabled, and desktop GLSL 4.5 has none of it. <c>AutoExposure.rvn</c> declared
///         <c>var average: RWTexture2D&lt;float4&gt;</c> and its cross-compiled GLSL ES was
///         <c>'average' : redefinition</c> with exactly one <c>average</c> in the file.
///     </para>
///     <para>
///         So the set is the <em>union</em>, and it has to be: a table consulted for one dialect
///         passes a shader that breaks the day someone asks for another. HLSL and MSL will each
///         bring their own the day a backend for them exists, and they belong here rather than in
///         that backend for the same reason.
///     </para>
///     <para>
///         Only names that reach GLSL's <em>global</em> scope are checked against this. A local and
///         a struct member legally shadow a built-in at every version — measured too — so a
///         <c>val distance</c> inside a function is fine, and the library has fifty of them.
///     </para>
///     <para>
///         Separate from <c>GlslTypes.Identifier</c>'s list, which holds the <em>keywords</em> and
///         mangles rather than refuses. Mangling is right there: <c>half</c> is not a name anything
///         binds by. It is wrong here, because a binding is bound <em>by name</em> on every GL
///         profile below 3.1, so a silently renamed uniform is a resource the host cannot find —
///         the <c>_112</c> failure this repository has already had once.
///     </para>
/// </remarks>
internal static class GlslBuiltIns {
    /// <summary>Every built-in function name, across GLSL 4.6 and GLSL ES 3.2.</summary>
    public static readonly IReadOnlySet<string> FunctionNames = new HashSet<string>(StringComparer.Ordinal) {
        // Angle and trigonometry.
        "radians", "degrees", "sin", "cos", "tan", "asin", "acos", "atan",
        "sinh", "cosh", "tanh", "asinh", "acosh", "atanh",

        // Exponential.
        "pow", "exp", "log", "exp2", "log2", "sqrt", "inversesqrt",

        // Common.
        "abs", "sign", "floor", "trunc", "round", "roundEven", "ceil", "fract", "mod", "modf",
        "min", "max", "clamp", "mix", "step", "smoothstep", "isnan", "isinf",
        "floatBitsToInt", "floatBitsToUint", "intBitsToFloat", "uintBitsToFloat",
        "fma", "frexp", "ldexp",

        // Floating-point pack and unpack.
        "packUnorm2x16", "packSnorm2x16", "packUnorm4x8", "packSnorm4x8",
        "unpackUnorm2x16", "unpackSnorm2x16", "unpackUnorm4x8", "unpackSnorm4x8",
        "packHalf2x16", "unpackHalf2x16", "packDouble2x32", "unpackDouble2x32",

        // Geometric.
        "length", "distance", "dot", "cross", "normalize", "ftransform", "faceforward",
        "reflect", "refract",

        // Matrix.
        "matrixCompMult", "outerProduct", "transpose", "determinant", "inverse",

        // Vector relational.
        "lessThan", "lessThanEqual", "greaterThan", "greaterThanEqual", "equal", "notEqual",
        "any", "all", "not",

        // Integer.
        "uaddCarry", "usubBorrow", "umulExtended", "imulExtended", "bitfieldExtract",
        "bitfieldInsert", "bitfieldReverse", "bitCount", "findLSB", "findMSB",

        // Texture lookup.
        "textureSize", "textureQueryLod", "textureQueryLevels", "textureSamples",
        "texture", "textureProj", "textureLod", "textureOffset", "texelFetch", "texelFetchOffset",
        "textureProjOffset", "textureLodOffset", "textureProjLod", "textureProjLodOffset",
        "textureGrad", "textureGradOffset", "textureProjGrad", "textureProjGradOffset",
        "textureGather", "textureGatherOffset", "textureGatherOffsets",

        // Atomic counter.
        "atomicCounter", "atomicCounterIncrement", "atomicCounterDecrement", "atomicCounterAdd",
        "atomicCounterSubtract", "atomicCounterMin", "atomicCounterMax", "atomicCounterAnd",
        "atomicCounterOr", "atomicCounterXor", "atomicCounterExchange", "atomicCounterCompSwap",

        // Atomic memory.
        "atomicAdd", "atomicMin", "atomicMax", "atomicAnd", "atomicOr", "atomicXor",
        "atomicExchange", "atomicCompSwap",

        // Image.
        "imageSize", "imageSamples", "imageLoad", "imageStore", "imageAtomicAdd", "imageAtomicMin",
        "imageAtomicMax", "imageAtomicAnd", "imageAtomicOr", "imageAtomicXor",
        "imageAtomicExchange", "imageAtomicCompSwap",

        // Fragment processing.
        "dFdx", "dFdy", "dFdxFine", "dFdyFine", "dFdxCoarse", "dFdyCoarse",
        "fwidth", "fwidthFine", "fwidthCoarse",
        "interpolateAtCentroid", "interpolateAtSample", "interpolateAtOffset",

        // Noise, which is in the grammar even where every implementation returns zero.
        "noise1", "noise2", "noise3", "noise4",

        // Geometry shader.
        "EmitStreamVertex", "EndStreamPrimitive", "EmitVertex", "EndPrimitive",

        // Shader invocation control and memory.
        "barrier", "memoryBarrier", "memoryBarrierAtomicCounter", "memoryBarrierBuffer",
        "memoryBarrierShared", "memoryBarrierImage", "groupMemoryBarrier",
        "anyInvocation", "allInvocations", "allInvocationsEqual",

        // ⚠ GL_EXT_shader_integer_functions2, which only ESSL 3.10+ has — and has whether or not
        // the extension was asked for. This block is the whole reason the table is a union.
        "countLeadingZeros", "countTrailingZeros", "absoluteDifference", "addSaturate",
        "subtractSaturate", "average", "averageRounded", "multiply32x16"
    };

    /// <summary>Whether a name would collide with a GLSL built-in function in some dialect.</summary>
    /// <param name="name">The declared name.</param>
    /// <returns>True when GLSL ES would call the declaration a redefinition.</returns>
    public static bool IsFunctionName(string name) => FunctionNames.Contains(name);
}
