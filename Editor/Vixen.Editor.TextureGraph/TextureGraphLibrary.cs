// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using Vixen.Editor.NodeGraph;

namespace Vixen.Editor.TextureGraph;

/// <summary>Where a sub-graph's exposed parameters are found, beside the sub-graph itself.</summary>
/// <remarks>
///     ⚠ <b>Two questions and therefore two interfaces, because the framework only asks one.</b>
///     <see cref="ISubGraphSource" /> answers "what graph does this node type stand for", which is
///     all <see cref="SubGraphs.Flatten" /> needs. A texture graph asks a second one — "and what
///     knobs does it declare" — because the nodes inside it may have been written with expressions
///     over them, and after inlining those expressions are in a graph whose compiler has never heard
///     of them.
/// </remarks>
interface ITextureGraphLibrary : ISubGraphSource {
    /// <summary>The exposed parameters of one published graph.</summary>
    /// <param name="type">Its node-type path.</param>
    /// <returns>Its parameters, or an empty list for a path this library has not got.</returns>
    IReadOnlyList<TextureGraphParameter> ParametersOf(string type);
}

/// <summary>
///     The published graphs a texture graph may contain as nodes: doc 48 § D9's sub-graphs, with
///     their knobs.
/// </summary>
/// <remarks>
///     <para>
///         <b>A wrapper over <see cref="SubGraphLibrary" /> rather than a second one</b>, because the
///         inlining, the recursion refusal and the depth limit are the framework's and there is no
///         version of them that is a texture graph's. What is added is the half doc 48 asks for and
///         the framework has no room for: the parameter list a published graph declares, which is
///         what <see cref="TextureGraphParameters.Definition" /> turns into the node's settings and
///         what <c>TextureGraphCompiler</c> binds an inlined node's expressions against.
///     </para>
///     <para>
///         ⚠ <b>What an author types into those settings does not reach the inlined nodes.</b>
///         <see cref="SubGraphs.Flatten" /> replaces the sub-graph node with the graph's contents and
///         the node — which is where the overrides are stored — is then gone, so an expression inside
///         a published graph folds against that graph's <em>declared defaults</em>. The knob is real,
///         saved and shown, and turning it changes nothing until
///         <a href="https://github.com/Rikarin/Vixen/issues/742">#742</a>.
///     </para>
///     <para>
///         ⚠ <b>The node type registered here is not <see cref="SubGraphs.Definition" />'s.</b>
///         <see cref="SubGraphLibrary.Add" />'s optional registry argument registers a definition
///         with the graph's interface as ports and <em>no settings</em>, which for a texture graph is
///         a node whose every knob is missing. So the graph is added without a registry and the
///         definition is registered here.
///     </para>
/// </remarks>
sealed class TextureGraphLibrary : ITextureGraphLibrary {
    readonly SubGraphLibrary graphs = new();
    readonly Dictionary<string, IReadOnlyList<TextureGraphParameter>> parameters = new(StringComparer.Ordinal);

    /// <summary>Every path published, in no particular order.</summary>
    public IEnumerable<string> Paths => graphs.Paths;

    /// <summary>Publishes a graph as a node type, with its exposed parameters as its settings.</summary>
    /// <param name="path">The menu path, and the key a containing graph stores.</param>
    /// <param name="graph">The graph.</param>
    /// <param name="exposed">Its exposed parameters, or empty for a graph with no knobs.</param>
    /// <param name="registry">The registry to add the node type to, when there is one.</param>
    /// <exception cref="ArgumentNullException"><paramref name="graph" /> or the parameter list is null.</exception>
    /// <exception cref="ArgumentException">
    ///     <paramref name="path" /> is empty or already published, or the parameter list does not
    ///     hold together.
    /// </exception>
    /// <remarks>
    ///     ⚠ <b>The parameters are checked here and refused as an argument, rather than reported when
    ///     something compiles against them.</b> A published graph is a library entry many graphs
    ///     will contain; a duplicate name or a default outside its own range in one of those is a
    ///     fault of the thing being published, and reporting it once per containing graph would name
    ///     the wrong author every time.
    /// </remarks>
    public void Publish(
        string path,
        NodeGraphModel graph,
        IReadOnlyList<TextureGraphParameter> exposed,
        NodeTypeRegistry? registry = null
    ) {
        ArgumentNullException.ThrowIfNull(exposed);

        var problems = TextureGraphParameters.Check(exposed);

        if (problems.Length > 0) {
            throw new ArgumentException(
                $"'{path}' cannot be published: {string.Join(" ", problems)}",
                nameof(exposed)
            );
        }

        graphs.Add(path, graph);
        parameters[path] = [.. exposed];
        registry?.Add(TextureGraphParameters.Definition(graph, exposed, path));
    }

    /// <inheritdoc />
    public IReadOnlyList<TextureGraphParameter> ParametersOf(string type) =>
        parameters.TryGetValue(type, out var exposed) ? exposed : [];

    /// <inheritdoc />
    public bool TryGet(string type, [NotNullWhen(true)] out NodeGraphModel? graph) => graphs.TryGet(type, out graph);
}
