// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax;
using Vixen.Core.Syntax.Diagnostics;
using Vixen.Raven;
using Vixen.Raven.Artefacts;
using Vixen.Raven.CodeGen;
using Vixen.Raven.Diagnostics;
using Vixen.Raven.IR;
using Vixen.Raven.Lowering;
using Vixen.Raven.Reflection;
using Vixen.Raven.Symbols;
using Vixen.Raven.Syntax;
using Xunit;

namespace Tests;

/// <summary>
///     Sized array types: <c>float4[4]</c>, <c>mat4[MaxBones]</c>, and the length as part of the type.
/// </summary>
/// <remarks>
///     <para>
///         The length is not a detail of an array type, it <em>is</em> part of it. Everything downstream
///         needs it: SPIR-V's <c>OpTypeArray</c> takes a constant extent, GLSL writes it into the
///         declaration, <c>ArrayStride</c> is computed from it, and the host reads it back out of the
///         reflection to size the buffer it uploads. So <c>float[4]</c> and <c>float[]</c> are
///         different types and neither converts to the other — an unsized array is a type no backend
///         can express, and converting into one would only be a way to fail later.
///     </para>
///     <para>
///         The one ambiguity a size introduces is <c>a[4]</c>: an element access, or an array type? Both
///         parsers decide it by <em>position</em> and never by what is between the brackets — in a type
///         position <c>[…]</c> sizes, in an expression it indexes. <see cref="ParserDifferentialTests" />
///         holds the ANTLR grammar to the same split.
///     </para>
/// </remarks>
public class SizedArrayTests {
    const string Probe = """
                         package A

                         shader Blur {
                             const val Taps = 4

                             var offsets: float4[Taps]
                             var weights: float[8]

                             [PixelShader]
                             [Semantic("SV_Target")]
                             func Pixel(): float4 {
                                 var sum = float4(0f, 0f, 0f, 0f)
                                 for (i in 0 .. Taps - 1) {
                                     sum = sum + offsets[i] * weights[i]
                                 }

                                 return sum
                             }
                         }

                         """;

    // --- The front end ----------------------------------------------------

    [Fact]
    public void ASizedArrayParsesAndRoundTrips() {
        var tree = SyntaxTree.ParseText(Probe, path: "Test.rvn");
        Assert.Empty(tree.Diagnostics);
        Assert.Equal(Probe, tree.GetRoot().ToFullString());
    }

    /// <summary>
    ///     The size is any constant expression, not only a literal.
    /// </summary>
    /// <remarks>
    ///     A <c>const</c>, an enum member and a <c>[Permutation] val</c> all qualify. The last is the
    ///     interesting one: it lets the <em>host</em> choose the length, which is what makes a light
    ///     list or a bone palette a budget rather than a hard-coded number.
    /// </remarks>
    [Theory]
    [InlineData("const val N = 6", "float[6]", 6)]
    [InlineData("const val N = 6", "float[N]", 6)]
    [InlineData("const val N = 3", "float[N * 2]", 6)]
    [InlineData("const val N = 3", "float[N + N]", 6)]
    [InlineData("[Permutation] val N: int = 6", "float[N]", 6)]
    public void TheSizeIsAConstantExpression(string declaration, string type, int expected) {
        var array = Assert.IsType<ArrayTypeSymbol>(FieldOf($"{declaration}\n    var data: {type}", "data").Type);
        Assert.Equal(expected, array.Length);
    }

    /// <summary>
    ///     A size the compiler cannot fold is refused, because a GPU allocates nothing at run time.
    /// </summary>
    [Fact]
    public void ANonConstantSizeIsRefused() {
        var diagnostics = Compile(
            """
            package A

            shader S {
                var count: int
                var data: float[count]
            }

            """
        );

        Assert.Contains(diagnostics, d => d.Id == "RVN2115" && d.IsError);
    }

