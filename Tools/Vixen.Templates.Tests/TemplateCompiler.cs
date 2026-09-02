// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Vixen.Cli;
using Vixen.Editor.Core;
using Vixen.Ui.Markup.Generators;

namespace Vixen.Templates.Tests;

/// <summary>A template's <c>.vxml</c>, handed to the generator without one existing on disk.</summary>
sealed class TemplateMarkup(string path, string text) : AdditionalText {
    public override string Path { get; } = path;

    public override SourceText GetText(CancellationToken cancellationToken = default) => SourceText.From(text);
}

/// <summary>The two MSBuild properties the VXML generator reads, and nothing else.</summary>
/// <remarks>
///     ⚠ <b>The same two <c>Vixen.Ui.targets</c> makes compiler-visible</b>, which is what a
///     scaffolded project gets from its <c>PackageReference</c>. A namespace or a project directory
///     spelled differently here would be testing a build nobody has.
/// </remarks>
sealed class TemplateBuildOptions(string projectDirectory, string rootNamespace) : AnalyzerConfigOptionsProvider {
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
    public static IReadOnlyList<string> Errors(ProjectTemplate template, string projectName) =>
        Compile(template, projectName).Errors;

    /// <summary>Compiles one template's C# and loads it, so a test can call what it wrote.</summary>
    /// <param name="template">The template.</param>
    /// <param name="projectName">The name to instantiate it under.</param>
    /// <returns>The scaffolded project, as a loaded assembly.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A step past what the rest of this file does, and worth the extra thirty lines
    ///         for exactly one kind of claim: what a template's code <em>does</em> when the host
    ///         calls it.</b> A template can name every type correctly, compile clean, and still be
    ///         wrong about the order the host calls it in — <c>AppConfig.Apply</c> runs before
    ///         <c>Game.OnConfigure</c>, so a scaffold that assigns a property the command line also
    ///         sets silently throws the operator's value away. That is a behaviour, and no amount of
    ///         reading the source as text asserts it.
    ///     </para>
    ///     <para>
    ///         The references are the same ones the compilation uses, which are the assemblies
    ///         loaded beside the test — so the loaded assembly resolves <c>Vixen.*</c> to the very
    ///         objects the test already holds, and a <c>Game</c> it produces really is a
    ///         <c>Vixen.App.Game</c>.
    ///     </para>
    /// </remarks>
    public static Assembly Load(ProjectTemplate template, string projectName) {
        var (compilation, errors) = Compile(template, projectName);

        if (errors.Count > 0) {
            throw new InvalidOperationException(
                $"{template.Id} does not compile, so it cannot be loaded:{Environment.NewLine}"
                + string.Join(Environment.NewLine, errors)
            );
        }

        using var image = new MemoryStream();
        var emitted = compilation.Emit(image);

        if (!emitted.Success) {
            throw new InvalidOperationException(
                $"{template.Id} compiles and does not emit:{Environment.NewLine}"
                + string.Join(Environment.NewLine, emitted.Diagnostics.Select(diagnostic => diagnostic.ToString()))
            );
        }

        return Assembly.Load(image.ToArray());
    }

