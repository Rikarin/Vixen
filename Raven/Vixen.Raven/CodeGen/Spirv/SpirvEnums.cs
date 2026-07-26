// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax.Diagnostics;

namespace Vixen.Raven.CodeGen.Spirv;

/// <summary>Values a module declares support for.</summary>
public enum SpirvCapability {
    Matrix = 0,
    Shader = 1,
    Float64 = 10
}

public enum SpirvAddressingModel {
    Logical = 0
}

public enum SpirvMemoryModel {
    Simple = 0,
    GLSL450 = 1
}

public enum SpirvExecutionModel {
    Vertex = 0,
    TessellationControl = 1,
    TessellationEvaluation = 2,
    Geometry = 3,
    Fragment = 4,
    GLCompute = 5
}

public enum SpirvExecutionMode {
    /// <summary>Fragment shaders must declare one of the two origins; Vulkan wants this one.</summary>
    OriginUpperLeft = 7,
    LocalSize = 17
}

public enum SpirvStorageClass {
    UniformConstant = 0,
    Input = 1,
    Uniform = 2,
    Output = 3,
    Function = 7
}

public enum SpirvDim {
    Dim2D = 1,
    Dim3D = 2,
    Cube = 3
}

public enum SpirvImageFormat {
    Unknown = 0
}

public enum SpirvDecoration {
    Block = 2,
    RowMajor = 4,
    ColMajor = 5,
    ArrayStride = 6,
    MatrixStride = 7,
    BuiltIn = 11,
    Location = 30,
    Binding = 33,
    DescriptorSet = 34,
    Offset = 35
}

public enum SpirvBuiltIn {
    Position = 0,
    PointSize = 1,
    FragCoord = 15,
    FragDepth = 22,

    // The compute dispatch ids. Numbers are from the SPIR-V spec's BuiltIn table; the names
    // are SPIR-V's, which differ from HLSL's semantics — see Symbols/ComputeBuiltIns.
    WorkgroupSize = 25,
    WorkgroupId = 26,
    LocalInvocationId = 27,
    GlobalInvocationId = 28,
    LocalInvocationIndex = 29
}

/// <summary>
///     The SPIR-V built-in each dispatch semantic maps to.
/// </summary>
/// <remarks>
///     Separate from <c>ComputeBuiltIns</c>'s GLSL mapping so that neither target's spelling is
///     the one the other has to be derived from — but both read the same
///     <see cref="Symbols.ComputeBuiltIn" />, so a built-in added in one place cannot be
///     silently missing here.
/// </remarks>
public static class SpirvBuiltIns {
    public static SpirvBuiltIn Of(Symbols.ComputeBuiltIn builtIn) =>
        builtIn switch {
            Symbols.ComputeBuiltIn.DispatchThreadId => SpirvBuiltIn.GlobalInvocationId,
            Symbols.ComputeBuiltIn.GroupId => SpirvBuiltIn.WorkgroupId,
            Symbols.ComputeBuiltIn.GroupThreadId => SpirvBuiltIn.LocalInvocationId,
            Symbols.ComputeBuiltIn.GroupIndex => SpirvBuiltIn.LocalInvocationIndex,
            _ => throw new ArgumentOutOfRangeException(nameof(builtIn), builtIn, "Not a dispatch built-in.")
        };
}

public enum SpirvFunctionControl {
    None = 0
}

public enum SpirvLoopControl {
    None = 0
}

public enum SpirvSelectionControl {
    None = 0
}

public enum SpirvMemorySemantics {
    None = 0
}

/// <summary>
///     The GLSL.std.450 extended instruction set, which is where most of the maths
///     intrinsics live. Numbers are from the extended instruction spec.
/// </summary>
public enum GlslStd450 {
    Round = 1,
    RoundEven = 2,
    Trunc = 3,
    FAbs = 4,
    SAbs = 5,
    FSign = 6,
    SSign = 7,
    Floor = 8,
    Ceil = 9,
    Fract = 10,
    Radians = 11,
    Degrees = 12,
    Sin = 13,
    Cos = 14,
    Tan = 15,
    Asin = 16,
    Acos = 17,
    Atan = 18,
    Atan2 = 25,
    Pow = 26,
    Exp = 27,
    Log = 28,
    Exp2 = 29,
    Log2 = 30,
    Sqrt = 31,
    InverseSqrt = 32,
    Determinant = 33,
    MatrixInverse = 34,
    FMin = 37,
    UMin = 38,
    SMin = 39,
    FMax = 40,
    UMax = 41,
    SMax = 42,
    FClamp = 43,
    UClamp = 44,
    SClamp = 45,
    FMix = 46,
    Step = 48,
    SmoothStep = 49,
    Length = 66,
    Distance = 67,
    Cross = 68,
    Normalize = 69,
    FaceForward = 70,
    Reflect = 71,
    Refract = 72
}
