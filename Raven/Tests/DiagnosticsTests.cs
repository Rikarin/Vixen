using Vixen.Raven.Diagnostics;
using Vixen.Raven.Syntax;
using Vixen.Raven.Text;
using Xunit;

namespace Tests;

/// <summary>
/// Phase 1d: lexer/parser errors surface as <see cref="Diagnostic"/>s with stable
/// ids, severities, and correct source spans; valid input reports none.
/// </summary>
public class DiagnosticsTests {
    [Theory]
    [InlineData("package A.B\n")]
    [InlineData("package A.B\n\nshader Foo {\n    val x: int\n}\n")]
    [InlineData("package A.B\n\nshader Foo {\n    func M() {\n        val f = (a: int) => 42\n    }\n}\n")]
    public void Valid_source_has_no_diagnostics(string source) {
        var tree = SyntaxTree.ParseText(source);
        Assert.Empty(tree.Diagnostics);
    }

    [Fact]
    public void Parser_error_is_reported_with_id_and_error_severity() {
        // `shader` with no name — the parser expects an identifier.
        var tree = SyntaxTree.ParseText("package A.B\n\nshader {\n\n}\n");

        Assert.NotEmpty(tree.Diagnostics);
        var diagnostic = tree.Diagnostics[0];
        Assert.Equal("RVN1001", diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.True(diagnostic.IsError);
    }

    [Fact]
    public void Invalid_character_is_reported_as_error_at_its_span() {
        // A stray backtick is not part of any token; ANTLR surfaces it as a syntax
        // error whose span points exactly at the offending character.
        var tree = SyntaxTree.ParseText("package A`B\n");

        var diagnostic = Assert.Single(tree.Diagnostics);
        Assert.True(diagnostic.IsError);
        Assert.Equal("`", tree.Text!.ToString(diagnostic.Location.SourceSpan));
    }

    [Fact]
    public void Diagnostic_span_points_at_the_offending_token() {
        var source = "package A.B\n\nshader Foo {\n    func M(] {\n    }\n}\n";
        var tree = SyntaxTree.ParseText(source);

        var diagnostic = Assert.Single(tree.Diagnostics, d => d.Id == "RVN1001");
        var span = diagnostic.Location.SourceSpan;

        // The offending `]` is the character the diagnostic should point at.
        Assert.Equal("]", tree.Text!.ToString(span));
    }

    [Fact]
    public void Diagnostic_location_reports_one_based_line_and_column() {
        var source = "package A.B\n\nshader Foo {\n    func M(] {\n    }\n}\n";
        var tree = SyntaxTree.ParseText(source);

        var diagnostic = Assert.Single(tree.Diagnostics, d => d.Id == "RVN1001");
        var lineSpan = diagnostic.Location.GetLineSpan();

        // `]` sits on the 4th line (index 3).
        Assert.Equal(3, lineSpan.Start.Line);
        Assert.Equal("]", tree.Text!.ToString(diagnostic.Location.SourceSpan));
    }

    [Fact]
    public void SourceText_maps_offsets_to_line_and_column() {
        var text = SourceText.From("ab\ncde\nf");

        Assert.Equal(new LinePosition(0, 0), text.GetLinePosition(0)); // 'a'
        Assert.Equal(new LinePosition(0, 2), text.GetLinePosition(2)); // '\n'
        Assert.Equal(new LinePosition(1, 0), text.GetLinePosition(3)); // 'c'
        Assert.Equal(new LinePosition(2, 0), text.GetLinePosition(7)); // 'f'
    }
}
