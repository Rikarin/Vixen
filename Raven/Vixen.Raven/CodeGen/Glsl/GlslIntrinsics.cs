// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Raven.IR;

namespace Vixen.Raven.CodeGen.Glsl;

/// <summary>Mapping IR opcodes onto GLSL's built-in functions.</summary>
public static class GlslIntrinsics {
    /// <summary>GLSL spells these the same as the IR, just lower-cased.</summary>
    static readonly Dictionary<IrIntrinsic, string> DirectNames = new() {
        [IrIntrinsic.Abs] = "abs",
        [IrIntrinsic.Sign] = "sign",
        [IrIntrinsic.Floor] = "floor",
        [IrIntrinsic.Ceil] = "ceil",
        [IrIntrinsic.Round] = "round",
        [IrIntrinsic.Truncate] = "trunc",
        [IrIntrinsic.Fract] = "fract",
        [IrIntrinsic.Sqrt] = "sqrt",
        [IrIntrinsic.InverseSqrt] = "inversesqrt",
        [IrIntrinsic.Exp] = "exp",
        [IrIntrinsic.Exp2] = "exp2",
        [IrIntrinsic.Log] = "log",
        [IrIntrinsic.Log2] = "log2",
        [IrIntrinsic.Sin] = "sin",
        [IrIntrinsic.Cos] = "cos",
        [IrIntrinsic.Tan] = "tan",
        [IrIntrinsic.Asin] = "asin",
        [IrIntrinsic.Acos] = "acos",
        [IrIntrinsic.Atan] = "atan",
        // GLSL overloads `atan` on arity rather than having a separate atan2.
        [IrIntrinsic.Atan2] = "atan",
        [IrIntrinsic.Radians] = "radians",
        [IrIntrinsic.Degrees] = "degrees",
        [IrIntrinsic.DdX] = "dFdx",
        [IrIntrinsic.DdY] = "dFdy",
        [IrIntrinsic.Min] = "min",
        [IrIntrinsic.Max] = "max",
        [IrIntrinsic.Pow] = "pow",
        [IrIntrinsic.Mod] = "mod",
        [IrIntrinsic.Step] = "step",
        [IrIntrinsic.Clamp] = "clamp",
        [IrIntrinsic.Lerp] = "mix",
        [IrIntrinsic.SmoothStep] = "smoothstep",
        [IrIntrinsic.Length] = "length",
        [IrIntrinsic.Distance] = "distance",
        [IrIntrinsic.Dot] = "dot",
        [IrIntrinsic.Cross] = "cross",
        [IrIntrinsic.Normalize] = "normalize",
        [IrIntrinsic.Reflect] = "reflect",
        [IrIntrinsic.Refract] = "refract",
        [IrIntrinsic.Transpose] = "transpose",
        [IrIntrinsic.All] = "all",
        [IrIntrinsic.Any] = "any"
    };

    /// <summary>
    ///     The GLSL expression for an intrinsic call, or null when GLSL has no way
    ///     to spell it.
    /// </summary>
    /// <param name="intrinsic">The opcode.</param>
    /// <param name="arguments">Already-emitted argument expressions.</param>
    /// <param name="argumentTypes">
    ///     The arguments' IR types, which sampling needs: the combined sampler to construct
    ///     follows from the texture's dimension.
    /// </param>
    /// <param name="resultType">
    ///     GLSL name of the result type, for saturate's clamp bounds.
    /// </param>
    /// <param name="result">
    ///     The result's IR type. A bit cast is named for the pair of types it goes between, so
    ///     the name alone is not enough.
    /// </param>
    public static string? Call(
        IrIntrinsic intrinsic,
        IReadOnlyList<string> arguments,
        IReadOnlyList<IrType> argumentTypes,
        string resultType,
        IrType result
    ) {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(argumentTypes);
        ArgumentNullException.ThrowIfNull(result);

        switch (intrinsic) {
            case IrIntrinsic.Saturate:
                // GLSL has no saturate; it is a clamp to the unit range.
                return arguments.Count == 1
                    ? $"clamp({arguments[0]}, {resultType}(0.0), {resultType}(1.0))"
                    : null;

            case IrIntrinsic.SampleTexture:
                // The texture and the sampler are two bindings right up to here, exactly as
                // in SPIR-V, and combine into one value for the sample itself.
                return arguments.Count == 3 && argumentTypes[0] is IrTextureType texture
                    ? $"texture({GlslTypes.Combined(texture)}({arguments[0]}, {arguments[1]}), {arguments[2]})"
                    : null;

            case IrIntrinsic.SampleTextureLevel:
                // Same pairing as Sample, with the level stated rather than derived. GLSL spells
                // the explicit-level form as its own function.
                return arguments.Count == 4 && argumentTypes[0] is IrTextureType levelled
                    ? $"textureLod({GlslTypes.Combined(levelled)}({arguments[0]}, {arguments[1]}), "
                    + $"{arguments[2]}, {arguments[3]})"
                    : null;

            case IrIntrinsic.TextureSize:
                // Samplerless, like texelFetch: a size is a property of the image, and pairing a
                // sampler in just to ask for it would need a sampler the shader may not have.
                return arguments.Count == 2 ? $"textureSize({arguments[0]}, {arguments[1]})" : null;

            case IrIntrinsic.BitCast:
                return arguments.Count == 1
                    ? BitCast(argumentTypes[0].ComponentType.Kind, result, arguments[0], resultType)
                    : null;

            case IrIntrinsic.LoadTexture:
                // No sampler: a fetch by integer coordinate does not filter, which is why
                // GL_EXT_samplerless_texture_functions exists and why SPIR-V's OpImageFetch
                // takes the plain image. The third coordinate carries the level, as in HLSL.
                return arguments.Count == 2
                    ? $"texelFetch({arguments[0]}, {arguments[1]}.xy, {arguments[1]}.z)"
                    : null;

            case IrIntrinsic.ArrayLength:
                return arguments.Count == 1 ? $"{arguments[0]}.length()" : null;

            default:
                return DirectNames.TryGetValue(intrinsic, out var name)
                    ? $"{name}({string.Join(", ", arguments)})"
                    : null;
        }
    }

    /// <summary>The GLSL spelling of a bit reinterpretation between two component types.</summary>
    /// <remarks>
    ///     GLSL names one function per direction across the float boundary and has none for
    ///     <c>int</c> ↔ <c>uint</c>, where its ordinary constructor is already defined to keep the
    ///     bit pattern. Same type in and out is the identity, and emits the argument untouched
    ///     rather than a constructor call that would read as a conversion.
    /// </remarks>
    static string? BitCast(IrTypeKind from, IrType result, string argument, string resultType) {
        var to = result.ComponentType.Kind;

        if (from == to) {
            return argument;
        }

        var name = (from, to) switch {
            (IrTypeKind.Float, IrTypeKind.Int) => "floatBitsToInt",
            (IrTypeKind.Float, IrTypeKind.UInt) => "floatBitsToUint",
            (IrTypeKind.Int, IrTypeKind.Float) => "intBitsToFloat",
            (IrTypeKind.UInt, IrTypeKind.Float) => "uintBitsToFloat",
            (IrTypeKind.Int, IrTypeKind.UInt) or (IrTypeKind.UInt, IrTypeKind.Int) => resultType,
            _ => null
        };

        return name is null ? null : $"{name}({argument})";
    }
}
