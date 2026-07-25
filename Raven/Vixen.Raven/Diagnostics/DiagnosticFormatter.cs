using System.Text;

namespace Vixen.Raven.Diagnostics;

/// <summary>Knobs for <see cref="DiagnosticFormatter" />.</summary>
public sealed record DiagnosticFormatterOptions {
    /// <summary>The plain one-line form, with no source excerpt and no colour.</summary>
    public static readonly DiagnosticFormatterOptions Plain = new() { ShowSource = false };

    /// <summary>
    ///     Show the offending source line with a caret under the span. Only possible
    ///     when the location is backed by its <see cref="Text.SourceText" />.
    /// </summary>
    public bool ShowSource { get; init; } = true;

    /// <summary>Wrap severity and caret in ANSI colour.</summary>
    public bool UseColor { get; init; }
}

/// <summary>
///     Renders a <see cref="Diagnostic" /> for a human: the Roslyn-style
///     <c>path(line,col): severity ID: message</c> header, and under it the source
///     line with the span underlined.
///     <code>
/// Shader.rvn(6,16): error RVN2003: The name 'nrmalize' could not be found
/// 
///   6 |     return nrmalize(v)
///     |            ^^^^^^^^
/// </code>
/// </summary>
public static class DiagnosticFormatter {
    const string Reset = "\u001b[0m";
    const string Dim = "\u001b[90m";

    public static string Format(Diagnostic diagnostic, DiagnosticFormatterOptions? options = null) {
        var builder = new StringBuilder();
        Append(builder, diagnostic, options ?? new DiagnosticFormatterOptions());
        return builder.ToString();
    }

    /// <summary>Formats a run of diagnostics, one blank line between them.</summary>
    public static string Format(IEnumerable<Diagnostic> diagnostics, DiagnosticFormatterOptions? options = null) {
        options ??= new();

        var builder = new StringBuilder();
        var first = true;

        foreach (var diagnostic in diagnostics) {
            if (!first) {
                builder.AppendLine();
            }

            first = false;
            Append(builder, diagnostic, options);
        }

        return builder.ToString();
    }

    static void Append(StringBuilder builder, Diagnostic diagnostic, DiagnosticFormatterOptions options) {
        var severity = diagnostic.Severity.ToString().ToLowerInvariant();
        var location = diagnostic.Location;

        if (!location.IsNone) {
            var file = location.FilePath.Length == 0 ? "<unknown>" : location.FilePath;
            builder.Append(file).Append('(').Append(location.GetLineSpan().Start).Append("): ");
        }

        builder
            .Append(Colored(severity + " " + diagnostic.Id, Color(diagnostic.Severity), options))
            .Append(": ")
            .AppendLine(diagnostic.GetMessage());

        if (options.ShowSource) {
            AppendSource(builder, diagnostic, options);
        }
    }

    static void AppendSource(StringBuilder builder, Diagnostic diagnostic, DiagnosticFormatterOptions options) {
        var location = diagnostic.Location;

        if (location.IsNone || location.SourceText is not { } text) {
            return;
        }

        var lineSpan = location.GetLineSpan();
        var line = lineSpan.Start.Line;

        if (line >= text.LineCount) {
            return;
        }

        var source = text.GetLineText(line);
        var number = (line + 1).ToString();
        var gutter = new string(' ', number.Length);

        // The span may run past the end of its first line; underline what fits.
        var start = Math.Min(lineSpan.Start.Character, source.Length);
        var end = lineSpan.End.Line == line ? Math.Min(lineSpan.End.Character, source.Length) : source.Length;
        var width = Math.Max(end - start, 1);

        builder.AppendLine();
        builder.Append(Colored($"  {number} | ", Dim, options)).AppendLine(source);

        // Copy the leading whitespace verbatim so a tab-indented line still lines
        // up: a tab stays a tab rather than becoming one column.
        var padding = new StringBuilder();
        for (var i = 0; i < start; i++) {
            padding.Append(i < source.Length && source[i] == '\t' ? '\t' : ' ');
        }

        builder
            .Append(Colored($"  {gutter} | ", Dim, options))
            .Append(padding)
            .AppendLine(Colored(new('^', width), Color(diagnostic.Severity), options));
    }

    static string Color(DiagnosticSeverity severity) =>
        severity switch {
            DiagnosticSeverity.Error => "\u001b[31m",
            DiagnosticSeverity.Warning => "\u001b[33m",
            DiagnosticSeverity.Info => "\u001b[36m",
            _ => Dim
        };

    static string Colored(string value, string color, DiagnosticFormatterOptions options) =>
        options.UseColor ? color + value + Reset : value;
}
