using Vixen.Raven;
using Vixen.Raven.Symbols;
using Vixen.Raven.Syntax;
using Xunit;
using static Tests.SemanticTestBase;

namespace Tests;

/// <summary>Phase 2b: the public <c>SemanticModel</c> surface.</summary>
public class SemanticModelTests {
    const string Source = """
                          package Vixen.Test

                          shader Lit {
                              var tint: float4

                              func Shade(factor: float): float4 {
                                  val scaled = tint * factor
                                  return scaled
                              }
                          }

                          """;

    [Fact]
    public void GetDeclaredSymbol_answers_for_every_declaration_kind() {
        var (compilation, tree, model) = Compile(Source);
        Assert.Empty(compilation.GetDiagnostics());

        var shader = Assert.IsAssignableFrom<NamedTypeSymbol>(
            model.GetDeclaredSymbol(FindNode<ShaderDeclarationSyntax>(tree))
        );
        Assert.Equal("Lit", shader.Name);

        var method = Assert.IsAssignableFrom<MethodSymbol>(
            model.GetDeclaredSymbol(FindNode<MethodDeclarationSyntax>(tree))
        );
        Assert.Equal("Shade", method.Name);

        var parameter = Assert.IsAssignableFrom<ParameterSymbol>(
            model.GetDeclaredSymbol(FindNode<ParameterSyntax>(tree))
        );
        Assert.Equal("factor", parameter.Name);

        var field = Assert.IsAssignableFrom<FieldSymbol>(
            model.GetDeclaredSymbol(FindNode<FieldDeclarationSyntax>(tree))
        );
        Assert.Equal("tint", field.Name);

        var local = Assert.IsType<LocalSymbol>(
            model.GetDeclaredSymbol(FindNode<VariableDeclarationSyntax>(tree, d => d.Identifier.ValueText == "scaled"))
        );
        Assert.Equal("float4", local.Type.ToDisplayString());
    }

    [Fact]
    public void GetSymbolInfo_resolves_a_name_to_the_member_it_refers_to() {
        var (compilation, tree, model) = Compile(Source);
        Assert.Empty(compilation.GetDiagnostics());

        var name = FindNode<IdentifierNameSyntax>(
            tree,
            n => n.Identifier.ValueText == "tint"
                && n.Parent is BinaryExpressionSyntax
        );

        var symbol = model.GetSymbolInfo(name).Symbol;
        var field = Assert.IsAssignableFrom<FieldSymbol>(symbol);
        Assert.Equal("Vixen.Test.Lit.tint", field.ToDisplayString());
    }

    [Fact]
    public void GetSymbolInfo_on_a_call_resolves_the_chosen_overload() {
        var (compilation, tree, model) = Compile(
            """
            package A

            shader S {
                func Use(v: float3) {
                    var d = dot(v, v)
                }
            }

            """
        );

        Assert.Empty(compilation.GetDiagnostics());

        var call = FindNode<InvocationExpressionSyntax>(tree);
        var method = Assert.IsAssignableFrom<MethodSymbol>(model.GetSymbolInfo(call).Symbol);

        Assert.Equal("dot", method.Name);
        Assert.Equal(MethodKind.Intrinsic, method.MethodKind);
        Assert.Equal("float3", method.Parameters[0].Type.ToDisplayString());
    }

    [Fact]
    public void GetTypeInfo_reports_the_expression_type() {
        var (compilation, tree, model) = Compile(Source);
        Assert.Empty(compilation.GetDiagnostics());

        var binary = FindNode<BinaryExpressionSyntax>(tree);
        Assert.Equal("float4", model.GetTypeInfo(binary).Type?.ToDisplayString());
    }

    [Fact]
    public void Queries_about_unbound_nodes_come_back_empty() {
        var (_, tree, model) = Compile(Source);

        var keyword = FindNode<SyntaxToken>(tree, t => t.Kind == SyntaxKind.ShaderKeyword);
        Assert.True(model.GetSymbolInfo(keyword).IsEmpty);
        Assert.Null(model.GetDeclaredSymbol(keyword));
    }

    [Fact]
    public void The_same_model_instance_is_returned_per_tree() {
        var (compilation, tree, model) = Compile(Source);
        Assert.Same(model, compilation.GetSemanticModel(tree));
    }

    [Fact]
    public void Compilation_diagnostics_gather_syntax_declaration_and_binding_errors() {
        // A syntax error, an unresolved type, and an undefined name in one file.
        var tree = SyntaxTree.ParseText(
            """
            package A

            shader S {
                val value: Missing

                func Use() {
                    var x = alsoMissing
                }
            }

            """,
            path: "Test.rvn"
        );

        var compilation = Compilation.Create("Test", tree);
        var ids = compilation.GetDiagnostics().Select(d => d.Id).ToArray();

        Assert.Equal(["RVN2002", "RVN2010"], ids);
    }

    [Fact]
    public void Diagnostics_are_ordered_by_position() {
        var diagnostics = Diagnose(
            """
            package A

            shader S {
                func Use() {
                    var second = laterMissing
                }

                func Earlier() {
                    var first = earlierMissing
                }
            }

            """
        );

        Assert.Equal(2, diagnostics.Count);
        Assert.True(diagnostics[0].Location.SourceSpan.Start < diagnostics[1].Location.SourceSpan.Start);
    }
}
