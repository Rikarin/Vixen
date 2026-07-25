// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Raven;
using Vixen.Raven.Binding;
using Vixen.Raven.Symbols;
using Vixen.Raven.Syntax;
using Xunit;
using static Tests.SemanticTestBase;

namespace Tests;

/// <summary>Phase 2a/2b: the declaration pass builds the symbol table from syntax.</summary>
public class SymbolTests {
    [Fact]
    public void Package_directive_creates_a_namespace_chain() {
        var compilation = AssertNoDiagnostics(
            """
            package Vixen.Test.Deep

            shader Empty { }

            """
        );

        var vixen = compilation.GlobalNamespace.GetNamespace("Vixen");
        Assert.NotNull(vixen);

        var test = vixen.GetNamespace("Test");
        Assert.NotNull(test);

        var deep = test.GetNamespace("Deep");
        Assert.NotNull(deep);
        Assert.Equal("Vixen.Test.Deep", deep.QualifiedName);
        Assert.NotNull(deep.GetTypeMember("Empty"));
    }

    [Fact]
    public void Type_declarations_get_their_declared_kind() {
        var compilation = AssertNoDiagnostics(
            """
            package A

            shader S { }

            struct T { }

            class C { }

            protocol P { }

            enum E {
                One,
                Two
            }

            """
        );

        Assert.Equal(TypeKind.Shader, FindType(compilation, "S").TypeKind);
        Assert.Equal(TypeKind.Struct, FindType(compilation, "T").TypeKind);
        Assert.Equal(TypeKind.Class, FindType(compilation, "C").TypeKind);
        Assert.Equal(TypeKind.Protocol, FindType(compilation, "P").TypeKind);
        Assert.Equal(TypeKind.Enum, FindType(compilation, "E").TypeKind);
    }

    [Fact]
    public void Base_list_separates_the_base_type_from_protocols() {
        var compilation = AssertNoDiagnostics(
            """
            package A

            protocol Drawable { }

            class Base { }

            class Derived : Base, Drawable { }

            """
        );

        var derived = FindType(compilation, "Derived");
        Assert.Equal("Base", derived.BaseType?.Name);
        Assert.Equal("Drawable", Assert.Single(derived.Interfaces).Name);
    }

    [Fact]
    public void Protocol_members_declare_a_signature_without_a_body() {
        const string Source = """
                              package A

                              protocol Vehicle {
                                  var Tint: float4

                                  func Start()
                                  func Describe(): float3
                              }

                              """;

        var (compilation, tree, _) = Compile(Source);
        Assert.Empty(compilation.GetDiagnostics());

        // Nothing in the syntax stands in for the missing body.
        var start = FindNode<MethodDeclarationSyntax>(tree, m => m.Identifier.ValueText == "Start");
        Assert.Null(start.Body);
        Assert.Null(start.ExpressionBody);

        var vehicle = FindType(compilation, "Vehicle");
        Assert.Equal(TypeKind.Protocol, vehicle.TypeKind);

        Assert.Equal("float4", GetMember<FieldSymbol>(vehicle, "Tint").Type.ToDisplayString());
        Assert.True(GetMember<MethodSymbol>(vehicle, "Start").ReturnType.IsVoid);
        Assert.Equal("float3", GetMember<MethodSymbol>(vehicle, "Describe").ReturnType.ToDisplayString());
    }

    [Fact]
    public void Inherited_members_are_reachable_through_the_base_chain() {
        var compilation = AssertNoDiagnostics(
            """
            package A

            class Base {
                val count: int
            }

            class Derived : Base { }

            """
        );

        var derived = FindType(compilation, "Derived");

        Assert.Empty(derived.GetMembers("count"));

        var inherited = Assert.IsAssignableFrom<FieldSymbol>(Assert.Single(Binder.LookupMembers(derived, "count")));

        Assert.Equal("int", inherited.Type.ToDisplayString());
        Assert.Equal("Base", inherited.ContainingType?.Name);
    }