    static (CSharpCompilation Compilation, IReadOnlyList<string> Errors) Compile(
        ProjectTemplate template,
        string projectName
    ) {
        var files = template.Instantiate(projectName, "0.1.0");

        var sources = files
            .Where(file => file.Path.EndsWith(".cs", StringComparison.Ordinal))
            .Select(file => CSharpSyntaxTree.ParseText(
                    Encoding.UTF8.GetString(file.Content),
                    ParseOptions,
                    ProjectDirectory + file.Path
                )
            )
            .Prepend(CSharpSyntaxTree.ParseText(ImplicitUsings, ParseOptions))
            .Concat(StyleSheetAccessors(files, projectName));

        // A template with an OutputType of Exe has an entry point and one without it does not, and
        // Roslyn will complain about whichever it was not told to expect. `OutputType` is in the
        // project file, so it is read from there rather than guessed at.
        //
        // ⚠ A template with SEVERAL projects is compiled as one library, and that is a deliberate
        // approximation rather than an oversight. What this gate is for is API drift — a template
        // naming a constructor overload the engine dropped last month — and one compilation over
        // every file catches exactly that. Compiling the projects separately would mean modelling
        // their reference graph here, which is a second implementation of the thing the template is
        // demonstrating; and compiling them together as an executable would fail on four `Main`
        // methods that are each the only one in their own assembly.
        //
        // What it therefore does NOT catch is a missing project reference: `.Realm` using a type
        // from `.Contracts` compiles here whether or not the csproj says so. That is asserted
        // separately, by reading the project files, which is where the reference graph is written
        // down anyway.
        var projects = files.Where(file => file.Path.EndsWith(".csproj", StringComparison.Ordinal)).ToList();

        var executable = projects.Count == 1
            && Encoding.UTF8.GetString(projects[0].Content)
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

        // ⚠ **The markup, compiled by the generator a scaffolded project's PackageReference brings.**
        // Without this the gate is checking less than it looks like it checks: `vixen-app` ships an
        // `AppShell.vxml` that `AppDocument.cs` mounts, so a compilation over only the `.cs` would
        // either fail on a type nobody wrote or — if the C# were arranged to avoid naming it — pass
        // while the markup beside it went unparsed. `Vixen.Ui.Markup.Generators` is referenced as a
        // library for exactly this, the way `Vixen.Ui.Markup.Generators.Tests` does.
        var markup = files
            .Where(file => file.Path.EndsWith(".vxml", StringComparison.Ordinal))
            .Select(file => new TemplateMarkup(ProjectDirectory + file.Path, Encoding.UTF8.GetString(file.Content)))
            .ToArray();

        if (markup.Length > 0) {
            CSharpGeneratorDriver
                .Create(
                    [new VxmlGenerator().AsSourceGenerator()],
                    markup,
                    ParseOptions,
                    new TemplateBuildOptions(ProjectDirectory, projectName)
                )
                .RunGeneratorsAndUpdateCompilation(compilation, out var updated, out var produced);

            compilation = (CSharpCompilation) updated;

            // The generator's own diagnostics point at the `.vxml`, with its line and column. They
            // are the ones worth reading when a template's markup is wrong, so they are reported
            // rather than left to show up as a missing type in the C# that mounts the component.
            return (compilation, [
                .. produced
                    .Concat(compilation.GetDiagnostics())
                    .Where(diagnostic => diagnostic.Severity is DiagnosticSeverity.Error)
                    .Select(diagnostic => diagnostic.ToString())
            ]);
        }

        return (compilation, [
            .. compilation.GetDiagnostics()
                .Where(diagnostic => diagnostic.Severity is DiagnosticSeverity.Error)
                .Select(diagnostic => diagnostic.ToString())
        ]);
    }

    /// <summary>Where the scaffolded project is pretended to live.</summary>
    /// <remarks>
    ///     The VXML generator builds a component's namespace from <c>RootNamespace</c> plus the
    ///     file's folders <em>below the project directory</em>, so every path handed to it has to
    ///     share a root or a component in the project's own directory would come out namespaced
    ///     after the whole absolute path. Nothing is read from disk; this is a prefix.
    /// </remarks>
    const string ProjectDirectory = "/scaffold/";

    /// <summary>
    ///     The class the utility build step would have written, in the shape it writes it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A stand-in, and the one thing in this file that is not the real mechanism.</b>
    ///         The utility stylesheet is produced by <c>Tools/Vixen.StyleGen</c> — an out-of-process
    ///         MSBuild step, not a source generator — so there is nothing to hand a
    ///         <c>GeneratorDriver</c>. What a template's C# depends on is the *shape* of what that
    ///         step emits, and that is what this supplies: the three members
    ///         <c>StyleGenRunner.Accessor</c> writes, under the name and namespace the
    ///         <c>.targets</c> default to.
    ///     </para>
    ///     <para>
    ///         So this catches a template naming a member the accessor does not have, and does not
    ///         catch the accessor changing shape. The second is what
    ///         <c>Vixen.StyleGen.Tests</c> is for.
    ///     </para>
    ///     <para>
    ///         Emitted only for a template that actually has a theme file, because that is the
    ///         condition the generation target itself carries: a project with no
    ///         <c>vixen.ui.vcss</c> gets no sheet and no accessor, and a stub here would let such a
    ///         template compile against a class its own build would never produce.
    ///     </para>
    /// </remarks>
    static IEnumerable<SyntaxTree> StyleSheetAccessors(IReadOnlyList<TemplateFile> files, string projectName) {
        if (!files.Any(file => Path.GetFileName(file.Path) == "vixen.ui.vcss")) {
            yield break;
        }

        yield return CSharpSyntaxTree.ParseText(
            $$"""
            namespace {{projectName}};

            internal static class VixenUtilityStyles {
                public const string Utilities = "";
                public const string Css = "";
                public const int RuleCount = 0;
            }
            """,
            ParseOptions
        );
    }
}
