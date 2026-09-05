// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Core.Yaml;
using Vixen.Editor.NodeGraph;

namespace Vixen.Editor.TextureGraph;

/// <summary>What reading one compound out of a library folder had to say.</summary>
/// <param name="Path">Its node-type path — the folders under the library root, then the file's stem.</param>
/// <param name="Source">Where the file is: an embedded resource name, or an absolute path.</param>
/// <param name="Problem">Why it was not published, or an empty string when it was.</param>
public readonly record struct TextureCompoundProblem(string Path, string Source, string Problem);

/// <summary>
///     The shipped compounds, and a project's own, as node types a graph may contain.
/// </summary>
/// <remarks>
///     <para>
///         <b>Doc 48 § D5's claim made true: the several hundred nodes are content.</b> A
///         <c>Histogram Scan</c> is a <c>Levels</c> with its numbers driven by two knobs; a
///         <c>Dirt</c> is a curvature multiplied by an occlusion. Neither is code, and the whole
///         argument for forty-one kernels rather than four hundred is that the rest of the catalogue
///         is <c>.vxtexgraph</c> files somebody authored in the tool. This is the mechanism that
///         turns a folder of them into a menu.
///     </para>
///     <para>
///         <b>Two roots, one path space, and shipped wins.</b> The shipped compounds are embedded in
///         this assembly — the arrangement <c>Shaders/*.rvn</c> already uses, and for the same reason:
///         there is nothing anybody can leave behind on a deployment. A project's own live in a
///         folder it names, and they are published into the same menu beside the shipped ones, which
///         is doc 48 § A.10's "a folder of <c>.vxtexgraph</c>" and § D14's "a third-party plugin
///         surface".
///     </para>
///     <para>
///         ⚠ <b>A project compound whose path collides with a shipped one is refused rather than
///         allowed to shadow it.</b> Overriding is what a library grows into wanting, and it is also
///         how a graph that worked yesterday quietly starts computing something else — an author's
///         half-finished copy of <c>Generators/Dirt</c>, saved under its own name, silently
///         rebinding every material that reads it. So the collision is a
///         <see cref="TextureCompoundProblem" /> naming both files, and a deliberate override with a
///         visible marker is a decision somebody makes on purpose rather than a behaviour that
///         arrives by accident.
///     </para>
///     <para>
///         ⚠ <b>Nothing in this tree calls <see cref="Publish" /> outside its own tests —
///         <a href="https://github.com/Rikarin/Vixen/issues/799">#799</a>.</b>
///         <c>TextureNodeLibrary.Create</c> in <c>Vixen.Editor.Texturing</c> registers the generated
///         <c>NodeTypes</c> and nothing else, so the shipped compounds are in the assembly, loadable,
///         compilable and <em>not in the panel's search</em> — and a <c>TextureGraphDocument</c> has
///         no <c>SubGraphSource</c> to give a compiler either. That is one call in a file this slice
///         does not own; it is written down here rather than left to be rediscovered.
///     </para>
/// </remarks>
public static class TextureCompoundLibrary {
    /// <summary>What a compound is written as.</summary>
    public const string Extension = ".vxtexgraph";

    /// <summary>The folder inside this assembly the shipped compounds are embedded from.</summary>
    const string Root = "Vixen.Editor.TextureGraph.Compounds.";

