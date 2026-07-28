// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Vixen.Input.Generators;

/// <summary>What the build told the generator about the project it is running in.</summary>
/// <param name="ProjectDirectory">What paths are relative to. Empty when MSBuild did not say.</param>
/// <param name="RootNamespace">The project's root namespace, or empty for the global one.</param>
internal readonly record struct ProjectOptions(string ProjectDirectory, string RootNamespace);

/// <summary>One <c>.vxinput</c>, reduced to the strings that decide what comes out of it.</summary>
/// <param name="FullPath">Where the file is.</param>
/// <param name="HintName">What the generated file is called inside the compilation.</param>
/// <param name="Namespace">Where the accessor class goes, or empty for the global namespace.</param>
/// <param name="FileName">The file's name without its extension, the default asset name.</param>
/// <param name="Text">The file's contents.</param>
/// <remarks>
///     ⚠ <b>The cache key of the whole generator, so it holds nothing but values.</b> An
///     <c>AdditionalText</c> is an object the host may hand out afresh on every compilation, so a
///     pipeline that carried one past the first step would compare references and re-read every
///     keystroke in an unrelated file.
/// </remarks>
internal readonly record struct VxInputSource(
    string FullPath,
    string HintName,
    string Namespace,
    string FileName,
    string Text
);

/// <summary>A reader diagnostic flattened into values a Roslyn one can be rebuilt from.</summary>
/// <param name="Message">What is wrong.</param>
/// <param name="FilePath">The <c>.vxinput</c> it is about.</param>
/// <param name="Line">The one-based line.</param>
/// <param name="Column">The one-based column.</param>
internal readonly record struct ReportedDiagnostic(string Message, string FilePath, int Line, int Column);

/// <summary>What one <c>.vxinput</c> compiled to.</summary>
/// <param name="HintName">The generated file's name.</param>
/// <param name="Source">The C#, or null when nothing could be generated.</param>
/// <param name="Diagnostics">Everything to report.</param>
internal readonly record struct VxInputOutput(
    string HintName,
    string? Source,
    EquatableArray<ReportedDiagnostic> Diagnostics
);

