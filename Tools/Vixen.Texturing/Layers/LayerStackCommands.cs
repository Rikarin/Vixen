// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Vixen.Authoring;

namespace Vixen.Texturing.Layers;

/// <summary>One place a set reads an anchor point: the layer, what part of it, and the reference.</summary>
/// <param name="Layer">The <see cref="LayerAsset.Id" /> of the layer that reads it.</param>
/// <param name="Where">Which part of that layer: <c>fill</c>, <c>mask</c>, <c>mask entry 2</c>, an input's port.</param>
/// <param name="Reference">The anchor point and the channel read.</param>
public sealed record LayerAnchorUse(string Layer, string Where, AnchorReference Reference);

/// <summary>Where a layer is in a stack: the texture set it belongs to, and its own identity.</summary>
/// <param name="Set">The <see cref="TextureSetAsset.Name" />.</param>
/// <param name="Id">The <see cref="LayerAsset.Id" />.</param>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             An id and not an index, and that is the whole reason <see cref="LayerAsset.Id" />
///             exists.
///         </b>
///         A command records where it acted so that its undo can act in the same place,
///         and both of the obvious coordinates move: an index moves when anything under the layer is
///         reordered, and a name moves when somebody renames it. An anchor already names a layer
///         this way for exactly that reason.
///     </para>
///     <para>
///         ⚠
///         <b>
///             What this does <em>not</em> inherit is a guarantee that the key is unique, and the
///             remark here used to say it did
///         </b>
///         (<a href="https://github.com/Rikarin/Vixen/issues/893">#893</a>).
///         <c>LayerStackGraph.Duplicates</c> is a <em>compile refusal</em>, and the panel builds its
///         rows from the document rather than from a compilation — so a stack that fails it was still
///         a stack whose rows were drawn and clicked, and a duplicate id meant the second layer's row
///         drove the first. It did not cover the empty id at all until the same issue, which is how a
///         file naming no ids got every layer the same one.
///     </para>
///     <para>
///         ⚠ <b>What closes it is <see cref="LayerStackEdit.Ambiguous" />, read by both halves.</b>
///         The refusal and the panel now ask one function which ids name more than one layer;
///         <c>LayerStackView</c> draws such a row with its name, a sentence and no controls at all,
///         so this type still has no uniqueness guarantee and there is no longer a gesture that
///         needs one. The addressing itself is unchanged: resolving an id still reaches the first
///         match, which is why the row is disarmed rather than re-pointed.
///     </para>
/// </remarks>
public readonly record struct LayerPath(string Set, string Id);

/// <summary>Where a layer <em>goes</em>: the set, the list it is in, and the place in that list.</summary>
/// <param name="Set">The <see cref="TextureSetAsset.Name" />.</param>
/// <param name="Parent">
///     The <see cref="LayerAsset.Id" /> of the group whose children the list is, or
///     <see langword="null" /> for the set's own list.
/// </param>
/// <param name="Index">Where in it, counting from the bottom of the composite.</param>
/// <remarks>
///     <para>
///         <b>
///             A different question from <see cref="LayerPath" />, and an insert is what makes the
///             difference matter.
///         </b>
///         A path names a layer that exists and resolves to whichever list
///         holds it; a slot names a place, which has to be said even when nothing is there — the
///         empty set's own list, or the gap a removed layer left. Every command that changes what a
///         list <em>contains</em> takes one of these, and every command that changes a layer takes a
///         path.
///     </para>
///     <para>
///         ⚠
///         <b>
///             <paramref name="Parent" /> is nullable rather than empty-for-none, and that is not a
///             style choice.
///         </b>
///         <see cref="LayerAsset.Id" /> defaults to <c>""</c> and a single id-less
///         layer addresses perfectly well —
///         <a href="https://github.com/Rikarin/Vixen/issues/966">
///             #966
///         </a>
///         — so <c>""</c> here would mean both "the set's own list" and "the children of
///         the layer with no id", and a group nobody named would swallow every top-level insert.
///     </para>
///     <para>
///         ⚠
///         <b>
///             An index and not a neighbour's id, which is the opposite of every other address in
///             this file.
///         </b>
///         An index is exactly what a reorder invalidates — which is why
///         <see cref="LayerPath" /> refuses one — but an insert has no layer to name yet, and the
///         two callers that hold a slot across time both re-resolve it: <see cref="AddLayerCommand" />
///         undoes by the id it inserted, and <see cref="RemoveLayerCommand" /> resolves the slot when
///         it removes rather than when it is built.
///     </para>
/// </remarks>
public readonly record struct LayerSlot(string Set, string? Parent, int Index);

/// <summary>Finding and moving a layer inside a stack, by <see cref="LayerPath" />.</summary>
/// <remarks>
///     ⚠ <b>A layer's parent list is what a reorder happens in, and it is not always the set's.</b> A
///     group's children are their own list, composited over whatever is beneath the group — so
///     "move this layer up" inside a group means a different list from the same words outside one,
///     and a lookup that only walked <see cref="TextureSetAsset.Layers" /> would silently do nothing
///     for every layer an artist put in a group.
/// </remarks>
public static class LayerStackEdit {
    /// <summary>The list holding a layer, and where in it.</summary>
    /// <param name="stack">The stack.</param>
    /// <param name="path">Which layer.</param>
    /// <param name="parent">The list it is in.</param>
    /// <param name="index">Where in that list, counting from the bottom of the composite.</param>
    /// <returns>Whether it was found.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stack" /> is null.</exception>
    public static bool TryFind(
        LayerStackAsset stack,
        LayerPath path,
        [NotNullWhen(true)] out List<LayerAsset>? parent,
        out int index
    ) {
        ArgumentNullException.ThrowIfNull(stack);

        parent = null;
        index = -1;

        if (stack.SetNamed(path.Set) is not { } set) {
            return false;
        }

        return Walk(set.Layers, path.Id, out parent, out index);
    }

