// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Ai;

/// <summary>Which key of a layout, as the two bytes a compiled node actually stores.</summary>
/// <param name="Index">Its position in the layout.</param>
/// <remarks>
///     A node references a key by index, never by name: the name is a <see cref="Symbol" /> that
///     survives for the inspector, the diagnostics and the file format, and nothing in a frame reads
///     it. That is also why a rename in the editor rewrites every reference in the open document —
///     the compiled form has no name to rename.
/// </remarks>
public readonly record struct BlackboardKey(ushort Index) {
    /// <summary>The key that names nothing.</summary>
    public static BlackboardKey Invalid => new(ushort.MaxValue);

    /// <summary>Whether this names a key.</summary>
    public bool IsValid => Index != ushort.MaxValue;

    /// <inheritdoc />
    public override string ToString() => IsValid ? Index.ToString(null, null) : "<invalid>";
}

/// <summary>One row of a compiled layout: what a key is called, what it holds and where it lives.</summary>
/// <param name="Name">The authored name, for diagnostics and the editor's picker.</param>
/// <param name="Type">What it holds.</param>
/// <param name="Offset">Where its bytes start in an instance.</param>
/// <param name="Size">How many bytes it takes.</param>
public readonly record struct BlackboardKeyDefinition(Symbol Name, BlackboardValueType Type, int Offset, int Size);

/// <summary>
///     A blackboard's shape, compiled once: names resolved to indices, indices to byte ranges.
/// </summary>
/// <remarks>
///     <para>
///         <b>The naive blackboard is a <c>Dictionary&lt;string, object&gt;</c>, and it is wrong in
///         four ways at once</b> — a hash per read, a box per value, an allocation per write of a
///         struct, and no cheap way to observe a key. A layout is authored once and compiled; an
///         instance is then a byte range, a key reference is a <see cref="ushort" />, and a read is a
///         span slice.
///     </para>
///     <para>
///         Immutable and shareable: a thousand agents on one tree share one layout and hold one
///         <see cref="Blackboard" /> each. Build one with <see cref="BlackboardLayoutBuilder" />.
///     </para>
/// </remarks>
public sealed class BlackboardLayout {
    readonly BlackboardKeyDefinition[] keys;
    readonly Dictionary<Symbol, ushort> byName;

    internal BlackboardLayout(BlackboardKeyDefinition[] keys, Dictionary<Symbol, ushort> byName, int size) {
        this.keys = keys;
        this.byName = byName;
        Size = size;
    }

    /// <summary>A layout with no keys. What an agent that needs no data gets.</summary>
    public static BlackboardLayout Empty { get; } = new([], [], 0);

    /// <summary>How many keys there are.</summary>
    public int Count => keys.Length;

    /// <summary>How many bytes an instance of this layout holds.</summary>
    public int Size { get; }

    /// <summary>The keys, in the order they were declared.</summary>
    public ReadOnlySpan<BlackboardKeyDefinition> Keys => keys;

    /// <summary>What a key holds and where.</summary>
    /// <param name="key">The key.</param>
    /// <exception cref="ArgumentOutOfRangeException">The key is not in this layout.</exception>
    public BlackboardKeyDefinition this[BlackboardKey key] {
        get {
            if (!key.IsValid || key.Index >= keys.Length) {
                throw new ArgumentOutOfRangeException(nameof(key), key, "Not a key of this layout.");
            }

            return keys[key.Index];
        }
    }

    /// <summary>Looks a key up by name.</summary>
    /// <param name="name">Its interned name.</param>
    /// <param name="key">Where to put it.</param>
    /// <returns>Whether the layout has it.</returns>
    public bool TryGetKey(Symbol name, out BlackboardKey key) {
        if (byName.TryGetValue(name, out var index)) {
            key = new(index);

            return true;
        }

        key = BlackboardKey.Invalid;

        return false;
    }

    /// <summary>Looks a key up by name, and refuses to carry on without it.</summary>
    /// <param name="name">Its name.</param>
    /// <returns>The key.</returns>
    /// <exception cref="KeyNotFoundException">The layout has no such key.</exception>
    /// <remarks>
    ///     For setup code and tests, where a missing key is a mistake to be shouted about rather than
    ///     a case to handle. A node compiler resolves its keys once and reports a
    ///     <c>NodeDiagnostic</c> instead.
    /// </remarks>
    public BlackboardKey Key(string name) =>
        TryGetKey(Symbol.Intern(name), out var key)
            ? key
            : throw new KeyNotFoundException($"No blackboard key called '{name}'.");
}

