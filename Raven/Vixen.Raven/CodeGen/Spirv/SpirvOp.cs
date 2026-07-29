// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0


namespace Vixen.Raven.CodeGen.Spirv;

/// <summary>
///     The SPIR-V opcodes this backend emits, with their spec numbers. Only the ones
///     actually used are listed — an enum of all 400 would be noise, and anything
///     missing is a compile error rather than a silently wrong word.
/// </summary>
public enum SpirvOp {
    Nop = 0,
    Undef = 1,
    Name = 5,
    MemberName = 6,
    Source = 3,
    Extension = 10,
    ExtInstImport = 11,
    ExtInst = 12,
    MemoryModel = 14,
    EntryPoint = 15,
    ExecutionMode = 16,
    Capability = 17,

    TypeVoid = 19,
    TypeBool = 20,
    TypeInt = 21,
    TypeFloat = 22,
    TypeVector = 23,
    TypeMatrix = 24,
    TypeImage = 25,
    TypeSampler = 26,
    TypeSampledImage = 27,
    TypeArray = 28,
    TypeRuntimeArray = 29,
    TypeStruct = 30,
    TypePointer = 32,
    TypeFunction = 33,

    ConstantTrue = 41,
    ConstantFalse = 42,
    Constant = 43,
    ConstantComposite = 44,
    ConstantNull = 46,

    Function = 54,
    FunctionParameter = 55,
    FunctionEnd = 56,
    FunctionCall = 57,

    Variable = 59,
    ArrayLength = 68,
    Load = 61,
    Store = 62,
    AccessChain = 65,
    Decorate = 71,
    MemberDecorate = 72,

    VectorExtractDynamic = 77,
    VectorShuffle = 79,
    CompositeConstruct = 80,
    CompositeExtract = 81,
    CompositeInsert = 82,
    Transpose = 84,

    SampledImage = 86,
    ImageSampleImplicitLod = 87,
    ImageSampleExplicitLod = 88,
    ImageFetch = 95,
    ImageRead = 98,
    ImageWrite = 99,
    ImageQuerySize = 104,
    ImageQuerySizeLod = 103,

    ConvertFToU = 109,
    ConvertFToS = 110,
    ConvertSToF = 111,
    ConvertUToF = 112,
    UConvert = 113,
    SConvert = 114,
    FConvert = 115,
    Bitcast = 124,

    SNegate = 126,
    FNegate = 127,
    IAdd = 128,
    FAdd = 129,
    ISub = 130,
    FSub = 131,
    IMul = 132,
    FMul = 133,
    UDiv = 134,
    SDiv = 135,
    FDiv = 136,
    UMod = 137,
    SRem = 138,
    SMod = 139,
    FRem = 140,

    // GLSL's `mod` takes the sign of the divisor, which is OpFMod; C's `%` takes
    // the sign of the dividend, which is OpFRem. The IR keeps them apart.
    FMod = 141,
    VectorTimesScalar = 142,
    MatrixTimesScalar = 143,
    VectorTimesMatrix = 144,
    MatrixTimesVector = 145,
    MatrixTimesMatrix = 146,
    Dot = 148,

    Any = 154,
    All = 155,

    LogicalEqual = 164,
    LogicalNotEqual = 165,
    LogicalOr = 166,
    LogicalAnd = 167,
    LogicalNot = 168,
    Select = 169,
    IEqual = 170,
    INotEqual = 171,
    UGreaterThan = 172,
    SGreaterThan = 173,
    UGreaterThanEqual = 174,
    SGreaterThanEqual = 175,
    ULessThan = 176,
    SLessThan = 177,
    ULessThanEqual = 178,
    SLessThanEqual = 179,
    FOrdEqual = 180,
    FOrdNotEqual = 182,
    FOrdLessThan = 184,
    FOrdGreaterThan = 186,
    FOrdLessThanEqual = 188,
    FOrdGreaterThanEqual = 190,

    ShiftRightLogical = 194,
    ShiftRightArithmetic = 195,
    ShiftLeftLogical = 196,
    BitwiseOr = 197,
    BitwiseXor = 198,
    BitwiseAnd = 199,
    Not = 200,

    DPdx = 207,
    DPdy = 208,

    // The atomics. Every one takes a pointer, a scope and a memory-semantics mask before its
    // operands, and every one answers with the value that was there — which is what makes an
    // atomic add an index allocator. Min and Max come in signed and unsigned forms where GLSL has
    // one name for both.
    AtomicExchange = 229,
    AtomicCompareExchange = 230,
    AtomicIAdd = 234,
    AtomicSMin = 236,
    AtomicUMin = 237,
    AtomicSMax = 238,
    AtomicUMax = 239,
    AtomicAnd = 240,
    AtomicOr = 241,
    AtomicXor = 242,

    LoopMerge = 246,
    SelectionMerge = 247,
    Label = 248,
    Branch = 249,
    BranchConditional = 250,
    Kill = 252,
    Return = 253,
    ReturnValue = 254,
    Unreachable = 255
}

/// <summary>Opcode spellings, for the disassembly listing.</summary>
public static class SpirvOpNames {
    public static string Of(SpirvOp op) => "Op" + op;
}
