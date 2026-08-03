// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core.Yaml;

namespace Vixen.Editor.Assets.Animation;

/// <summary>A project's own kind of clip metadata, authored on the same timeline.</summary>
/// <remarks>
///     <para>
///         <b>The last of doc 34's Part 4 seams, and the one that was named without being built.</b>
///         A clip's <c>extensions</c> block already round-trips anything this build does not
///         understand — that is P0's contract and it holds without any of this. What it cannot do on
///         its own is let a project's kind be <em>checked</em> or <em>shown</em>: an unrecognised
///         block is carried silently, so a typo in it survives the import, ships, and does nothing.
///     </para>
///     <para>
///         ⚠ <b>Registering a kind must never change whether it round-trips.</b> A project that adds
///         an extension and later removes the plugin has to get the same file back, so nothing here
///         may rewrite a block — an extension reads and reports, and the bytes are the author's.
///     </para>
/// </remarks>
public interface IClipMetadataExtension {
    /// <summary>The key in the clip's <c>extensions</c> block this reads.</summary>
    string Kind { get; }

    /// <summary>One line for a panel, saying what the block holds.</summary>
    /// <param name="node">The block.</param>
    /// <returns>The description.</returns>
    string Describe(YamlNode node);

    /// <summary>Says what is wrong with the block, if anything.</summary>
    /// <param name="node">The block.</param>
    /// <param name="problems">Where to put what is wrong.</param>
    /// <returns>Whether the block is usable.</returns>
    bool Validate(YamlNode node, ICollection<string> problems);
}

/// <summary>The metadata kinds a build understands. Everything else is carried and not read.</summary>
/// <remarks>
///     ⚠ <b>Told rather than discovered</b>, for <see cref="BuiltInImporters" />'s reason: a scan
///     would make "which kinds did this build check" a question with a different answer in the editor
///     and in a worker process, and the disagreement shows up as a file that imports clean on one
///     machine and complains on another.
/// </remarks>
public sealed class ClipMetadataExtensions {
    readonly Dictionary<string, IClipMetadataExtension> known = new(StringComparer.Ordinal);

    /// <summary>The set a build ships with.</summary>
    public static ClipMetadataExtensions Default { get; } = new ClipMetadataExtensions().Add(new ClipNotesExtension());

    /// <summary>How many kinds it knows.</summary>
    public int Count => known.Count;

    /// <summary>Registers a kind.</summary>
    /// <param name="extension">The extension.</param>
    /// <returns>This, so registration reads as a list.</returns>
    public ClipMetadataExtensions Add(IClipMetadataExtension extension) {
        ArgumentNullException.ThrowIfNull(extension);

        known[extension.Kind] = extension;
        return this;
    }

    /// <summary>The extension for a kind, or <see langword="null" /> if nothing reads it.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>The extension.</returns>
    public IClipMetadataExtension? For(string kind) => known.GetValueOrDefault(kind);

    /// <summary>Checks every block a clip carries, and says which ones nobody reads.</summary>
    /// <param name="blocks">The clip's extension blocks.</param>
    /// <param name="problems">Where to put what is wrong.</param>
    /// <param name="unread">Where to put the kinds nothing understands.</param>
    /// <returns>Whether everything a kind was registered for is usable.</returns>
    /// <remarks>
    ///     ⚠ <b>An unread kind is reported and is not a problem.</b> Carrying what this build has no
    ///     type for is the whole point of the block; the report exists because "this kind is spelled
    ///     wrong" and "this kind belongs to a plugin that is not loaded" look identical from here, and
    ///     an author who is told which is which can tell them apart.
    /// </remarks>
    public bool Check(
        IReadOnlyDictionary<string, YamlNode> blocks,
        ICollection<string> problems,
        ICollection<string>? unread = null
    ) {
        ArgumentNullException.ThrowIfNull(blocks);
        ArgumentNullException.ThrowIfNull(problems);

        var ok = true;

        foreach (var (kind, node) in blocks) {
            if (For(kind) is not { } extension) {
                unread?.Add(kind);
                continue;
            }

            ok &= extension.Validate(node, problems);
        }

        return ok;
    }
}

/// <summary>The shipped kind: timed notes an animator leaves for whoever picks the clip up.</summary>
/// <remarks>
///     <para>
///         Deliberately something the engine does <em>nothing</em> with, because that is what makes it
///         a fair example of the seam. The runtime never reads it, the pipeline never bakes it, and a
///         build with no editor carries it through untouched — and yet a typo in it is now caught at
///         import rather than discovered by a person who wanted to read it.
///     </para>
///     <para>
///         <code>
///         extensions:
///           notes:
///             - time: 0.4
///               text: the weight is on the back foot here, do not re-time past 1.1
///         </code>
///     </para>
/// </remarks>
public sealed class ClipNotesExtension : IClipMetadataExtension {
    /// <inheritdoc />
    public string Kind => "notes";

    /// <inheritdoc />
    public string Describe(YamlNode node) {
        var count = node is YamlSequence sequence ? sequence.Items.Count : 0;

        return count == 1 ? "1 note" : string.Create(CultureInfo.InvariantCulture, $"{count} notes");
    }

    /// <inheritdoc />
    public bool Validate(YamlNode node, ICollection<string> problems) {
        ArgumentNullException.ThrowIfNull(problems);

        if (node is not YamlSequence sequence) {
            problems.Add("'notes' is not a list. Every note is an entry with a time and some text.");
            return false;
        }

        var ok = true;

        foreach (var item in sequence.Items) {
            if (item is not YamlMapping note) {
                problems.Add("A note is not a mapping. Each one wants a 'time' and a 'text'.");
                ok = false;

                continue;
            }

            if (note["text"] is null) {
                problems.Add("A note has no 'text', so it says nothing to whoever reads it.");
                ok = false;
            }

            if (note["time"] is { } time
                && (time is not YamlScalar scalar
                    || !float.TryParse(scalar.Value, CultureInfo.InvariantCulture, out _))) {
                problems.Add("A note's 'time' is not a number, so nothing could place it on the timeline.");
                ok = false;
            }
        }

        return ok;
    }
}
