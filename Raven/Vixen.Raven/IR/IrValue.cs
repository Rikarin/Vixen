// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0


namespace Vixen.Raven.IR;

/// <summary>
///     A value produced by exactly one instruction. Ids are unique within a
///     function and never reassigned, so the instruction stream is in SSA form —
///     what mutates is memory, reached through <see cref="IrPlace" />.
/// </summary>
public sealed class IrValue(int id, IrType type) {
    public int Id { get; } = id;
    public IrType Type { get; } = type;

    public override string ToString() => $"%{Id}";
}

/// <summary>Where a variable lives, which decides how a backend declares it.</summary>
public enum IrVariableKind {
    /// <summary>A shader-level binding: uniform, texture or sampler.</summary>
    Global,
    Parameter,
    Local
}

/// <summary>
///     A named, addressable storage slot. Locals stay in memory in this IR rather
///     than being promoted to SSA registers — the backends want declarations they
///     can name, and a later mem2reg pass can promote them if a target prefers it.
/// </summary>
public sealed class IrVariable(string name, IrType type, IrVariableKind kind, bool byReference = false) {
    public string Name { get; } = name;
    public IrType Type { get; } = type;
    public IrVariableKind Kind { get; } = kind;

    /// <summary>
    ///     True for a parameter the caller passes by reference, from <c>inout</c>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <see cref="Type" /> stays the referent's type rather than becoming a pointer type: the
    ///         IR has no pointer types, and giving it one for this would change what every existing
    ///         load and store of a parameter means. The flag says how the storage is reached; the
    ///         type says what is in it.
    ///     </para>
    ///     <para>
    ///         Only ever set on a <see cref="IrVariableKind.Parameter" />. A local is already its own
    ///         storage and a global is reached by name, so neither has a caller to be reached
    ///         through.
    ///     </para>
    /// </remarks>
    public bool IsByReference { get; } = byReference;

    string Prefix =>
        Kind switch {
            IrVariableKind.Global => "@",
            // `&` so an IR dump shows at a glance which parameters the caller sees writes through.
            IrVariableKind.Parameter => IsByReference ? "&" : "$",
            _ => "!"
        };

    public override string ToString() => $"{Prefix}{Name}";
}

/// <summary>One step of an <see cref="IrPlace" />'s access chain.</summary>
public abstract class IrAccess {
    /// <summary>The type reached by applying this step to <paramref name="input" />.</summary>
    public abstract IrType ResultType(IrType input);
}

/// <summary>Selects a struct field by index.</summary>
public sealed class IrFieldAccess(int index) : IrAccess {
    public int Index { get; } = index;

    public override IrType ResultType(IrType input) =>
        input is IrStructType structType && Index >= 0 && Index < structType.Fields.Count
            ? structType.Fields[Index].Type
            : IrScalarType.Void;

    public override string ToString() => $".{Index}";
}

/// <summary>Indexes an array, vector or matrix with a runtime value.</summary>
/// <remarks>
///     <c>m[i]</c> is the <em>i</em>-th column, which is what SPIR-V's access chain and GLSL's
///     <c>[]</c> both give — so it costs nothing in either backend, where a row would cost a gather.
///     Because storage makes the shader's matrix the transpose of the host's, that column is the
///     host matrix's <em>row</em> i, which is also the intuitive reading. See docs/plan/07 § E.
/// </remarks>
public sealed class IrIndexAccess(IrValue index) : IrAccess {
    public IrValue Index { get; } = index;

    public override IrType ResultType(IrType input) =>
        input switch {
            IrArrayType array => array.Element,
            IrVectorType vector => vector.Component,
            IrMatrixType matrix => matrix.ColumnType,
            _ => IrScalarType.Void
        };

    public override string ToString() => $"[{Index}]";
}

/// <summary>
///     Selects lanes of a vector: <c>v.xy</c> is <c>Swizzle(0, 1)</c>. A single
///     component yields the scalar, more than one a shorter vector.
/// </summary>
public sealed class IrSwizzleAccess(int[] components) : IrAccess {
    public IReadOnlyList<int> Components { get; } = components;

    public override IrType ResultType(IrType input) =>
        input is not IrVectorType vector
            ? IrScalarType.Void
            : Components.Count == 1
                ? vector.Component
                : new IrVectorType(vector.Component, Components.Count);

    public override string ToString() => "." + string.Concat(Components.Select(c => "xyzw"[c]));
}

/// <summary>
///     A storage location: a variable plus a chain of accesses into it. This is
///     SPIR-V's access chain, and it emits directly as <c>a.b[i].xy</c> in a
///     source-level target.
/// </summary>
public sealed class IrPlace(IrVariable root, IReadOnlyList<IrAccess>? chain = null) {
    public IrVariable Root { get; } = root;
    public IReadOnlyList<IrAccess> Chain { get; } = chain ?? [];

    /// <summary>The type of the storage this place designates.</summary>
    public IrType Type {
        get {
            var type = Root.Type;
            foreach (var access in Chain) {
                type = access.ResultType(type);
            }

            return type;
        }
    }

    /// <summary>This place with one more access step appended.</summary>
    public IrPlace With(IrAccess access) => new(Root, [.. Chain, access]);

    public override string ToString() => Root + string.Concat(Chain.Select(a => a.ToString()));
}
