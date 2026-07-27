// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections;
using System.Diagnostics.CodeAnalysis;

namespace Vixen.Core.Yaml;

/// <summary>How a scalar was written, and how it should be written back.</summary>
public enum YamlScalarStyle {
    /// <summary>Let the emitter decide. What new content gets.</summary>
    Any,

    /// <summary>No quotes: <c>Srgb</c>.</summary>
    Plain,

    /// <summary>Single quotes: <c>'2048'</c>.</summary>
    SingleQuoted,

    /// <summary>Double quotes, with escapes: <c>"a\nb"</c>.</summary>
    DoubleQuoted,

    /// <summary>A literal block, newlines kept: <c>|</c>.</summary>
    Literal,

    /// <summary>A folded block, newlines joined: <c>&gt;</c>.</summary>
    Folded
}

/// <summary>How a sequence or mapping was written, and how it should be written back.</summary>
public enum YamlCollectionStyle {
    /// <summary>Let the emitter decide: flow if it is small and entirely scalar, block otherwise.</summary>
    Any,

    /// <summary>One entry per line, indented.</summary>
    Block,

    /// <summary>On one line, in brackets or braces.</summary>
    Flow
}

/// <summary>A node in a YAML document, and everything needed to write it back unchanged.</summary>
/// <remarks>
///     <para>
///         This is a *lossy-on-purpose-nowhere* model. It carries the tag, the style, and the
///         comments, because [08](../../../docs/plan/08-asset-pipeline-and-addressables.md) requires
///         that reading a <c>.meta</c> file and writing it out again produces the same bytes — that
///         property is what makes migrating a hundred thousand <c>.meta</c> files a diff nobody has
///         to review. A model that dropped the style would rewrite <c>{ u: Repeat, v: Repeat }</c>
///         as two indented lines, and a model that dropped comments would delete what an artist
///         wrote to explain a setting.
///     </para>
///     <para>
///         What it does not carry is anchors and aliases. The dialect has none: an asset reference
///         is a <c>vx:</c> scalar, which is a better answer to the same question, and admitting
///         anchors would mean admitting the cycles that come with them.
///     </para>
/// </remarks>
public abstract class YamlNode {
    /// <summary>The type tag without its <c>!</c>, or <see langword="null" />.</summary>
    /// <remarks>
    ///     <c>!TextureImporter</c> reads as <c>"TextureImporter"</c>. This is the discriminator the
    ///     object mapper resolves through the generated type registry, so a tag is a type name and
    ///     never a URI.
    /// </remarks>
    public string? Tag { get; set; }

    /// <summary>Whole-line comments written above this node, without their <c>#</c>.</summary>
    public List<string> LeadingComments { get; } = [];

    /// <summary>A comment written after this node on the same line, without its <c>#</c>.</summary>
    public string? TrailingComment { get; set; }
}

/// <summary>A single value.</summary>
/// <remarks>
///     The value is always the text as written; interpreting it as a number, a boolean or an enum is
///     the object mapper's job, which is the only layer that knows what type was expected. A YAML
///     reader that guessed here would be the reason <c>version: 1.20</c> becomes <c>1.2</c>.
/// </remarks>
public sealed class YamlScalar : YamlNode {
    /// <summary>The text.</summary>
    public string Value { get; set; }

    /// <summary>How it was written.</summary>
    public YamlScalarStyle Style { get; set; }

    /// <summary>Creates a scalar.</summary>
    /// <param name="value">The text.</param>
    /// <param name="style">How to write it.</param>
    public YamlScalar(string value, YamlScalarStyle style = YamlScalarStyle.Any) {
        ArgumentNullException.ThrowIfNull(value);
        Value = value;
        Style = style;
    }

    /// <summary>Renders the value.</summary>
    /// <returns>The text.</returns>
    public override string ToString() => Value;
}

