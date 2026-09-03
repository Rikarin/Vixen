// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Yaml;
using Vixen.Editor.NodeGraph;
using Vixen.Editor.ShaderGraph;

namespace Vixen.Editor.Assets.Shading;

/// <summary>One graph's Raven, or the reason there is none.</summary>
/// <param name="Path">The <c>.vxshadergraph</c> it came from.</param>
/// <param name="Name">
///     The shader declaration's name, which a <c>.vxmat</c> names to draw with it. Empty when the
///     graph did not compile.
/// </param>
/// <param name="Text">The generated source, or empty.</param>
/// <param name="Diagnostics">Everything the graph compiler had to say, formatted for a build log.</param>
public readonly record struct ShaderGraphSourceFile(
    string Path,
    string Name,
    string Text,
    IReadOnlyList<string> Diagnostics
) {
    /// <summary>Whether there is source to compile.</summary>
    public bool Compiled => Text.Length > 0;
}

/// <summary>
///     Every shader graph in a project, as the Raven a shader compilation is given.
/// </summary>
/// <remarks>
///     <para>
///         <b>The step that turns "the graph emits text nobody reads" into a shader a material can
///         name.</b> Both compilations in this repository — the editor's <c>EditorEffects</c> and the
///         build's <c>ShaderBuildRunner</c> — were assembled from <c>*.rvn</c> found on disk under
///         <c>Assets/</c>, so a graph that emitted perfect Raven was invisible to both. This is what
///         they enumerate as well.
///     </para>
///     <para>
///         ⚠ <b>Generated in memory and never written to disk, which is a decision and not a
///         shortcut.</b> A generated <c>.rvn</c> beside its graph would acquire a <c>.meta</c>, an
///         address and a place in the asset browser; it would be committed by somebody, edited by
///         somebody else, and then silently overwritten by the next import. <c>RavenEffectCompiler</c>
///         has taken in-memory sources since the graph previews needed them, and its own remarks say
///         why: a shader that was never a file should not become one.
///     </para>
///     <para>
///         ⚠ <b>A graph that does not compile contributes nothing and is not an exception.</b> One
///         broken graph must not fail every material in the project, which is what adding
///         unparseable text to a shared compilation does — <c>RavenEffectCompiler</c>'s constructor
///         throws on a source that will not parse, and the editor's refusal message is then about
///         the whole library. The diagnostics come back on the record instead, for the caller to
///         report against the file that caused them.
///     </para>
///     <para>
///         <b>Only surface graphs.</b> A standalone graph is a whole shader with its own stages and
///         its own <c>worldViewProjection</c>; putting one into the same compilation as the library
///         is harmless but pointless, since nothing can name it as a material. It is skipped rather
///         than reported, because a standalone graph is a legitimate thing to have — it is what a
///         preview thumbnail is, and what an author hands to <c>raven compile</c>.
///     </para>
/// </remarks>
public static class ShaderGraphSources {
    /// <summary>What a shader graph is written as.</summary>
    public const string Extension = ".vxshadergraph";

    /// <summary>Compiles every graph under a directory, in a stable order.</summary>
    /// <param name="assets">The project's <c>Assets/</c>, or any directory to search.</param>
    /// <param name="registry">
    ///     The node library the graphs are compiled against. Null takes the built-in one, which is
    ///     what a project with no node plugins has.
    /// </param>
    /// <returns>One record per graph, whether or not it compiled.</returns>
    /// <exception cref="ArgumentException"><paramref name="assets" /> is empty.</exception>
    /// <remarks>
    ///     Ordered for the reason <c>ShaderBuildRunner.Sources</c> orders its files: the source hash
    ///     every artefact carries is taken over the texts in the order they were read, so an
    ///     enumeration that depended on the file system would make a cache entry stale on a machine
    ///     that sorted differently.
    /// </remarks>
    public static IReadOnlyList<ShaderGraphSourceFile> All(string assets, NodeTypeRegistry? registry = null) {
        ArgumentException.ThrowIfNullOrEmpty(assets);

        if (!Directory.Exists(assets)) {
            return [];
        }

        registry ??= Library();

        List<ShaderGraphSourceFile> compiled = [];

        foreach (var path in Directory.EnumerateFiles(assets, "*" + Extension, SearchOption.AllDirectories)
                     .Order(StringComparer.Ordinal)) {
            compiled.Add(Of(path, registry));
        }

        return compiled;
    }

