// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax.Diagnostics;
using Vixen.Raven;
using Vixen.Raven.Diagnostics;
using Vixen.Raven.IR;
using Vixen.Raven.Lowering;
using Vixen.Raven.Symbols;
using Vixen.Raven.Syntax;
using Xunit;
using static Tests.LoweringTestBase;
using static Tests.SemanticTestBase;

namespace Tests;

/// <summary>
///     <c>compose</c>: a protocol-typed slot on a shader, filled by a concrete shader chosen
///     when the shader is compiled. The shader is written once against the feature; each
///     material says which implementation to use, and the call resolves statically.
/// </summary>
public class ComposeTests {
    /// <summary>
    ///     Two implementations, with one declared <em>after</em> the shader that composes it.
    ///     Materials pick implementations in whatever order they were authored, so a slot must
    ///     not care where in the file its implementation sits.
    /// </summary>
    const string Material = """
                            package A

                            protocol IDiffuseModel {
                                func Diffuse(albedo: float4): float4
                            }

                            shader Lambert : IDiffuseModel {
                                func Diffuse(albedo: float4): float4 {
                                    return albedo * 0.5f
                                }
                            }

                            shader Lit {
                                compose val diffuse: IDiffuseModel

                                var tint: float4

                                [FragmentShader]
                                func Shade(): float4 {
                                    return diffuse.Diffuse(tint)
                                }
                            }

                            shader OrenNayar : IDiffuseModel {
                                func Diffuse(albedo: float4): float4 {
                                    return albedo * 0.25f
                                }
                            }

                            """;

    static (Compilation Compilation, IrModule Module) LowerWith(string source, ComposeBindings bindings) {
        var tree = SyntaxTree.ParseText(source, path: "Test.rvn");
        Assert.Empty(tree.Diagnostics);

        var compilation = Compilation.Create("Test", PermutationValues.Empty, bindings, [tree]);
        var semantic = compilation.GetDiagnostics();
        Assert.True(
            semantic.Count == 0,
            "Expected no semantic diagnostics, got:\n" + string.Join("\n", semantic.Select(d => d.ToString()))
        );

        var bag = new DiagnosticBag();
        var module = Lowerer.Lower(compilation, bag);
        IrVerifier.Verify(module, bag);
        Assert.Empty(bag.ToArray());

        return (compilation, module);
    }

    static IReadOnlyList<Diagnostic> DiagnosticsWith(string source, ComposeBindings bindings) {
        var tree = SyntaxTree.ParseText(source, path: "Test.rvn");
        Assert.Empty(tree.Diagnostics);

        return Compilation.Create("Test", PermutationValues.Empty, bindings, [tree]).GetDiagnostics();
    }

    static ComposeBindings Bind(string slot, string shader) => ComposeBindings.Create([new(slot, shader)]);

    /// <summary>
    ///     Generates GLSL. The module holds every implementation — all types are lowered —
    ///     so the emitted unit is where it shows that only the bound one ships.
    /// </summary>
    static string GenerateWith(string source, ComposeBindings bindings) {
        var (_, module) = LowerWith(source, bindings);
        var bag = new DiagnosticBag();
        var backend = Vixen.Raven.CodeGen.TargetBackends.Create("glsl")!;
        var generated = backend.Generate(module, bag);

        Assert.DoesNotContain(bag.ToArray(), d => d.IsError);
        return Assert.Single(generated).Code;
    }

    static FieldSymbol Slot(Compilation compilation, string shader, string name) =>
        Assert.Single(FindType(compilation, shader).GetMembers(name).OfType<FieldSymbol>());

    // --- Resolution --------------------------------------------------------

    [Fact]
    public void A_slot_resolves_to_the_bound_shader() {
        var (compilation, _) = LowerWith(Material, Bind("diffuse", "Lambert"));
        var slot = Slot(compilation, "Lit", "diffuse");

        Assert.True(slot.IsCompose);
        Assert.Equal("Lambert", slot.ComposedType?.Name);
    }

    /// <summary>
    ///     A slot is not data. It must not take a uniform slot or a constant-buffer field —
    ///     nothing about it survives to the target.
    /// </summary>
    [Fact]
    public void A_slot_is_not_a_binding() {
        var (compilation, module) = LowerWith(Material, Bind("diffuse", "Lambert"));

        Assert.Equal(ResourceKind.None, Slot(compilation, "Lit", "diffuse").ResourceKind);
        Assert.DoesNotContain("diffuse", FindShader(module, "Lit").Bindings.Select(b => b.Variable.Name));

        // The uniform beside it is still bound.
        Assert.Contains("tint", FindShader(module, "Lit").Bindings.Select(b => b.Variable.Name));
    }

