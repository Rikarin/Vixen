// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using Vixen.Editor.Core;
using Vixen.Terrain;
using TerrainMap = Vixen.Terrain.Terrain;

namespace Vixen.Editor.Terrain;

/// <summary>
///     Adding, removing and reordering an edit layer, as one undo entry each.
/// </summary>
/// <remarks>
///     <para>
///         <b>The edit-layer stack panel's verbs — [docs/plan/31 § The terrain panel].</b> Every one
///         of them changes what the composite is, so every one of them invalidates the whole terrain:
///         reordering commutes for the sums and <em>not</em> for the clamp at the top of the height
///         range, which is <see cref="TerrainMap.MoveLayer" />'s own remark and is why none of these
///         tries to be clever about which tiles moved.
///     </para>
///     <para>
///         ⚠ <b>A removed layer is held by its command, not rebuilt from a name.</b>
///         <see cref="TerrainMap.AddLayer" /> makes an empty layer, so an undo built on it would put
///         back the name and none of the ground. <see cref="TerrainMap.InsertLayer" /> exists for
///         this and nothing else.
///     </para>
/// </remarks>
public static class TerrainLayerCommands {
    /// <summary>Adds a layer on top of the stack.</summary>
    /// <param name="terrain">The terrain.</param>
    /// <param name="name">What it is called.</param>
    /// <returns>The command, and the layer it will add.</returns>
    public static (IEditorCommand Command, TerrainEditLayer Layer) Add(TerrainMap terrain, string name) {
        ArgumentNullException.ThrowIfNull(terrain);

        var layer = new TerrainEditLayer(terrain.Description, name);

        return (new InsertLayerCommand(terrain, layer, terrain.Layers.Count, "Add Terrain Layer"), layer);
    }

    /// <summary>Removes a layer and everything on it.</summary>
    /// <param name="terrain">The terrain.</param>
    /// <param name="layer">Which layer.</param>
    /// <returns>The command.</returns>
    /// <exception cref="ArgumentException">The layer is not in this terrain.</exception>
    public static IEditorCommand Remove(TerrainMap terrain, TerrainEditLayer layer) {
        ArgumentNullException.ThrowIfNull(terrain);

        var index = terrain.IndexOf(layer);

        if (index < 0) {
            throw new ArgumentException("The layer is not in this terrain.", nameof(layer));
        }

        return new RemoveLayerCommand(terrain, layer, index);
    }

    /// <summary>Copies a layer, deltas and all, and puts the copy above it.</summary>
    /// <param name="terrain">The terrain.</param>
    /// <param name="layer">Which layer.</param>
    /// <returns>The command, and the copy it will add.</returns>
    public static (IEditorCommand Command, TerrainEditLayer Layer) Duplicate(
        TerrainMap terrain,
        TerrainEditLayer layer
    ) {
        ArgumentNullException.ThrowIfNull(terrain);
        ArgumentNullException.ThrowIfNull(layer);

        var index = terrain.IndexOf(layer);

        if (index < 0) {
            throw new ArgumentException("The layer is not in this terrain.", nameof(layer));
        }

        var copy = layer.Clone();
        copy.Name = layer.Name + " Copy";

        return (new InsertLayerCommand(terrain, copy, index + 1, "Duplicate Terrain Layer"), copy);
    }

    /// <summary>Moves a layer up or down the stack.</summary>
    /// <param name="terrain">The terrain.</param>
    /// <param name="from">Where it is.</param>
    /// <param name="to">Where it goes.</param>
    /// <returns>The command.</returns>
    public static IEditorCommand Move(TerrainMap terrain, int from, int to) {
        ArgumentNullException.ThrowIfNull(terrain);

        return new MoveLayerCommand(terrain, from, to);
    }

    /// <summary>Empties a layer without removing it.</summary>
    /// <param name="terrain">The terrain.</param>
    /// <param name="layer">Which layer.</param>
    /// <returns>The command.</returns>
    public static IEditorCommand Clear(TerrainMap terrain, TerrainEditLayer layer) =>
        new ReplaceLayerCommand(terrain, layer, Empty(layer), "Clear Terrain Layer");

