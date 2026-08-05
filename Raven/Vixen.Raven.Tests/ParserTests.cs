// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Raven.Syntax;
using Xunit;

namespace Tests;

/// <summary>
///     End-to-end parse of the shipped language sample: the tree that comes back
///     must expose the package name and imports the source declares.
/// </summary>
public class ParserTests {
    [Fact]
    void Test_SyntaxTree() {
        var text = File.ReadAllText("../../../../Library/Example1.rvn");

        var tree = SyntaxTree.ParseText(text);

        var root = tree.GetRoot();
        var compilationUnit = Assert.IsType<CompilationUnitSyntax>(root);

        var name = Assert.IsType<QualifiedNameSyntax>(compilationUnit.Package.PackageName);
        Assert.Equal("Vixen", Assert.IsAssignableFrom<SimpleNameSyntax>(name.Left).Identifier.Text);
        Assert.Equal("Test", name.Right.Identifier.Text);

        Assert.Equal(2, compilationUnit.Imports.Count);
    }

    /// <summary>
    ///     An attribute list with several attributes in it, and an attribute argument written with
    ///     a name.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Both shapes used to be shown by <c>Example1.rvn</c> and are not any more:
    ///         <c>[FooBar, BarFoo]</c> and <c>[FooBar(mode: "linear")]</c> name attributes the
    ///         compiler does not read, which is <c>RVN2138</c> now that an unrecognised attribute is
    ///         named rather than dropped in silence, and that file is asserted to bind with nothing
    ///         reported at all.
    ///     </para>
    ///     <para>
    ///         The syntax is still the syntax, so the coverage moves here rather than going away —
    ///         and here is where it belonged: what it establishes is that the <em>parser</em> builds
    ///         the list and the <c>NameColon</c>, which is true of an attribute whatever the binder
    ///         later makes of the name.
    ///     </para>
    /// </remarks>
    [Fact]
    void An_attribute_list_holds_several_attributes_and_a_named_argument() {
        var tree = SyntaxTree.ParseText(
            """
            package A

            [FooBar, BarFoo]
            struct S {
                [FooBar(mode: "linear")]
                var probe: float
            }

            """
        );

        Assert.Empty(tree.Diagnostics);

        var unit = Assert.IsType<CompilationUnitSyntax>(tree.GetRoot());
        Assert.Equal(1, unit.Members.Count);

        var structure = Assert.IsType<StructDeclarationSyntax>(unit.Members[0]);
        Assert.Equal(1, structure.AttributeLists.Count);

        var onType = structure.AttributeLists[0]!.Attributes;
        Assert.Equal(2, onType.Count);
        Assert.Equal("FooBar", onType[0].Name.ToString());
        Assert.Equal("BarFoo", onType[1].Name.ToString());

        Assert.Equal(1, structure.Members.Count);

        var field = Assert.IsType<FieldDeclarationSyntax>(structure.Members[0]);
        Assert.Equal(1, field.AttributeLists.Count);

        var onField = field.AttributeLists[0]!.Attributes;
        Assert.Equal(1, onField.Count);

        var arguments = onField[0].ArgumentList!.Arguments;
        Assert.Equal(1, arguments.Count);
        Assert.Equal("mode", arguments[0].NameColon!.Name.Identifier.Text);
    }
}