/// <summary>An ordered list of nodes.</summary>
public sealed class YamlSequence : YamlNode, IEnumerable<YamlNode> {
    /// <summary>The items, in order.</summary>
    public List<YamlNode> Items { get; } = [];

    /// <summary>How it was written.</summary>
    public YamlCollectionStyle Style { get; set; }

    /// <summary>How many items there are.</summary>
    public int Count => Items.Count;

    /// <summary>One item.</summary>
    /// <param name="index">Which one.</param>
    public YamlNode this[int index] => Items[index];

    /// <summary>Adds an item.</summary>
    /// <param name="node">The item.</param>
    /// <returns>This sequence, for chaining.</returns>
    public YamlSequence Add(YamlNode node) {
        ArgumentNullException.ThrowIfNull(node);
        Items.Add(node);
        return this;
    }

    /// <inheritdoc />
    public IEnumerator<YamlNode> GetEnumerator() => Items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>An ordered map from scalar keys to nodes.</summary>
/// <remarks>
///     <para>
///         <b>Ordered, and the order is the schema's.</b> Keys come out in the order the C# record
///         declares them, which is what makes two people editing the same asset produce a diff of
///         the lines they changed rather than of every line.
///     </para>
///     <para>
///         <b>Keys are strings.</b> YAML permits a mapping, a sequence or a tagged node as a key;
///         the dialect permits none of those, and reading one is an error naming the file rather
///         than a shape the rest of the pipeline has to be defensive about for ever.
///     </para>
/// </remarks>
public sealed class YamlMapping : YamlNode, IEnumerable<KeyValuePair<string, YamlNode>> {
    readonly List<KeyValuePair<string, YamlNode>> entries = [];

    /// <summary>The entries, in order.</summary>
    public IReadOnlyList<KeyValuePair<string, YamlNode>> Entries => entries;

    /// <summary>How it was written.</summary>
    public YamlCollectionStyle Style { get; set; }

    /// <summary>How many entries there are.</summary>
    public int Count => entries.Count;

    /// <summary>The keys, in order.</summary>
    public IEnumerable<string> Keys => entries.Select(entry => entry.Key);

    /// <summary>One entry's value.</summary>
    /// <param name="key">Which one.</param>
    /// <returns>Its value, or <see langword="null" /> if there is no such key.</returns>
    public YamlNode? this[string key] => TryGet(key, out var node) ? node : null;

    /// <summary>Adds an entry, or replaces the value of one that is already there.</summary>
    /// <param name="key">The key.</param>
    /// <param name="value">The value.</param>
    /// <returns>This mapping, for chaining.</returns>
    /// <remarks>
    ///     Replacing keeps the key's original position, because moving it would be a diff nobody
    ///     asked for.
    /// </remarks>
    public YamlMapping Set(string key, YamlNode value) {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);

        for (var index = 0; index < entries.Count; index++) {
            if (string.Equals(entries[index].Key, key, StringComparison.Ordinal)) {
                entries[index] = new(key, value);
                return this;
            }
        }

        entries.Add(new(key, value));
        return this;
    }

    /// <summary>Removes an entry.</summary>
    /// <param name="key">The key.</param>
    /// <returns>Whether there was one.</returns>
    public bool Remove(string key) {
        for (var index = 0; index < entries.Count; index++) {
            if (string.Equals(entries[index].Key, key, StringComparison.Ordinal)) {
                entries.RemoveAt(index);
                return true;
            }
        }

        return false;
    }

    /// <summary>Looks up an entry.</summary>
    /// <param name="key">The key.</param>
    /// <param name="value">Its value.</param>
    /// <returns>Whether there was one.</returns>
    public bool TryGet(string key, [NotNullWhen(true)] out YamlNode? value) {
        foreach (var entry in entries) {
            if (string.Equals(entry.Key, key, StringComparison.Ordinal)) {
                value = entry.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    /// <inheritdoc />
    public IEnumerator<KeyValuePair<string, YamlNode>> GetEnumerator() => entries.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