    /// <summary>The ids that name more than one layer of a set, and therefore name none of them.</summary>
    /// <param name="set">The texture set.</param>
    /// <returns>Every id carried by two or more of its layers, the empty one included.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="set" /> is null.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>One rule with two readers, and that is the point of it being here</b>
    ///         (<a href="https://github.com/Rikarin/Vixen/issues/893">#893</a>).
    ///         <c>LayerStackGraph.Duplicates</c> turns this into a compile refusal and
    ///         <c>LayerStackView</c> turns it into a row with no controls on it; the two disagreeing
    ///         about which ids are ambiguous is the failure mode — a stack the compiler refuses whose
    ///         rows the panel still offers to move, or the reverse. Five exact-equality roll calls in
    ///         this workstream have gone red on a second transcription of a known set, and this is a
    ///         set.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The empty id is in it, and only when a second layer also has none.</b>
    ///         <see cref="LayerAsset.Id" /> defaults to empty, so a hand-written file naming no ids
    ///         gives every layer the same one — but a stack with a single unnamed layer addresses
    ///         perfectly well, and <c>""</c> is what names it. What makes an id useless is that it is
    ///         shared, not that it is short.
    ///     </para>
    /// </remarks>
    public static IReadOnlySet<string> Ambiguous(TextureSetAsset set) {
        ArgumentNullException.ThrowIfNull(set);

        HashSet<string> seen = new(StringComparer.Ordinal);
        HashSet<string> shared = new(StringComparer.Ordinal);

        Walk(set.Layers);

        return shared;

        void Walk(List<LayerAsset> layers) {
            foreach (var layer in layers) {
                if (!seen.Add(layer.Id)) {
                    shared.Add(layer.Id);
                }

                Walk(layer.Children);
            }
        }
    }

    /// <summary>The layer at a path, or <see langword="null" />.</summary>
    /// <param name="stack">The stack.</param>
    /// <param name="path">Which layer.</param>
    /// <returns>The layer.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stack" /> is null.</exception>
    public static LayerAsset? Find(LayerStackAsset stack, LayerPath path) =>
        TryFind(stack, path, out var parent, out var index) ? parent[index] : null;

    /// <summary>Puts a different layer in that place.</summary>
    /// <param name="stack">The stack.</param>
    /// <param name="path">Which layer.</param>
    /// <param name="value">What to put there.</param>
    /// <returns>Whether there was a layer to replace.</returns>
    /// <exception cref="ArgumentNullException">The stack or the value is null.</exception>
    public static bool Replace(LayerStackAsset stack, LayerPath path, LayerAsset value) {
        ArgumentNullException.ThrowIfNull(value);

        if (!TryFind(stack, path, out var parent, out var index)) {
            return false;
        }

        parent[index] = value;

        return true;
    }

    /// <summary>The set a name chooses, or the first one when it chooses none.</summary>
    /// <param name="stack">The stack.</param>
    /// <param name="name">The <see cref="TextureSetAsset.Name" /> wanted, or empty for no choice.</param>
    /// <returns>The set, or <see langword="null" /> when the stack has none at all.</returns>
    /// <exception cref="ArgumentNullException">The stack or the name is null.</exception>
    /// <remarks>
    ///     <para>
    ///         <b>
    ///             <a href="https://github.com/Rikarin/Vixen/issues/927">#927</a>'s decision, in one
    ///             function so that seven call sites cannot each make it differently.
    ///         </b>
    ///         A texture set is
    ///         a material slot of the model — <see cref="TextureSetAsset.Mesh" /> narrows it to one of
    ///         the model's meshes — so each set has its own atlas, its own channels, its own layers and
    ///         its own <c>.vxpaint</c> files. Two things follow and neither is a preference: a stroke
    ///         is texels in <em>one</em> atlas, so painting cannot span sets; and one list holding two
    ///         independent composites would be two orders in one list, with "move up" across the
    ///         boundary meaning nothing. So the editor works on one set at a time, chosen once and read
    ///         by everything.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The fallback is required rather than defensive.</b> A stack whose set was renamed,
    ///         or one opened while the chooser still names the set of the stack before it, has to open
    ///         rather than refuse — and the fallback is what makes this change a no-op for every
    ///         single-set stack, which is every stack that exists today.
    ///     </para>
    /// </remarks>
    public static TextureSetAsset? SetFor(LayerStackAsset stack, string name) {
        ArgumentNullException.ThrowIfNull(stack);
        ArgumentNullException.ThrowIfNull(name);

        if (name.Length > 0 && stack.SetNamed(name) is { } named) {
            return named;
        }

        return stack.Sets.Count > 0 ? stack.Sets[0] : null;
    }

    /// <summary>The list a slot names, whether or not anything is in it.</summary>
    /// <param name="stack">The stack.</param>
    /// <param name="slot">Which list.</param>
    /// <param name="list">The list itself, live.</param>
    /// <returns>Whether the set — and the group, when the slot names one — was found.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stack" /> is null.</exception>
    /// <remarks>
    ///     ⚠ <b>The live list and not a copy, which is what makes an insert an insert.</b> Every
    ///     record here shares its collection members with the value <c>with</c> copied it from, so
    ///     the <see cref="List{T}" /> a group's <see cref="LayerAsset.Children" /> hands back is the
    ///     one the stack is holding — the same property <see cref="Replace" /> and <see cref="Move" />
    ///     already rely on.
    /// </remarks>
    public static bool TryList(
        LayerStackAsset stack,
        LayerSlot slot,
        [NotNullWhen(true)] out List<LayerAsset>? list
    ) {
        ArgumentNullException.ThrowIfNull(stack);

        list = null;

        if (stack.SetNamed(slot.Set) is not { } set) {
            return false;
        }

        if (slot.Parent is null) {
            list = set.Layers;

            return true;
        }

        if (Find(stack, new(slot.Set, slot.Parent)) is not { } parent) {
            return false;
        }

        list = parent.Children;

        return true;
    }

