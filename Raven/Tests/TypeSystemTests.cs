using Vixen.Raven.Symbols;
using Xunit;

namespace Tests;

/// <summary>Phase 2a: the built-in type table and the conversion rules over it.</summary>
public class TypeSystemTests {
    [Theory]
    [InlineData("int", TypeKind.Scalar)]
    [InlineData("float", TypeKind.Scalar)]
    [InlineData("float3", TypeKind.Vector)]
    [InlineData("mat3x4", TypeKind.Matrix)]
    [InlineData("string", TypeKind.Class)]
    [InlineData("Texture2D", TypeKind.Resource)]
    public void Built_in_types_are_resolvable_by_name(string name, TypeKind expected) {
        var type = BuiltInTypes.Lookup(name);
        Assert.NotNull(type);
        Assert.Equal(expected, type.TypeKind);
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
        Assert.Null(BuiltInTypes.Vector(SpecialType.String, 2));
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
    [InlineData("string", "int", ConversionKind.None)]
    public void Conversions_are_classified(string from, string to, ConversionKind expected) {
        var source = BuiltInTypes.Lookup(from)!;
        var target = BuiltInTypes.Lookup(to)!;

        Assert.Equal(expected, Conversions.Classify(source, target).Kind);
    }

    [Fact]
    public void Nullable_wrapping_and_the_null_literal_convert_as_expected() {
        var nullableInt = new NullableTypeSymbol(BuiltInTypes.Int);

        Assert.Equal(ConversionKind.ImplicitNullable, Conversions.Classify(BuiltInTypes.Int, nullableInt).Kind);
        Assert.Equal(ConversionKind.ExplicitNullable, Conversions.Classify(nullableInt, BuiltInTypes.Int).Kind);
        Assert.Equal(
            ConversionKind.ImplicitNullLiteral,
            Conversions.Classify(NullTypeSymbol.Instance, nullableInt).Kind);

        Assert.False(Conversions.AdmitsNull(BuiltInTypes.Int));
        Assert.True(Conversions.AdmitsNull(nullableInt));
        Assert.True(Conversions.AdmitsNull(BuiltInTypes.String));
    }

    [Fact]
    public void Boxing_to_object_exists_but_costs_more_than_widening() {
        var boxing = Conversions.Classify(BuiltInTypes.Int, BuiltInTypes.Object);
        var widening = Conversions.Classify(BuiltInTypes.Int, BuiltInTypes.Float);

        Assert.Equal(ConversionKind.Boxing, boxing.Kind);
        Assert.True(boxing.IsImplicit);
        Assert.True(widening.Cost < boxing.Cost);
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
        Assert.Equal(new ArrayTypeSymbol(BuiltInTypes.Int), new ArrayTypeSymbol(BuiltInTypes.Int));
        Assert.NotEqual(new ArrayTypeSymbol(BuiltInTypes.Int), new ArrayTypeSymbol(BuiltInTypes.Float));
        Assert.NotEqual(new ArrayTypeSymbol(BuiltInTypes.Int), new ArrayTypeSymbol(BuiltInTypes.Int, 2));

        Assert.Equal(new NullableTypeSymbol(BuiltInTypes.Int), new NullableTypeSymbol(BuiltInTypes.Int));

        // Element names are not part of tuple identity, matching C#.
        Assert.Equal(
            new TupleTypeSymbol([BuiltInTypes.Int, BuiltInTypes.String], ["code", "message"]),
            new TupleTypeSymbol([BuiltInTypes.Int, BuiltInTypes.String], [null, null]));
    }

    [Fact]
    public void Display_strings_read_like_source() {
        Assert.Equal("int[]", new ArrayTypeSymbol(BuiltInTypes.Int).ToDisplayString());
        Assert.Equal("int[,]", new ArrayTypeSymbol(BuiltInTypes.Int, 2).ToDisplayString());
        Assert.Equal("int?", new NullableTypeSymbol(BuiltInTypes.Int).ToDisplayString());
        Assert.Equal(
            "(code: int, string)",
            new TupleTypeSymbol([BuiltInTypes.Int, BuiltInTypes.String], ["code", null]).ToDisplayString());
        Assert.Equal(
            "(int) -> bool",
            new FunctionTypeSymbol([BuiltInTypes.Int], BuiltInTypes.Bool).ToDisplayString());
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
