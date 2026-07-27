// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace Vixen.Ui.Markup.Generators.Tests;

/// <summary>A <c>.vxml</c> the driver can be handed without one existing on disk.</summary>
sealed class MarkupFile(string path, string text) : AdditionalText {
    public override string Path { get; } = path;

    public override SourceText GetText(CancellationToken cancellationToken = default) =>
        SourceText.From(text);
}

/// <summary>The two MSBuild properties the generator reads, and nothing else.</summary>
sealed class BuildOptions(string projectDirectory, string rootNamespace) : AnalyzerConfigOptionsProvider {
    public override AnalyzerConfigOptions GlobalOptions { get; } = new Options(
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            ["build_property.projectdir"] = projectDirectory,
            ["build_property.rootnamespace"] = rootNamespace
        }
    );

    public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => Options.Empty;

    public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => Options.Empty;

    sealed class Options(Dictionary<string, string> values) : AnalyzerConfigOptions {
        public static readonly Options Empty = new([]);

        public override bool TryGetValue(string key, out string value) {
            var found = values.TryGetValue(key, out var result);
            value = result ?? string.Empty;
            return found;
        }
    }
}

/// <summary>Runs the generator the way a build runs it.</summary>
static class Harness {
    public const string ProjectDirectory = "/repo/Game/";

    public const string RootNamespace = "Game";

    /// <summary>Everything loaded beside the test, which is the runtime the output calls.</summary>
    public static readonly ImmutableArray<MetadataReference> References = [
        .. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(System.IO.Path.PathSeparator)
            .Concat(Directory.EnumerateFiles(AppContext.BaseDirectory, "Vixen.*.dll"))
            .Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
    ];

    /// <summary>A compilation with the given C# in it and nothing generated yet.</summary>
    public static CSharpCompilation Compilation(params (string Path, string Text)[] files) =>
        CSharpCompilation.Create(
            "Generated",
            files.Select(file => CSharpSyntaxTree.ParseText(
                file.Text,
                new CSharpParseOptions(LanguageVersion.Latest),
                file.Path
            )),
            References,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable
            )
        );

    /// <summary>A driver over the given markup files, with step tracking on.</summary>
    public static GeneratorDriver Driver(params MarkupFile[] files) =>
        CSharpGeneratorDriver.Create(
            [new VxmlGenerator().AsSourceGenerator()],
            files,
            new CSharpParseOptions(LanguageVersion.Latest),
            new BuildOptions(ProjectDirectory, RootNamespace),
            new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true)
        );

    /// <summary>Runs the generator once and hands back what it produced.</summary>
    public static Run Once(string markup, string path = ProjectDirectory + "Counter.vxml", params (string Path, string Text)[] csharp) {
        var driver = Driver(new MarkupFile(path, markup));
        var compilation = Compilation(csharp);
        var after = driver.RunGeneratorsAndUpdateCompilation(compilation, out var updated, out _);

        return new Run(after, updated);
    }

    /// <summary>What one generator run produced.</summary>
    /// <param name="Driver">The driver, for a second run or for its tracked steps.</param>
    /// <param name="Compilation">The compilation with the generated trees folded in.</param>
    public readonly record struct Run(GeneratorDriver Driver, Compilation Compilation) {
        /// <summary>What the generator itself said.</summary>
        public ImmutableArray<Diagnostic> Diagnostics => Driver.GetRunResult().Diagnostics;

        /// <summary>The generated files, source text and all.</summary>
        public ImmutableArray<SyntaxTree> Generated => Driver.GetRunResult().GeneratedTrees;

        /// <summary>Errors from compiling the generated code together with the hand-written code.</summary>
        public ImmutableArray<Diagnostic> Errors =>
            [.. Compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error)];

        /// <summary>The one generated file, when there should only be one.</summary>
        public string Source => Generated.Single().ToString();
    }
}
