// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

namespace Vixen.Editor.Scripts;

/// <summary>One thing the compiler had to say about one place in one file.</summary>
/// <param name="IsError">Whether it stopped the build.</param>
/// <param name="File">The file, or empty for something not about a file.</param>
/// <param name="Line">The line, from one.</param>
/// <param name="Column">The column, from one.</param>
/// <param name="Id">The compiler's own code — <c>CS0103</c>.</param>
/// <param name="Message">What it said.</param>
/// <remarks>
///     ⚠ <b>A span rather than a line of console text, which is the whole reason this path uses
///     Roslyn in process.</b> <c>ProjectAssemblies</c> runs <c>dotnet build</c> and hands back
///     everything the console said, because a caller reporting a game build's failure has nowhere
///     better to put it. A panel listing errors somebody can click is a different thing and needs the
///     file, the line and the column as data.
/// </remarks>
public readonly record struct ScriptDiagnostic(bool IsError, string File, int Line, int Column, string Id, string Message) {
    /// <summary>The one-line form a console shows.</summary>
    public override string ToString() =>
        string.IsNullOrEmpty(File)
            ? $"{Id}: {Message}"
            : $"{System.IO.Path.GetFileName(File)}({Line},{Column}): {Id}: {Message}";
}

/// <summary>What compiling a project's <c>Editor/</c> folder produced.</summary>
/// <param name="AssemblyPath">Where the assembly landed, or <see langword="null" /> if none was written.</param>
/// <param name="Diagnostics">Everything the compiler said, errors and warnings together.</param>
/// <param name="Sources">How many files went in.</param>
/// <remarks>
///     ⚠ <b>A project with no <c>Editor/</c> folder is not a failure and is not an error.</b> Most
///     projects are content and scenes; an editor that reported a problem for one would be reporting
///     the absence of a feature nobody asked for. That case is no assembly, no diagnostics and no
///     sources, and <see cref="Failed" /> is false for it.
/// </remarks>
public sealed record ScriptBuild(string? AssemblyPath, IReadOnlyList<ScriptDiagnostic> Diagnostics, int Sources) {
    /// <summary>Nothing to build.</summary>
    public static ScriptBuild None { get; } = new(null, [], 0);

    /// <summary>Whether there were sources and no assembly came out of them.</summary>
    public bool Failed => Sources > 0 && AssemblyPath is null;

    /// <summary>Just the errors, which is what a panel leads with.</summary>
    public IEnumerable<ScriptDiagnostic> Errors => Diagnostics.Where(diagnostic => diagnostic.IsError);
}

/// <summary>The C# compiler, pointed at a project's own editor scripts.</summary>
/// <remarks>
///     <para>
///         <b>Doc 36 § P5, and Unity's headline workflow.</b> Code under a project's <c>Editor/</c>
///         folder is compiled into an editor-only assembly and is not part of what a build ships. It
///         is a convention with a compilation consequence, which is what makes "just write a script"
///         work at all.
///     </para>
///     <para>
///         ⚠ <b>In process rather than <c>dotnet build</c>, unlike the game code beside it.</b>
///         <c>ProjectAssemblies</c> shells out because a game's <c>.csproj</c> is a real project with
///         a restore, an SDK and package references that only MSBuild knows how to resolve. An
///         <c>Editor/</c> folder is a pile of <c>.cs</c> files with no project file, referencing
///         exactly what the running editor has loaded — so there is nothing for MSBuild to work out,
///         and a second process per keystroke would make the loop useless.
///     </para>
///     <para>
///         ⚠ <b>The references are the host's loaded assemblies, snapshotted.</b> That is a wider set
///         than a <c>.csproj</c> would name, and deliberately: a script author has no project file to
///         add a reference to, so what they can call is what the editor is running. The consequence is
///         that a script can break when the editor loads a panel it had not loaded before — which is
///         why the set is taken once per build rather than per file, so at least one build is
///         self-consistent.
///     </para>
/// </remarks>
public static class ScriptCompiler {
    /// <summary>What a project's editor-only assembly is called.</summary>
    /// <remarks>
    ///     ⚠ <b>Fixed rather than derived from the project's name.</b> It is the id the plugin host
    ///     holds the scripts under and the name a stack trace carries, and both have to survive
    ///     somebody renaming a folder. Nothing outside the editor ever sees it: the assembly is never
    ///     written into a build.
    /// </remarks>
    public const string AssemblyName = "Vixen.Project.EditorScripts";

    /// <summary>The folder a project keeps its editor scripts in.</summary>
    public const string FolderName = "Editor";

    /// <summary>Every editor script in a project, in a stable order.</summary>
    /// <param name="projectRoot">The project's root.</param>
    /// <returns>The files, or empty.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Every <c>Editor/</c> folder under the project, not just the one at the root.</b>
    ///         Unity's rule is that the folder name is what matters wherever it appears, so a feature
    ///         can keep its editor code beside the runtime code it is about. A single root folder
    ///         would make the convention a location instead of a convention.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Sorted, because a compiler's output has to be reproducible.</b> The file system's
    ///         order is not defined and differs between machines; two builds of the same sources
    ///         disagreeing about which duplicate-definition error comes first is a diff nobody can
    ///         review.
    ///     </para>
    ///     <para>
    ///         <c>Library/</c> and <c>bin/</c> and <c>obj/</c> are skipped: they hold what a build
    ///         produced, and a generated file that happens to sit under a folder called
    ///         <c>Editor</c> is not a script somebody wrote.
    ///     </para>
    /// </remarks>
    public static IReadOnlyList<string> Sources(string projectRoot) {
        ArgumentException.ThrowIfNullOrEmpty(projectRoot);

        if (!Directory.Exists(projectRoot)) {
            return [];
        }

        List<string> found = [];

        foreach (var folder in Directory.EnumerateDirectories(projectRoot, FolderName, SearchOption.AllDirectories)) {
            if (IsBuildOutput(projectRoot, folder)) {
                continue;
            }

            found.AddRange(Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories));
        }

