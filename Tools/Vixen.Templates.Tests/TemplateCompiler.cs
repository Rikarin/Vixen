// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Vixen.Cli;

namespace Vixen.Templates.Tests;

/// <summary>Compiles the C# a template writes, without building a project.</summary>
/// <remarks>
///     <para>
///         <b>This is the gate the templates otherwise do not have.</b> Nothing in the repository
///         compiles <c>templates/**/*.cs</c> — they are somebody else's project, deliberately outside
///         every glob — so a template that names a constructor overload the engine dropped last month
///         builds a perfectly good package and fails on the machine of the first person to run
///         <c>dotnet new</c>. Roslyn over the same assemblies the project's <c>PackageReference</c>s
///         resolve to is the closest thing to that person's first build that runs in a unit test.
///     </para>
///     <para>
///         ⚠ <b>What it does not check is the project file.</b> The <c>PackageReference</c>s and the
///         SDK version are asserted separately, by reading them; whether they restore is a question
///         for a feed that has the packages on it, which is CI's and not this test's.
///     </para>
/// </remarks>
static class TemplateCompiler {
    /// <summary>Everything loaded beside the test, which is what the templates compile against.</summary>
    /// <remarks>
    ///     The same arrangement as <c>Vixen.Ui.Markup.Generators.Tests</c>: the trusted platform
    ///     assemblies plus every <c>Vixen.*.dll</c> the test project's references dropped into the
    ///     output directory.
    /// </remarks>
    static readonly ImmutableArray<MetadataReference> References = [
        .. ((string) AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Concat(Directory.EnumerateFiles(AppContext.BaseDirectory, "Vixen.*.dll"))
            .Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal)
            .Select(path => (MetadataReference) MetadataReference.CreateFromFile(path))
    ];

    /// <summary>
    ///     What <c>ImplicitUsings</c> generates, written out because Roslyn does not.
    /// </summary>
    /// <remarks>
    ///     Every template turns implicit usings on, so a compilation without these would fail on
    ///     <c>File</c>, <c>Path</c> and <c>Enumerable</c> and be reporting the test's arrangement
    ///     rather than the template's code. This is the .NET SDK's own list for
    ///     <c>Microsoft.NET.Sdk</c>.
    /// </remarks>
    const string ImplicitUsings = """
        global using global::System;
        global using global::System.Collections.Generic;
        global using global::System.IO;
        global using global::System.Linq;
        global using global::System.Net.Http;
        global using global::System.Threading;
        global using global::System.Threading.Tasks;
        """;

    /// <summary>How the templates are parsed.</summary>
    /// <remarks>
    ///     ⚠ <c>Preview</c> rather than <c>Latest</c>, and it is about this package rather than about
    ///     the templates. <c>Microsoft.CodeAnalysis.CSharp</c> is pinned some way behind the SDK the
    ///     repository builds with, so features that are shipped and ordinary to the real compiler —
    ///     <c>params ReadOnlySpan&lt;T&gt;</c>, ref-struct interfaces — are still gated here. Parsing
    ///     as preview makes this compilation accept what the SDK already accepts; it does not let a
    ///     template use anything the SDK would refuse.
    /// </remarks>
    static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);

    /// <summary>Compiles one template's C# and returns whatever the compiler said.</summary>
    /// <param name="template">The template.</param>
    /// <param name="projectName">The name to instantiate it under.</param>
    /// <returns>The errors, one per line, or an empty list.</returns>
    public static IReadOnlyList<string> Errors(ProjectTemplate template, string projectName) {
        var files = template.Instantiate(projectName, "0.1.0");

        var sources = files
            .Where(file => file.Path.EndsWith(".cs", StringComparison.Ordinal))
            .Select(file => CSharpSyntaxTree.ParseText(
                    Encoding.UTF8.GetString(file.Content),
                    ParseOptions,
                    file.Path
                )
            )
            .Prepend(CSharpSyntaxTree.ParseText(ImplicitUsings, ParseOptions));

        // A template with an OutputType of Exe has an entry point and one without it does not, and
        // Roslyn will complain about whichever it was not told to expect. `OutputType` is in the
        // project file, so it is read from there rather than guessed at.
        var project = files.Single(file => file.Path.EndsWith(".csproj", StringComparison.Ordinal));
        var executable = Encoding.UTF8.GetString(project.Content)
            .Contains("<OutputType>Exe</OutputType>", StringComparison.Ordinal);

        var compilation = CSharpCompilation.Create(
            projectName,
            sources,
            References,
            new CSharpCompilationOptions(
                executable ? OutputKind.ConsoleApplication : OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable
            )
        );

        return compilation.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity is DiagnosticSeverity.Error)
            .Select(diagnostic => diagnostic.ToString())
            .ToArray();
    }
}