    /// <summary>The slot a layer currently occupies.</summary>
    /// <param name="stack">The stack.</param>
    /// <param name="path">Which layer.</param>
    /// <returns>Where it is, or <see langword="null" /> when nothing answers to that path.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stack" /> is null.</exception>
    /// <remarks>
    ///     ⚠
    ///     <b>
    ///         Its own walk rather than <see cref="TryFind" />'s, because the parent's <em>id</em> is
    ///         the one thing that walk cannot report.
    ///     </b>
    ///     <see cref="TryFind" /> hands back the list and
    ///     the index, which is everything a reorder needs and one short of what an insert needs: a
    ///     list is an object, and a command that held one across an undo would be holding a
    ///     collection the document may have replaced.
    /// </remarks>
    public static LayerSlot? SlotOf(LayerStackAsset stack, LayerPath path) {
        ArgumentNullException.ThrowIfNull(stack);

        if (stack.SetNamed(path.Set) is not { } set) {
            return null;
        }

        return Locate(set.Layers, null);

        LayerSlot? Locate(List<LayerAsset> layers, string? parent) {
            for (var index = 0; index < layers.Count; index++) {
                if (string.Equals(layers[index].Id, path.Id, StringComparison.Ordinal)) {
                    return new LayerSlot(path.Set, parent, index);
                }

                if (Locate(layers[index].Children, layers[index].Id) is { } found) {
                    return found;
                }
            }

            return null;
        }
    }

    /// <summary>Puts a layer into a list at a place.</summary>
    /// <param name="stack">The stack.</param>
    /// <param name="slot">Where it goes. An index equal to the list's length appends.</param>
    /// <param name="layer">What goes there.</param>
    /// <returns>Whether it went in.</returns>
    /// <exception cref="ArgumentNullException">The stack or the layer is null.</exception>
    public static bool Insert(LayerStackAsset stack, LayerSlot slot, LayerAsset layer) {
        ArgumentNullException.ThrowIfNull(layer);

        if (!TryList(stack, slot, out var list) || slot.Index < 0 || slot.Index > list.Count) {
            return false;
        }

        list.Insert(slot.Index, layer);

        return true;
    }

    /// <summary>Takes a layer out, and says where it was.</summary>
    /// <param name="stack">The stack.</param>
    /// <param name="path">Which layer.</param>
    /// <param name="slot">Where it was, so that putting it back is one call.</param>
    /// <returns>Whether there was a layer to take out.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stack" /> is null.</exception>
    /// <remarks>
    ///     ⚠ <b>A group goes with its children, and that is what makes the removal reversible.</b>
    ///     <see cref="LayerAsset.Children" /> is a member of the record that comes out, so the whole
    ///     subtree is held by whoever is holding the layer — an undo puts back the tree rather than a
    ///     stump, and nothing has to walk it.
    /// </remarks>
    public static bool Remove(LayerStackAsset stack, LayerPath path, out LayerSlot slot) {
        slot = default;

        if (SlotOf(stack, path) is not { } found || !TryFind(stack, path, out var parent, out var index)) {
            return false;
        }

        slot = found;
        parent.RemoveAt(index);

        return true;
    }

    /// <summary>An id no layer of a set carries, built from a stem.</summary>
    /// <param name="set">The texture set.</param>
    /// <param name="stem">What the id reads as, before the number.</param>
    /// <returns>The first of <c>stem-1</c>, <c>stem-2</c>, … that nothing already answers to.</returns>
    /// <exception cref="ArgumentNullException">The set or the stem is null.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Readable and counted rather than a GUID, and the trade is deliberate.</b> A
    ///         <c>.vxlayers</c> is a file people read and merge, and an anchor names a layer by this
    ///         string — <c>Anchor: layer-2</c> is a line somebody can resolve a conflict in and
    ///         <c>Anchor: 3f2a…</c> is not. What a counted id costs is that two branches each adding
    ///         a layer both reach <c>layer-2</c>; what makes that affordable is that the collision is
    ///         <em>loud</em> — <c>Ambiguous</c> reports it, the compiler refuses the stack and the
    ///         panel draws both rows with no controls
    ///         (<a href="https://github.com/Rikarin/Vixen/issues/893">#893</a>).
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             Every layer in the set counts, groups walked into, and not only the list being
    ///             inserted into.
    ///         </b>
    ///         An id is unique in the <em>set</em> — that is what an anchor and a
    ///         <see cref="LayerPath" /> both assume — so a per-list counter would hand the second
    ///         group's first child the id the first group's first child already has.
    ///     </para>
    /// </remarks>
    public static string FreeId(TextureSetAsset set, string stem) {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(stem);

        HashSet<string> taken = new(StringComparer.Ordinal);

        Walk(set.Layers);

        for (var number = 1;; number++) {
            var wanted = stem + "-" + number.ToString(CultureInfo.InvariantCulture);

            if (taken.Add(wanted)) {
                return wanted;
            }
        }

        void Walk(List<LayerAsset> layers) {
            foreach (var layer in layers) {
                taken.Add(layer.Id);
                Walk(layer.Children);
            }
        }
    }

    /// <summary>Moves a layer within the list it is already in.</summary>
    /// <param name="stack">The stack.</param>
    /// <param name="path">Which layer.</param>
    /// <param name="delta">How far, in composite order: <c>+1</c> is one step later, over its neighbour.</param>
    /// <returns>Whether it moved.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stack" /> is null.</exception>
    /// <remarks>
    ///     ⚠ <b>In composite order, which is the file's order and the reverse of the panel's.</b>
    ///     <see cref="TextureSetAsset.Layers" /> is stored bottom first so that reading the file is
    ///     reading the arithmetic; a panel draws the top layer at the top and therefore asks for
    ///     <c>+1</c> when somebody presses the button labelled <em>up</em>. Expressing the move in
    ///     the file's terms keeps the one reversal in the view, which is where that member's own
    ///     remarks ask for it — a command that took "up" would put a second copy of the reversal in
    ///     the undo history, where nobody reading the file can see it.
    /// </remarks>
    public static bool Move(LayerStackAsset stack, LayerPath path, int delta) {
        if (delta == 0 || !TryFind(stack, path, out var parent, out var index)) {
            return false;
        }

        var target = index + delta;

        if (target < 0 || target >= parent.Count) {
            return false;
        }

        var layer = parent[index];

        parent.RemoveAt(index);
        parent.Insert(target, layer);

        return true;
    }

