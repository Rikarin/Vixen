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
}
