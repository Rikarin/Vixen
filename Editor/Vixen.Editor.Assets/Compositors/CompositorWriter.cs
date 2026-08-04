// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core.Yaml;
using Vixen.Rendering.Compositor;

namespace Vixen.Editor.Assets.Compositors;

/// <summary>Writes a <see cref="GraphicsCompositorAsset" /> as <c>.vxcompositor</c> text.</summary>
/// <remarks>
///     <para>
///         <see cref="CompositorImporter" />'s other direction, and it lives beside the importer for
///         the same reason the importer lives here: <c>Vixen.Rendering</c> deliberately does not
///         reference <c>Vixen.Core.Yaml</c> — the asset model is a plain object model, and YAML is
///         one way of making one — so both ends of the text format belong to the tooling assembly
///         that owns the dialect. The explode paths of docs/plan/39 § 5 are the callers: the
///         <c>vixen frame explode</c> command today, the editor's explode button when doc 36's
///         panel grows one.
///     </para>
///     <para>
///         <b>What it writes is what a person would have written.</b> Every member equal to its
///         record's default is left out — an exploded file should read like sample 13, not like an
///         object dump — and what remains is the schema's order under the schema's camelCase keys,
///         with <c>!TypeName</c> tags exactly where the reader needs them: on every node whose type
///         its member's declared type does not already say. <c>version:</c> alone is written even
///         at its default, first, because a document's schema version is a statement, not a
///         setting.
///     </para>
///     <para>
///         The round trip is the contract: <see cref="YamlSerializer.Parse{T}" /> over this text
///         binds back to a structurally identical asset, which is what makes explode an ejection
///         rather than a translation — the document that comes out builds the same frame the object
///         that went in would have.
///     </para>
/// </remarks>
public static class CompositorWriter {
    // The reader's own registration, for the same reason the importer makes it: Color3 and the
    // vectors write as plain scalars, and the generator describes no such shape on its own.
    static CompositorWriter() => MathScalars.Register();

    static readonly YamlSerializerOptions Options = new(OmitDefaults: true);

    /// <summary>How wide a comment line may run before it wraps.</summary>
    /// <remarks>
    ///     Eighty-eight plus the deepest indent a comment lands at — a render pass's children — is
    ///     the repository's own line width. A note arrives as a sentence or two, and writing it as
    ///     one 200-column line would make the exploded file unreadable in exactly the way the
    ///     comments exist to prevent.
    /// </remarks>
    const int CommentWidth = 88;

    /// <summary>Writes a document.</summary>
    /// <param name="document">The asset to write.</param>
    /// <param name="comments">
    ///     A comment per asset instance — what <c>PostEffectFactory</c>'s notes overload produces —
    ///     or <see langword="null" /> for a bare document. Matched by
    ///     reference against the document's own resources, stages, buffers and nodes; an instance
    ///     the document does not contain is simply never asked for.
    /// </param>
    /// <param name="header">
    ///     Comment lines for the top of the file, above everything, or <see langword="null" /> for
    ///     none — where the explode command states what the file is and that nothing regenerates it.
    /// </param>
    /// <returns>The YAML, ending in a newline.</returns>
    public static string Write(
        GraphicsCompositorAsset document,
        IReadOnlyDictionary<object, string>? comments = null,
        string? header = null
    ) {
        ArgumentNullException.ThrowIfNull(document);

        var root = WithVersionFirst((YamlMapping)YamlSerializer.Serialize(document, Options), document.Version);

        if (header is { Length: > 0 }) {
            foreach (var line in header.Split('\n')) {
                root.LeadingComments.AddRange(Wrap(line));
            }
        }

        if (comments is { Count: > 0 }) {
            Decorate(root, document, comments);
        }

        return YamlWriter.Write(root);
    }

    /// <summary>
    ///     Puts <c>version:</c> first and unconditionally. Omit-defaults would drop it — the current
    ///     schema version is the default — but a document's version is the one member that must be
    ///     written precisely when it says nothing surprising, because the file outlives the build
    ///     that wrote it.
    /// </summary>
    static YamlMapping WithVersionFirst(YamlMapping emitted, int version) {
        var ordered = new YamlMapping();

        ordered.Set("version", new YamlScalar(version.ToString(CultureInfo.InvariantCulture), YamlScalarStyle.Plain));

        foreach (var (key, value) in emitted.Entries) {
            if (!string.Equals(key, "version", StringComparison.Ordinal)) {
                ordered.Set(key, value);
            }
        }

        return ordered;
    }