    /// <summary>Every anchor point a set declares, by name, with the id of the layer that publishes it.</summary>
    /// <param name="set">The set.</param>
    /// <returns>Name to layer id, in the file's order; a name declared twice keeps its first layer.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="set" /> is null.</exception>
    public static IReadOnlyDictionary<string, string> AnchorPoints(TextureSetAsset set) {
        ArgumentNullException.ThrowIfNull(set);

        Dictionary<string, string> points = new(StringComparer.Ordinal);

        foreach (var layer in Everything(set.Layers)) {
            if (layer.AnchorPoint.Length > 0) {
                points.TryAdd(layer.AnchorPoint, layer.Id);
            }
        }

        return points;
    }

    /// <summary>Every reference to an anchor point in a set: who reads which, where and in which channel.</summary>
    /// <param name="set">The set.</param>
    /// <returns>The references, in the file's order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="set" /> is null.</exception>
    /// <remarks>
    ///     ⚠ <b>Only references the compiler reads.</b> A mask's anchor is listed when its source is
    ///     <see cref="LayerMaskSource.Anchor" />, and a fill's when it is <see cref="LayerFillSource.Anchor" />: a name
    ///     left behind by a source changed since reads nothing, and a list saying it did would be the defect.
    /// </remarks>
    public static IReadOnlyList<LayerAnchorUse> AnchorUses(TextureSetAsset set) {
        ArgumentNullException.ThrowIfNull(set);

        List<LayerAnchorUse> uses = [];

        foreach (var layer in Everything(set.Layers)) {
            if (layer is { Kind: LayerKind.Fill, Fill: LayerFillSource.Anchor }) {
                uses.Add(new(layer.Id, "fill", layer.Anchor));
            }

            foreach (var (port, input) in layer.Inputs) {
                if (input.Source == LayerInputSource.Anchor) {
                    uses.Add(new(layer.Id, $"input {port}", input.Anchor));
                }
            }

            if (layer.Mask.Source == LayerMaskSource.Anchor) {
                uses.Add(new(layer.Id, "mask", layer.Mask.Anchor));
            }

            if (layer.Mask.Source == LayerMaskSource.Generator) {
                foreach (var (port, reference) in layer.Mask.GeneratorInputs) {
                    uses.Add(new(layer.Id, $"mask generator {port}", reference));
                }
            }

            for (var index = 0; index < layer.Mask.Layers.Count; index++) {
                var entry = layer.Mask.Layers[index];
                var where = $"mask entry {(index + 1).ToString(CultureInfo.InvariantCulture)}";

                if (entry.Source == LayerMaskSource.Anchor) {
                    uses.Add(new(layer.Id, where, entry.Anchor));
                }

                if (entry.Source == LayerMaskSource.Generator) {
                    foreach (var (port, reference) in entry.GeneratorInputs) {
                        uses.Add(new(layer.Id, $"{where} generator {port}", reference));
                    }
                }
            }

            foreach (var effect in layer.Mask.Effects) {
                foreach (var (port, input) in effect.Inputs) {
                    if (input.Source == LayerInputSource.Anchor) {
                        uses.Add(new(layer.Id, $"mask effect {effect.Node} {port}", input.Anchor));
                    }
                }
            }
        }

        return uses;
    }

    /// <summary>A name for an anchor point that no layer of the set publishes: the one wanted, or it numbered.</summary>
    /// <param name="set">The set.</param>
    /// <param name="wanted">The name asked for.</param>
    /// <returns>The name.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static string FreeAnchor(TextureSetAsset set, string wanted) {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(wanted);

        var taken = AnchorPoints(set);

        if (!taken.ContainsKey(wanted)) {
            return wanted;
        }

