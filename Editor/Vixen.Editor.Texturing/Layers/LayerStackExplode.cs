// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Yaml;
using Vixen.Editor.NodeGraph;

namespace Vixen.Editor.Texturing.Layers;

/// <summary>A layer stack, turned into the graph it stood for — one-way.</summary>
/// <remarks>
///     <para>
///         <b>Doc 48 § D1, and the same gesture doc 39's frame and doc 40's panel already have.</b>
///         The artist who outgrows the stack gets the <em>real</em> graph rather than a simplified
///         picture of it: every node here is a node they could have placed, wired the way the stack
///         composited, with a comment saying which layer each composite came out of.
///     </para>
///     <para>
///         ⚠ <b>One-way, and the file says so at the top.</b> There is no reader that turns a
///         <c>.vxtexgraph</c> back into a <c>.vxlayers</c> and there will not be one: a graph is
///         strictly more expressive, so the inverse is either a refusal for most graphs or a lossy
///         guess. <see cref="Explode" /> leaves the <c>.vxlayers</c> alone and writes a new file, so
///         the recovery is opening the stack again rather than an undo.
///     </para>
///     <para>
///         ⚠ <b>The decoration is comments and a name, and nothing else — deliberately.</b> Doc 48
///         exit criterion 6 asks for a stack and its explosion to bake byte-identical outputs, and
///         the only reason that is provable is that the explosion adds nothing a compiler reads. A
///         decoration that inserted so much as a pass-through node would move every op index and
///         emit an op of its own — and an op's seed is mixed from <c>TextureOp.Identity</c>, which
///         is derived from the node that emitted it, so the inserted node's own dispatches would
///         draw numbers the stack's compilation never drew. ⚠ Until
///         <a href="https://github.com/Rikarin/Vixen/issues/875">#875</a> the seed was mixed from
///         the op's <em>index</em>, so the damage was worse and broader: a noise three layers
///         further up drew a different picture. Either way it is the drift criterion 6 exists to
///         catch.
///     </para>
/// </remarks>
static class LayerStackExplode {
    /// <summary>What the exploded graph is called, so a reader knows not to expect knobs.</summary>
    /// <remarks>
    ///     Deliberately close to <c>StandardFrameDocument.ExplodedHeader</c>'s words: two spellings
    ///     of "this is one-way" would be two claims a reader has to reconcile.
    /// </remarks>
    public const string ExplodedHeader =
        "Exploded from a .vxlayers layer stack — one-way, deliberately. The layers are gone, every "
        + "node below is yours to edit, and nothing regenerates this file.";

    /// <summary>What an exploded graph is written as.</summary>
    public const string Extension = ".vxtexgraph";

    /// <summary>The graph one texture set's stack stood for, with its comments.</summary>
    /// <param name="stack">The document.</param>
    /// <param name="set">Which of its sets.</param>
    /// <returns>The graph and what building it had to say.</returns>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    /// <remarks>
    ///     ⚠ <b>It builds its own graph rather than being handed the compiled one.</b> Sharing the
    ///     object would make criterion 6's differential compare a thing with itself: two builds of
    ///     one stack have to agree before the round trip is worth measuring, and a builder that
    ///     enumerated a dictionary somewhere would fail here first.
    /// </remarks>
    public static LayerStackBuild Explode(LayerStackAsset stack, TextureSetAsset set) {
        ArgumentNullException.ThrowIfNull(stack);
        ArgumentNullException.ThrowIfNull(set);

        var build = LayerStackGraph.Build(stack, set);

        Decorate(build);

        return build;
    }

    /// <summary>Puts the header and one comment per note onto a built graph.</summary>
    /// <param name="build">What to decorate.</param>
    /// <exception cref="ArgumentNullException"><paramref name="build" /> is null.</exception>
    public static void Decorate(LayerStackBuild build) {
        ArgumentNullException.ThrowIfNull(build);

        var graph = build.Graph;

        graph.Comments.Add(new() { Text = ExplodedHeader, Position = new(0f, -140f), Size = new(760f, 96f) });

        foreach (var note in build.Notes) {
            if (!graph.TryGet(note.Node, out var node)) {
                continue;
            }

            graph.Comments.Add(new() {
                Text = note.Text,
                Position = new(node.Position.X, node.Position.Y - 84f),
                Size = new(300f, 72f)
            });
        }
    }

    /// <summary>The exploded graph as a <c>.vxtexgraph</c> would hold it.</summary>
    /// <param name="build">The decorated graph.</param>
    /// <returns>The YAML, LF-terminated.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="build" /> is null.</exception>
    public static string ToYaml(LayerStackBuild build) {
        ArgumentNullException.ThrowIfNull(build);

        var text = YamlSerializer.ToYaml(NodeGraphDocument.Save(build.Graph))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        return text.Length > 0 && !text.EndsWith('\n') ? text + "\n" : text;
    }

    /// <summary>Reads an exploded graph back, as a compiler takes it.</summary>
    /// <param name="yaml">What <see cref="ToYaml" /> wrote.</param>
    /// <param name="diagnostics">What reading it had to say.</param>
    /// <returns>The graph.</returns>
    /// <remarks>
    ///     ⚠ <b>The half of the round trip criterion 6 actually measures.</b> A setting the writer
    ///     drops, a value the reader cannot bind or a wire whose port was renamed all show up here
    ///     and nowhere earlier — the in-memory graph is correct by construction and the file is not.
    /// </remarks>
    public static NodeGraphModel Read(string yaml, out IReadOnlyList<NodeDiagnostic> diagnostics) =>
        NodeGraphDocument.Load(YamlSerializer.Parse<NodeGraphAsset>(yaml), out diagnostics);

    /// <summary>Writes the exploded graph beside the stack, without touching the stack.</summary>
    /// <param name="stack">The document.</param>
    /// <param name="set">Which of its sets.</param>
    /// <param name="path">Where to write the <c>.vxtexgraph</c>, absolute.</param>
    /// <returns>The build, so a caller can report what it had to say.</returns>
    /// <exception cref="ArgumentNullException">The stack or the set is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="path" /> is empty.</exception>
    /// <remarks>
    ///     ⚠ <b>Through a temporary and then moved</b>, for <c>TextureGraphDocument.SaveCore</c>'s
    ///     reason: a write interrupted halfway must not leave a truncated graph where the work was.
    /// </remarks>
    public static LayerStackBuild Write(LayerStackAsset stack, TextureSetAsset set, string path) {
        ArgumentException.ThrowIfNullOrEmpty(path);

        var build = Explode(stack, set);
        var temporary = path + ".tmp";

        File.WriteAllText(temporary, ToYaml(build));
        File.Move(temporary, path, overwrite: true);

        return build;
    }
}