    /// <summary>
    ///     Zero is refused along with the negatives: <c>OpTypeArray</c> requires a length greater than
    ///     zero, and GLSL rejects a zero-length array too. Caught here so the two cannot disagree.
    /// </summary>
    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("2 - 2")]
    public void ANonPositiveSizeIsRefused(string size) {
        var diagnostics = Compile(
            $$"""
              package A

              shader S {
                  var data: float[{{size}}]
              }

              """
        );

        Assert.Contains(diagnostics, d => d.Id == "RVN2116" && d.IsError);
    }

    /// <summary>
    ///     A sized array and an unsized one are different types, and neither converts to the other.
    /// </summary>
    /// <remarks>
    ///     Pinned because the tempting alternative — letting <c>T[4]</c> widen to <c>T[]</c> — would
    ///     let code bind and then fail in the backend, since an unsized array is <c>RVN4001</c> in
    ///     both. A declaration you cannot lower is not a useful thing to convert into.
    /// </remarks>
    [Fact]
    public void ASizedArrayIsNotAnUnsizedOne() {
        Assert.NotEqual(new(BuiltInTypes.Float, 1, 4), new ArrayTypeSymbol(BuiltInTypes.Float));
        Assert.NotEqual(new(BuiltInTypes.Float, 1, 4), new ArrayTypeSymbol(BuiltInTypes.Float, 1, 8));
        Assert.Equal(new(BuiltInTypes.Float, 1, 4), new ArrayTypeSymbol(BuiltInTypes.Float, 1, 4));

        Assert.Equal("float[4]", new ArrayTypeSymbol(BuiltInTypes.Float, 1, 4).ToDisplayString());
        Assert.Equal("float[]", new ArrayTypeSymbol(BuiltInTypes.Float).ToDisplayString());

        var diagnostics = Compile(
            """
            package A

            struct S {
                static func Make(): float[] => [1f, 2f]
            }

            """
        );

        Assert.Contains(diagnostics, d => d.Id == "RVN2020" && d.IsError);
    }

    /// <summary>
    ///     A multi-dimensional array is never sized, and two rank specifiers nest right to left.
    /// </summary>
    /// <remarks>
    ///     <c>float[2][3]</c> is two arrays of three, the reading C and GLSL give it — and the reading
    ///     the GLSL declaration <c>float name[2][3]</c> has to match. The order is only observable once
    ///     a rank carries a size, which is why it was free to be either way before.
    /// </remarks>
    [Fact]
    public void RanksNestRightToLeft() {
        var outer = Assert.IsType<ArrayTypeSymbol>(FieldOf("var data: float[2][3]", "data").Type);
        Assert.Equal(2, outer.Length);

        var inner = Assert.IsType<ArrayTypeSymbol>(outer.ElementType);
        Assert.Equal(3, inner.Length);

        // `[,]` adds a dimension instead of a size, so there is nothing to be sized.
        var rank2 = Assert.IsType<ArrayTypeSymbol>(FieldOf("var grid: float[,]", "grid").Type);
        Assert.Equal(2, rank2.Rank);
        Assert.Null(rank2.Length);
    }

    /// <summary>A sized array's <c>Length</c> is a constant, so it can size another array.</summary>
    [Fact]
    public void LengthIsAConstant() {
        var length = Assert.IsType<ArrayTypeSymbol>(FieldOf("var data: float[5]", "data").Type)
            .GetMembers()
            .OfType<FieldSymbol>()
            .Single(member => member.Name == "Length");

        Assert.True(length.IsConst);
        Assert.Equal(5, length.ConstantValue);

        // And nothing is claimed for an unsized one.
        Assert.Null(
            Assert.IsType<ArrayTypeSymbol>(FieldOf("var data: float[]", "data").Type)
                .GetMembers()
                .OfType<FieldSymbol>()
                .Single(member => member.Name == "Length")
                .ConstantValue
        );
    }

    // --- Bounds -----------------------------------------------------------

    /// <summary>
    ///     A constant index outside the array is a diagnostic, not undefined behaviour.
    /// </summary>
    /// <remarks>
    ///     Out of bounds on a GPU means a wrong pixel on one driver and a device loss on another. When
    ///     both the index and the length are known there is no reason to find out which. A
    ///     <em>runtime</em> index says nothing and is left alone — this is a certainty check, not a
    ///     bounds analysis.
    /// </remarks>
    [Theory]
    [InlineData("data[4]", true)]
    [InlineData("data[99]", true)]
    [InlineData("data[3]", false)]
    [InlineData("data[0]", false)]
    public void AConstantIndexIsCheckedAgainstTheLength(string access, bool refused) {
        var diagnostics = Compile(
            $$"""
              package A

              shader S {
                  var data: float[4]

                  [PixelShader]
                  [Semantic("SV_Target")]
                  func Pixel(): float4 {
                      return float4({{access}})
                  }
              }

              """
        );

        Assert.Equal(refused, diagnostics.Any(d => d.Id == "RVN2117" && d.IsError));
    }

    [Fact]
    public void ARuntimeIndexIsNotChecked() {
        var diagnostics = Compile(
            """
            package A

            shader S {
                var data: float[4]
                var which: int

                [PixelShader]
                [Semantic("SV_Target")]
                func Pixel(): float4 {
                    return float4(data[which])
                }
            }

            """
        );

        Assert.DoesNotContain(diagnostics, d => d.Id == "RVN2117");
    }

    // --- Collection expressions -------------------------------------------

    /// <summary>
    ///     A collection literal infers its own length, and a spread contributes its own count.
    /// </summary>
    /// <remarks>
    ///     This is what makes a literal lowerable at all. Flattening <c>[1, ..xs, 5]</c> means emitting
    ///     one element per index of <c>xs</c>, which needs <c>xs</c>'s length — it was <c>RVN3002</c>
    ///     until an array type had one.
    /// </remarks>
    [Fact]
    public void ACollectionLiteralInfersItsLength() {
        Assert.Equal(3, LengthOfLocal("val xs = [1f, 2f, 3f]"));
        Assert.Equal(6, LengthOfLocal("val ys = [1f, 2f]\n        val xs = [0f, ..ys, ..ys, 5f]"));
    }

    /// <summary>A spread of an <em>unsized</em> array still cannot be flattened.</summary>
    [Fact]
    public void ASpreadOfAnUnsizedArrayIsRefusedByLowering() {
        var bag = new DiagnosticBag();
        var compilation = Compilation.Create(
            "Test",
            SyntaxTree.ParseText(
                """
                package A

                shader S {
                    var loose: float[]

                    [PixelShader]
                    [Semantic("SV_Target")]
                    func Pixel(): float4 {
                        val xs = [0f, ..loose]
                        return float4(xs[0])
                    }
                }

                """,
                path: "Test.rvn"
            )
        );

        Assert.Empty(compilation.GetDiagnostics());
        Lowerer.Lower(compilation, bag);

        Assert.Contains(bag.ToArray(), d => d.Id == "RVN3002" && d.IsError);
    }

    // --- Lowering and layout ----------------------------------------------

    [Fact]
    public void TheLengthReachesTheIr() {
        var offsets = FindGlobal(Lower(Probe), "offsets");
        var array = Assert.IsType<IrArrayType>(offsets.Type);

        Assert.Equal(4, array.Length);
        Assert.Equal("array<vec<f32,4>,4>", array.Name);
    }

    /// <summary>
    ///     Every array in a uniform block reports an <c>ArrayStride</c>, and std140 rounds it to 16.
    /// </summary>
    /// <remarks>
    ///     The round-up is exactly what std430 drops, and it is the rule that surprises people: an
    ///     array of <c>float</c> costs four bytes per element on the host and sixteen in the block. The
    ///     reflection and the SPIR-V decoration are computed by the same <see cref="ShaderLayout" />,
    ///     which is what stops them disagreeing.
    /// </remarks>
    [Fact]
    public void TheReflectionReportsTheStride() {
        var reflection = ReflectionBuilder.Describe(Lower(Probe).Shaders.Single());
        var members = reflection.Sets.SelectMany(set => set.Bindings).SelectMany(b => b.Members).ToArray();

        var offsets = members.Single(m => m.Name == "offsets");
        Assert.Equal(16, offsets.ArrayStride);
        Assert.Equal(64, offsets.Size);

        // A float array pays the same 16 per element — the std140 round-up, not a bug.
        var weights = members.Single(m => m.Name == "weights");
        Assert.Equal(16, weights.ArrayStride);
        Assert.Equal(128, weights.Size);
        Assert.Equal(64, weights.Offset);
    }

    // --- Both backends ----------------------------------------------------

    /// <summary>
    ///     GLSL puts the extents after the name in a declaration and before it in a constructor.
    /// </summary>
    /// <remarks>
    ///     Both are legal GLSL for the same type, which is why the emitter has two spellings rather
    ///     than one: <c>float weights[8]</c> declares, <c>float[4](…)</c> constructs.
    /// </remarks>
    [Fact]
    public void GlslDeclaresAndConstructsArrays() {
        var glsl = CodeGenTestBase.GenerateOne(Probe);

        Assert.Contains("vec4 offsets[4];", glsl, StringComparison.Ordinal);
        Assert.Contains("float weights[8];", glsl, StringComparison.Ordinal);
        Assert.Contains("offsets[", glsl, StringComparison.Ordinal);
    }

    [Fact]
    public void GlslConstructsALiteral() {
        var glsl = CodeGenTestBase.GeneratePixel(
            """
                    val xs = [1f, 2f, 3f, 4f]
                    return float4(xs[0], xs[1], xs[2], xs[3])
            """
        );

        Assert.Contains("float[4](1.0, 2.0, 3.0, 4.0)", glsl, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A nested array is one declaration with two extents, not a nested type name.
    /// </summary>
    [Fact]
    public void GlslNestsExtentsInOneDeclaration() {
        var glsl = CodeGenTestBase.GenerateOne(
            """
            package A

            shader S {
                var stack: float[2][3]

                [PixelShader]
                [Semantic("SV_Target")]
                func Pixel(): float4 {
                    return float4(stack[0][1])
                }
            }

            """
        );

        Assert.Contains("float stack[2][3];", glsl, StringComparison.Ordinal);
    }

    [Fact]
    public void SpirvDecoratesTheStrideAndTakesAConstantExtent() {
        var unit = Generate(Probe, "spirv").Single();

        Assert.Contains("OpTypeArray", unit.Code, StringComparison.Ordinal);
        Assert.Contains("ArrayStride 16", unit.Code, StringComparison.Ordinal);
        SpirvTestBase.Validate(unit);
    }

    /// <summary>
    ///     Reading a whole struct out of a uniform block, which an array of structs makes routine.
    /// </summary>
    /// <remarks>
    ///     A block's struct is declared with <c>Offset</c> decorations, which makes it a distinct
    ///     <c>OpTypeStruct</c> from the one a local of the same Raven type gets, and SPIR-V has no
    ///     conversion between two struct types. So <c>lights[i]</c> is not one <c>OpLoad</c>: each leaf
    ///     is loaded through its own access chain and the aggregate rebuilt with
    ///     <c>OpCompositeConstruct</c>. This was <c>RVN4002</c> until a light list needed it.
    /// </remarks>
    [Fact]
    public void SpirvReadsAStructOutOfAnArrayInAUniformBlock() {
        var unit = Generate(LightLoop, "spirv").Single();

        Assert.Contains("OpCompositeConstruct", unit.Code, StringComparison.Ordinal);
        SpirvTestBase.Validate(unit);
    }

    /// <summary>
    ///     A member that is an <em>array of</em> matrices is a matrix member for layout purposes.
    /// </summary>
    /// <remarks>
    ///     Found by <c>Library/Pipeline/ShadowCaster.rvn</c>'s <c>mat4[256]</c> bone palette:
    ///     <c>spirv-val</c> rejected the module outright — "Structure decorated as Block must be
    ///     explicitly laid out with MatrixStride decorations" — because the decoration was only
    ///     written for a member whose own type was a matrix.
    /// </remarks>
    [Fact]
    public void SpirvDecoratesAMatrixArrayWithItsStride() {
        var source = """
                     package A

                     shader S {
                         var bones: mat4[4]

                         [VertexShader]
                         [Semantic("SV_Position")]
                         func Vertex(position: float3): float4 {
                             return bones[1] * float4(position, 1f)
                         }
                     }

                     """;

        var unit = Generate(source, "spirv").Single();
        Assert.Contains("MatrixStride 16", unit.Code, StringComparison.Ordinal);
        SpirvTestBase.Validate(unit);
    }

    /// <summary>
    ///     Both reference compilers accept every shape: a sized uniform, a local, a literal, a spread,
    ///     an array of structs and an array of matrices.
    /// </summary>
    /// <remarks>
    ///     The verdict that matters. Raven's own opinion about what is legal GLSL or valid SPIR-V is
    ///     worth nothing next to a full front end reading every line the emitter produced.
    /// </remarks>
    [Fact]
    public void ReferenceToolsAcceptEveryShape() {
        Assert.SkipUnless(ReferenceCompiler.Available, "glslc is not on PATH (brew install shaderc).");

        foreach (var source in new[] { Probe, LightLoop }) {
            foreach (var unit in Generate(source, "glsl")) {
                Assert.NotEmpty(ReferenceCompiler.GlslToSpirv(unit.Code, unit.Stage));
            }

            foreach (var unit in Generate(source, "spirv")) {
                SpirvTestBase.Validate(unit);
            }
        }
    }

    // --- Across a .rvnlib boundary ----------------------------------------

    /// <summary>
    ///     A sized array survives compilation to a library and back, as a parameter and as a return
    ///     type.
    /// </summary>
    /// <remarks>
    ///     The length has to be in the metadata, not only the IR: a reference resolves signatures out
    ///     of the symbol layer, and a signature that lost its length would resolve to a type the source
    ///     never declared — silently, and only until something tried to emit it.
    /// </remarks>
    [Fact]
    public void ASizedArrayCrossesALibraryBoundary() {
        var library = BuildLibrary(
            """
            package A.Lib

            struct Windows {
                static func Blackman(): float[3] => [0.42f, 0.5f, 0.08f]

                static func Sum(kernel: float[3], samples: float[3]): float {
                    var total = 0f
                    for (i in 0 .. 2) {
                        total = total + kernel[i] * samples[i]
                    }

                    return total
                }
            }

            """
        );

        var compilation = Compilation.Create(
            "Use",
            [RavenReference.FromLibrary(CompiledLibraryReader.Read(CompiledLibraryWriter.Write(library)))],
            [
                SyntaxTree.ParseText(
                    """
                    package A.Use

                    import A.Lib

                    shader S {
                        [PixelShader]
                        [Semantic("SV_Target")]
                        func Pixel(): float4 {
                            val k = Windows.Blackman()
                            return float4(Windows.Sum(k, [1f, 2f, 3f]))
                        }
                    }

                    """,
                    path: "Use.rvn"
                )
            ]
        );

        Assert.Empty(compilation.GetDiagnostics());

        // The length came back through the *metadata*, not merely through the IR: a reference
        // resolves signatures out of the symbol layer, so a length lost here would silently
        // resolve to a type the source never declared.
        var windows = Assert.Single(compilation.GetReferencedTypes(), type => type.Name == "Windows");

        var sum = Assert.Single(windows.GetMembers("Sum").OfType<MethodSymbol>());
        Assert.Equal(3, Assert.IsType<ArrayTypeSymbol>(sum.Parameters[0].Type).Length);
        Assert.Equal(3, Assert.IsType<ArrayTypeSymbol>(sum.Parameters[1].Type).Length);

        var blackman = Assert.Single(windows.GetMembers("Blackman").OfType<MethodSymbol>());
        Assert.Equal(3, Assert.IsType<ArrayTypeSymbol>(blackman.ReturnType).Length);

        var bag = new DiagnosticBag();
        var module = Lowerer.Lower(compilation, bag);
        Assert.True(IrVerifier.Verify(module, bag), string.Join("\n", bag.Select(d => d.ToString())));
        Assert.True(bag.IsEmpty, string.Join("\n", bag.Select(d => d.ToString())));
    }

    // --- Helpers ----------------------------------------------------------

    const string LightLoop = """
                             package A

                             struct Light {
                                 var position: float3
                                 var range: float
                                 var color: float3
                                 var kind: float
                             }

                             struct Shade {
                                 static func One(light: Light, p: float3): float3 {
                                     val d = light.position - p
                                     return light.color * light.range / max(dot(d, d), 0.0001f)
                                 }
                             }

                             shader LightLoop {
                                 [Permutation] val MaxLights: int = 8

                                 var lights: Light[MaxLights]
                                 var lightCount: int
                                 var cameraPosition: float3

                                 [PixelShader]
                                 [Semantic("SV_Target")]
                                 func Pixel(): float4 {
                                     var sum = float3(0f, 0f, 0f)
                                     for (i in 0 .. MaxLights - 1) {
                                         if (i >= lightCount) {
                                             break
                                         }

                                         sum = sum + Shade.One(lights[i], cameraPosition)
                                     }

                                     return float4(sum, 1f)
                                 }
                             }

                             """;

    static IReadOnlyList<Diagnostic> Compile(string source) =>
        Compilation.Create("Test", SyntaxTree.ParseText(source, path: "Test.rvn")).GetDiagnostics();

    /// <summary>One field of a shader whose body is nothing but the given members.</summary>
    static FieldSymbol FieldOf(string members, string name) {
        var compilation = Compilation.Create(
            "Test",
            SyntaxTree.ParseText($"package A\n\nshader S {{\n    {members}\n}}\n", path: "Test.rvn")
        );

        Assert.Empty(compilation.GetDiagnostics());

        return compilation.GetAllTypes()
            .Single(type => type.Name == "S")
            .GetMembers()
            .OfType<FieldSymbol>()
            .Single(field => field.Name == name);
    }

    /// <summary>The inferred length of the last local a pixel body declares.</summary>
    static int? LengthOfLocal(string body) {
        var source = $$"""
                       package A

                       shader S {
                           [PixelShader]
                           [Semantic("SV_Target")]
                           func Pixel(): float4 {
                       {{body}}
                               return float4(xs[0])
                           }
                       }

                       """;

        var tree = SyntaxTree.ParseText(source, path: "Test.rvn");
        Assert.Empty(tree.Diagnostics);

        var compilation = Compilation.Create("Test", tree);
        Assert.Empty(compilation.GetDiagnostics());

        var model = compilation.GetSemanticModel(tree);
        var declaration = Descendants(tree.GetRoot())
            .OfType<VariableDeclarationSyntax>()
            .Last(node => node.Identifier.ValueText == "xs");

        return (model.GetDeclaredSymbol(declaration) as LocalSymbol)?.Type is ArrayTypeSymbol array
            ? array.Length
            : null;
    }

    static IEnumerable<SyntaxNode> Descendants(SyntaxNode node) {
        foreach (var child in node.ChildNodesAndTokens()) {
            yield return child;

            foreach (var deeper in Descendants(child)) {
                yield return deeper;
            }
        }
    }

    static IrModule Lower(string source) {
        var compilation = Compilation.Create("Test", SyntaxTree.ParseText(source, path: "Test.rvn"));
        Assert.Empty(compilation.GetDiagnostics());

        var bag = new DiagnosticBag();
        var module = Lowerer.Lower(compilation, bag);
        Assert.True(IrVerifier.Verify(module, bag), string.Join("\n", bag.Select(d => d.ToString())));
        Assert.True(bag.IsEmpty, string.Join("\n", bag.Select(d => d.ToString())));
        return module;
    }

    static IReadOnlyList<GeneratedSource> Generate(string source, string target) {
        var bag = new DiagnosticBag();
        var generated = TargetBackends.Create(target)!.Generate(Lower(source), bag);

        var errors = bag.ToArray().Where(d => d.IsError).ToArray();
        Assert.True(errors.Length == 0, string.Join("\n", errors.Select(d => d.ToString())));

        return generated;
    }

    static IrBinding FindGlobal(IrModule module, string name) =>
        module.Shaders.SelectMany(shader => shader.Bindings).Single(binding => binding.Name == name);

    static CompiledLibrary BuildLibrary(string source) {
        var compilation = Compilation.Create("Lib", SyntaxTree.ParseText(source, path: "Lib.rvn"));
        Assert.Empty(compilation.GetDiagnostics());

        var bag = new DiagnosticBag();
        var library = LibraryBuilder.Build(compilation, Lowerer.LowerWithLinks(compilation, bag), bag);
        Assert.DoesNotContain(bag.ToArray(), d => d.IsError);
        return library;
    }
}
