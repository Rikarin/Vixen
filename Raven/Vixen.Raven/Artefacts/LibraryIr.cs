// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Vixen.Raven.IR;

namespace Vixen.Raven.Artefacts;

/// <summary>
///     The lowered half of a <see cref="CompiledLibrary" />: the structs and functions a
///     consumer links against.
/// </summary>
/// <remarks>
///     <para>
///         A mirror of <see cref="IrModule" /> minus its shaders. A library exports functions
///         and types; a pipeline stage belongs to the effect that declares it, so a library's
///         entry points, bindings and permutation declarations are deliberately absent — see
///         <c>LibraryDiagnostics.EntryPointNotExported</c>.
///     </para>
///     <para>
///         Cross-references travel as names and indices rather than as objects: a call names its
///         callee, a struct-typed field names the struct, and a place names its root variable by
///         position. That is what lets the reader rebuild a graph with cycles in it — two
///         structs may hold each other, and a function may be called before it is read.
///     </para>
/// </remarks>
public sealed record LibraryIr {
    public ImmutableArray<LibraryIrStruct> Structs { get; init; } = [];
    public ImmutableArray<LibraryIrFunction> Functions { get; init; } = [];
}

/// <summary>A lowered aggregate.</summary>
public sealed record LibraryIrStruct {
    public required string Name { get; init; }
    public ImmutableArray<LibraryIrField> Fields { get; init; } = [];
}

/// <summary>One field of a lowered aggregate.</summary>
public sealed record LibraryIrField(string Name, LibraryIrTypeReference Type);

/// <summary>A named, addressable storage slot: a parameter or a local.</summary>
public sealed record LibraryIrVariable(string Name, LibraryIrTypeReference Type);

/// <summary>
///     A lowered function: a signature, its storage, its SSA value table and a structured body.
/// </summary>
/// <remarks>
///     <para>
///         <see cref="Values" /> is the value table, and the instruction stream refers to a value
///         by its id alone. Writing each type once rather than at every mention keeps the artefact
///         small and makes the reader's interning trivial — id <c>i</c> becomes one object, which
///         is what the IR verifier's define-once check needs.
///     </para>
///     <para>
///         A table of <c>(id, type)</c> pairs rather than an array indexed by id, because the
///         numbering can have gaps: dead-branch elimination drops a folded branch after its
///         condition was numbered. <see cref="ValueCount" /> therefore travels separately instead
///         of being inferred from the table's length.
///     </para>
/// </remarks>
public sealed record LibraryIrFunction {
    public required string Name { get; init; }
    public required LibraryIrTypeReference ReturnType { get; init; }
    public ImmutableArray<LibraryIrVariable> Parameters { get; init; } = [];
    public ImmutableArray<LibraryIrVariable> Locals { get; init; } = [];

    /// <summary>The type of every value the function defines, keyed by value id.</summary>
    public ImmutableArray<LibraryIrValue> Values { get; init; } = [];

    /// <summary>How many values the function numbered, gaps included.</summary>
    public int ValueCount { get; init; }

    public LibraryIrBlock Body { get; init; } = new();
}

/// <summary>One entry of a function's value table.</summary>
public sealed record LibraryIrValue(int Id, LibraryIrTypeReference Type);

/// <summary>A type in the lowered IR.</summary>
/// <remarks>
///     Shares <see cref="IrTypeKind" /> with the IR itself rather than restating it, because the
///     IR's type set is already the closed one every target can represent — a second enum would
///     only be able to drift from it.
/// </remarks>
public sealed record LibraryIrTypeReference {
    public IrTypeKind Kind { get; init; }

    /// <summary>The scalar each lane holds, for a vector or a matrix.</summary>
    public IrTypeKind Component { get; init; }

    /// <summary>Lane count of a vector.</summary>
    public int Size { get; init; }

    public int Rows { get; init; }
    public int Columns { get; init; }

    /// <summary>Array length, absent for an unsized array.</summary>
    public int? Length { get; init; }

    /// <summary>Element type of an array.</summary>
    public LibraryIrTypeReference? Element { get; init; }

    /// <summary>Name of the struct, for <see cref="IrTypeKind.Struct" />.</summary>
    public string? Struct { get; init; }

    public IrTextureDimension Dimension { get; init; }

    /// <summary>What a sample of a texture returns.</summary>
    public LibraryIrTypeReference? Sampled { get; init; }
}