    /// <summary>Compiles one graph.</summary>
    /// <param name="path">The <c>.vxshadergraph</c>.</param>
    /// <param name="registry">The node library, or null for the built-in one.</param>
    /// <returns>The record.</returns>
    /// <exception cref="ArgumentException"><paramref name="path" /> is empty.</exception>
    public static ShaderGraphSourceFile Of(string path, NodeTypeRegistry? registry = null) {
        ArgumentException.ThrowIfNullOrEmpty(path);

        registry ??= Library();

        string text;

        try {
            text = File.ReadAllText(path);
        } catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) {
            return new(path, string.Empty, string.Empty, [failure.Message]);
        }

        return From(path, text, registry);
    }

    /// <summary>Compiles a graph whose text is already in hand.</summary>
    /// <param name="path">What to call it — the name a diagnostic points at.</param>
    /// <param name="text">The graph, as YAML.</param>
    /// <param name="registry">The node library, or null for the built-in one.</param>
    /// <returns>The record.</returns>
    /// <exception cref="ArgumentException"><paramref name="path" /> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="text" /> is null.</exception>
    /// <remarks>
    ///     ⚠ <b>What an importer has to use, and the reason this overload exists.</b> An
    ///     <c>ImportContext</c>'s <c>SourcePath</c> is a <em>virtual</em> path —
    ///     <c>/Assets/Thing.vxshadergraph</c> — because an import runs over a project's VFS and not
    ///     over the file system. Handing it to <see cref="Of(string, NodeTypeRegistry)" /> is a
    ///     <c>DirectoryNotFoundException</c> reported against the asset, which reads as a missing
    ///     file and is really a path from the wrong namespace.
    /// </remarks>
    public static ShaderGraphSourceFile From(string path, string text, NodeTypeRegistry? registry = null) {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(text);

        registry ??= Library();

        if (text.Trim().Length == 0) {
            // What the editor's "create shader graph" leaves behind before anybody opens it. Not a
            // diagnostic: an empty file is a graph nobody has started, and a build that complained
            // about one would complain every time somebody made a file and went to lunch.
            return new(path, string.Empty, string.Empty, []);
        }

        NodeGraphModel graph;

        try {
            var stored = YamlSerializer.Parse<NodeGraphAsset>(text);

            graph = NodeGraphDocument.Load(stored, out var repairs);

            if (repairs.Count > 0) {
                return Result(
                    path,
                    registry,
                    graph,
                    [.. repairs.Select(repair => $"{repair.Id}: {repair.Message}")]
                );
            }
        } catch (Exception failure) when (failure is YamlParseException or YamlBindingException
                                              or FormatException or NotSupportedException) {
            return new(path, string.Empty, string.Empty, [$"It is not a readable shader graph: {failure.Message}"]);
        }

        return Result(path, registry, graph, []);
    }

    /// <summary>Compiles a loaded graph and packages what came out.</summary>
    static ShaderGraphSourceFile Result(
        string path,
        NodeTypeRegistry registry,
        NodeGraphModel graph,
        List<string> notes
    ) {
        var result = new ShaderGraphCompiler(registry) {
            DefaultName = System.IO.Path.GetFileNameWithoutExtension(path)
        }.Compile(graph);

        notes.AddRange(result.Diagnostics.Select(diagnostic => $"{diagnostic.Id}: {diagnostic.Message}"));

        if (!result.Succeeded) {
            return new(path, string.Empty, string.Empty, notes);
        }

        // A standalone graph is skipped rather than refused; see the type's remarks.
        return result.Value.Kind == ShaderGraphKind.Surface
            ? new(path, result.Value.Name, result.Value.Source, notes)
            : new(path, result.Value.Name, string.Empty, notes);
    }

    /// <summary>The built-in node library.</summary>
    static NodeTypeRegistry Library() {
        var registry = new NodeTypeRegistry();

        NodeTypes.Register(registry);

        return registry;
    }
}