/// <summary>
///     Turns every <c>.vxinput</c> in a project into the class that makes its actions members.
/// </summary>
/// <remarks>
///     <para>
///         <c>Input.Player.Move.ReadValue&lt;Vector2&gt;()</c> instead of
///         <c>input.FindAction("Player/Move")</c>. The point is not the typing saved, it is that
///         renaming an action in the asset becomes a compiler error at every use site rather than a
///         string that resolves to null on the frame the player presses it.
///     </para>
///     <para>
///         <b>The document is emitted into the class as a constant.</b> The generated
///         <c>Create()</c> therefore needs no file, no content pipeline and no asset database, which
///         is what makes a <c>.vxinput</c> work in a unit test and in a trimmed NativeAOT player on
///         the first day. A game that ships its actions as content instead hands the loaded text to
///         the generated constructor.
///     </para>
///     <para>
///         ⚠ <b>A generator is judged by what it does <i>not</i> re-run.</b> Every step here is
///         keyed on values, so editing a C# file re-runs nothing and editing one <c>.vxinput</c>
///         re-reads that file alone. The pipeline never calls <c>Collect()</c>.
///     </para>
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class VxInputGenerator : IIncrementalGenerator {
    const string Extension = ".vxinput";

    /// <summary>The name of the step that reads a <c>.vxinput</c> down to values.</summary>
    public const string ReadStep = "VxInput.Read";

    /// <summary>The name of the step that binds the document and emits the accessor.</summary>
    public const string CompileStep = "VxInput.Compile";

    /// <summary>The code a document that will not read is reported under.</summary>
    public const string ReadDiagnosticId = "VXIN0001";

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context) {
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

    static VxInputSource Read(AdditionalText text, ProjectOptions options, CancellationToken token) {
        var contents = text.GetText(token)?.ToString();

        if (contents is null) {
            // Unreadable — reported by the host as its own error. Filtered out above.
            return default;
        }

        var relative = Relative(text.Path, options.ProjectDirectory);

        return new(
            text.Path,
            HintName(relative),
            Namespace(options.RootNamespace, relative),
            FileName(relative),
            contents
        );
    }

    /// <summary>The file's path below the project directory, with forward slashes.</summary>
    /// <remarks>
    ///     Relative rather than absolute because the hint name is built from it and a hint name is
    ///     part of the compilation — an absolute path would make one machine's build differ from
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
    ///     files share a hint name, so <c>Input/Game.vxinput</c> and <c>Input_Game.vxinput</c> must
    ///     not both come out as <c>Input_Game</c>. Doubling the underscores before folding the
    ///     separators into single ones is what keeps the map one-to-one.
    /// </remarks>
    static string HintName(string relative) {
        var withoutExtension = StripExtension(relative);
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

    static string FileName(string relative) {
        var withoutExtension = StripExtension(relative);
        var separator = withoutExtension.LastIndexOf('/');
        return separator < 0 ? withoutExtension : withoutExtension.Substring(separator + 1);
    }

    static string StripExtension(string relative) =>
        relative.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)
            ? relative.Substring(0, relative.Length - Extension.Length)
            : relative;

    /// <summary>The root namespace plus the file's folders, the way a C# file beside it is named.</summary>
    static string Namespace(string rootNamespace, string relative) {
        var builder = new StringBuilder(rootNamespace);
        var lastSeparator = relative.LastIndexOf('/');

        if (lastSeparator <= 0) {
            return builder.ToString();
        }

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

    static VxInputOutput Compile(VxInputSource source, CancellationToken token) {
        var result = InputActionAssetReader.Read(source.Text, source.FileName);
        token.ThrowIfCancellationRequested();

        var reported = ImmutableArray.CreateBuilder<ReportedDiagnostic>(result.Diagnostics.Count);

        foreach (var diagnostic in result.Diagnostics) {
            reported.Add(new(diagnostic.Message, source.FullPath, diagnostic.Line, diagnostic.Column));
        }

        // An asset with problems still emits. A binding this build cannot read is one error the
        // author fixes; a class that failed to exist is one error per use site across the project,
        // and the real cause buried among them.
        var text = result.Asset is null
            ? null
            : VxInputEmitter.Emit(result.Asset, source.Namespace, source.Text);

        return new(source.HintName, text, new(reported.ToImmutable()));
    }

    // ================================================================== Producing

    static void Produce(SourceProductionContext context, VxInputOutput output) {
        foreach (var diagnostic in output.Diagnostics) {
            context.ReportDiagnostic(Rebuild(diagnostic));
        }

        if (output.Source is { } source) {
            context.AddSource(output.HintName, SourceText.From(source, Encoding.UTF8));
        }
    }

    /// <summary>Rebuilds a Roslyn diagnostic that points at the <c>.vxinput</c> rather than at C#.</summary>
    /// <remarks>
    ///     The message goes in as an argument rather than as the template, because it quotes the
    ///     author's own source — a control path, a processor name — and a brace in a composite format
    ///     string is a placeholder or an exception. Under <c>{0}</c> nothing in it is read as
    ///     formatting.
    /// </remarks>
    static Diagnostic Rebuild(ReportedDiagnostic diagnostic) {
        var descriptor = new DiagnosticDescriptor(
            ReadDiagnosticId,
            "The input asset could not be read",
            "{0}",
            "VxInput",
            DiagnosticSeverity.Error,
            isEnabledByDefault: true
        );

        // The reader counts from one and Roslyn from zero. A diagnostic that lands a line late is
        // worse than one with no position at all, because the author reads the line it points at.
        var line = Math.Max(diagnostic.Line - 1, 0);
        var column = Math.Max(diagnostic.Column - 1, 0);

        var location = Location.Create(
            diagnostic.FilePath,
            new TextSpan(0, 0),
            new LinePositionSpan(new(line, column), new(line, column))
        );

        return Diagnostic.Create(descriptor, location, diagnostic.Message);
    }
}