        for (var number = 2;; number++) {
            var numbered = wanted + " " + number.ToString(CultureInfo.InvariantCulture);

            if (!taken.ContainsKey(numbered)) {
                return numbered;
            }
        }
    }

    /// <summary>Layers with every reference to one anchor point pointed at another name, and nothing else changed.</summary>
    /// <param name="layers">The layers, which this does not change.</param>
    /// <param name="from">The name read now.</param>
    /// <param name="to">The name to read.</param>
    /// <returns>New layers, sharing nothing a later edit could reach with the ones given.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <remarks>
    ///     What renaming an anchor point rewrites, so that a rename is one undoable edit and no reference is left
    ///     reading a name nothing publishes. The anchor point itself is not renamed here.
    /// </remarks>
    public static List<LayerAsset> Reanchored(IEnumerable<LayerAsset> layers, string from, string to) {
        ArgumentNullException.ThrowIfNull(layers);
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        return [..layers.Select(Layer)];

        AnchorReference Moved(AnchorReference reference) =>
            string.Equals(reference.Anchor, from, StringComparison.Ordinal)
                ? reference with { Anchor = to }
                : reference;

        Dictionary<string, AnchorReference> Ports(Dictionary<string, AnchorReference> inputs) =>
            inputs.ToDictionary(static pair => pair.Key, pair => Moved(pair.Value), StringComparer.Ordinal);

        Dictionary<string, LayerInputAsset> Sources(Dictionary<string, LayerInputAsset> inputs) =>
            inputs.ToDictionary(
                static pair => pair.Key,
                pair => pair.Value with { Anchor = Moved(pair.Value.Anchor) },
                StringComparer.Ordinal
            );

        LayerAsset Layer(LayerAsset layer) =>
            layer with {
                Anchor = Moved(layer.Anchor),
                Inputs = Sources(layer.Inputs),
                Mask = layer.Mask with {
                    Anchor = Moved(layer.Mask.Anchor),
                    GeneratorInputs = Ports(layer.Mask.GeneratorInputs),
                    Layers = [
                        ..layer.Mask.Layers.Select(entry => entry with {
                                Anchor = Moved(entry.Anchor), GeneratorInputs = Ports(entry.GeneratorInputs)
                            }
                        )
                    ],
                    Effects = [..layer.Mask.Effects.Select(effect => effect with { Inputs = Sources(effect.Inputs) })]
                },
                Children = [..layer.Children.Select(Layer)]
            };
    }

    /// <summary>Whether a group can be flattened into a canvas that composites the picture it composites now.</summary>
    /// <param name="set">The set.</param>
    /// <param name="id">The group's id.</param>
    /// <param name="backdrop">
    ///     Whether its canvas has to hold what is beneath it: a pass-through group at the bottom of the set whose
    ///     children read what is beneath them, which is what the whole stack flattened is.
    /// </param>
    /// <param name="reason">Why not, or empty.</param>
    /// <returns>Whether it can.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Exact or refused, never approximately right.</b> A group under any mode but
    ///         <see cref="LayerBlendMode.Copy" /> is isolated, so its children's composite over transparency is all it
    ///         ever contributes and the canvas is that. A pass-through group's children composite straight onto what is
    ///         beneath — and as long as each covers it with <see cref="LayerBlendMode.Copy" /> over, that equals their own
    ///         composite laid over it afterwards, because source-over is associative. A filter or any other operator
    ///         <em>reads</em> what is beneath, which a canvas cannot hold without holding the backdrop too; that is
    ///         allowed only where the backdrop is the set's channel defaults, at the bottom of the stack.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An anchor point inside is refused when anything outside reads it</b>, since a flattened group's
    ///         children are not built and the reference would have no picture.
    ///     </para>
    /// </remarks>
    public static bool CanFlatten(TextureSetAsset set, string id, out bool backdrop, out string reason) {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(id);

        backdrop = false;
        reason = string.Empty;

        if (!Walk(set.Layers, id, out var parent, out var index)) {
            reason = $"This stack has no layer '{id}'.";

            return false;
        }

        var group = parent[index];

        if (group.Kind != LayerKind.Group) {
            reason =
                $"'{Named(group)}' is a {group.Kind.ToString().ToLowerInvariant()} layer, and only a group flattens.";

            return false;
        }

        if (group.Flattened) {
            reason = $"'{Named(group)}' is flattened already.";

            return false;
        }

        HashSet<string> inside = new(StringComparer.Ordinal);
        HashSet<string> within = new(StringComparer.Ordinal);

        foreach (var child in Everything(group.Children)) {
            within.Add(child.Id);

            if (child.AnchorPoint.Length > 0) {
                inside.Add(child.AnchorPoint);
            }
        }

        foreach (var use in AnchorUses(set)) {
            if (inside.Contains(use.Reference.Anchor) && !within.Contains(use.Layer)) {
                reason =
                    $"'{use.Layer}' reads the anchor point '{use.Reference.Anchor}' inside '{Named(group)}', which "
                    + "a flattened group would no longer build. Point it elsewhere first.";

                return false;
            }
        }

        if (group.Blend != LayerBlendMode.Copy || Associative(group.Children)) {
            return true;
        }

        if (ReferenceEquals(parent, set.Layers) && Beneath(parent, index)) {
            backdrop = true;

            return true;
        }

        reason =
            $"'{Named(group)}' passes through, and a layer inside it reads what is beneath the group — a filter, or "
            + "a blend other than Copy — so no canvas could hold its picture without holding that too. Give the group a "
            + "blend mode of its own, or flatten the whole stack.";

        return false;

        static bool Associative(List<LayerAsset> layers) =>
            layers.TrueForAll(static layer => !layer.Enabled
                || (layer.Kind != LayerKind.Filter
                    && layer.Blend == LayerBlendMode.Copy
                    && (layer.Kind != LayerKind.Group || layer.Flattened || Associative(layer.Children)))
            );

        // Nothing enabled beneath it, so what it composites over is the channel defaults.
        static bool Beneath(List<LayerAsset> layers, int index) {
            for (var below = 0; below < index; below++) {
                if (layers[below].Enabled) {
                    return false;
                }
            }

            return true;
        }
    }

    static string Named(LayerAsset layer) => layer.Name.Length > 0 ? layer.Name : layer.Id;

    /// <summary>Every layer of a list, groups walked into, in the file's order.</summary>
    static IEnumerable<LayerAsset> Everything(IEnumerable<LayerAsset> layers) {
        foreach (var layer in layers) {
            yield return layer;

            foreach (var child in Everything(layer.Children)) {
                yield return child;
            }
        }
    }

    static bool Walk(
        List<LayerAsset> layers,
        string id,
        [NotNullWhen(true)] out List<LayerAsset>? parent,
        out int index
    ) {
        for (var position = 0; position < layers.Count; position++) {
            if (string.Equals(layers[position].Id, id, StringComparison.Ordinal)) {
                parent = layers;
                index = position;

                return true;
            }

            if (Walk(layers[position].Children, id, out parent, out index)) {
                return true;
            }
        }

        parent = null;
        index = -1;

        return false;
    }
}

