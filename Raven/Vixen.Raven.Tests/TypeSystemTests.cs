using Vixen.Raven.Symbols;
using Xunit;
using static Tests.SemanticTestBase;

namespace Tests;

/// <summary>Phase 2a: the built-in type table and the conversion rules over it.</summary>
public class TypeSystemTests {
    [Theory]
    [InlineData("int", TypeKind.Scalar)]
    [InlineData("float", TypeKind.Scalar)]
    [InlineData("float3", TypeKind.Vector)]
    [InlineData("mat3x4", TypeKind.Matrix)]
    [InlineData("Texture2D", TypeKind.Resource)]
    public void Built_in_types_are_resolvable_by_name(string name, TypeKind expected) {
        var type = BuiltInTypes.Lookup(name);
        Assert.NotNull(type);
        Assert.Equal(expected, type.TypeKind);
    }

    /// <summary>
    ///     Square matrices have exactly one spelling: <c>mat2</c>, <c>mat3</c>, <c>mat4</c>.
    ///     The <c>NxN</c> forms are not tokens and are not aliases.
    /// </summary>
    [Theory]
    [InlineData("mat2x2")]
    [InlineData("mat3x3")]
    [InlineData("mat4x4")]
    public void Square_matrices_have_no_NxN_spelling(string name) => Assert.Null(BuiltInTypes.Lookup(name));

    /// <summary>
    ///     End-to-end: <c>mat4x4</c> has no MAT4X4 token, so it reaches the binder as a
    ///     plain identifier and is rejected like any other unknown type name.
    /// </summary>
    [Fact]
    public void Mat4x4_is_not_a_type() {
        var diagnostic = Assert.Single(
            AssertDiagnostics(
                """
                package A

                shader S {
                    var m: mat4x4
                }

                """,
                "RVN2002"
            )
        );

        Assert.Contains("mat4x4", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void Vectors_and_matrices_report_their_shape() {
        Assert.Equal(3, BuiltInTypes.Float3.ComponentCount);
        Assert.Same(BuiltInTypes.Float, BuiltInTypes.Float3.ComponentType);

        Assert.Equal(3, BuiltInTypes.Mat3x4.Rows);
        Assert.Equal(4, BuiltInTypes.Mat3x4.Columns);
        Assert.Equal(12, BuiltInTypes.Mat3x4.ComponentCount);
        Assert.Same(BuiltInTypes.Float, BuiltInTypes.Mat3x4.ComponentType);

        Assert.Same(BuiltInTypes.Int4, BuiltInTypes.Vector(SpecialType.Int, 4));
        Assert.Null(BuiltInTypes.Vector(SpecialType.Bool2, 2));
    }

    [Theory]
    [InlineData("int", "float", ConversionKind.ImplicitNumeric)]
    [InlineData("int", "double", ConversionKind.ImplicitNumeric)]
    [InlineData("float", "double", ConversionKind.ImplicitNumeric)]
    [InlineData("int2", "float2", ConversionKind.ImplicitNumeric)]
    [InlineData("float", "float3", ConversionKind.ImplicitSplat)]
    [InlineData("int", "int", ConversionKind.Identity)]
    [InlineData("double", "int", ConversionKind.ExplicitNumeric)]
    [InlineData("float3", "int3", ConversionKind.ExplicitNumeric)]
    [InlineData("float2", "float3", ConversionKind.None)]
    [InlineData("bool", "int", ConversionKind.None)]
    [InlineData("mat3", "float3", ConversionKind.None)]
    public void Conversions_are_classified(string from, string to, ConversionKind expected) {
        var source = BuiltInTypes.Lookup(from)!;
        var target = BuiltInTypes.Lookup(to)!;

        Assert.Equal(expected, Conversions.Classify(source, target).Kind);
    }


    [Fact]
    public void The_error_type_converts_anywhere_so_one_mistake_reports_once() {
        Assert.True(Conversions.Classify(ErrorTypeSymbol.Instance, BuiltInTypes.Int).IsImplicit);
        Assert.True(Conversions.Classify(BuiltInTypes.Int, ErrorTypeSymbol.Instance).IsImplicit);
    }

    [Fact]
    public void A_common_type_widens_both_operands() {
        Assert.Same(BuiltInTypes.Float, Conversions.FindCommonType(BuiltInTypes.Int, BuiltInTypes.Float));
        Assert.Same(BuiltInTypes.Float3, Conversions.FindCommonType(BuiltInTypes.Float, BuiltInTypes.Float3));
        Assert.Same(BuiltInTypes.Float2, Conversions.FindCommonType(BuiltInTypes.Int2, BuiltInTypes.Float2));
        Assert.Null(Conversions.FindCommonType(BuiltInTypes.Float2, BuiltInTypes.Float3));
    }

    [Fact]
    public void Structural_types_compare_by_shape_not_identity() {
        Assert.Equal(new(BuiltInTypes.Int), new ArrayTypeSymbol(BuiltInTypes.Int));
        Assert.NotEqual(new(BuiltInTypes.Int), new ArrayTypeSymbol(BuiltInTypes.Float));
        Assert.NotEqual(new(BuiltInTypes.Int), new ArrayTypeSymbol(BuiltInTypes.Int, 2));

        // Element names are not part of tuple identity, matching C#.
        Assert.Equal(
            new([BuiltInTypes.Int, BuiltInTypes.Float], ["code", "value"]),
            new TupleTypeSymbol([BuiltInTypes.Int, BuiltInTypes.Float], [null, null])
        );
    }

    [Fact]
    public void Display_strings_read_like_source() {
        Assert.Equal("int[]", new ArrayTypeSymbol(BuiltInTypes.Int).ToDisplayString());
        Assert.Equal("int[,]", new ArrayTypeSymbol(BuiltInTypes.Int, 2).ToDisplayString());
        Assert.Equal(
            "(code: int, float)",
            new TupleTypeSymbol([BuiltInTypes.Int, BuiltInTypes.Float], ["code", null]).ToDisplayString()
        );
        Assert.Equal("int..", new SequenceTypeSymbol(BuiltInTypes.Int).ToDisplayString());
    }

    [Fact]
    public void Swizzle_members_are_writable_only_when_the_components_are_distinct() {
        var repeated = Assert.IsType<SynthesizedFieldSymbol>(Assert.Single(BuiltInTypes.Float4.GetMembers("xx")));
        var distinct = Assert.IsType<SynthesizedFieldSymbol>(Assert.Single(BuiltInTypes.Float4.GetMembers("xy")));

        Assert.True(repeated.IsReadOnly);
        Assert.False(distinct.IsReadOnly);

        // Out of range for the vector's width, and mixed component sets.
        Assert.Empty(BuiltInTypes.Float2.GetMembers("z"));
        Assert.Empty(BuiltInTypes.Float4.GetMembers("xg"));
        Assert.Empty(BuiltInTypes.Float4.GetMembers("nope"));
    }
}
