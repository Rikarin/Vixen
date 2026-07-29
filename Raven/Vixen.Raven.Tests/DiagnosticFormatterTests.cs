// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax.Diagnostics;
using Vixen.Core.Syntax.Text;
using Vixen.Raven;
using Vixen.Raven.Diagnostics;
using Vixen.Raven.Syntax;
using Xunit;

namespace Tests;

/// <summary>Phase 5: what a diagnostic looks like when a person reads it.</summary>
public class DiagnosticFormatterTests {
    static readonly DiagnosticDescriptor Descriptor =
        new("RVN9999", "Test", "something is wrong with '{0}'", "Test", DiagnosticSeverity.Error);

    [Fact]
    public void The_header_carries_the_file_position_severity_id_and_message() {
        var rendered = Format("val x = 1\nval y = 2\n", new(14, 1), "Shader.rvn");

        Assert.StartsWith("Shader.rvn(2,5): error RVN9999: something is wrong with 'y'", rendered);
    }

    [Fact]
    public void The_offending_line_is_shown_with_the_span_underlined() {
        var rendered = Format("package A\nval answer = 42\n", new(14, 6));

        Assert.Contains("  2 | val answer = 42", rendered);
        Assert.Contains("    |     ^^^^^^", rendered);
    }

    [Fact]
    public void A_span_that_runs_past_its_line_underlines_what_fits() {
        // The span covers both lines; only the first is shown.
        var rendered = Format("first\nsecond\n", TextSpan.FromBounds(0, 12));

        Assert.Contains("  1 | first", rendered);
        Assert.Contains("    | ^^^^^", rendered);
    }

    [Fact]
    public void An_empty_span_still_gets_one_caret() {
        var rendered = Format("abc\n", new(3, 0));

        Assert.Contains("    ^", rendered);
    }

    [Fact]
    public void A_tab_indented_line_keeps_its_tabs_in_the_caret_row() {
        var rendered = Format("\t\tvalue\n", new(2, 5));

        // A tab copied as a tab lines up in any terminal; a space would not.
        Assert.Contains("| \t\t^^^^^", rendered);
    }

    [Fact]
    public void The_plain_form_is_the_header_alone() {
        var rendered = Format("val x = 1\n", new(4, 1), options: DiagnosticFormatterOptions.Plain);

        Assert.Equal("Test.rvn(1,5): error RVN9999: something is wrong with 'x'", rendered.TrimEnd());
    }

    [Fact]
    public void A_diagnostic_with_no_location_renders_without_a_position() {
        var rendered = DiagnosticFormatter.Format(Diagnostic.Create(Descriptor, Location.None, "it"));

        Assert.Equal("error RVN9999: something is wrong with 'it'", rendered.TrimEnd());
    }

    [Fact]
    public void Colour_is_opt_in_and_wraps_the_severity_and_the_carets() {
        var text = "val x = 1\n";
        var plain = Format(text, new(4, 1));
        var colored = Format(text, new(4, 1), options: new() { UseColor = true });

        // Ordinal throughout: a culture-sensitive comparison treats an escape
        // character as ignorable and finds it in any string at all.
        Assert.DoesNotContain("\u001b", plain, StringComparison.Ordinal);
        Assert.Contains("\u001b[31merror RVN9999\u001b[0m", colored, StringComparison.Ordinal);
        Assert.Contains("\u001b[31m^\u001b[0m", colored, StringComparison.Ordinal);
    }

    [Fact]
    public void Severities_get_their_own_colour() {
        Assert.Contains("\u001b[33m", Format(DiagnosticSeverity.Warning), StringComparison.Ordinal);
        Assert.Contains("\u001b[36m", Format(DiagnosticSeverity.Info), StringComparison.Ordinal);
    }

    [Fact]
    public void A_run_of_diagnostics_is_separated_by_a_blank_line() {
        var tree = SyntaxTree.ParseText(
            """
            package A

            shader S {
                [FragmentShader]
                func Fragment(): float4 {
                    return float4(missingA, missingB, 0, 1)
                }
            }

            """,
            path: "Two.rvn"
        );

        var diagnostics = Compilation.Create("Test", tree).GetDiagnostics();
        Assert.Equal(2, diagnostics.Count);

        var rendered = DiagnosticFormatter.Format(diagnostics);

        Assert.Contains("missingA", rendered);
        Assert.Contains("missingB", rendered);
        Assert.Contains("\n\n", rendered);
    }

    static string Format(DiagnosticSeverity severity) {
        var descriptor = new DiagnosticDescriptor("RVN9999", "Test", "message", "Test", severity);
        var text = SourceText.From("val x = 1\n");
        var location = Location.Create("Test.rvn", new(4, 1), text);

        return DiagnosticFormatter.Format(Diagnostic.Create(descriptor, location), new() { UseColor = true });
    }

    static string Format(
        string source,
        TextSpan span,
        string path = "Test.rvn",
        DiagnosticFormatterOptions? options = null
    ) {
        var text = SourceText.From(source);
        var location = Location.Create(path, span, text);
        var diagnostic = Diagnostic.Create(Descriptor, location, text.ToString(span).Trim());

        return DiagnosticFormatter.Format(diagnostic, options);
    }
}