/// <summary>One layer replaced by another version of itself, as one undo entry.</summary>
/// <remarks>
///     <para>
///         <b>One command type for every property of a layer, rather than one per property.</b> A
///         <see cref="LayerAsset" /> is a record with fifteen members and a command per member would
///         be fifteen types whose bodies are the same two lines; what actually differs between
///         "set the blend mode" and "drag the opacity" is the sentence in the undo menu and whether
///         two of them are one edit, so both of those are parameters and nothing else is.
///     </para>
///     <para>
///         ⚠
///         <b>
///             The record's collection members are shared by <c>with</c>, and a command that forgets
///             it has no undo at all.
///         </b>
///         <c>after = before with { Blend = … }</c> hands the new value the
///         <em>same</em> <c>List&lt;string&gt; Channels</c> instance — so a caller that then mutated
///         that list in place would be mutating the before-image too, and <see cref="Undo" /> would
///         put back a layer that already holds the new value. Every caller here builds a new
///         collection instead; that is not a style preference, it is what makes the entry reversible.
///     </para>
///     <para>
///         ⚠
///         <b>
///             Merging is by an explicit key rather than by type, because the two cases really are
///             different.
///         </b>
///         Dragging an opacity slider is one edit and produces one entry; choosing a
///         blend mode twice is two decisions and produces two. A command type that merged with every
///         command of its own type would collapse the second into the first, and one that never
///         merged would put three hundred entries in the history for one drag. The key is what the
///         call site knows and the type cannot: <c>PaintStrokeCommand</c> answers the same question
///         by writing <em>never</em> down and saying why.
///     </para>
/// </remarks>
public sealed class SetLayerCommand : IAuthoringCommand {
    readonly ILayerStackDocument document;
    readonly LayerPath path;
    readonly LayerAsset before;
    readonly LayerAsset after;
    readonly string mergeKey;

    /// <summary>Records a replacement.</summary>
    /// <param name="document">The stack it happens in.</param>
    /// <param name="path">Which layer.</param>
    /// <param name="before">What it was.</param>
    /// <param name="after">What it becomes.</param>
    /// <param name="name">What the undo entry says.</param>
    /// <param name="mergeKey">
    ///     What makes two of these one edit, or empty for a change that always stands alone.
    /// </param>
    /// <exception cref="ArgumentNullException">The document, the before or the after is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="name" /> is empty.</exception>
    public SetLayerCommand(
        ILayerStackDocument document,
        LayerPath path,
        LayerAsset before,
        LayerAsset after,
        string name,
        string mergeKey = ""
    ) {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentException.ThrowIfNullOrEmpty(name);

        this.document = document;
        this.path = path;
        this.before = before;
        this.after = after;
        this.mergeKey = mergeKey;

        Name = name;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠
    ///     <b>
    ///         Unlike <c>PaintStrokeCommand</c>, this one applies the change rather than recording an
    ///         applied one.
    ///     </b>
    ///     A stroke exists because a brush already painted texels and the command is
    ///     built at pointer-up; a property set has nothing that happened first, so the caller builds
    ///     the command and <c>Execute</c> is what makes the edit. Both shapes are in this repository
    ///     and the difference is whose job the first application is.
    /// </remarks>
    public void Do(ICommandContext context) => LayerStackEdit.Replace(document.Document, path, after);

    /// <inheritdoc />
    public void Undo(ICommandContext context) => LayerStackEdit.Replace(document.Document, path, before);

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠
    ///     <b>
    ///         The merged entry keeps <em>this</em> command's after-image and the <em>previous</em>
    ///         one's before-image
    ///     </b>
    ///     , which is what <see cref="IAuthoringCommand.TryMergeWith" /> asks for:
    ///     undoing a merged drag has to reach the value the slider held before the drag started, not
    ///     the one it held a frame ago.
    /// </remarks>
    public bool TryMergeWith(IAuthoringCommand previous, [NotNullWhen(true)] out IAuthoringCommand? merged) {
        merged = null;

        if (mergeKey.Length == 0
            || previous is not SetLayerCommand earlier
            || !ReferenceEquals(earlier.document, document)
            || earlier.path != path
            || !string.Equals(earlier.mergeKey, mergeKey, StringComparison.Ordinal)) {
            return false;
        }

        merged = new SetLayerCommand(document, path, earlier.before, after, Name, mergeKey);

        return true;
    }
}

/// <summary>The stack bound to a different model, as one undo entry.</summary>
/// <remarks>
///     <para>
///         <b>
///             <a href="https://github.com/Rikarin/Vixen/issues/920">#920</a>, and it is the first
///             command here that edits the stack rather than a layer in it.
///         </b>
///         <see cref="SetLayerCommand" /> reaches a <see cref="LayerAsset" /> through a
///         <see cref="LayerPath" />; a binding is the whole file's, so what is replaced is
///         <c>ILayerStackDocument.Document</c> itself.
///     </para>
///     <para>
///         ⚠
///         <b>
///             A <c>with</c> on the record shares its <see cref="LayerStackAsset.Sets" /> list, and
///             that is what makes this safe to interleave with layer edits.
///         </b>
///         The before-image and the
///         after-image hold the <em>same</em> list, so a layer moved between binding and undoing
///         stays moved — which is what an artist means. It is the opposite of
///         <see cref="SetLayerCommand" />'s rule about collections, and for the opposite reason:
///         there the collection is part of what is being reverted, here it is not.
///     </para>
/// </remarks>
public sealed class SetModelCommand : IAuthoringCommand {
    readonly ILayerStackDocument document;
    readonly string before;
    readonly string after;

