using Vixen.Raven.IR;

namespace Vixen.Raven.CodeGen.Spirv;

/// <summary>How an intrinsic reaches SPIR-V.</summary>
/// <param name="Extended">A GLSL.std.450 instruction, invoked through <c>OpExtInst</c>.</param>
/// <param name="Core">A core opcode taking the same operands.</param>
readonly record struct SpirvIntrinsicMapping(GlslStd450? Extended, SpirvOp? Core) {
    internal bool IsMapped => Extended is not null || Core is not null;
}

/// <summary>
///     Maps <see cref="IrIntrinsic" /> onto SPIR-V. Most land in the GLSL.std.450
///     extended set; a handful are core opcodes. Several — <c>abs</c>, <c>min</c>,
///     <c>clamp</c> — have one instruction per signedness, which is why the component
///     type is part of the question.
/// </summary>
/// <remarks>
///     Four intrinsics have no single-instruction form and are built by the emitter:
///     <c>saturate</c> (a clamp against constants it has to materialize),
///     <c>sampleTexture</c> and <c>loadTexture</c> (which pair an image with a
///     sampler first), and <c>arrayLength</c> (a constant, since the IR has no
///     runtime-sized arrays).
/// </remarks>
static class SpirvIntrinsics {
    static SpirvIntrinsicMapping Extended(GlslStd450 op) => new(op, null);

    static SpirvIntrinsicMapping Core(SpirvOp op) => new(null, op);

    static SpirvIntrinsicMapping Signed(IrTypeKind component, GlslStd450 real, GlslStd450 integer) =>
        component is IrTypeKind.Float or IrTypeKind.Double ? Extended(real) : Extended(integer);

    static SpirvIntrinsicMapping Numeric(
        IrTypeKind component,
        GlslStd450 real,
        GlslStd450 signed,
        GlslStd450 unsigned
    ) =>
        component switch {
            IrTypeKind.Float or IrTypeKind.Double => Extended(real),
            IrTypeKind.UInt => Extended(unsigned),
            _ => Extended(signed)
        };

    static SpirvIntrinsicMapping Numeric(IrTypeKind component, SpirvOp real, SpirvOp signed, SpirvOp unsigned) =>
        component switch {
            IrTypeKind.Float or IrTypeKind.Double => Core(real),
            IrTypeKind.UInt => Core(unsigned),
            _ => Core(signed)
        };

    internal static SpirvIntrinsicMapping Map(IrIntrinsic intrinsic, IrTypeKind component) =>
        intrinsic switch {
            IrIntrinsic.Abs => Signed(component, GlslStd450.FAbs, GlslStd450.SAbs),
            IrIntrinsic.Sign => Signed(component, GlslStd450.FSign, GlslStd450.SSign),
            IrIntrinsic.Floor => Extended(GlslStd450.Floor),
            IrIntrinsic.Ceil => Extended(GlslStd450.Ceil),
            IrIntrinsic.Round => Extended(GlslStd450.Round),
            IrIntrinsic.Truncate => Extended(GlslStd450.Trunc),
            IrIntrinsic.Fract => Extended(GlslStd450.Fract),
            IrIntrinsic.Sqrt => Extended(GlslStd450.Sqrt),
            IrIntrinsic.InverseSqrt => Extended(GlslStd450.InverseSqrt),
            IrIntrinsic.Exp => Extended(GlslStd450.Exp),
            IrIntrinsic.Exp2 => Extended(GlslStd450.Exp2),
            IrIntrinsic.Log => Extended(GlslStd450.Log),
            IrIntrinsic.Log2 => Extended(GlslStd450.Log2),
            IrIntrinsic.Sin => Extended(GlslStd450.Sin),
            IrIntrinsic.Cos => Extended(GlslStd450.Cos),
            IrIntrinsic.Tan => Extended(GlslStd450.Tan),
            IrIntrinsic.Asin => Extended(GlslStd450.Asin),
            IrIntrinsic.Acos => Extended(GlslStd450.Acos),
            IrIntrinsic.Atan => Extended(GlslStd450.Atan),
            IrIntrinsic.Atan2 => Extended(GlslStd450.Atan2),
            IrIntrinsic.Radians => Extended(GlslStd450.Radians),
            IrIntrinsic.Degrees => Extended(GlslStd450.Degrees),
            IrIntrinsic.Pow => Extended(GlslStd450.Pow),
            IrIntrinsic.Step => Extended(GlslStd450.Step),
            IrIntrinsic.SmoothStep => Extended(GlslStd450.SmoothStep),
            IrIntrinsic.Lerp => Extended(GlslStd450.FMix),
            IrIntrinsic.Length => Extended(GlslStd450.Length),
            IrIntrinsic.Distance => Extended(GlslStd450.Distance),
            IrIntrinsic.Cross => Extended(GlslStd450.Cross),
            IrIntrinsic.Normalize => Extended(GlslStd450.Normalize),
            IrIntrinsic.Reflect => Extended(GlslStd450.Reflect),
            IrIntrinsic.Refract => Extended(GlslStd450.Refract),

            IrIntrinsic.Min => Numeric(component, GlslStd450.FMin, GlslStd450.SMin, GlslStd450.UMin),
            IrIntrinsic.Max => Numeric(component, GlslStd450.FMax, GlslStd450.SMax, GlslStd450.UMax),
            IrIntrinsic.Clamp => Numeric(component, GlslStd450.FClamp, GlslStd450.SClamp, GlslStd450.UClamp),

            // `mod` takes the sign of its divisor, so it is OpFMod rather than the
            // OpFRem that the `%` operator lowers to.
            IrIntrinsic.Mod => Numeric(component, SpirvOp.FMod, SpirvOp.SMod, SpirvOp.UMod),

            IrIntrinsic.Dot => Core(SpirvOp.Dot),
            IrIntrinsic.Transpose => Core(SpirvOp.Transpose),
            IrIntrinsic.All => Core(SpirvOp.All),
            IrIntrinsic.Any => Core(SpirvOp.Any),
            IrIntrinsic.DdX => Core(SpirvOp.DPdx),
            IrIntrinsic.DdY => Core(SpirvOp.DPdy),

            _ => default
        };
}
