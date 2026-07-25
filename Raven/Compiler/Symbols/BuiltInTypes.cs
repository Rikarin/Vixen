using Vixen.Raven.Syntax;

namespace Vixen.Raven.Symbols;

/// <summary>
///     The compiler's intrinsic type table: scalars, vectors, matrices and the GPU
///     resource types — everything Raven has, because everything Raven has must
///     exist on a GPU. All instances are singletons, so reference equality is type
///     identity for everything in here.
/// </summary>
public static class BuiltInTypes {
    public static readonly PrimitiveTypeSymbol Void = new("void", SpecialType.Void, TypeKind.Void);

    public static readonly PrimitiveTypeSymbol Bool = new("bool", SpecialType.Bool, TypeKind.Scalar);
    public static readonly PrimitiveTypeSymbol Int = new("int", SpecialType.Int, TypeKind.Scalar);
    public static readonly PrimitiveTypeSymbol UInt = new("uint", SpecialType.UInt, TypeKind.Scalar);
    public static readonly PrimitiveTypeSymbol Float = new("float", SpecialType.Float, TypeKind.Scalar);
    public static readonly PrimitiveTypeSymbol Double = new("double", SpecialType.Double, TypeKind.Scalar);

    public static readonly PrimitiveTypeSymbol Bool2 = Vec("bool2", SpecialType.Bool2, SpecialType.Bool, 2);
    public static readonly PrimitiveTypeSymbol Bool3 = Vec("bool3", SpecialType.Bool3, SpecialType.Bool, 3);
    public static readonly PrimitiveTypeSymbol Bool4 = Vec("bool4", SpecialType.Bool4, SpecialType.Bool, 4);
    public static readonly PrimitiveTypeSymbol Int2 = Vec("int2", SpecialType.Int2, SpecialType.Int, 2);
    public static readonly PrimitiveTypeSymbol Int3 = Vec("int3", SpecialType.Int3, SpecialType.Int, 3);
    public static readonly PrimitiveTypeSymbol Int4 = Vec("int4", SpecialType.Int4, SpecialType.Int, 4);
    public static readonly PrimitiveTypeSymbol UInt2 = Vec("uint2", SpecialType.UInt2, SpecialType.UInt, 2);
    public static readonly PrimitiveTypeSymbol UInt3 = Vec("uint3", SpecialType.UInt3, SpecialType.UInt, 3);
    public static readonly PrimitiveTypeSymbol UInt4 = Vec("uint4", SpecialType.UInt4, SpecialType.UInt, 4);
    public static readonly PrimitiveTypeSymbol Float2 = Vec("float2", SpecialType.Float2, SpecialType.Float, 2);
    public static readonly PrimitiveTypeSymbol Float3 = Vec("float3", SpecialType.Float3, SpecialType.Float, 3);
    public static readonly PrimitiveTypeSymbol Float4 = Vec("float4", SpecialType.Float4, SpecialType.Float, 4);
    public static readonly PrimitiveTypeSymbol Double2 = Vec("double2", SpecialType.Double2, SpecialType.Double, 2);
    public static readonly PrimitiveTypeSymbol Double3 = Vec("double3", SpecialType.Double3, SpecialType.Double, 3);
    public static readonly PrimitiveTypeSymbol Double4 = Vec("double4", SpecialType.Double4, SpecialType.Double, 4);

    public static readonly PrimitiveTypeSymbol Mat2 = Mat("mat2", SpecialType.Mat2, 2, 2);
    public static readonly PrimitiveTypeSymbol Mat2x3 = Mat("mat2x3", SpecialType.Mat2x3, 2, 3);
    public static readonly PrimitiveTypeSymbol Mat2x4 = Mat("mat2x4", SpecialType.Mat2x4, 2, 4);
    public static readonly PrimitiveTypeSymbol Mat3 = Mat("mat3", SpecialType.Mat3, 3, 3);
    public static readonly PrimitiveTypeSymbol Mat3x2 = Mat("mat3x2", SpecialType.Mat3x2, 3, 2);
    public static readonly PrimitiveTypeSymbol Mat3x4 = Mat("mat3x4", SpecialType.Mat3x4, 3, 4);
    public static readonly PrimitiveTypeSymbol Mat4 = Mat("mat4", SpecialType.Mat4, 4, 4);
    public static readonly PrimitiveTypeSymbol Mat4x2 = Mat("mat4x2", SpecialType.Mat4x2, 4, 2);
    public static readonly PrimitiveTypeSymbol Mat4x3 = Mat("mat4x3", SpecialType.Mat4x3, 4, 3);

    public static readonly BuiltInNamedTypeSymbol Sampler =
        new("Sampler", SpecialType.Sampler, TypeKind.Resource, ResourceKind.Sampler);

    public static readonly BuiltInNamedTypeSymbol Texture2D =
        new("Texture2D", SpecialType.Texture2D, TypeKind.Resource, ResourceKind.Texture);

    public static readonly BuiltInNamedTypeSymbol Texture3D =
        new("Texture3D", SpecialType.Texture3D, TypeKind.Resource, ResourceKind.Texture);

    public static readonly BuiltInNamedTypeSymbol TextureCube =
        new("TextureCube", SpecialType.TextureCube, TypeKind.Resource, ResourceKind.Texture);

    static readonly Dictionary<string, NamedTypeSymbol> byName;
    static readonly Dictionary<SpecialType, PrimitiveTypeSymbol> bySpecialType;
    static readonly Dictionary<SyntaxKind, PrimitiveTypeSymbol> byKeyword;

