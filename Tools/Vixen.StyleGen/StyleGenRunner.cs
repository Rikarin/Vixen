// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Vixen.Ui.Styling.Utilities;

namespace Vixen.StyleGen;

/// <summary>What one run produced, and everything it wants to say about it.</summary>
/// <param name="Css">The whole sheet: the hand-written rules, then the utilities.</param>
/// <param name="Utilities">The generated half on its own.</param>
/// <param name="Unrecognised">Candidates the generator did not know. Prose, mostly.</param>
/// <param name="Errors">Reasons the build should fail.</param>
/// <param name="RuleCount">How many utility rules were emitted.</param>
internal sealed record StyleGenResult(
    string Css,
    string Utilities,
    IReadOnlyList<string> Unrecognised,
    IReadOnlyList<string> Errors,
    int RuleCount
);

/// <summary>The build step: tokens in, a stylesheet out.</summary>
/// <remarks>
///     <para>
///         <b>This is the thing the utility system's README listed as ⏳.</b> Before it, every project
///         wanting utilities wrote its own startup bootstrap — embed the markup as resources, walk
///         the manifest, run the scanner, run the generator — which
///         <c>Samples/14-Mmo/Mmo.Ui/Theme/MmoStyles.cs</c> still does and whose own remarks admit it
///         is standing in for this. A second copy of that in the editor would have made the bootstrap
///         the pattern rather than the placeholder.
///     </para>
///     <para>
///         ⚠ <b>Out of process, and the reason is a dependency rather than a preference.</b> The
///         obvious shape for "read some files at build time and emit code" is an
///         <c>IIncrementalGenerator</c>, and it cannot be one here: <see cref="ThemeTokens" /> reads
///         YAML through <c>Vixen.Core.Yaml</c>, which is YamlDotNet, and an analyzer's dependencies
///         do not travel with it — <c>OutputItemType="Analyzer"</c> contributes one DLL, so every
///         consuming project would have to put the rest on the analyzer path itself.
///         <c>Vixen.Ui.Markup.Generators</c> escapes the same trap by <em>linking</em> its front
///         end's source into the analyzer, which works because that front end is Vixen's own code and
///         does not work here, because YamlDotNet is a package with no source to link. Running the
///         shipped assembly in a process avoids the question entirely, at the cost of one process
///         launch per build that changed a scanned file.
///     </para>
///     <para>
///         ⚠ <b>It writes a file, and that is worth more than it looks.</b> A generated constant is
///         enough for the compiler and no use to anything else; a <c>.vcss</c> in <c>obj/</c> is what
///         a hot-reload watcher can watch and what an asset-pipeline step can take as an input. The
///         accessor is emitted <i>as well</i>, so a project that only wants to call
///         <c>document.Load(…)</c> never touches the file system.
///     </para>
/// </remarks>
internal static class StyleGenRunner {
    /// <summary>Runs the step.</summary>
    /// <param name="request">What to do.</param>
    /// <returns>The sheet, and what to say about it.</returns>
    internal static StyleGenResult Run(StyleGenRequest request) {
        ArgumentNullException.ThrowIfNull(request);

        var errors = new List<string>();
        var tokens = ReadTokens(request.Tokens, errors);

        foreach (var diagnostic in tokens.Diagnostics) {
            errors.Add($"{request.Tokens}: {diagnostic}");
        }

        var candidates = new HashSet<string>(StringComparer.Ordinal);

        foreach (var path in request.Scan) {
            if (!File.Exists(path)) {
                // Not an error. MSBuild hands over a glob's worth of items and a file deleted
                // between evaluation and execution is a normal thing in an incremental build; the
                // failure worth reporting is a *token* file that is missing, which is above.
                continue;
            }

            CandidateScanner.Scan(File.ReadAllText(path), candidates);
        }

        foreach (var safe in request.Safelist) {
            candidates.Add(safe);
        }

        var generator = new UtilityGenerator(tokens);
        var utilities = generator.Generate(candidates);

        // ⚠ The base sheets first, and `@apply` expanded before anything else sees them. The layer
        // is what makes a component rule beat a utility, so the order here is not what decides it —
        // which is exactly why the base sheet goes first: written second it would win on source
        // order too, and a layering regression would then be invisible.
        var css = new StringBuilder();
        var expander = new ApplyExpander(tokens);

        foreach (var path in request.Base) {
            if (!File.Exists(path)) {
                errors.Add($"{path}: the base stylesheet is not there");
                continue;
            }

            css.Append(expander.Expand(File.ReadAllText(path))).Append('\n');

            foreach (var diagnostic in expander.Diagnostics) {
                errors.Add($"{path}: {diagnostic}");
            }
        }

        css.Append(utilities);

        return new StyleGenResult(css.ToString(), utilities, [.. generator.Unrecognised], errors, generator.RuleCount);
    }