/// <summary>
///     A step inside a lowered body. Polymorphic through a discriminator so the artefact reads
///     the way the IR dump does — <c>"op": "call"</c> rather than a tag number.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "op")]
[JsonDerivedType(typeof(LibraryIrBlock), "block")]
[JsonDerivedType(typeof(LibraryIrConstant), "const")]
[JsonDerivedType(typeof(LibraryIrLoad), "load")]
[JsonDerivedType(typeof(LibraryIrStore), "store")]
[JsonDerivedType(typeof(LibraryIrUnary), "unary")]
[JsonDerivedType(typeof(LibraryIrBinary), "binary")]
[JsonDerivedType(typeof(LibraryIrConvert), "convert")]
[JsonDerivedType(typeof(LibraryIrIntrinsic), "intrinsic")]
[JsonDerivedType(typeof(LibraryIrCall), "call")]
[JsonDerivedType(typeof(LibraryIrConstruct), "construct")]
[JsonDerivedType(typeof(LibraryIrExtract), "extract")]
[JsonDerivedType(typeof(LibraryIrSelect), "select")]
[JsonDerivedType(typeof(LibraryIrIf), "if")]
[JsonDerivedType(typeof(LibraryIrLoop), "loop")]
[JsonDerivedType(typeof(LibraryIrReturn), "return")]
[JsonDerivedType(typeof(LibraryIrBreak), "break")]
[JsonDerivedType(typeof(LibraryIrContinue), "continue")]
public abstract record LibraryIrStatement;

/// <summary>A sequence of statements.</summary>
public sealed record LibraryIrBlock : LibraryIrStatement {
    public ImmutableArray<LibraryIrStatement> Statements { get; init; } = [];
}

/// <summary>A constant materialized into a value; a null <paramref name="Value" /> is the type's zero.</summary>
public sealed record LibraryIrConstant(int Result, LibraryValue? Value) : LibraryIrStatement;

public sealed record LibraryIrLoad(int Result, LibraryIrPlace Place) : LibraryIrStatement;

public sealed record LibraryIrStore(LibraryIrPlace Place, int Value) : LibraryIrStatement;

public sealed record LibraryIrUnary(int Result, IrUnaryOp Op, int Operand) : LibraryIrStatement;

public sealed record LibraryIrBinary(int Result, IrBinaryOp Op, int Left, int Right) : LibraryIrStatement;

public sealed record LibraryIrConvert(int Result, IrConversionKind ConversionKind, int Operand) : LibraryIrStatement;

/// <summary>A built-in operation. <paramref name="Result" /> is absent for a void one.</summary>
public sealed record LibraryIrIntrinsic(int? Result, IrIntrinsic Intrinsic, ImmutableArray<int> Arguments)
    : LibraryIrStatement;

/// <summary>A call, naming its callee. The name is resolved once every function exists.</summary>
public sealed record LibraryIrCall(int? Result, string Function, ImmutableArray<int> Arguments) : LibraryIrStatement;

public sealed record LibraryIrConstruct(int Result, ImmutableArray<int> Arguments) : LibraryIrStatement;

public sealed record LibraryIrExtract(int Result, int Source, ImmutableArray<LibraryIrAccess> Chain)
    : LibraryIrStatement;

public sealed record LibraryIrSelect(int Result, int Condition, int WhenTrue, int WhenFalse) : LibraryIrStatement;

public sealed record LibraryIrIf(int Condition, LibraryIrBlock Then, LibraryIrBlock? Else) : LibraryIrStatement;

/// <summary>A structured loop. <paramref name="TestBeforeBody" /> is false for <c>repeat … while</c>.</summary>
public sealed record LibraryIrLoop(
    LibraryIrBlock Condition,
    int ConditionValue,
    LibraryIrBlock Body,
    LibraryIrBlock? Continue,
    bool TestBeforeBody
) : LibraryIrStatement;

public sealed record LibraryIrReturn(int? Value) : LibraryIrStatement;

public sealed record LibraryIrBreak : LibraryIrStatement;

public sealed record LibraryIrContinue : LibraryIrStatement;

/// <summary>
///     A storage location: a root variable plus a chain of accesses into it.
/// </summary>
/// <param name="Root">
///     Index into the function's parameters followed by its locals. A library function has no
///     global root — a body that reads a shader binding is refused at write time (RVN5001), so
///     the two lists are the whole namespace.
/// </param>
/// <param name="Chain">The access steps, outermost first.</param>
public sealed record LibraryIrPlace(int Root, ImmutableArray<LibraryIrAccess> Chain);

/// <summary>One step of an access chain.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "at")]
[JsonDerivedType(typeof(LibraryIrFieldAccess), "field")]
[JsonDerivedType(typeof(LibraryIrIndexAccess), "index")]
[JsonDerivedType(typeof(LibraryIrSwizzleAccess), "swizzle")]
public abstract record LibraryIrAccess;

/// <summary>Selects a struct field by index.</summary>
public sealed record LibraryIrFieldAccess(int Index) : LibraryIrAccess;

/// <summary>Indexes an array, vector or matrix with a value.</summary>
public sealed record LibraryIrIndexAccess(int Index) : LibraryIrAccess;

/// <summary>Selects lanes of a vector.</summary>
public sealed record LibraryIrSwizzleAccess(ImmutableArray<int> Components) : LibraryIrAccess;
