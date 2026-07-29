// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0


namespace Vixen.Raven.Symbols;

/// <summary>
///     The built-in shader function library. Every entry is a
///     <see cref="MethodSymbol" /> in the global scope, so calls to <c>dot</c> or
///     <c>normalize</c> go through ordinary overload resolution and end up in the
///     bound tree like any other call.
/// </summary>
public static class Intrinsics {
    static readonly PrimitiveTypeSymbol[] FloatTypes = [
        BuiltInTypes.Float, BuiltInTypes.Float2, BuiltInTypes.Float3, BuiltInTypes.Float4
    ];

    static readonly PrimitiveTypeSymbol[] FloatVectors = [
        BuiltInTypes.Float2, BuiltInTypes.Float3, BuiltInTypes.Float4
    ];

    static readonly PrimitiveTypeSymbol[] IntTypes = [
        BuiltInTypes.Int, BuiltInTypes.Int2, BuiltInTypes.Int3, BuiltInTypes.Int4
    ];

    static readonly PrimitiveTypeSymbol[] BoolVectors = [
        BuiltInTypes.Bool2, BuiltInTypes.Bool3, BuiltInTypes.Bool4
    ];

    static readonly PrimitiveTypeSymbol[] Matrices = [
        BuiltInTypes.Mat2, BuiltInTypes.Mat2x3, BuiltInTypes.Mat2x4,
        BuiltInTypes.Mat3, BuiltInTypes.Mat3x2, BuiltInTypes.Mat3x4,
        BuiltInTypes.Mat4, BuiltInTypes.Mat4x2, BuiltInTypes.Mat4x3
    ];

    /// <summary>Element-wise functions over floating-point scalars and vectors.</summary>
    static readonly string[] FloatUnary = [
        "abs", "sign", "floor", "ceil", "round", "trunc", "frac", "saturate",
        "sqrt", "rsqrt", "exp", "exp2", "log", "log2",
        "sin", "cos", "tan", "asin", "acos", "atan",
        "radians", "degrees", "ddx", "ddy"
    ];

    static readonly string[] FloatBinary = ["min", "max", "pow", "atan2", "mod", "step"];

    static readonly Dictionary<string, MethodSymbol[]> ByName;

    /// <summary>Every intrinsic overload, for scope population and tests.</summary>
    public static IEnumerable<MethodSymbol> All => ByName.Values.SelectMany(m => m);

