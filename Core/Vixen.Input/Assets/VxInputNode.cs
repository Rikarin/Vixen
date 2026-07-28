// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;

namespace Vixen.Input;

/// <summary>One node of a parsed <c>.vxinput</c> document.</summary>
/// <remarks>
///     <para>
///         <b>Why this exists next to <c>Vixen.Core.Yaml</c>.</b> The action asset is read twice —
///         once at run time, and once inside the compiler by
///         <c>Vixen.Input.Generators</c> so that <c>Input.Player.Move</c> is a real member. A source
///         generator runs on <c>netstandard2.1</c> and cannot reference a <c>net10.0</c> assembly,
///         and an analyzer's <c>ProjectReference</c> dependencies do not travel to the analyzer path,
///         so <c>Vixen.Core.Yaml</c> — which is <c>net10.0</c> and carries a NuGet dependency of its
///         own — cannot be the reader on that side.
///     </para>
///     <para>
///         The alternative to this file is <em>two</em> readers that must agree, which is the failure
///         mode where a binding compiles into an accessor the runtime then declines to resolve. So
///         the whole of <c>Assets/</c> is written against nothing but the BCL and is compiled into
///         the generator by source link: one reader, two language surfaces, checked by the build.
///     </para>
///     <para>
///         The dialect is deliberately narrow — block mappings, block sequences and scalars, which is
///         all [doc 08](../../../docs/plan/08-asset-pipeline-and-addressables.md)'s YAML dialect uses
///         for a document of this shape. A <c>.vxinput</c> is therefore ordinary YAML that the asset
///         database's own reader also understands, and the importer's reference scan keeps working
///         over it unchanged.
///     </para>
/// </remarks>
public abstract class VxInputNode {
    private protected VxInputNode(int line) => Line = line;

    /// <summary>The one-based line the node started on, for a diagnostic.</summary>
    public int Line { get; }
}

/// <summary>A single value: a name, a path, a number, a keyword.</summary>
/// <param name="line">The line it was written on.</param>
/// <param name="value">The text, with quoting already removed.</param>
public sealed class VxInputScalar(int line, string value) : VxInputNode(line) {
    /// <summary>The text.</summary>
    public string Value { get; } = value;

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>An ordered list of nodes.</summary>
/// <param name="line">The line it started on.</param>
/// <param name="items">The items.</param>
public sealed class VxInputSequence(int line, IReadOnlyList<VxInputNode> items) : VxInputNode(line) {
    /// <summary>The items, in document order.</summary>
    public IReadOnlyList<VxInputNode> Items { get; } = items;

    /// <inheritdoc />
    public override string ToString() => $"[{Items.Count} items]";
}

/// <summary>A set of key/value pairs, in the order they were written.</summary>
/// <param name="line">The line it started on.</param>
/// <param name="entries">The entries.</param>
/// <remarks>
///     Order is kept because it is the order the emitter writes back, and a document that reorders
///     itself on every save is a diff nobody can review.
/// </remarks>
public sealed class VxInputMapping(int line, IReadOnlyList<KeyValuePair<string, VxInputNode>> entries)
    : VxInputNode(line) {
    /// <summary>The entries, in document order.</summary>
    public IReadOnlyList<KeyValuePair<string, VxInputNode>> Entries { get; } = entries;

    /// <summary>Finds an entry by key, case-insensitively.</summary>
    /// <param name="key">The key.</param>
    /// <param name="value">Its value.</param>
    /// <returns><see langword="false" /> if the mapping has no such key.</returns>
    /// <remarks>
    ///     Case-insensitive because these files are hand-edited and someone who typed
    ///     <c>ControlSchemes</c> meant <c>controlSchemes</c> — the same rule
    ///     <c>Vixen.Core.Yaml</c>'s object mapper applies, for the same reason.
    /// </remarks>
    public bool TryGet(string key, [NotNullWhen(true)] out VxInputNode? value) {
        for (var index = 0; index < Entries.Count; index++) {
            if (string.Equals(Entries[index].Key, key, StringComparison.OrdinalIgnoreCase)) {
                value = Entries[index].Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    /// <inheritdoc />
    public override string ToString() => $"{{{Entries.Count} entries}}";
}
