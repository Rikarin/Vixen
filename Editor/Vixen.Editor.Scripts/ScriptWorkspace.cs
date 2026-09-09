// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

namespace Vixen.Editor.Scripts;

/// <summary>A project's editor scripts, compiled again and again without starting over.</summary>
/// <remarks>
///     <para>
///         <b>Doc 36 § P5's incremental compilation.</b> <see cref="ScriptCompiler.Compile" /> reads,
///         parses and metadata-loads everything on every save; a project with a dozen scripts does not
///         notice and one with hundreds does. What is kept between builds is the two expensive things
///         and nothing else: the parsed syntax tree of every file whose text has not changed, and the
///         <see cref="MetadataReference" /> set — a few hundred assemblies whose metadata Roslyn reads
///         and caches <i>per reference object</i>, so handing it the same objects again is what makes
///         the second build cheap.
///     </para>
///     <para>
///         ⚠ <b>Staleness is decided by the text and not by a timestamp, deliberately.</b> A
///         last-write time plus a length is the classic file cache and the classic file-cache bug: two
///         saves inside one filesystem tick that leave the file the same length are indistinguishable,
///         and the symptom is an editor running the code somebody deleted. Reading the file is what
///         the old path did anyway; what this skips is the parse, which is the part that costs.
///     </para>
///     <para>
///         ⚠ <b>The reference set is rebuilt when the default context's assembly count changes.</b>
///         <c>ScriptCompiler</c>'s own remarks say a script can break when the editor loads a panel it
///         had not loaded before; that stays true, and a set held for ever would have made it worse by
///         freezing the answer at whatever was loaded when the project opened.
///     </para>
///     <para>
///         ⚠ <b>Not thread-safe, and it does not need to be.</b> A rebuild is raised by the asset
///         watcher's pump on the frame thread, which is the only caller — see <c>ScriptsModule</c>.
///     </para>
/// </remarks>
public sealed class ScriptWorkspace {
    readonly Dictionary<string, Parsed> parsed = new(StringComparer.Ordinal);

    IReadOnlyList<MetadataReference>? references;
    int referencedAssemblies;

    /// <summary>How many files this workspace is holding parsed trees for.</summary>
    /// <remarks>
    ///     ⚠ <b>Part of what a test asserts, because a cache that only ever grows is the other way
    ///     this goes wrong.</b> A file somebody deleted has to leave, or the compilation still
    ///     contains its types and the editor keeps loading the tool that was removed.
    /// </remarks>
    public int Cached => parsed.Count;

    /// <summary>Compiles the project's editor scripts, reusing whatever has not changed.</summary>
    /// <param name="projectRoot">The project's root.</param>
    /// <param name="output">The folder the assembly is written into.</param>
    /// <returns>What happened, including how many files had to be parsed.</returns>
    public ScriptBuild Compile(string projectRoot, string output) {
        ArgumentException.ThrowIfNullOrEmpty(projectRoot);
        ArgumentException.ThrowIfNullOrEmpty(output);

        var sources = ScriptCompiler.Sources(projectRoot);

        if (sources.Count == 0) {
            // ⚠ Emptied rather than left. Deleting the last script is an ordinary thing to do, and a
            // workspace that kept its trees would rebuild the assembly from them the moment one file
            // came back.
            parsed.Clear();

            return ScriptBuild.None;
        }

        List<SyntaxTree> trees = [];
        List<ScriptDiagnostic> problems = [];
        HashSet<string> present = new(StringComparer.Ordinal);
        var reparsed = 0;

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

            present.Add(file);

            if (parsed.TryGetValue(file, out var held) && string.Equals(held.Text, text, StringComparison.Ordinal)) {
                trees.Add(held.Tree);
                continue;
            }

            var tree = CSharpSyntaxTree.ParseText(text, ScriptCompiler.Options, file, System.Text.Encoding.UTF8);

            parsed[file] = new(text, tree);
            trees.Add(tree);
            reparsed++;
        }

        // ⚠ After the loop and over what was actually found, so a file that vanished stops being in
        // the compilation. A rename is a delete and an add, and without this it would be both types
        // at once — a duplicate-definition error about a file that no longer exists.
        foreach (var gone in parsed.Keys.Where(file => !present.Contains(file)).ToList()) {
            parsed.Remove(gone);
        }

        if (problems.Count > 0) {
            return new(null, problems, sources.Count) { Parsed = reparsed };
        }

        var compilation = CSharpCompilation.Create(
            ScriptCompiler.AssemblyName,
            trees,
            Referenced(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Debug)
        );

        Directory.CreateDirectory(output);

        var assemblyPath = Path.Combine(output, ScriptCompiler.AssemblyName + ".dll");
        var symbolsPath = Path.ChangeExtension(assemblyPath, ".pdb");

        EmitResult result;

        // ⚠ Emitted into memory and written afterwards, so a failed emit cannot leave half a file
        // where the previous good one was. The loader would read it, and "the assembly is corrupt"
        // is a much worse message than the error that actually happened.
        using (var assembly = new MemoryStream())
        using (var symbols = new MemoryStream()) {
            result = compilation.Emit(assembly, symbols, options: new(debugInformationFormat: DebugInformationFormat.PortablePdb));

            problems.AddRange(result.Diagnostics.Where(ScriptCompiler.Reportable).Select(ScriptCompiler.Describe));

            if (result.Success) {
                File.WriteAllBytes(assemblyPath, assembly.ToArray());
                File.WriteAllBytes(symbolsPath, symbols.ToArray());
            }
        }

        return new(result.Success ? assemblyPath : null, problems, sources.Count) { Parsed = reparsed };
    }

    // ⚠ There is no `Clear`, deliberately. One of these belongs to one `EditorScripts`, which belongs
    // to one open project and goes when it closes — so a method for "forget everything" would have
    // been a public surface with no caller, which is this repository's commonest defect wearing a
    // helpful name.

    IReadOnlyList<MetadataReference> Referenced() {
        var loaded = ScriptCompiler.LoadedAssemblies();

        if (references is not null && loaded == referencedAssemblies) {
            return references;
        }

        referencedAssemblies = loaded;

        return references = ScriptCompiler.References();
    }

    readonly record struct Parsed(string Text, SyntaxTree Tree);
}
