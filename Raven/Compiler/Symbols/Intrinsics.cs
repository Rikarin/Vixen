namespace Vixen.Raven.Symbols;

/// <summary>
/// The built-in shader function library. Every entry is a
/// <see cref="MethodSymbol"/> in the global scope, so calls to <c>dot</c> or
/// <c>normalize</c> go through ordinary overload resolution and end up in the
/// bound tree like any other call.
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

        ByName = methods
            .GroupBy(m => m.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
    }

    /// <summary>Every intrinsic overload, for scope population and tests.</summary>
    public static IEnumerable<MethodSymbol> All => ByName.Values.SelectMany(m => m);

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

            // mul(matRxC, vecC) -> vecR   and   mul(vecR, matRxC) -> vecC
            var columnVector = BuiltInTypes.Vector(SpecialType.Float, matrix.Columns);
            var rowVector = BuiltInTypes.Vector(SpecialType.Float, matrix.Rows);

            if (columnVector is not null && rowVector is not null) {
                methods.Add(Method("mul", rowVector, ("m", matrix), ("v", columnVector)));
                methods.Add(Method("mul", columnVector, ("v", rowVector), ("m", matrix)));
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

    static PrimitiveTypeSymbol? FindMatrix(int rows, int columns) =>
        Matrices.FirstOrDefault(m => m.Rows == rows && m.Columns == columns);

    static MethodSymbol Method(string name, TypeSymbol returnType, params (string Name, TypeSymbol Type)[] parameters) =>
        new SynthesizedMethodSymbol(null, name, returnType, parameters);
}