    /// <summary>Adds a layer's deltas into the one below it and drops the layer.</summary>
    /// <param name="terrain">The terrain.</param>
    /// <param name="index">Which layer. Must not be the bottom one.</param>
    /// <returns>The command.</returns>
    /// <remarks>
    ///     ⚠ <b>The undo puts back <em>two</em> layers' worth of state, because the operation
    ///     destroys both.</b> The upper one is gone and the lower one has been added to, and the
    ///     addition has no inverse: the lower layer may have held something at every sample the
    ///     upper one touched. So the command holds a clone of the lower layer taken before, which is
    ///     what <see cref="TerrainEditLayer.Clone" /> is for.
    /// </remarks>
    public static IEditorCommand Collapse(TerrainMap terrain, int index) {
        ArgumentNullException.ThrowIfNull(terrain);
        ArgumentOutOfRangeException.ThrowIfLessThan(index, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, terrain.Layers.Count);

        return new CollapseLayerCommand(terrain, index);
    }

    /// <summary>Renames a layer.</summary>
    /// <param name="terrain">The terrain.</param>
    /// <param name="layer">Which layer.</param>
    /// <param name="name">Its new name.</param>
    /// <returns>The command.</returns>
    public static IEditorCommand Rename(TerrainMap terrain, TerrainEditLayer layer, string name) =>
        new LayerPropertyCommand(
            terrain,
            layer,
            nameof(TerrainEditLayer.Name),
            "Rename Terrain Layer",
            target => target.Name,
            (target, value) => target.Name = (string)value!,
            name,
            recomposites: false
        );

    /// <summary>Shows or hides a layer.</summary>
    /// <param name="terrain">The terrain.</param>
    /// <param name="layer">Which layer.</param>
    /// <param name="visible">Whether it contributes.</param>
    /// <returns>The command.</returns>
    public static IEditorCommand SetVisible(TerrainMap terrain, TerrainEditLayer layer, bool visible) =>
        new LayerPropertyCommand(
            terrain,
            layer,
            nameof(TerrainEditLayer.IsVisible),
            visible ? "Show Terrain Layer" : "Hide Terrain Layer",
            target => target.IsVisible,
            (target, value) => target.IsVisible = (bool)value!,
            visible
        );

    /// <summary>Locks a layer against the brush, or unlocks it.</summary>
    /// <param name="terrain">The terrain.</param>
    /// <param name="layer">Which layer.</param>
    /// <param name="locked">Whether it refuses edits.</param>
    /// <returns>The command.</returns>
    public static IEditorCommand SetLocked(TerrainMap terrain, TerrainEditLayer layer, bool locked) =>
        new LayerPropertyCommand(
            terrain,
            layer,
            nameof(TerrainEditLayer.IsLocked),
            locked ? "Lock Terrain Layer" : "Unlock Terrain Layer",
            target => target.IsLocked,
            (target, value) => target.IsLocked = (bool)value!,
            locked,
            recomposites: false
        );

    /// <summary>Changes how much of a layer's heights reach the composite.</summary>
    /// <param name="terrain">The terrain.</param>
    /// <param name="layer">Which layer.</param>
    /// <param name="alpha">The new alpha. Signed; 1 is unchanged.</param>
    /// <returns>The command.</returns>
    /// <remarks>
    ///     ⚠ <b>This one merges, and the two above do not.</b> A slider drag is three hundred
    ///     changes and one edit; a visibility toggle is one of each. That is
    ///     <see cref="IEditorCommand.TryMergeWith" />'s whole purpose and the reason the default is
    ///     not to merge — a command type that has not thought about it should not be claiming two
    ///     operations are one.
    /// </remarks>
    public static IEditorCommand SetHeightAlpha(TerrainMap terrain, TerrainEditLayer layer, float alpha) =>
        new LayerPropertyCommand(
            terrain,
            layer,
            nameof(TerrainEditLayer.HeightAlpha),
            "Set Layer Height Alpha",
            target => target.HeightAlpha,
            (target, value) => target.HeightAlpha = (float)value!,
            alpha,
            merges: true
        );

