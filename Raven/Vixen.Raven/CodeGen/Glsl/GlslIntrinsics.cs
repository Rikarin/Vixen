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
    /// <param name="resultType">GLSL name of the result type, for saturate's clamp bounds.</param>
    public static string? Call(IrIntrinsic intrinsic, IReadOnlyList<string> arguments, string resultType) {
        switch (intrinsic) {
            case IrIntrinsic.Saturate:
                // GLSL has no saturate; it is a clamp to the unit range.
                return arguments.Count == 1
                    ? $"clamp({arguments[0]}, {resultType}(0.0), {resultType}(1.0))"
                    : null;

            case IrIntrinsic.SampleTexture:
                // The sampler operand disappears: GLSL's sampler2D is already the
                // texture and its sampling state combined.
                return arguments.Count == 3 ? $"texture({arguments[0]}, {arguments[2]})" : null;

            case IrIntrinsic.LoadTexture:
                // The third coordinate carries the level, as it does in HLSL.
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
}
