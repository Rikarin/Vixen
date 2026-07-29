// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax.Diagnostics;

namespace Vixen.Raven.CodeGen.Spirv;

/// <summary>Values a module declares support for.</summary>
public enum SpirvCapability {
    Matrix = 0,
    Shader = 1,
    Float64 = 10,

    /// <summary>64-bit integer types. Optional on Vulkan, and absent from WebGPU entirely.</summary>
    Int64 = 11,

    /// <summary>
    ///     An atomic on a 64-bit integer.
    /// </summary>
    /// <remarks>
    ///     Separate from <see cref="Int64" /> and separately optional: <c>VK_KHR_shader_atomic_int64</c>
    ///     is what a device advertises, and a device may have 64-bit integers without atomics on
    ///     them. Declaring the type's capability alone would be a module <c>spirv-val</c> accepts
    ///     and a driver refuses.
    /// </remarks>
    Int64Atomics = 12,

    /// <summary>Asking an image about itself — <c>OpImageQuerySizeLod</c> and its siblings.</summary>
    ImageQuery = 50,

    /// <summary>
    ///     An index into a descriptor array may differ across the invocations of a subgroup.
    /// </summary>
    /// <remarks>
    ///     The capability the whole idea rests on. A merged draw is fragments of several materials in
    ///     one dispatch, so an index that had to be uniform would mean the merge bought nothing —
    ///     and, worse, would be undefined rather than refused if it were not declared.
    /// </remarks>
    ShaderNonUniform = 5301,

    /// <summary>A descriptor array declared without a length.</summary>
    RuntimeDescriptorArray = 5302
}

/// <summary>The SPIR-V extensions this backend declares, spelled as the registry spells them.</summary>
/// <remarks>
///     Constants rather than literals at the use site, because an <c>OpExtension</c> whose name is
///     misspelt is not an error anywhere in this compiler: the module carries a string nothing
///     recognises, and the capability it was meant to enable is rejected by the validator with a
///     message about the capability.
/// </remarks>
public static class SpirvExtensions {
    /// <summary>
    ///     <c>SPV_EXT_descriptor_indexing</c> — unbounded descriptor arrays and non-uniform indexing.
    /// </summary>
    /// <remarks>
    ///     Both of its capabilities became core in SPIR-V 1.5, which Vulkan 1.2 consumes. Declared
    ///     unconditionally because Raven targets SPIR-V 1.0 for the Vulkan 1.1 floor
    ///     ([05](../../../../docs/plan/05-graphics-rhi.md)), and an extension declared where it is
    ///     already core is legal and ignored — where the reverse is a module no 1.1 driver accepts.
    /// </remarks>
    public const string DescriptorIndexing = "SPV_EXT_descriptor_indexing";
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

/// <summary>
///     How far an atomic or a barrier reaches.
/// </summary>
/// <remarks>
///     Not a decoration or a literal but an <em>id</em> everywhere it is used: SPIR-V spells scopes
///     and memory semantics as constants so that a specialization constant can supply one. This
///     enum is what those constants hold.
/// </remarks>
public enum SpirvScope {
    /// <summary>The whole dispatch — what an atomic on a storage buffer needs.</summary>
    Device = 1,

    /// <summary>One workgroup — what a barrier waits for, and what shared memory is shared by.</summary>
    Workgroup = 2
}

public enum SpirvStorageClass {
    UniformConstant = 0,
    Input = 1,
    Uniform = 2,
    Output = 3,

    /// <summary>
    ///     Storage one workgroup shares: one copy per group, gone when the group retires.
    /// </summary>
    /// <remarks>
    ///     Legal only under the <c>GLCompute</c> execution model, which is why lowering refuses a
    ///     <c>groupshared</c> variable any other stage can reach rather than leaving it here.
    /// </remarks>
    Workgroup = 4,

    Function = 7,

    /// <summary>
    ///     The push-constant block: read-only state the host writes into the command buffer rather
    ///     than into a descriptor.
    /// </summary>
    PushConstant = 9,

    /// <summary>
    ///     A storage buffer. Distinct from <see cref="Uniform" /> because it is the writable one and
    ///     because it carries a std430 layout; in SPIR-V 1.0 it also requires the
    ///     <c>SPV_KHR_storage_buffer_storage_class</c> extension, which Vulkan 1.0 has and 1.1 folded in.
    /// </summary>
    StorageBuffer = 12
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
    BufferBlock = 3,
    RowMajor = 4,
    ColMajor = 5,
    ArrayStride = 6,
    MatrixStride = 7,
    BuiltIn = 11,

    /// <summary>This varying is not interpolated. Required on a fragment input of integer type.</summary>
    Flat = 14,

    Location = 30,
    Binding = 33,
    DescriptorSet = 34,
    NonWritable = 24,
    Offset = 35,

    /// <summary>
    ///     This value may differ across a subgroup — put on the index into a descriptor array and on
    ///     what the access chain produced from it.
    /// </summary>
    /// <remarks>
    ///     Both, and that is the part worth writing down. The index says the number varies; the
    ///     result says the <em>descriptor</em> does, and it is the second that a driver reads to stop
    ///     hoisting the load out of whatever it thought was uniform control flow. A module carrying
    ///     only one of them validates and then produces one material's texture on every fragment of
    ///     a merged draw — which looks exactly like a merge that worked.
    /// </remarks>
    NonUniform = 5300
}

public enum SpirvBuiltIn {
    Position = 0,
    PointSize = 1,
    FragCoord = 15,
    FrontFacing = 17,
    FragDepth = 22,

    // The stage-supplied values. Numbers are from the SPIR-V spec's BuiltIn table; the names
    // are SPIR-V's, which differ from HLSL's semantics — see Symbols/StageBuiltIns.
    WorkgroupSize = 25,
    WorkgroupId = 26,
    LocalInvocationId = 27,
    GlobalInvocationId = 28,
    LocalInvocationIndex = 29,
    VertexIndex = 42,
    InstanceIndex = 43
}

/// <summary>
///     The SPIR-V built-in each stage semantic maps to.
/// </summary>
/// <remarks>
///     Separate from <c>StageBuiltIns</c>'s GLSL name so that neither target's spelling is the one
///     the other has to be derived from — but both read the same
///     <see cref="Symbols.StageBuiltIn" />, so a built-in added in one place cannot be silently
///     missing here. Kept as an enumerant rather than flattened to its number, because that is
///     what puts <c>BuiltIn VertexIndex</c> rather than <c>BuiltIn 42</c> in a listing.
/// </remarks>
public static class SpirvBuiltIns {
    public static SpirvBuiltIn Of(Symbols.StageBuiltIn builtIn) =>
        builtIn switch {
            Symbols.StageBuiltIn.DispatchThreadId => SpirvBuiltIn.GlobalInvocationId,
            Symbols.StageBuiltIn.GroupId => SpirvBuiltIn.WorkgroupId,
            Symbols.StageBuiltIn.GroupThreadId => SpirvBuiltIn.LocalInvocationId,
            Symbols.StageBuiltIn.GroupIndex => SpirvBuiltIn.LocalInvocationIndex,
            Symbols.StageBuiltIn.VertexId => SpirvBuiltIn.VertexIndex,
            Symbols.StageBuiltIn.InstanceId => SpirvBuiltIn.InstanceIndex,
            Symbols.StageBuiltIn.IsFrontFace => SpirvBuiltIn.FrontFacing,
            _ => throw new ArgumentOutOfRangeException(nameof(builtIn), builtIn, "Not a stage built-in.")
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