    /// <summary>And how much of its paint does.</summary>
    /// <param name="terrain">The terrain.</param>
    /// <param name="layer">Which layer.</param>
    /// <param name="alpha">The new alpha, 0…1.</param>
    /// <returns>The command.</returns>
    public static IEditorCommand SetWeightAlpha(TerrainMap terrain, TerrainEditLayer layer, float alpha) =>
        new LayerPropertyCommand(
            terrain,
            layer,
            nameof(TerrainEditLayer.WeightAlpha),
            "Set Layer Weight Alpha",
            target => target.WeightAlpha,
            (target, value) => target.WeightAlpha = (float)value!,
            alpha,
            merges: true
        );

    static TerrainEditLayer Empty(TerrainEditLayer layer) {
        ArgumentNullException.ThrowIfNull(layer);

        return new(layer.Description, layer.Name, layer.Kind) {
            HeightAlpha = layer.HeightAlpha,
            WeightAlpha = layer.WeightAlpha,
            IsVisible = layer.IsVisible,
            IsLocked = layer.IsLocked
        };
    }
}

/// <summary>Puts a layer into the stack; undoing takes it back out.</summary>
sealed class InsertLayerCommand(TerrainMap terrain, TerrainEditLayer layer, int index, string name)
    : IEditorCommand {
    /// <inheritdoc />
    public string Name => name;

    /// <inheritdoc />
    public void Do(EditorContext context) {
        terrain.InsertLayer(index, layer);
        terrain.Resolve();
    }

    /// <inheritdoc />
    public void Undo(EditorContext context) {
        terrain.RemoveLayer(layer);
        terrain.Resolve();
    }
}

/// <summary>Takes one back out; undoing puts it back where it was.</summary>
sealed class RemoveLayerCommand(TerrainMap terrain, TerrainEditLayer layer, int index) : IEditorCommand {
    /// <inheritdoc />
    public string Name => "Remove Terrain Layer";

    /// <inheritdoc />
    public void Do(EditorContext context) {
        terrain.RemoveLayer(layer);
        terrain.Resolve();
    }

    /// <inheritdoc />
    public void Undo(EditorContext context) {
        terrain.InsertLayer(index, layer);
        terrain.Resolve();
    }
}

/// <summary>Moves a layer up or down the stack.</summary>
sealed class MoveLayerCommand(TerrainMap terrain, int from, int to) : IEditorCommand {
    /// <inheritdoc />
    public string Name => "Reorder Terrain Layers";

    /// <inheritdoc />
    public void Do(EditorContext context) {
        terrain.MoveLayer(from, to);
        terrain.Resolve();
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Not <c>MoveLayer(from, to)</c> reversed as <c>MoveLayer(to, from)</c> by accident.</b>
    ///     A remove-then-insert is its own inverse only when the arguments are swapped, and the two
    ///     spellings agree for adjacent layers and disagree for everything else — which is how a
    ///     reorder undo passes its first test and fails on a stack of four.
    /// </remarks>
    public void Undo(EditorContext context) {
        terrain.MoveLayer(to, from);
        terrain.Resolve();
    }
}

/// <summary>Swaps a layer's contents for another's, in place.</summary>
/// <remarks>
///     ⚠ <b>The object in the stack stays the object in the stack.</b> The panel's selection, the
///     stroke being recorded and the mode all hold the layer by reference, so a clear implemented as
///     remove-and-add would leave every one of them pointing at a layer nothing composites. What is
///     swapped is the contents.
/// </remarks>
sealed class ReplaceLayerCommand(TerrainMap terrain, TerrainEditLayer layer, TerrainEditLayer replacement, string name)
    : IEditorCommand {
    TerrainEditLayer previous = layer.Clone();

    /// <inheritdoc />
    public string Name => name;

    /// <inheritdoc />
    public void Do(EditorContext context) {
        previous = layer.Clone();
        Copy(replacement, layer);
        terrain.InvalidateAll();
        terrain.Resolve();
    }

    /// <inheritdoc />
    public void Undo(EditorContext context) {
        Copy(previous, layer);
        terrain.InvalidateAll();
        terrain.Resolve();
    }

    internal static void Copy(TerrainEditLayer from, TerrainEditLayer into) {
        into.Clear();

        foreach (var (chunkX, chunkZ) in from.OccupiedChunks()) {
            for (var z = 0; z < TerrainEditLayer.ChunkSize; z++) {
                for (var x = 0; x < TerrainEditLayer.ChunkSize; x++) {
                    var sampleX = (chunkX * TerrainEditLayer.ChunkSize) + x;
                    var sampleZ = (chunkZ * TerrainEditLayer.ChunkSize) + z;
                    var delta = from.DeltaAt(sampleX, sampleZ);

                    if (delta != 0) {
                        into.SetDelta(sampleX, sampleZ, delta);
                    }
                }
            }
        }

        into.HeightAlpha = from.HeightAlpha;
        into.WeightAlpha = from.WeightAlpha;
        into.IsVisible = from.IsVisible;
        into.IsLocked = from.IsLocked;
    }
}

/// <summary>Collapses a layer into the one below it, holding what both were.</summary>
sealed class CollapseLayerCommand : IEditorCommand {
    readonly TerrainMap terrain;
    readonly TerrainEditLayer upper;
    readonly TerrainEditLayer lower;
    readonly TerrainEditLayer lowerBefore;
    readonly int index;