    /// <summary>
    ///     Walks the emitted tree beside the asset tree and puts each comment above the YAML it
    ///     explains.
    /// </summary>
    /// <remarks>
    ///     A parallel walk rather than a comment-aware emitter, because the serializer emits members
    ///     in declaration order and collections in element order — so the two trees correspond by
    ///     construction, and the only knowledge this needs is which node kinds have children, which
    ///     is knowledge the expansion's <c>Find</c> already relies on.
    /// </remarks>
    static void Decorate(YamlMapping root, GraphicsCompositorAsset document, IReadOnlyDictionary<object, string> comments) {
        DecorateItems(root["stages"], document.Stages, comments);
        DecorateItems(root["resources"], document.Resources, comments);
        DecorateItems(root["buffers"], document.Buffers, comments);

        if (document.Game is { } game && root["game"] is { } node) {
            DecorateNode(node, game, comments, Before(root, "game"));
        }
    }

    /// <summary>The value written on the lines above a key — what a comment on it would follow.</summary>
    static YamlNode? Before(YamlMapping mapping, string key) {
        YamlNode? previous = null;

        foreach (var (name, value) in mapping.Entries) {
            if (string.Equals(name, key, StringComparison.Ordinal)) {
                return previous;
            }

            previous = value;
        }

        return null;
    }

    static void DecorateItems<TDeclared>(
        YamlNode? node,
        TDeclared[] items,
        IReadOnlyDictionary<object, string> comments
    ) where TDeclared : class {
        if (node is not YamlSequence sequence || sequence.Count != items.Length) {
            return;
        }

        for (var index = 0; index < items.Length; index++) {
            Attach(sequence[index], items[index], comments, index > 0 ? sequence[index - 1] : null);
        }
    }

    static void DecorateNode(
        YamlNode node,
        ISceneRendererAsset asset,
        IReadOnlyDictionary<object, string> comments,
        YamlNode? predecessor
    ) {
        Attach(node, asset, comments, predecessor);

        var children = asset switch {
            SequenceAsset sequence => sequence.Children,
            RenderPassAsset pass => pass.Children,
            _ => []
        };

        if (children.Length > 0
            && node is YamlMapping mapping
            && mapping["children"] is YamlSequence emitted
            && emitted.Count == children.Length) {
            for (var index = 0; index < children.Length; index++) {
                DecorateNode(emitted[index], children[index], comments, index > 0 ? emitted[index - 1] : null);
            }
        }
    }

    static void Attach(YamlNode node, object asset, IReadOnlyDictionary<object, string> comments, YamlNode? predecessor) {
        if (node.LeadingComments.Count != 0 || !comments.TryGetValue(asset, out var comment)) {
            return;
        }

        if (predecessor is not null) {
            EndInScalar(predecessor);
        }

        foreach (var line in comment.Split('\n')) {
            node.LeadingComments.AddRange(Wrap(line));
        }
    }

    /// <summary>
    ///     Forces whatever renders on the line above a comment to end in a scalar.
    /// </summary>
    /// <remarks>
    ///     ⚠ Because the reader cannot take the alternative: YamlDotNet's parser — the token layer
    ///     under <see cref="YamlReader" /> — refuses a comment that directly follows a flow
    ///     collection's closing bracket where a block-mapping key is expected, so
    ///     <c>passes: [A, B]</c> with the next node's comment under it is a file the engine writes
    ///     and then cannot read. Writing the trailing chain in block style costs a few lines
    ///     exactly where a comment follows and nothing anywhere else, and it is the difference
    ///     between an exploded file and an exploded file that re-imports.
    /// </remarks>
    static void EndInScalar(YamlNode node) {
        switch (node) {
            case YamlMapping { Count: > 0 } mapping:
                mapping.Style = YamlCollectionStyle.Block;
                EndInScalar(mapping.Entries[^1].Value);
                break;

            case YamlSequence { Count: > 0 } sequence:
                sequence.Style = YamlCollectionStyle.Block;
                EndInScalar(sequence.Items[^1]);
                break;
        }
    }

    static List<string> Wrap(string text) {
        var lines = new List<string>();
        var line = new System.Text.StringBuilder();

        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries)) {
            if (line.Length > 0 && line.Length + 1 + word.Length > CommentWidth) {
                lines.Add(line.ToString());
                line.Clear();
            }

            if (line.Length > 0) {
                line.Append(' ');
            }

            line.Append(word);
        }

        if (line.Length > 0 || lines.Count == 0) {
            lines.Add(line.ToString());
        }

        return lines;
    }
}