    /// <summary>Every intrinsic type, for scope population and tests.</summary>
    public static IReadOnlyCollection<NamedTypeSymbol> All => byName.Values;

    static BuiltInTypes() {
        PrimitiveTypeSymbol[] primitives = [
            Void, Bool, Int, UInt, Float, Double,
            Bool2, Bool3, Bool4, Int2, Int3, Int4, UInt2, UInt3, UInt4,
            Float2, Float3, Float4, Double2, Double3, Double4,
            Mat2, Mat2x3, Mat2x4, Mat3, Mat3x2, Mat3x4, Mat4, Mat4x2, Mat4x3
        ];

        NamedTypeSymbol[] named = [Sampler, Texture2D, Texture3D, TextureCube];

        byName = new(StringComparer.Ordinal);
        bySpecialType = [];

        foreach (var type in primitives) {
            byName[type.Name] = type;
            bySpecialType[type.SpecialType] = type;
        }

        foreach (var type in named) {
            byName[type.Name] = type;
        }

        // `mat4x4` is not a lexer keyword; `mat4` is the 4×4 matrix. The alias
        // keeps a name-based lookup (imports, docs) from surprising anyone.
        byName["mat4x4"] = Mat4;

        byKeyword = new() {
            [SyntaxKind.BoolKeyword] = Bool,
            [SyntaxKind.Bool2Keyword] = Bool2,
            [SyntaxKind.Bool3Keyword] = Bool3,
            [SyntaxKind.Bool4Keyword] = Bool4,
            [SyntaxKind.IntKeyword] = Int,
            [SyntaxKind.Int2Keyword] = Int2,
            [SyntaxKind.Int3Keyword] = Int3,
            [SyntaxKind.Int4Keyword] = Int4,
            [SyntaxKind.UIntKeyword] = UInt,
            [SyntaxKind.UInt2Keyword] = UInt2,
            [SyntaxKind.UInt3Keyword] = UInt3,
            [SyntaxKind.UInt4Keyword] = UInt4,
            [SyntaxKind.FloatKeyword] = Float,
            [SyntaxKind.Float2Keyword] = Float2,
            [SyntaxKind.Float3Keyword] = Float3,
            [SyntaxKind.Float4Keyword] = Float4,
            [SyntaxKind.DoubleKeyword] = Double,
            [SyntaxKind.Double2Keyword] = Double2,
            [SyntaxKind.Double3Keyword] = Double3,
            [SyntaxKind.Double4Keyword] = Double4,
            [SyntaxKind.Mat2Keyword] = Mat2,
            [SyntaxKind.Mat2x3Keyword] = Mat2x3,
            [SyntaxKind.Mat2x4Keyword] = Mat2x4,
            [SyntaxKind.Mat3Keyword] = Mat3,
            [SyntaxKind.Mat3x2Keyword] = Mat3x2,
            [SyntaxKind.Mat3x4Keyword] = Mat3x4,
            [SyntaxKind.Mat4Keyword] = Mat4,
            [SyntaxKind.Mat4x2Keyword] = Mat4x2,
            [SyntaxKind.Mat4x3Keyword] = Mat4x3
        };

        AddResourceMembers();
    }

    /// <summary>Resolves an intrinsic type by its source name (<c>float3</c>, <c>Texture2D</c>).</summary>
    public static NamedTypeSymbol? Lookup(string name) => byName.GetValueOrDefault(name);

    /// <summary>The primitive type for a <see cref="SpecialType" />; throws for non-primitives.</summary>
    public static PrimitiveTypeSymbol FromSpecialType(SpecialType specialType) => bySpecialType[specialType];

    /// <summary>The type behind a <c>PredefinedType</c> keyword token, or null.</summary>
    public static PrimitiveTypeSymbol? FromKeyword(SyntaxKind kind) => byKeyword.GetValueOrDefault(kind);

    /// <summary>The vector of <paramref name="component" /> with this many lanes, or null if there is none.</summary>
    public static PrimitiveTypeSymbol? Vector(SpecialType component, int count) {
        if (count == 1) {
            return bySpecialType.GetValueOrDefault(component);
        }

        var name = component switch {
            SpecialType.Bool => "bool",
            SpecialType.Int => "int",
            SpecialType.UInt => "uint",
            SpecialType.Float => "float",
            SpecialType.Double => "double",
            _ => null
        };

        return name is null || count is < 2 or > 4
            ? null
            : (PrimitiveTypeSymbol?)byName.GetValueOrDefault(name + count);
    }

    static PrimitiveTypeSymbol Vec(string name, SpecialType specialType, SpecialType component, int count) =>
        new(name, specialType, TypeKind.Vector, component, count);

    static PrimitiveTypeSymbol Mat(string name, SpecialType specialType, int rows, int columns) =>
        new(name, specialType, TypeKind.Matrix, SpecialType.Float, rows, columns);

    static void AddResourceMembers() {
        Texture2D.SetMembers(
            [
                new SynthesizedMethodSymbol(Texture2D, "Sample", Float4, [("sampler", Sampler), ("uv", Float2)]),
                new SynthesizedMethodSymbol(Texture2D, "Load", Float4, [("coordinate", Int3)])
            ]
        );

        Texture3D.SetMembers(
            [
                new SynthesizedMethodSymbol(Texture3D, "Sample", Float4, [("sampler", Sampler), ("uvw", Float3)])
            ]
        );

        TextureCube.SetMembers(
            [
                new SynthesizedMethodSymbol(
                    TextureCube,
                    "Sample",
                    Float4,
                    [("sampler", Sampler), ("direction", Float3)]
                )
            ]
        );
    }
}