    internal CollapseLayerCommand(TerrainMap terrain, int index) {
        this.terrain = terrain;
        this.index = index;

        upper = terrain.Layers[index];
        lower = terrain.Layers[index - 1];
        lowerBefore = lower.Clone();
    }

    /// <inheritdoc />
    public string Name => "Collapse Terrain Layer";

    /// <inheritdoc />
    public void Do(EditorContext context) {
        // Re-taken rather than trusted, because a redo runs after an undo has put the lower layer
        // back and after anything the artist did in between was itself undone.
        ReplaceLayerCommand.Copy(lower, lowerBefore);

        terrain.Collapse(index);
        terrain.Resolve();
    }

    /// <inheritdoc />
    public void Undo(EditorContext context) {
        ReplaceLayerCommand.Copy(lowerBefore, lower);
        terrain.InsertLayer(index, upper);
        terrain.Resolve();
    }
}

/// <summary>One settable thing about a layer, before and after.</summary>
sealed class LayerPropertyCommand(
    TerrainMap terrain,
    TerrainEditLayer layer,
    string property,
    string name,
    Func<TerrainEditLayer, object?> read,
    Action<TerrainEditLayer, object?> write,
    object? value,
    bool merges = false,
    bool recomposites = true
) : IEditorCommand {
    object? previous = read(layer);

    /// <inheritdoc />
    public string Name => name;

    /// <summary>Which layer this is about, for the merge check.</summary>
    internal TerrainEditLayer Layer => layer;

    /// <summary>And which of its properties.</summary>
    internal string Property => property;

    /// <summary>Whether the value it undoes to may be taken over by a later command.</summary>
    internal bool Merges => merges;

    /// <summary>What it was, so a merge can hand it forward.</summary>
    internal object? Previous {
        get => previous;
        set => previous = value;
    }

    /// <inheritdoc />
    public void Do(EditorContext context) => Applied(value);

    /// <inheritdoc />
    public void Undo(EditorContext context) => Applied(previous);

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>The merged command must undo to what <paramref name="previous" /> would have undone
    ///     to — the value before the drag started, not the value one frame ago.</b> That is
    ///     <see cref="IEditorCommand.TryMergeWith" />'s contract and the one thing easy to get
    ///     backwards: the receiver is the <em>new</em> command swallowing the old one.
    /// </remarks>
    public bool TryMergeWith(IEditorCommand previous, [NotNullWhen(true)] out IEditorCommand? merged) {
        merged = null;

        if (!merges
            || previous is not LayerPropertyCommand earlier
            || !earlier.Merges
            || !ReferenceEquals(earlier.Layer, layer)
            || !string.Equals(earlier.Property, property, StringComparison.Ordinal)) {
            return false;
        }

        this.previous = earlier.Previous;
        merged = this;

        return true;
    }

    void Applied(object? to) {
        write(layer, to);

        if (recomposites) {
            terrain.InvalidateAll();
            terrain.Resolve();
        }
    }
}