    static Intrinsics() {
        List<MethodSymbol> methods = [];

        foreach (var name in FloatUnary) {
            foreach (var type in FloatTypes) {
                methods.Add(Method(name, type, ("x", type)));
            }
        }

        // abs/min/max/clamp also make sense on integers.
        foreach (var type in IntTypes) {
            methods.Add(Method("abs", type, ("x", type)));
            methods.Add(Method("sign", BuiltInTypes.Int, ("x", type)));
            methods.Add(Method("min", type, ("a", type), ("b", type)));
            methods.Add(Method("max", type, ("a", type), ("b", type)));
            methods.Add(Method("clamp", type, ("x", type), ("min", type), ("max", type)));
        }

        foreach (var name in FloatBinary) {
            foreach (var type in FloatTypes) {
                methods.Add(Method(name, type, ("a", type), ("b", type)));
            }
        }

        foreach (var type in FloatTypes) {
            methods.Add(Method("clamp", type, ("x", type), ("min", type), ("max", type)));
            methods.Add(Method("lerp", type, ("a", type), ("b", type), ("t", type)));
            methods.Add(Method("mix", type, ("a", type), ("b", type), ("t", type)));
            methods.Add(Method("smoothstep", type, ("edge0", type), ("edge1", type), ("x", type)));
            methods.Add(Method("length", BuiltInTypes.Float, ("x", type)));
            methods.Add(Method("distance", BuiltInTypes.Float, ("a", type), ("b", type)));
            methods.Add(Method("dot", BuiltInTypes.Float, ("a", type), ("b", type)));
            methods.Add(Method("normalize", type, ("x", type)));
            methods.Add(Method("reflect", type, ("incident", type), ("normal", type)));
            methods.Add(Method("refract", type, ("incident", type), ("normal", type), ("eta", BuiltInTypes.Float)));
        }

        methods.Add(Method("cross", BuiltInTypes.Float3, ("a", BuiltInTypes.Float3), ("b", BuiltInTypes.Float3)));

        foreach (var type in BoolVectors) {
            methods.Add(Method("all", BuiltInTypes.Bool, ("x", type)));
            methods.Add(Method("any", BuiltInTypes.Bool, ("x", type)));
        }

        AddMatrixIntrinsics(methods);
        AddBitCastIntrinsics(methods);
        AddAtomicIntrinsics(methods);
        AddBarrierIntrinsics(methods);

        ByName = methods
            .GroupBy(m => m.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
    }

    /// <summary>Overloads of the intrinsic with this name, or an empty list.</summary>
    public static IReadOnlyList<MethodSymbol> Lookup(string name) =>
        ByName.GetValueOrDefault(name) ?? (IReadOnlyList<MethodSymbol>)[];

    public static bool IsIntrinsic(string name) => ByName.ContainsKey(name);

    static void AddMatrixIntrinsics(List<MethodSymbol> methods) {
        foreach (var matrix in Matrices) {
            // transpose(matRxC) -> matCxR
            if (FindMatrix(matrix.Columns, matrix.Rows) is { } transposed) {
                methods.Add(Method("transpose", transposed, ("m", matrix)));
            }

            methods.Add(Method("mul", matrix, ("m", matrix), ("s", BuiltInTypes.Float)));

            // mul(matRxC, vecC) -> vecR   and   mul(vecR, matRxC) -> vecC.
            // Named for their lane counts, not their roles: vecC has one lane per column, so it is
            // the operand a matrix multiplies and the result of being multiplied by one.
            var vecC = BuiltInTypes.Vector(SpecialType.Float, matrix.Columns);
            var vecR = BuiltInTypes.Vector(SpecialType.Float, matrix.Rows);

            if (vecC is not null && vecR is not null) {
                methods.Add(Method("mul", vecR, ("m", matrix), ("v", vecC)));
                methods.Add(Method("mul", vecC, ("v", vecR), ("m", matrix)));
            }

            // mul(matRxK, matKxC) -> matRxC
            foreach (var other in Matrices) {
                if (matrix.Columns != other.Rows) {
                    continue;
                }

                if (FindMatrix(matrix.Rows, other.Columns) is { } product) {
                    methods.Add(Method("mul", product, ("a", matrix), ("b", other)));
                }
            }
        }
    }

    /// <summary>
    ///     <c>asfloat</c>, <c>asint</c> and <c>asuint</c>: the same bits read as another type.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Not a conversion. <c>int(1.5f)</c> is 1; <c>asint(1.5f)</c> is 1069547520, the
    ///         32 bits of the float read as an integer. What needs it is packing — a normal or a
    ///         colour squeezed into a <c>uint</c> in a storage buffer, then read back — which is
    ///         otherwise impossible to write, since a conversion is the only other way to change a
    ///         value's type and it changes the bits.
    ///     </para>
    ///     <para>
    ///         Lane count is preserved and never changed: reinterpreting across widths is a
    ///         different operation with different alignment rules, and neither target's single
    ///         instruction does it. The identity overloads (<c>asfloat(float)</c>) are declared so a
    ///         generic-looking call site does not have to know which side it is on; they emit
    ///         nothing.
    ///     </para>
    /// </remarks>
    static void AddBitCastIntrinsics(List<MethodSymbol> methods) {
        SpecialType[] components = [SpecialType.Float, SpecialType.Int, SpecialType.UInt];

        foreach (var (name, target) in
                 new[] { ("asfloat", SpecialType.Float), ("asint", SpecialType.Int), ("asuint", SpecialType.UInt) }) {
            for (var lanes = 1; lanes <= 4; lanes++) {
                if (BuiltInTypes.Vector(target, lanes) is not { } returnType) {
                    continue;
                }

                foreach (var component in components) {
                    if (BuiltInTypes.Vector(component, lanes) is { } parameter) {
                        methods.Add(Method(name, returnType, ("x", parameter)));
                    }
                }
            }
        }
    }

    /// <summary>
    ///     The atomics: an indivisible read-modify-write of one integer in a writable resource.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Named as GLSL names them, because that is what both this language's readers and its
    ///         nearer target already say. They are free functions rather than members of
    ///         <c>RWBuffer</c> so that the target can be any place inside one — <c>counts[i]</c>, but
    ///         also <c>cells[i].population</c> — which a member taking an index could not reach.
    ///     </para>
    ///     <para>
    ///         <b>The first parameter is shared storage, not a value</b>, and that is the one thing
    ///         about them that is not ordinary. Nothing in the signature can say so — Raven has
    ///         <c>inout</c>, but <c>inout</c> is copy-in/copy-out by definition and a copy cannot be
    ///         atomic — so the requirement is checked after overload resolution
    ///         (<c>RVN2130</c>) and honoured in lowering, which takes the argument's place instead of
    ///         its value.
    ///     </para>
    ///     <para>
    ///         <b>Scalar integers only.</b> GLSL 4.5 core has no atomic on a float and none on a
    ///         vector, so anything else would be a signature one backend could not emit. The result
    ///         is always the value found there before the operation, which is what makes an atomic
    ///         add an index allocator rather than a counter nobody can read.
    ///     </para>
    ///     <para>
    ///         <b>Both widths, and the 64-bit one is why the width matters.</b> A single-pass
    ///         software rasterizer wants <c>atomicMax</c> on a word packing depth above id: with 32
    ///         bits you get depth <em>or</em> a usable id, and the alternative is two passes over
    ///         the same triangles. Nothing implicitly widens into the 64-bit overloads — see
    ///         <c>Conversions.NumericScalars</c> — so which width an atomic operates at is always
    ///         something the author wrote.
    ///     </para>
    /// </remarks>
    static void AddAtomicIntrinsics(List<MethodSymbol> methods) {
        string[] binary = ["atomicAdd", "atomicMin", "atomicMax", "atomicAnd", "atomicOr", "atomicXor", "atomicExchange"];

        foreach (var type in new[] { BuiltInTypes.Int, BuiltInTypes.UInt, BuiltInTypes.Int64, BuiltInTypes.UInt64 }) {
            foreach (var name in binary) {
                methods.Add(Method(name, type, ("target", type), ("value", type)));
            }

            // Comparand before value, as GLSL's atomicCompSwap orders them. SPIR-V's operands are the
            // other way round, which the emitter deals with rather than the author.
            methods.Add(Method("atomicCompareExchange", type, ("target", type), ("comparand", type), ("value", type)));
        }
    }

    /// <summary>
    ///     The barriers: the two things a workgroup can be made to agree about.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Named as GLSL names them, for the reason the atomics are — and with GLSL's meaning
    ///         rather than a subset of it: in a compute stage <c>barrier()</c> is both an execution
    ///         barrier and a memory barrier over shared storage, which is the only combination that
    ///         makes the code after it correct. Splitting them would offer a primitive whose every
    ///         plausible use is a bug.
    ///     </para>
    ///     <para>
    ///         <b>Nullary and <c>void</c>.</b> There is nothing to pass and nothing to receive; the
    ///         whole effect is on the other invocations of the group, which is why these are the
    ///         only intrinsics with neither arguments nor a result.
    ///     </para>
    ///     <para>
    ///         Legal in a compute stage and nowhere else — refused by lowering
    ///         (<c>RVN3012</c>) rather than here, because which stages reach a function is a
    ///         property of the call graph and not of the file the call is written in.
    ///     </para>
    /// </remarks>
    static void AddBarrierIntrinsics(List<MethodSymbol> methods) {
        methods.Add(Method("barrier", BuiltInTypes.Void));
        methods.Add(Method("memoryBarrierShared", BuiltInTypes.Void));
    }

    /// <summary>Whether a name is one of the barriers, which a non-compute stage may not reach.</summary>
    public static bool IsBarrier(string name) => name is "barrier" or "memoryBarrierShared";

    /// <summary>Whether a name is one of the atomics, whose first argument is storage.</summary>
    /// <remarks>
    ///     Asked by the binder, which has to refuse a non-place, and by the lowerer, which has to take
    ///     the place rather than the value. One list, so the two cannot come to disagree about which
    ///     calls are special.
    /// </remarks>
    public static bool IsAtomic(string name) =>
        name.StartsWith("atomic", StringComparison.Ordinal) && IsIntrinsic(name);

    static PrimitiveTypeSymbol? FindMatrix(int rows, int columns) =>
        Matrices.FirstOrDefault(m => m.Rows == rows && m.Columns == columns);

    static MethodSymbol
        Method(string name, TypeSymbol returnType, params (string Name, TypeSymbol Type)[] parameters) =>
        new SynthesizedMethodSymbol(null, name, returnType, parameters);
}