/// <summary>Collects keys and compiles them into a <see cref="BlackboardLayout" />.</summary>
/// <remarks>
///     <para>
///         Declaration order is the key order and therefore the index order, which makes a compiled
///         layout a pure function of the file it came from — the property every part of this
///         subsystem leans on when it breaks a tie on an index.
///     </para>
///     <para>
///         ⚠ <b>It refuses a symbol collision, and that is the whole reason the check exists.</b>
///         Two differently spelled key names hashing alike would silently become one key, and the
///         agent would read a value somebody else wrote. <see cref="Symbol" />'s remarks make the
///         argument: 32 bits collide about one time in fifty thousand words, and the composition of
///         a vocabulary is the one place that still has both spellings in hand.
///     </para>
/// </remarks>
public sealed class BlackboardLayoutBuilder {
    readonly List<BlackboardKeyDefinition> keys = [];
    readonly Dictionary<Symbol, ushort> byName = [];

    int size;

    /// <summary>How many keys have been added.</summary>
    public int Count => keys.Count;

    /// <summary>Adds a key.</summary>
    /// <param name="name">What it is called.</param>
    /// <param name="type">What it holds.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="InvalidOperationException">
    ///     The name is already a key, hashes the same as one, or the layout is full.
    /// </exception>
    public BlackboardLayoutBuilder Add(string name, BlackboardValueType type) {
        ArgumentException.ThrowIfNullOrEmpty(name);

        var symbol = Symbol.Intern(name);

        if (symbol.TryGetCollision(out var first, out var second)) {
            throw new InvalidOperationException(
                $"The blackboard key names '{first}' and '{second}' hash to the same symbol. Rename one."
            );
        }

        return Add(symbol, type);
    }

    /// <summary>Adds a key that has already been interned.</summary>
    /// <param name="name">Its symbol.</param>
    /// <param name="type">What it holds.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="InvalidOperationException">
    ///     The name is already a key, or the layout is full.
    /// </exception>
    public BlackboardLayoutBuilder Add(Symbol name, BlackboardValueType type) {
        if (!name.IsSome) {
            throw new InvalidOperationException("A blackboard key must have a name.");
        }

        if (byName.ContainsKey(name)) {
            throw new InvalidOperationException($"'{name}' is already a key of this blackboard.");
        }

        // ushort.MaxValue is BlackboardKey.Invalid, so it is not a usable index. A blackboard with
        // sixty-five thousand keys is a design problem rather than a capacity problem, and the limit
        // is here so that the two-byte key reference never has to grow.
        if (keys.Count >= ushort.MaxValue) {
            throw new InvalidOperationException("A blackboard layout may hold at most 65 534 keys.");
        }

        var width = SizeOf(type);

        // Aligned to the width of the *fields* inside it rather than to the whole value: a Vector3
        // is three four-byte floats and wants four, not twelve. A bool is one byte and aligns to
        // one, which is what keeps a mostly-boolean board small. Assigning offsets here rather than
        // letting the runtime lay them out is what makes a compiled layout's dump a golden file.
        size = Align(size, AlignmentOf(type));
        byName[name] = (ushort)keys.Count;
        keys.Add(new(name, type, size, width));
        size += width;

        return this;
    }

    /// <summary>Compiles what has been collected.</summary>
    /// <returns>The layout.</returns>
    public BlackboardLayout Build() => new([.. keys], new(byName), size);

    /// <summary>How many bytes a value of this type takes.</summary>
    /// <param name="type">The type.</param>
    /// <returns>Its size.</returns>
    /// <exception cref="ArgumentOutOfRangeException">It is not one of the six.</exception>
    internal static int SizeOf(BlackboardValueType type) => type switch {
        BlackboardValueType.Bool => 1,
        BlackboardValueType.Int => 4,
        BlackboardValueType.Float => 4,
        BlackboardValueType.Vector3 => 12,

        // (int Id, int Version, short WorldId), padded to four. Twelve is the worst case in the
        // whole table, which is what "a key is twelve bytes at worst" means.
        BlackboardValueType.Entity => 12,
        BlackboardValueType.Symbol => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Not one of the six.")
    };

    /// <summary>What a value of this type must start on.</summary>
    /// <param name="type">The type.</param>
    /// <returns>Its alignment in bytes.</returns>
    internal static int AlignmentOf(BlackboardValueType type) => type == BlackboardValueType.Bool ? 1 : 4;

    static int Align(int offset, int alignment) => (offset + alignment - 1) / alignment * alignment;
}