    /// <summary>Records a binding.</summary>
    /// <param name="document">The stack.</param>
    /// <param name="model">The model's project-relative path, or empty to unbind.</param>
    /// <param name="name">What the undo entry says.</param>
    /// <exception cref="ArgumentNullException">The document or the model is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="name" /> is empty.</exception>
    public SetModelCommand(ILayerStackDocument document, string model, string name) {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrEmpty(name);

        this.document = document;
        before = document.Document.Model;
        after = model;

        Name = name;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public void Do(ICommandContext context) => document.Document = document.Document with { Model = after };

    /// <inheritdoc />
    public void Undo(ICommandContext context) => document.Document = document.Document with { Model = before };

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Never, which is <see cref="MoveLayerCommand" />'s answer and not the slider's.</b>
    ///     Choosing a mesh is one decision per gesture: an artist who bound the wrong model, then the
    ///     right one, and pressed undo means to be back on the wrong one, not unbound.
    /// </remarks>
    public bool TryMergeWith(IAuthoringCommand previous, [NotNullWhen(true)] out IAuthoringCommand? merged) {
        merged = null;

        return false;
    }
}

/// <summary>Which of the model's meshes a set is narrowed to, as one undo entry.</summary>
/// <remarks>
///     <para>
///         <b>
///             <a href="https://github.com/Rikarin/Vixen/issues/941">#941</a>'s edit, and it is
///             <see cref="SetModelCommand" /> one level down.
///         </b>
///         A model file splits into one mesh per
///         material slot, which is what a texture set is; without narrowing, a two-set stack gets one
///         coverage map over every island in the model and the <c>Body</c> set can be painted
///         anywhere <c>Head</c> has surface.
///     </para>
///     <para>
///         ⚠ <b>Keyed by the set's position rather than by the set object</b>, because
///         <c>TextureSetAsset</c> is a record the document replaces wholesale on every edit — an undo
///         holding the object would write into a set the stack no longer contains, and the panel
///         would go on showing the value it had.
///     </para>
/// </remarks>
public sealed class SetMeshCommand : IAuthoringCommand {
    readonly ILayerStackDocument document;
    readonly int set;
    readonly string before;
    readonly string after;

    /// <summary>Records a narrowing.</summary>
    /// <param name="document">The stack.</param>
    /// <param name="set">Which set, by index.</param>
    /// <param name="mesh">The mesh's name in the project, or empty for every mesh in the model.</param>
    /// <param name="name">What the undo entry says.</param>
    /// <exception cref="ArgumentNullException">The document or the mesh is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">There is no set at that index.</exception>
    /// <exception cref="ArgumentException"><paramref name="name" /> is empty.</exception>
    public SetMeshCommand(ILayerStackDocument document, int set, string mesh, string name) {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentOutOfRangeException.ThrowIfNegative(set);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(set, document.Document.Sets.Count);

        this.document = document;
        this.set = set;
        before = document.Document.Sets[set].Mesh;
        after = mesh;

        Name = name;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public void Do(ICommandContext context) => Write(after);

    /// <inheritdoc />
    public void Undo(ICommandContext context) => Write(before);

    /// <inheritdoc />
    /// <inheritdoc cref="SetModelCommand.TryMergeWith" path="/remarks" />
    public bool TryMergeWith(IAuthoringCommand previous, [NotNullWhen(true)] out IAuthoringCommand? merged) {
        merged = null;

        return false;
    }

    /// <summary>Puts one set back with a different mesh, leaving the others as they are.</summary>
    void Write(string mesh) {
        var sets = document.Document.Sets.ToList();

        sets[set] = sets[set] with { Mesh = mesh };
        document.Document = document.Document with { Sets = sets };
    }
}

/// <summary>A layer moved past its neighbour, as one undo entry.</summary>
/// <remarks>
///     <para>
///         <b>
///             What <a href="https://github.com/Rikarin/Vixen/issues/819">#819</a> names first, and the
///             one edit whose effect is not a number.
///         </b>
///         A reorder changes which layer's operator reads
///         which backdrop, so what it produces is a different <em>graph</em> — which is why the test
///         that covers it compares two compiled plans rather than two lists.
///     </para>
///     <para>
///         ⚠ <b>It never merges, and that is a decision rather than the default.</b> Two presses of
///         the same button are two edits: an artist who moved a layer up twice and pressed undo means
///         to be one step down, not back where they started. That is the opposite answer from the
///         opacity slider next to it, and the two sitting in one file is the clearest place to say
///         why they differ — a drag is one gesture reported many times, a reorder is many gestures.
///     </para>
/// </remarks>
public sealed class MoveLayerCommand : IAuthoringCommand {
    readonly ILayerStackDocument document;
    readonly LayerPath path;
    readonly int delta;

