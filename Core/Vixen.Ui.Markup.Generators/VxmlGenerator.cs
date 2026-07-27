// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Vixen.Ui.Markup.Binding;
using Vixen.Ui.Markup.Emit;
using VxmlDiagnostic = Vixen.Core.Syntax.Diagnostics.Diagnostic;
using VxmlSeverity = Vixen.Core.Syntax.Diagnostics.DiagnosticSeverity;
using VxmlSyntaxTree = Vixen.Ui.Markup.Syntax.SyntaxTree;

namespace Vixen.Ui.Markup.Generators;

/// <summary>
///     Turns every <c>.vxml</c> in a project into the C# partial class that builds it.
/// </summary>
/// <remarks>
///     <para>
///         The build half of the markup channel. <c>Vixen.Ui.Markup</c> is the compiler — lexer,
///         parser, binder, emitter — and this is what runs it: an
///         <see cref="IIncrementalGenerator" /> over the <c>.vxml</c> files the build hands it as
///         additional texts. Editing a file therefore changes a method body in the compilation,
///         which is what <c>dotnet watch</c> turns into a metadata update and what
///         <c>Vixen.Ui.HotReload</c> is waiting for on the other side.
///     </para>
///     <para>
///         ⚠ <b>A generator is judged by what it does <i>not</i> re-run.</b> Every step here is
///         keyed on values — paths, contents, namespaces — so editing a C# file re-runs nothing,
///         and editing one <c>.vxml</c> re-parses that file alone. The pipeline never calls
///         <c>Collect()</c>: batching every file into one array would make every edit invalidate
///         every output, which is the usual way a generator ends up incremental in name only.
///     </para>
///     <para>
///         ⚠ <b>Syntax errors stop the emit; binding errors do not.</b> A <c>VXML1xxx</c> means the
///         tree is a guess made during recovery, and emitting from a guess produces C# that may not
///         parse — which buries the one diagnostic the author needs under a page of noise about
///         generated code they cannot see. A <c>VXML2xxx</c> means the tree is right and its
///         meaning is wrong, so the class is still emitted: the type keeps existing, and the error
///         count stays at the one real cause instead of one per use site across the project.
///     </para>
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class VxmlGenerator : IIncrementalGenerator {
    const string Extension = ".vxml";

    /// <summary>The name of the step that reads a <c>.vxml</c> down to values.</summary>
    /// <remarks>
    ///     Named so a test can ask what the pipeline did rather than how long it took. An
    ///     incremental generator's whole claim is about the second run, and the only way to check it
    ///     is to look at the reasons Roslyn recorded for each step.
    /// </remarks>
    public const string ReadStep = "Vxml.Read";

    /// <summary>The name of the step that lexes, parses, binds and emits.</summary>
    public const string CompileStep = "Vxml.Compile";

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context) {
        // Only the two MSBuild properties this needs, pulled out as strings. The options provider
        // itself is not equatable, so anything downstream of it whole would never cache.
        var options = context.AnalyzerConfigOptionsProvider.Select(
            static (provider, _) => {
                provider.GlobalOptions.TryGetValue("build_property.projectdir", out var directory);
                provider.GlobalOptions.TryGetValue("build_property.rootnamespace", out var @namespace);
                return new ProjectOptions(directory ?? string.Empty, @namespace ?? string.Empty);
            }
        );

        var sources = context.AdditionalTextsProvider
            .Where(static text => text.Path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
            .Combine(options)
            .Select(static (pair, token) => Read(pair.Left, pair.Right, token))
            .Where(static source => source.Text is not null)
            .WithTrackingName(ReadStep);

        var outputs = sources
            .Select(static (source, token) => Compile(source, token))
            .WithTrackingName(CompileStep);

        context.RegisterSourceOutput(outputs, static (production, output) => Produce(production, output));
    }

    // ================================================================== Reading

    static VxmlSource Read(AdditionalText text, ProjectOptions options, CancellationToken token) {
        var contents = text.GetText(token)?.ToString();
        if (contents is null) {
            // Unreadable — reported by the host as its own error. Filtered out above.
            return default;
        }

        var relative = Relative(text.Path, options.ProjectDirectory);

        return new VxmlSource(
            text.Path,
            HintName(relative),
            Namespace(options.RootNamespace, relative),
            contents
        );
    }

    /// <summary>The file's path below the project directory, with forward slashes.</summary>
    /// <remarks>
    ///     Relative rather than absolute because the hint name is built from it and a hint name is
    ///     part of the compilation. An absolute path would make one machine's build differ from
    ///     another's, which is the property <c>Deterministic</c> is set for.
    /// </remarks>
    static string Relative(string path, string projectDirectory) {
        var normalised = path.Replace('\\', '/');
        if (projectDirectory.Length == 0) {
            return normalised.TrimStart('/');
        }

        var root = projectDirectory.Replace('\\', '/');
        if (!root.EndsWith('/')) {
            root += "/";
        }

        return normalised.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            ? normalised.Substring(root.Length)
            : normalised.TrimStart('/');
    }

    /// <summary>What the generated file is called inside the compilation.</summary>
    /// <remarks>
    ///     ⚠ <b>The encoding is injective, and it has to be.</b> Roslyn throws when two generated
    ///     files share a hint name, so <c>Ui/Counter.vxml</c> and <c>Ui_Counter.vxml</c> must not
    ///     both come out as <c>Ui_Counter</c>. Doubling the underscores before folding the
    ///     separators into single ones is what keeps the map one-to-one — the alternative, naming
    ///     the file after the component, collides between two folders and crashes the build with a
    ///     message about neither of them.
    /// </remarks>
    static string HintName(string relative) {
        var withoutExtension = relative.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)
            ? relative.Substring(0, relative.Length - Extension.Length)
            : relative;

        var builder = new StringBuilder(withoutExtension.Length + 8);
        foreach (var character in withoutExtension) {
            switch (character) {
                case '_':
                    builder.Append("__");
                    break;

                case '/':
                    builder.Append('_');
                    break;

                default:
                    builder.Append(char.IsLetterOrDigit(character) ? character : '_');
                    break;
            }
        }

        return builder.Append(".g.cs").ToString();
    }

    /// <summary>The root namespace plus the file's folders, the way a C# file beside it is named.</summary>
    static string Namespace(string rootNamespace, string relative) {
        var builder = new StringBuilder(rootNamespace);

        var lastSeparator = relative.LastIndexOf('/');
        if (lastSeparator > 0) {
            foreach (var segment in relative.Substring(0, lastSeparator).Split('/')) {
                var identifier = Identifier(segment);
                if (identifier.Length == 0) {
                    continue;
                }

                if (builder.Length > 0) {
                    builder.Append('.');
                }

                builder.Append(identifier);
            }
        }

        return builder.ToString();
    }

    static string Identifier(string segment) {
        if (segment.Length == 0 || segment == "." || segment == "..") {
            return string.Empty;
        }

        var builder = new StringBuilder(segment.Length + 1);
        if (!char.IsLetter(segment[0]) && segment[0] != '_') {
            builder.Append('_');
        }

        foreach (var character in segment) {
            builder.Append(char.IsLetterOrDigit(character) || character == '_' ? character : '_');
        }

        return builder.ToString();
    }

    // ================================================================== Compiling

    static VxmlOutput Compile(VxmlSource source, CancellationToken token) {
        var tree = VxmlSyntaxTree.ParseText(source.Text, source.FullPath);
        token.ThrowIfCancellationRequested();

        var component = Binder.Bind(tree, out var diagnostics);
        token.ThrowIfCancellationRequested();

        var reported = ImmutableArray.CreateBuilder<ReportedDiagnostic>(diagnostics.Length);
        var recovered = false;

        foreach (var diagnostic in diagnostics) {
            reported.Add(Flatten(diagnostic, source.FullPath));
            recovered |= diagnostic.IsError
                && string.Equals(diagnostic.Descriptor.Category, "Syntax", StringComparison.Ordinal);
        }

        var emit = component is not null && !recovered;
        var text = emit
            ? ComponentEmitter.Emit(component!, source.FullPath, NullIfEmpty(source.Namespace))
            : null;

        return new VxmlOutput(source.HintName, text, new EquatableArray<ReportedDiagnostic>(reported.ToImmutable()));
    }

    static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    static ReportedDiagnostic Flatten(VxmlDiagnostic diagnostic, string path) {
        var lines = diagnostic.Location.GetLineSpan();

        return new ReportedDiagnostic(
            diagnostic.Descriptor.Id,
            diagnostic.Descriptor.Title,
            diagnostic.Descriptor.Category,
            diagnostic.GetMessage(),
            Severity(diagnostic.Severity),
            diagnostic.Location.FilePath.Length == 0 ? path : diagnostic.Location.FilePath,
            diagnostic.Location.SourceSpan.Start,
            diagnostic.Location.SourceSpan.Length,
            lines.Start.Line,
            lines.Start.Character,
            lines.End.Line,
            lines.End.Character
        );
    }

    static DiagnosticSeverity Severity(VxmlSeverity severity) =>
        severity switch {
            VxmlSeverity.Error => DiagnosticSeverity.Error,
            VxmlSeverity.Warning => DiagnosticSeverity.Warning,
            VxmlSeverity.Info => DiagnosticSeverity.Info,
            _ => DiagnosticSeverity.Hidden
        };

    // ================================================================== Producing

    static void Produce(SourceProductionContext context, VxmlOutput output) {
        foreach (var diagnostic in output.Diagnostics) {
            context.ReportDiagnostic(Rebuild(diagnostic));
        }

        if (output.Source is { } source) {
            context.AddSource(output.HintName, SourceText.From(source, Encoding.UTF8));
        }
    }

    /// <summary>Rebuilds a Roslyn diagnostic that points at the <c>.vxml</c> rather than at C#.</summary>
    /// <remarks>
    ///     <para>
    ///         The message goes in as an argument rather than as the template, because a VXML
    ///         message quotes the author's own source — <c>'&lt;div&gt;' is never closed</c>, and
    ///         VXML1005's is a bare <c>'{'</c> — and a brace in a composite format string is a
    ///         placeholder or an exception. Under <c>{0}</c> nothing in it is ever read as
    ///         formatting.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>This is insurance, and it is labelled as insurance because the sabotage that
    ///         was meant to prove it necessary failed.</b> Handing the formatted message in as the
    ///         template produces exactly the same output: Roslyn catches the
    ///         <see cref="FormatException" /> and falls back to the unformatted template, which
    ///         here is the finished message, so the brace arrives intact and nothing crashes.
    ///         Measured rather than assumed — the first version of this comment claimed a CS8785,
    ///         and there is none. What the fallback would do to a message that still had a
    ///         placeholder in it is discard the arguments silently, and it is an implementation
    ///         detail with no contract either way. Kept for that, not for a failure anyone has seen.
    ///     </para>
    /// </remarks>
    static Diagnostic Rebuild(ReportedDiagnostic diagnostic) {
        var descriptor = new DiagnosticDescriptor(
            diagnostic.Id,
            diagnostic.Title,
            "{0}",
            diagnostic.Category,
            diagnostic.Severity,
            isEnabledByDefault: true
        );

        var location = Location.Create(
            diagnostic.FilePath,
            new TextSpan(diagnostic.Start, diagnostic.Length),
            new LinePositionSpan(
                new LinePosition(diagnostic.StartLine, diagnostic.StartCharacter),
                new LinePosition(diagnostic.EndLine, diagnostic.EndCharacter)
            )
        );

        return Diagnostic.Create(descriptor, location, diagnostic.Message);
    }
}