    [Fact]
    public void Fields_carry_their_type_mutability_and_constant_value() {
        var compilation = AssertNoDiagnostics(
            """
            package A

            shader S {
                const val Multiplier = 42
                val fixed: float3
                var mutable: int
            }

            """
        );

        var shader = FindType(compilation, "S");

        var multiplier = GetMember<FieldSymbol>(shader, "Multiplier");
        Assert.True(multiplier.IsConst);
        Assert.True(multiplier.IsReadOnly);
        Assert.Equal(42, multiplier.ConstantValue);
        Assert.Equal("int", multiplier.Type.ToDisplayString());

        Assert.True(GetMember<FieldSymbol>(shader, "fixed").IsReadOnly);
        Assert.False(GetMember<FieldSymbol>(shader, "mutable").IsReadOnly);
        Assert.Equal("float3", GetMember<FieldSymbol>(shader, "fixed").Type.ToDisplayString());
    }

    [Fact]
    public void Method_signatures_resolve_parameters_and_return_type() {
        var compilation = AssertNoDiagnostics(
            """
            package A

            shader S {
                func Blend(a: float4, b: float4, t: float = 0.5): float4 {
                    return a
                }
            }

            """
        );

        var method = GetMember<MethodSymbol>(FindType(compilation, "S"), "Blend");

        Assert.Equal("float4", method.ReturnType.ToDisplayString());
        Assert.Equal(3, method.Parameters.Count);
        Assert.Equal("float4", method.Parameters[0].Type.ToDisplayString());
        Assert.True(method.Parameters[2].HasDefaultValue);
        Assert.Equal(2, method.MinimumArgumentCount);
        Assert.Equal("A.S.Blend(a: float4, b: float4, t: float): float4", method.ToDisplayString());
    }

    [Fact]
    public void Expression_bodied_method_infers_its_return_type() {
        var compilation = AssertNoDiagnostics(
            """
            package A

            shader S {
                func Answer() => 42
            }

            """
        );

        Assert.Equal("int", GetMember<MethodSymbol>(FindType(compilation, "S"), "Answer").ReturnType.ToDisplayString());
    }

    [Fact]
    public void Constructors_are_methods_named_ctor() {
        var compilation = AssertNoDiagnostics(
            """
            package A

            class C {
                init(value: int) { }
            }

            """
        );

        var constructor = Assert.Single(FindType(compilation, "C").Constructors);
        Assert.Equal(MethodKind.Constructor, constructor.MethodKind);
        Assert.True(constructor.ReturnType.IsVoid);
        Assert.Equal("int", Assert.Single(constructor.Parameters).Type.ToDisplayString());
    }

    [Fact]
    public void Properties_report_their_accessors() {
        var compilation = AssertNoDiagnostics(
            """
            package A

            shader S {
                var backing: int

                var readable: int {
                    get => backing
                }

                var writable: int {
                    get => backing
                    set => backing = value
                }
            }

            """
        );

        var shader = FindType(compilation, "S");

        var readable = GetMember<PropertySymbol>(shader, "readable");
        Assert.True(readable.HasGetter);
        Assert.False(readable.HasSetter);
        Assert.True(readable.IsReadOnly);

        var writable = GetMember<PropertySymbol>(shader, "writable");
        Assert.True(writable.HasSetter);
    }

    [Fact]
    public void Enum_members_are_constants_of_the_enum_type() {
        var compilation = AssertNoDiagnostics(
            """
            package A

            enum Mode {
                Off,
                On = 5,
                Auto
            }

            """
        );

        var mode = FindType(compilation, "Mode");

        Assert.Equal(0, GetMember<FieldSymbol>(mode, "Off").ConstantValue);
        Assert.Equal(5, GetMember<FieldSymbol>(mode, "On").ConstantValue);
        Assert.Equal(2, GetMember<FieldSymbol>(mode, "Auto").ConstantValue);
        Assert.Same(mode, GetMember<FieldSymbol>(mode, "Off").Type);
    }