    /// <summary>Writes whatever the request asked to be written.</summary>
    /// <param name="request">The request.</param>
    /// <param name="result">What the run produced.</param>
    /// <remarks>
    ///     ⚠ <b>Only when the bytes differ.</b> An output rewritten with identical content still has
    ///     a new timestamp, and a timestamp is what every incremental build in the tool chain reads —
    ///     so an unconditional write makes the compiler rerun on every build and, worse, makes a
    ///     watcher fire on every build. This is the whole of the incrementality that MSBuild's
    ///     <c>Inputs</c>/<c>Outputs</c> cannot express, because it is about the content rather than
    ///     about the timestamps.
    /// </remarks>
    internal static void Write(StyleGenRequest request, StyleGenResult result) {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(result);

        if (request.Output is { } output) {
            WriteIfDifferent(output, result.Css);
        }

        if (request.Report is { } report) {
            WriteIfDifferent(report, string.Join('\n', result.Unrecognised));
        }

        if (request.Accessor is { } accessor) {
            WriteIfDifferent(accessor, Accessor(request, result));
        }
    }

    /// <summary>The generated C#: the sheet as constants, and nothing else.</summary>
    /// <remarks>
    ///     ⚠ <b>Constants rather than a file read, so that a shipped game has no build-time artefact
    ///     to deploy.</b> The <c>.vcss</c> in <c>obj/</c> is for the tools; the binary carries the
    ///     text. Split one literal per line and concatenated — string concatenation of literals is a
    ///     constant expression, so this is still a <c>const</c>, and a diff of the generated file is
    ///     readable instead of being one line ten thousand characters long.
    /// </remarks>
    internal static string Accessor(StyleGenRequest request, StyleGenResult result) {
        var text = new StringBuilder();

        text.AppendLine("// <auto-generated />");
        text.AppendLine("// Generated by Vixen.StyleGen. Do not edit: the next build overwrites it.");
        text.AppendLine("#nullable enable");
        text.AppendLine();
        text.Append("namespace ").Append(request.Namespace).AppendLine(";");
        text.AppendLine();
        text.AppendLine("/// <summary>The stylesheet this project's markup and tokens compile to.</summary>");
        text.Append(request.Public ? "public" : "internal").Append(" static class ").AppendLine(request.Class);
        text.AppendLine("{");

        text.AppendLine("    /// <summary>The utilities this project uses, in <c>@layer utilities</c>.</summary>");
        Literal(text, "Utilities", result.Utilities);
        text.AppendLine();
        text.AppendLine("    /// <summary>The whole sheet: the hand-written rules first, then the utilities.</summary>");
        Literal(text, "Css", result.Css);
        text.AppendLine();
        text.AppendLine("    /// <summary>How many utility rules were emitted.</summary>");
        text.Append("    public const int RuleCount = ").Append(result.RuleCount).AppendLine(";");

        text.AppendLine("}");

        return text.ToString();
    }

    static void Literal(StringBuilder text, string name, string value) {
        text.Append("    public const string ").Append(name).Append(" =");

        if (value.Length == 0) {
            text.AppendLine(" \"\";");
            return;
        }

        text.AppendLine();

        var lines = value.Split('\n');

        for (var index = 0; index < lines.Length; index++) {
            var last = index == lines.Length - 1;
            var line = last ? lines[index] : lines[index] + "\n";

            if (last && line.Length == 0) {
                // A trailing newline made the split produce an empty tail; the line before it
                // already carries the `\n`.
                text.Length -= 3;
                text.AppendLine(";");
                return;
            }

            text.Append("        \"").Append(Escape(line)).Append('"').AppendLine(last ? ";" : " +");
        }
    }

    static string Escape(string line) {
        var escaped = new StringBuilder(line.Length + 8);

        foreach (var c in line) {
            switch (c) {
                case '\\': escaped.Append("\\\\"); break;
                case '"': escaped.Append("\\\""); break;
                case '\n': escaped.Append("\\n"); break;
                case '\r': escaped.Append("\\r"); break;
                default: escaped.Append(c); break;
            }
        }

        return escaped.ToString();
    }

    static ThemeTokens ReadTokens(string? path, List<string> errors) {
        if (path is null) {
            return new ThemeTokens();
        }

        if (!File.Exists(path)) {
            errors.Add($"{path}: there is no theme file here");
            return new ThemeTokens();
        }

        return ThemeTokens.Parse(File.ReadAllText(path));
    }

    static void WriteIfDifferent(string path, string content) {
        if (Path.GetDirectoryName(path) is { Length: > 0 } directory) {
            Directory.CreateDirectory(directory);
        }

        if (File.Exists(path) && string.Equals(File.ReadAllText(path), content, StringComparison.Ordinal)) {
            return;
        }

        File.WriteAllText(path, content);
    }
}
