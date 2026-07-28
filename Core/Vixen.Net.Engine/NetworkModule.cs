// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Net.Messaging;

namespace Vixen.Net.Engine;

/// <summary>A composable, nestable unit of networked state.</summary>
/// <remarks>
///     <para>
///         <b>The primitive, and everything else here is built out of it</b> — which is
///         <c>docs/plan/16</c>'s instruction rather than a preference: "building the built-ins out of
///         the same primitive users get is the right discipline and proves the primitive". A
///         <see cref="SyncVar{T}" /> is a field in a module; a behaviour's own state is a module; a
///         health system that a dozen behaviours want is a module they each hold one of.
///     </para>
///     <para>
///         <b>Modules nest, and a nested one's fields are the outer one's fields.</b> Flattened at
///         registration, so what reaches the wire is one lane layout rather than a tree walked per
///         tick — and so the bandwidth report names a field <c>Player.Vitals.Health</c> without
///         anything having to reconstruct the path.
///     </para>
///     <para>
///         State only. Remote calls stay where they were — a module holding RPCs would need its own
///         entry in the manifest and its own ownership question, and neither has an answer that is
///         different from the object's.
///     </para>
/// </remarks>
public abstract class NetworkModule {
    readonly List<ISyncField> fields = [];
    readonly List<NetworkModule> children = [];

    WireLane[] lanes = [];
    bool sealedUp;

    /// <summary>What this module is called within its parent, or the empty string at the root.</summary>
    public string Name { get; private set; } = string.Empty;

    /// <summary>Every field, this module's and its children's, in wire order.</summary>
    public IReadOnlyList<ISyncField> Fields => fields;

    /// <summary>The fixed-width layout the whole module occupies.</summary>
    public ReadOnlySpan<WireLane> Lanes => lanes;

    /// <summary>Whether anything in it has changed since it was last sent.</summary>
    public bool IsDirty {
        get {
            // Not `field`, which is a contextual keyword inside a property accessor from C# 14 and
            // binds to a synthesised backing field rather than to the loop variable.
            foreach (var declared in fields) {
                if (declared.IsDirty) {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>Declares a field. Call from a constructor, before the module is used.</summary>
    /// <typeparam name="TField">The field's type.</typeparam>
    /// <param name="field">The field.</param>
    /// <param name="name">What to call it.</param>
    /// <returns>The field, so a declaration can be one line.</returns>
    /// <exception cref="InvalidOperationException">The module has already been used.</exception>
    protected TField Declare<TField>(TField field, string name) where TField : ISyncField {
        ArgumentNullException.ThrowIfNull(field);
        Refuse();

        field.Rename(name);
        fields.Add(field);

        return field;
    }

    /// <summary>Declares a nested module. Call from a constructor, before the module is used.</summary>
    /// <typeparam name="TModule">The module's type.</typeparam>
    /// <param name="module">The module.</param>
    /// <param name="name">What to call it.</param>
    /// <returns>The module, so a declaration can be one line.</returns>
    /// <remarks>
    ///     A different name from the one that declares a field, rather than an overload. The two
    ///     constraints do not exclude each other to the compiler's satisfaction, and a reader
    ///     choosing between "declare a field" and "declare a module" is better served by being told
    ///     which they picked.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The module has already been used.</exception>
    protected TModule Nest<TModule>(TModule module, string name) where TModule : NetworkModule {
        ArgumentNullException.ThrowIfNull(module);
        Refuse();

        module.Name = name;
        children.Add(module);

        return module;
    }

    /// <summary>Fixes the layout, after which nothing more may be declared.</summary>
    /// <param name="path">The dotted path to this module, for naming its fields.</param>
    /// <remarks>
    ///     Done once, when the module is first attached. The layout has to be the same on both ends
    ///     and for the whole of a session, so the point at which it stops being able to change is a
    ///     thing worth having rather than a restriction: a field declared halfway through a match
    ///     would shift every lane after it and desynchronise everything already in flight.
    /// </remarks>
    public void Seal(string path = "") {
        if (sealedUp) {
            return;
        }

        // This module's own fields are renamed *before* the children's are folded in. Renaming
        // afterwards would prefix the children's names a second time — they were already given the
        // full path by their own Seal — and produce Player.Player.Vitals.Health.
        foreach (var own in fields) {
            own.Rename(Join(path, own.Name));
        }

        foreach (var child in children) {
            child.Seal(Join(path, child.Name));
            fields.AddRange(child.fields);
        }

        var layout = new List<WireLane>();

        foreach (var declared in fields) {
            foreach (var lane in declared.Lanes) {
                layout.Add(lane);
            }
        }

        lanes = [.. layout];
        sealedUp = true;
    }

    /// <summary>Writes every field.</summary>
    /// <param name="writer">Where the bits go.</param>
    public void Write(ref BitWriter writer) {
        foreach (var field in fields) {
            field.Write(ref writer);
        }
    }

    /// <summary>Reads every field, in the order they were written.</summary>
    /// <param name="reader">Where the bits come from.</param>
    /// <returns>Whether they were well-formed.</returns>
    public bool Apply(ref BitReader reader) {
        foreach (var field in fields) {
            if (!field.Apply(ref reader)) {
                return false;
            }
        }

        return true;
    }

    /// <summary>Marks everything as sent.</summary>
    public void ClearDirty() {
        foreach (var field in fields) {
            field.ClearDirty();
        }
    }

    static string Join(string path, string name) => path.Length == 0 ? name : $"{path}.{name}";

    void Refuse() {
        if (sealedUp) {
            throw new InvalidOperationException(
                "This module's layout is fixed. Declare fields in the constructor: a field added later "
                + "would shift every lane after it and desynchronise what is already in flight."
            );
        }
    }
}