    /// <summary>The names of the compounds this assembly ships, as node-type paths.</summary>
    /// <remarks>
    ///     ⚠ <b>Derived from the manifest rather than listed.</b> A compound exists because a file
    ///     ships, exactly as a kernel does — so a list would be a second opinion about the folder,
    ///     and the roll call that counts them would be counting the list.
    /// </remarks>
    public static ImmutableArray<string> Shipped { get; } = [
        .. typeof(TextureCompoundLibrary).Assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(Root, StringComparison.Ordinal)
                && name.EndsWith(Extension, StringComparison.Ordinal))
            .Select(PathOfResource)
            .Order(StringComparer.Ordinal)
    ];

    /// <summary>The text of one shipped compound.</summary>
    /// <param name="path">Its node-type path, as <see cref="Shipped" /> holds it.</param>
    /// <returns>The file, or null when this assembly ships no such compound.</returns>
    /// <remarks>
    ///     What a tool that wants to <em>show</em> a shipped compound reads — and what the roll call
    ///     over the folder's contents walks, because a published node type has already lost the
    ///     difference between a port a file named and a port the file's node type actually has.
    /// </remarks>
    public static string? Source(string path) {
        ArgumentException.ThrowIfNullOrEmpty(path);

        var assembly = typeof(TextureCompoundLibrary).Assembly;

        using var stream = assembly.GetManifestResourceStream(
            Root + path.Replace('/', '.') + Extension
        );

        if (stream is null) {
            return null;
        }

        using StreamReader reader = new(stream);

        return reader.ReadToEnd();
    }

    /// <summary>Publishes every compound, shipped and then the project's, as node types.</summary>
    /// <param name="registry">The registry the node types are added to.</param>
    /// <param name="folder">A project's own compound folder, or null for the shipped ones alone.</param>
    /// <param name="problems">Every file that could not be published, and why.</param>
    /// <returns>The library, to be handed to a compiler as its <c>SubGraphSource</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="registry" /> is null.</exception>
    /// <remarks>
    ///     ⚠ <b>A file that will not read is reported and skipped, not thrown.</b> One unreadable
    ///     compound in a project folder must not cost an author every other node in the menu — which
    ///     is <c>MeshMapLibrary.Sidecar</c>'s decision, in another assembly, for the same reason.
    /// </remarks>
    public static ISubGraphSource Publish(
        NodeTypeRegistry registry,
        string? folder,
        out ImmutableArray<TextureCompoundProblem> problems
    ) {
        ArgumentNullException.ThrowIfNull(registry);

        TextureGraphLibrary library = new();
        var found = ImmutableArray.CreateBuilder<TextureCompoundProblem>();
        var assembly = typeof(TextureCompoundLibrary).Assembly;

        foreach (var resource in assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(Root, StringComparison.Ordinal)
                && name.EndsWith(Extension, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)) {
            using var stream = assembly.GetManifestResourceStream(resource);

            if (stream is null) {
                continue;
            }

            using StreamReader reader = new(stream);

            Add(library, registry, found, PathOfResource(resource), resource, reader.ReadToEnd());
        }

        if (folder is { Length: > 0 } && Directory.Exists(folder)) {
            foreach (var file in Directory
                .EnumerateFiles(folder, "*" + Extension, SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal)) {
                string text;

                try {
                    text = File.ReadAllText(file);
                } catch (IOException failure) {
                    found.Add(new(PathOfFile(folder, file), file, failure.Message));

                    continue;
                }

                Add(library, registry, found, PathOfFile(folder, file), file, text);
            }
        }

        problems = found.ToImmutable();

        return library;
    }

    /// <summary>Reads one compound and publishes it, or records why it could not be.</summary>
    static void Add(
        TextureGraphLibrary library,
        NodeTypeRegistry registry,
        ImmutableArray<TextureCompoundProblem>.Builder problems,
        string path,
        string source,
        string text
    ) {
        NodeGraphAsset stored;

        try {
            stored = YamlSerializer.Parse<NodeGraphAsset>(text);
        } catch (Exception failure)
            when (failure is YamlParseException or YamlBindingException or NotSupportedException) {
            problems.Add(new(path, source, failure.Message));

            return;
        }

        var graph = NodeGraphDocument.Load(stored, out var diagnostics);

        // ⚠ A repair is not a failure, and the difference matters here more than it does in a
        // document an author has open. `Load` drops an edge to a port that no longer exists and says
        // so; for a file somebody is editing that is a note in the panel, and for a *published* node
        // type it is a compound that will quietly compute something else in every graph that
        // contains it. So it is reported — and still published, because refusing would take the rest
        // of the library with it whenever a node gains a port.
        foreach (var diagnostic in diagnostics) {
            problems.Add(new(path, source, diagnostic.Message));
        }

        try {
            library.Publish(path, graph, TextureGraphParameters.Declared(graph.Parameters), registry);
        } catch (ArgumentException failure) {
            problems.Add(new(path, source, failure.Message));
        }
    }

    /// <summary>The node-type path an embedded compound is published under.</summary>
    /// <remarks>
    ///     ⚠ <b>The dots of a manifest resource name are the folder separators, and there is no way
    ///     to tell them from a dot somebody put in a file name.</b> So a shipped compound's file name
    ///     may not contain one — <c>TextureCompoundLibraryTests</c> holds that, because the failure
    ///     is a node published under a path with a phantom folder in it rather than an error.
    /// </remarks>
    static string PathOfResource(string resource) =>
        resource[Root.Length..^Extension.Length].Replace('.', '/');

    /// <summary>The node-type path a project's compound is published under.</summary>
    static string PathOfFile(string folder, string file) =>
        Path.ChangeExtension(Path.GetRelativePath(folder, file), null).Replace(Path.DirectorySeparatorChar, '/');
}