    [Fact]
    public void Nested_types_belong_to_their_container() {
        var compilation = AssertNoDiagnostics(
            """
            package A

            shader Outer {
                struct Inner {
                    val x: int
                }
            }

            """
        );

        var inner = FindType(compilation, "Inner");
        Assert.Equal("Outer", inner.ContainingType?.Name);
        Assert.Equal("A.Outer.Inner", inner.ToDisplayString());
    }

    [Fact]
    public void Generic_types_expose_parameters_and_substitute_on_construction() {
        var compilation = AssertNoDiagnostics(
            """
            package A

            class Box<T> {
                val value: T

                func Get(): T {
                    return value
                }
            }

            class Holder {
                val boxed: Box<int>
            }

            """
        );

        var box = FindType(compilation, "Box");
        Assert.Equal(1, box.Arity);
        Assert.Equal("T", Assert.Single(box.TypeParameters).Name);

        var boxed = GetMember<FieldSymbol>(FindType(compilation, "Holder"), "boxed").Type;
        Assert.Equal("A.Box<int>", boxed.ToDisplayString());

        var constructed = Assert.IsType<ConstructedNamedTypeSymbol>(boxed);
        Assert.Equal("int", Assert.Single(constructed.GetMembers("value")).As<FieldSymbol>().Type.ToDisplayString());
        Assert.Equal(
            "int",
            Assert.Single(constructed.GetMembers("Get")).As<MethodSymbol>().ReturnType.ToDisplayString()
        );
    }

    [Fact]
    public void Type_parameter_constraints_are_resolved() {
        var compilation = AssertNoDiagnostics(
            """
            package A

            protocol Drawable {
                func Draw() { }
            }

            class Renderer<T> where T : Drawable {
                val item: T
            }

            """
        );

        var parameter = Assert.Single(FindType(compilation, "Renderer").TypeParameters);
        Assert.Equal("Drawable", Assert.Single(parameter.ConstraintTypes).ToDisplayString().Split('.').Last());
    }

    [Fact]
    public void Modifiers_map_to_accessibility_and_staticness() {
        var compilation = AssertNoDiagnostics(
            """
            package A

            shader S {
                public func Visible() { }
                private func Hidden() { }
                static func Shared() { }
                abstract func Later() { }
            }

            """
        );

        var shader = FindType(compilation, "S");

        Assert.Equal(Accessibility.Public, GetMember<MethodSymbol>(shader, "Visible").DeclaredAccessibility);
        Assert.Equal(Accessibility.Private, GetMember<MethodSymbol>(shader, "Hidden").DeclaredAccessibility);
        Assert.True(GetMember<MethodSymbol>(shader, "Shared").IsStatic);
        Assert.True(GetMember<MethodSymbol>(shader, "Later").IsAbstract);
    }

    [Fact]
    public void Imports_bring_another_package_into_scope() {
        var libraryTree = SyntaxTree.ParseText(
            """
            package Vixen.Core

            struct Ray {
                val origin: float3
            }

            """,
            path: "Library.rvn"
        );

        var shaderTree = SyntaxTree.ParseText(
            """
            package Vixen.App

            import Vixen.Core

            shader S {
                val ray: Ray
            }

            """,
            path: "Shader.rvn"
        );

        var compilation = Compilation.Create("Test", libraryTree, shaderTree);
        Assert.Empty(compilation.GetDiagnostics());

        var field = GetMember<FieldSymbol>(FindType(compilation, "S"), "ray");
        Assert.Equal("Vixen.Core.Ray", field.Type.ToDisplayString());
    }
}

file static class SymbolAssertions {
    public static T As<T>(this Symbol symbol) where T : Symbol => Assert.IsAssignableFrom<T>(symbol);
}
