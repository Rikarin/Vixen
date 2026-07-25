using Vixen.Raven.Syntax;
using Xunit;

namespace Tests;

/// <summary>
///     Verifies the red overlay over the green tree: lazy child realization with
///     stable identity, parent linkage back to the root, and absolute positions
///     derived from green widths.
/// </summary>
public class RedTreeTests {
    [Fact]
    public void Children_are_cached_and_identity_stable() {
        var root = Parse("package A.B\n");
        Assert.Same(root.Package, root.Package);
        Assert.Same(root.Package.PackageName, root.Package.PackageName);
    }

    [Fact]
    public void Parent_links_back_to_root() {
        var root = Parse("package A.B\n");
        var package = root.Package;
        var name = (QualifiedNameSyntax)package.PackageName;

        Assert.Null(root.Parent);
        Assert.Same(root, package.Parent);
        Assert.Same(package, name.Parent);
        Assert.Same(name, name.Left.Parent);
    }

    [Fact]
    public void Positions_are_ordered_and_within_parent() {
        var root = Parse("package Vixen.Test\n");
        var name = (QualifiedNameSyntax)root.Package.PackageName;

        var left = (IdentifierNameSyntax)name.Left;
        var right = name.Right;

        // "Vixen" precedes "Test" in source order.
        Assert.True(left.Span.Start < right.Span.Start);

        // Child spans fall within the parent's full span.
        Assert.True(root.FullSpan.Contains(name.FullSpan));
        Assert.True(name.FullSpan.Contains(left.FullSpan));

        // Identifier text survives the green/red round trip.
        Assert.Equal("Vixen", left.Identifier.Text);
        Assert.Equal("Test", right.Identifier.Text);
    }

    static CompilationUnitSyntax Parse(string text) =>
        Assert.IsType<CompilationUnitSyntax>(SyntaxTree.ParseText(text).GetRoot());
}