    [Fact]
    public void A_qualified_binding_wins_over_a_bare_one() {
        var (compilation, _) = LowerWith(
            Material,
            ComposeBindings.Create([new("diffuse", "Lambert"), new("Lit.diffuse", "OrenNayar")])
        );

        Assert.Equal("OrenNayar", Slot(compilation, "Lit", "diffuse").ComposedType?.Name);
    }

    // --- The call resolves statically --------------------------------------

    [Fact]
    public void A_call_through_a_slot_becomes_a_direct_call_to_the_implementation() {
        var (_, module) = LowerWith(Material, Bind("diffuse", "Lambert"));

        // The call names a function, not a dispatch through a value.
        Assert.Contains("call", PrintFunction(module, "Shade"), StringComparison.Ordinal);

        // And the protocol's own declaration produced nothing to call.
        Assert.DoesNotContain("IDiffuseModel", PrintFunction(module, "Shade"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Lambert", "0.5", "0.25")]
    [InlineData("OrenNayar", "0.25", "0.5")]
    public void Only_the_bound_implementation_is_emitted(string implementation, string used, string unused) {
        var glsl = GenerateWith(Material, Bind("diffuse", implementation));

        Assert.Contains(used, glsl, StringComparison.Ordinal);
        Assert.DoesNotContain(unused, glsl, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The two materials must differ, or the assertions above would pass for the wrong
    ///     reason.
    /// </summary>
    [Fact]
    public void Two_bindings_produce_different_code() =>
        Assert.NotEqual(
            GenerateWith(Material, Bind("diffuse", "Lambert")),
            GenerateWith(Material, Bind("diffuse", "OrenNayar"))
        );

    /// <summary>
    ///     <c>OrenNayar</c> is declared after the shader that composes it. Before function
    ///     shells existed this failed to lower, because a body was lowered before a callee
    ///     declared later in the module had been registered.
    /// </summary>
    [Fact]
    public void An_implementation_declared_after_the_composing_shader_still_resolves() =>
        Assert.Contains("0.25", GenerateWith(Material, Bind("diffuse", "OrenNayar")), StringComparison.Ordinal);

    [Fact]
    public void An_implementation_inherited_from_a_base_shader_resolves() {
        var (_, module) = LowerWith(
            """
            package A

            protocol IDiffuseModel {
                func Diffuse(albedo: float4): float4
            }

            shader BaseModel {
                func Diffuse(albedo: float4): float4 {
                    return albedo * 0.75f
                }
            }

            shader Derived : BaseModel, IDiffuseModel {
            }

            shader Lit {
                compose val diffuse: IDiffuseModel

                var tint: float4

                func Shade(): float4 {
                    return diffuse.Diffuse(tint)
                }
            }

            """,
            Bind("diffuse", "Derived")
        );

        Assert.Contains(
            "0.75",
            IrPrinter.Print(FindShader(module, "BaseModel").Functions[0]),
            StringComparison.Ordinal
        );
    }

    // --- Validation --------------------------------------------------------

    [Fact]
    public void An_unfilled_slot_is_rejected() {
        var diagnostic = Assert.Single(DiagnosticsWith(Material, ComposeBindings.Empty));

        Assert.Equal("RVN2073", diagnostic.Id);
        Assert.Contains("diffuse", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_slot_outside_a_shader_is_rejected() =>
        AssertDiagnostics(
            """
            package A

            protocol IThing {
                func Do(): int
            }

            struct S {
                compose val thing: IThing
            }

            """,
            "RVN2070"
        );

    [Fact]
    public void A_slot_that_is_not_protocol_typed_is_rejected() =>
        AssertDiagnostics(
            """
            package A

            shader S {
                compose val thing: float4
            }

            """,
            "RVN2071"
        );

    [Fact]
    public void A_slot_with_an_initializer_is_rejected() {
        var ids = DiagnosticsWith(
                """
                package A

                protocol IThing {
                    func Do(): int
                }

                shader Impl : IThing {
                    func Do(): int {
                        return 1
                    }
                }

                shader S {
                    compose val thing: IThing = 1
                }

                """,
                Bind("thing", "Impl")
            )
            .Select(d => d.Id);

        Assert.Contains("RVN2072", ids);
    }

    [Fact]
    public void A_binding_naming_an_unknown_type_is_rejected() {
        var diagnostic = Assert.Single(DiagnosticsWith(Material, Bind("diffuse", "NoSuchShader")));

        Assert.Equal("RVN2074", diagnostic.Id);
        Assert.Contains("NoSuchShader", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_binding_to_something_other_than_a_shader_is_rejected() {
        var diagnostic = Assert.Single(DiagnosticsWith(Material, Bind("diffuse", "IDiffuseModel")));

        Assert.Equal("RVN2075", diagnostic.Id);
        Assert.Contains("protocol", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    /// <summary>
    ///     The check that makes the whole thing type-safe: a shader can only fill a slot if it
    ///     actually declares the protocol.
    /// </summary>
    [Fact]
    public void A_binding_that_does_not_implement_the_protocol_is_rejected() {
        var diagnostic = Assert.Single(
            DiagnosticsWith(
                """
                package A

                protocol IDiffuseModel {
                    func Diffuse(albedo: float4): float4
                }

                shader Unrelated {
                    func Diffuse(albedo: float4): float4 {
                        return albedo
                    }
                }

                shader Lit {
                    compose val diffuse: IDiffuseModel
                }

                """,
                Bind("diffuse", "Unrelated")
            )
        );

        Assert.Equal("RVN2076", diagnostic.Id);
        Assert.Contains("Unrelated", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void Assigning_to_a_slot_is_rejected_with_a_reason() {
        var ids = DiagnosticsWith(
                """
                package A

                protocol IThing {
                    func Do(): int
                }

                shader Impl : IThing {
                    func Do(): int {
                        return 1
                    }
                }

                shader S {
                    compose val thing: IThing

                    func Probe() {
                        thing = thing
                    }
                }

                """,
                Bind("thing", "Impl")
            )
            .Select(d => d.Id);

        Assert.Contains("RVN2077", ids);
    }
}

/// <summary>Parsing the binding set itself.</summary>
public class ComposeBindingsTests {
    [Fact]
    public void Empty_binds_nothing() {
        Assert.Equal(0, ComposeBindings.Empty.Count);
        Assert.Null(ComposeBindings.Empty.Resolve("Lit", "diffuse"));
    }

    [Fact]
    public void A_bare_slot_applies_to_every_shader() {
        var bindings = ComposeBindings.Parse(["diffuse=Lambert"]);

        Assert.Equal("Lambert", bindings.Resolve("Lit", "diffuse"));
        Assert.Equal("Lambert", bindings.Resolve("Unlit", "diffuse"));
    }

    [Fact]
    public void A_qualified_slot_only_applies_to_its_shader() {
        var bindings = ComposeBindings.Parse(["Lit.diffuse=Lambert"]);

        Assert.Equal("Lambert", bindings.Resolve("Lit", "diffuse"));
        Assert.Null(bindings.Resolve("Unlit", "diffuse"));
    }

    [Fact]
    public void A_qualified_slot_overrides_a_bare_one() {
        var bindings = ComposeBindings.Parse(["diffuse=Lambert", "Lit.diffuse=OrenNayar"]);

        Assert.Equal("OrenNayar", bindings.Resolve("Lit", "diffuse"));
        Assert.Equal("Lambert", bindings.Resolve("Unlit", "diffuse"));
    }

    [Fact]
    public void Whitespace_is_trimmed_and_blank_entries_skipped() {
        var bindings = ComposeBindings.Parse([" diffuse = Lambert ", "", "   "]);

        Assert.Equal(1, bindings.Count);
        Assert.Equal("Lambert", bindings.Resolve("Lit", "diffuse"));
    }

    [Fact]
    public void A_malformed_entry_is_rejected() {
        Assert.Throws<ArgumentException>(() => ComposeBindings.Parse(["diffuse"]));
        Assert.Throws<ArgumentException>(() => ComposeBindings.Parse(["=Lambert"]));
        Assert.Throws<ArgumentException>(() => ComposeBindings.Parse(["diffuse="]));
    }

    [Fact]
    public void TryParse_reports_instead_of_throwing() {
        Assert.False(ComposeBindings.TryParse(["diffuse"], out _, out var error));
        Assert.Contains("slot=Shader", error!, StringComparison.Ordinal);
    }
}