    /// <summary>Records a move.</summary>
    /// <param name="document">The stack it happens in.</param>
    /// <param name="path">Which layer.</param>
    /// <param name="delta">How far in composite order. <c>+1</c> is one step later.</param>
    /// <param name="name">What the undo entry says.</param>
    /// <exception cref="ArgumentNullException"><paramref name="document" /> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="name" /> is empty, or the move is nowhere.</exception>
    public MoveLayerCommand(ILayerStackDocument document, LayerPath path, int delta, string name) {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrEmpty(name);

        if (delta == 0) {
            throw new ArgumentException(
                "A move of nothing is not an undo entry; ask CanMove before making one.",
                nameof(delta)
            );
        }

        this.document = document;
        this.path = path;
        this.delta = delta;

        Name = name;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <summary>Whether a layer could move that far in the list it is in.</summary>
    /// <param name="stack">The stack.</param>
    /// <param name="path">Which layer.</param>
    /// <param name="delta">How far in composite order.</param>
    /// <returns>Whether the move would do anything.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stack" /> is null.</exception>
    /// <remarks>
    ///     What a panel disables the button from, so that the top row's <em>up</em> is greyed rather
    ///     than pressed to no effect — and the same question <see cref="Do" /> would answer with a
    ///     silent no.
    /// </remarks>
    public static bool CanMove(LayerStackAsset stack, LayerPath path, int delta) {
        if (delta == 0 || !LayerStackEdit.TryFind(stack, path, out var parent, out var index)) {
            return false;
        }

        var target = index + delta;

        return target >= 0 && target < parent.Count;
    }

    /// <inheritdoc />
    public void Do(ICommandContext context) => LayerStackEdit.Move(document.Document, path, delta);

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠
    ///     <b>
    ///         The opposite delta and not a remembered index, which is only the same thing because
    ///         the move stays inside one list.
    ///     </b>
    ///     Removing at <c>i</c> and inserting at <c>i + d</c>
    ///     shifts everything between them by one step the other way, and putting the layer back at
    ///     <c>i</c> restores every one of them — so the inverse of a move is a move. A command that
    ///     could also change a layer's parent would need the whole position rather than the offset.
    /// </remarks>
    public void Undo(ICommandContext context) => LayerStackEdit.Move(document.Document, path, -delta);

    /// <inheritdoc />
    /// <remarks>Never. See the type's remarks.</remarks>
    public bool TryMergeWith(IAuthoringCommand previous, [NotNullWhen(true)] out IAuthoringCommand? merged) {
        merged = null;

        return false;
    }
}

/// <summary>A layer put into a stack, as one undo entry.</summary>
/// <remarks>
///     <para>
///         <b><a href="https://github.com/Rikarin/Vixen/issues/882">#882</a>'s other half.</b> The
///         source editor landed and add and delete did not, and the issue says why they are one
///         change: <see cref="LayerStackEdit" /> already finds a layer's parent list by id, so an
///         insert is the same shape as <see cref="MoveLayerCommand" /> — what it needed was a way to
///         name a <em>place</em>, which is <see cref="LayerSlot" />.
///     </para>
///     <para>
///         ⚠ <b>The undo removes by the id it inserted rather than by the slot it inserted at.</b>
///         Those differ the moment anything else moves: a layer added at index 2 and then dragged to
///         the top is still the layer this command put there, and an undo that emptied index 2 would
///         delete somebody else's. It is the same argument <see cref="LayerPath" /> makes about
///         indices, applied to the one command that has to hold one.
///     </para>
///     <para>
///         ⚠
///         <b>
///             Nothing here makes the id unique — <see cref="LayerStackEdit.FreeId" /> does, at the
///             call site.
///         </b>
///         A command that generated one would generate it again on every redo, and a
///         redo that produced a different id would orphan every anchor an artist had pointed at the
///         layer in between.
///     </para>
/// </remarks>
public sealed class AddLayerCommand : IAuthoringCommand {
    readonly ILayerStackDocument document;
    readonly LayerSlot slot;
    readonly LayerAsset layer;

    /// <summary>Records an insertion.</summary>
    /// <param name="document">The stack it happens in.</param>
    /// <param name="slot">Where the layer goes.</param>
    /// <param name="layer">The layer, id and all.</param>
    /// <param name="name">What the undo entry says.</param>
    /// <exception cref="ArgumentNullException">The document or the layer is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="name" /> is empty.</exception>
    public AddLayerCommand(ILayerStackDocument document, LayerSlot slot, LayerAsset layer, string name) {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentException.ThrowIfNullOrEmpty(name);

        this.document = document;
        this.slot = slot;
        this.layer = layer;

        Name = name;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public void Do(ICommandContext context) => LayerStackEdit.Insert(document.Document, slot, layer);

    /// <inheritdoc />
    public void Undo(ICommandContext context) =>
        LayerStackEdit.Remove(document.Document, new(slot.Set, layer.Id), out _);

    /// <inheritdoc />
    /// <inheritdoc cref="SetModelCommand.TryMergeWith" path="/remarks" />
    public bool TryMergeWith(IAuthoringCommand previous, [NotNullWhen(true)] out IAuthoringCommand? merged) {
        merged = null;

        return false;
    }
}

/// <summary>A layer taken out of a stack, as one undo entry.</summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             Where it was is resolved when it is removed, not when the command is built, and that
///             is what makes a redo land in the right place.
///         </b>
///         A command is built once and can run many
///         times — undo, then some other edit, then redo — so an index recorded at construction is an
///         index the stack has since renumbered. Every other command here keys on something stable; a
///         removal cannot, because the thing it names stops existing.
///     </para>
///     <para>
///         ⚠ <b>A group takes its children with it and gives them back.</b> The record that comes out
///         holds <see cref="LayerAsset.Children" />, so what this command holds is the subtree rather
///         than a stump — an undo re-inserts the whole of it with one call and nothing walks.
///     </para>
///     <para>
///         ⚠ <b>An anchor pointing at the deleted layer is left dangling on purpose.</b>
///         <c>LayerStackGraph</c> refuses an anchor that names no layer and says so in the list under
///         the rows; rewriting other layers' masks from a delete would be an edit an artist did not
///         ask for, inside an undo entry that says "Delete Layer".
///     </para>
/// </remarks>
public sealed class RemoveLayerCommand : IAuthoringCommand {
    readonly ILayerStackDocument document;
    readonly LayerPath path;
    LayerSlot slot;
    LayerAsset? removed;

    /// <summary>Records a removal.</summary>
    /// <param name="document">The stack it happens in.</param>
    /// <param name="path">Which layer.</param>
    /// <param name="name">What the undo entry says.</param>
    /// <exception cref="ArgumentNullException"><paramref name="document" /> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="name" /> is empty.</exception>
    public RemoveLayerCommand(ILayerStackDocument document, LayerPath path, string name) {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrEmpty(name);

        this.document = document;
        this.path = path;

        Name = name;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public void Do(ICommandContext context) {
        removed = LayerStackEdit.Find(document.Document, path);

        if (!LayerStackEdit.Remove(document.Document, path, out slot)) {
            removed = null;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Nothing happens when the removal did not, which is the state a redo of a command whose
    ///     layer somebody else has already deleted is in — an insert of <see langword="null" /> would
    ///     be a hole in the list rather than something a person could act on.
    /// </remarks>
    public void Undo(ICommandContext context) {
        if (removed is not null) {
            LayerStackEdit.Insert(document.Document, slot, removed);
        }
    }

    /// <inheritdoc />
    /// <inheritdoc cref="SetModelCommand.TryMergeWith" path="/remarks" />
    public bool TryMergeWith(IAuthoringCommand previous, [NotNullWhen(true)] out IAuthoringCommand? merged) {
        merged = null;

        return false;
    }
}