        found.Sort(StringComparer.Ordinal);
        return found;
    }

    static bool IsBuildOutput(string projectRoot, string folder) {
        var relative = Path.GetRelativePath(projectRoot, folder);

        foreach (var part in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) {
            if (part is "Library" or "bin" or "obj" or "Build") {
                return true;
            }
        }

        return false;
    }

    /// <summary>Compiles a project's editor scripts into an assembly beside its library.</summary>
    /// <param name="projectRoot">The project's root.</param>
    /// <param name="output">The folder the assembly is written into.</param>
    /// <returns>What happened.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Written to disk rather than kept as bytes, because <c>PluginLoadContext</c> takes
    ///         a path.</b> That is not a workaround — the context reads the file into memory rather
    ///         than mapping it, precisely so the next build can overwrite what it just loaded, and a
    ///         file on disk is also what a debugger attaches symbols to.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Symbols are portable and are written beside it.</b> A script author's exception
    ///         arriving with a line number in their own file is most of what makes this workflow
    ///         usable; the cost is a file the editor deletes with the assembly.
    ///     </para>
    /// </remarks>
    public static ScriptBuild Compile(string projectRoot, string output) {
        ArgumentException.ThrowIfNullOrEmpty(projectRoot);
        ArgumentException.ThrowIfNullOrEmpty(output);

        var sources = Sources(projectRoot);

        if (sources.Count == 0) {
            return ScriptBuild.None;
        }

        List<SyntaxTree> trees = [];
        List<ScriptDiagnostic> problems = [];

        foreach (var file in sources) {
            string text;

            try {
                text = File.ReadAllText(file);
            } catch (IOException exception) {
                // ⚠ Reported rather than thrown. A save in flight is the commonest reason a watched
                // file cannot be read, and taking the editor down for it would make the loop the
                // thing that is unreliable.
                problems.Add(new(true, file, 1, 1, "VXS0001", $"could not be read: {exception.Message}"));
                continue;
            }

            trees.Add(CSharpSyntaxTree.ParseText(text, Options, file, System.Text.Encoding.UTF8));
        }

        if (problems.Count > 0) {
            return new(null, problems, sources.Count);
        }

        var compilation = CSharpCompilation.Create(
            AssemblyName,
            trees,
            References(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Debug)
        );

        Directory.CreateDirectory(output);

        var assemblyPath = Path.Combine(output, AssemblyName + ".dll");
        var symbolsPath = Path.ChangeExtension(assemblyPath, ".pdb");

        EmitResult result;

        // ⚠ Emitted into memory and written afterwards, so a failed emit cannot leave half a file
        // where the previous good one was. The loader would read it, and "the assembly is corrupt"
        // is a much worse message than the error that actually happened.
        using (var assembly = new MemoryStream())
        using (var symbols = new MemoryStream()) {
            result = compilation.Emit(assembly, symbols, options: new(debugInformationFormat: DebugInformationFormat.PortablePdb));

            problems.AddRange(result.Diagnostics.Where(Reportable).Select(Describe));

            if (result.Success) {
                File.WriteAllBytes(assemblyPath, assembly.ToArray());
                File.WriteAllBytes(symbolsPath, symbols.ToArray());
            }
        }

        return new(result.Success ? assemblyPath : null, problems, sources.Count);
    }

    /// <summary>The language the scripts are compiled as.</summary>
    /// <remarks>
    ///     ⚠ <b>The same version the engine is written in, and nullable reference types on.</b> A
    ///     script author copying a snippet out of the guide has to be compiling under the rules the
    ///     guide was written against, and a warning about a null they can actually get is worth more
    ///     in a script than anywhere else — nothing else in the process will catch it.
    /// </remarks>
    static CSharpParseOptions Options { get; } = new(LanguageVersion.Preview);

    /// <summary>Everything the host has loaded that a script may call.</summary>
    static IReadOnlyList<MetadataReference> References() {
        List<MetadataReference> references = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (var assembly in AssemblyLoadContext.Default.Assemblies) {
            // ⚠ A dynamic assembly has no file to read metadata out of, and asking for `Location`
            // throws for one on some runtimes rather than answering empty. Both are skipped.
            if (assembly.IsDynamic || Where(assembly) is not { Length: > 0 } location) {
                continue;
            }

            if (seen.Add(location)) {
                references.Add(MetadataReference.CreateFromFile(location));
            }
        }

        return references;
    }

    static string Where(Assembly assembly) {
        try {
            return assembly.Location;
        } catch (NotSupportedException) {
            return string.Empty;
        }
    }

    /// <summary>Whether a compiler message is worth showing.</summary>
    /// <remarks>
    ///     ⚠ <b>Hidden and info are dropped and warnings are kept.</b> A script's warnings are the
    ///     only static review it will ever get — there is no build server and no pull request — so
    ///     the panel shows them, and <see cref="ScriptBuild.Errors" /> is what decides whether
    ///     anything loaded.
    /// </remarks>
    static bool Reportable(Diagnostic diagnostic) =>
        diagnostic.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning;

    static ScriptDiagnostic Describe(Diagnostic diagnostic) {
        var span = diagnostic.Location.GetLineSpan();

        return new(
            diagnostic.Severity == DiagnosticSeverity.Error,
            span.Path ?? string.Empty,
            span.StartLinePosition.Line + 1,
            span.StartLinePosition.Character + 1,
            diagnostic.Id,
            diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture)
        );
    }
}
