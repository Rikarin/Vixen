// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Editor.Core;
using Vixen.Foliage;

namespace Vixen.Editor.Terrain;

/// <summary>
///     One foliage stroke as one entry in the undo history.
/// </summary>
/// <remarks>
///     <para>
///         <b>The third stroke command, and the one whose record is instances rather than a
///         rectangle.</b> A sculpt stroke holds a rect of deltas and a paint stroke holds a rect of
///         weights, because both write a grid; a foliage stroke writes a list, so what it holds is
///         what it added and what it took away.
///     </para>
///     <para>
///         ⚠ <b>Redo re-adds rather than re-scattering, and that is not the same thing.</b> The
///         scatter is deterministic from its seed, so re-running it would produce the same trees — but
///         only if nothing else changed in between, and an undo stack does not promise that. Somebody
///         who erased a clearing, undid it, then undid the stroke before it, has changed what the
///         spacing rejection sees. Holding the instances is the only version that is exact.
///     </para>
///     <para>
///         ⚠ <b>Addresses do not survive the round trip and the command does not pretend they do.</b>
///         Undoing a stroke removes what it added, which shifts every index after it; the re-add
///         produces new addresses. So the command works in instances and the editor re-resolves its
///         selection after any edit.
///     </para>
/// </remarks>
public sealed class FoliageStrokeCommand : IEditorCommand {
    readonly FoliageVolume volume;
    readonly FoliageInstance[] added;
    readonly int[] addedTypes;
    readonly FoliageInstance[] removed;
    readonly int[] removedTypes;
    readonly Action<FoliageVolume>? changed;

    /// <summary>Records an applied stroke.</summary>
    /// <param name="volume">The volume.</param>
    /// <param name="placed">What it added, by address, read before anything else moved.</param>
    /// <param name="erased">What it took away.</param>
    /// <param name="erasedTypes">Which type each of those was.</param>
    /// <param name="name">What the undo entry says.</param>
    /// <param name="changed">Told when the instances move.</param>
    /// <exception cref="ArgumentException">The erased instances and their types do not match up.</exception>
    public FoliageStrokeCommand(
        FoliageVolume volume,
        IReadOnlyList<FoliageAddress> placed,
        IReadOnlyList<FoliageInstance> erased,
        IReadOnlyList<int> erasedTypes,
        string name,
        Action<FoliageVolume>? changed = null
    ) {
        ArgumentNullException.ThrowIfNull(volume);
        ArgumentNullException.ThrowIfNull(placed);
        ArgumentNullException.ThrowIfNull(erased);
        ArgumentNullException.ThrowIfNull(erasedTypes);

        if (erased.Count != erasedTypes.Count) {
            throw new ArgumentException(
                $"{erased.Count} erased instances came with {erasedTypes.Count} types.",
                nameof(erasedTypes)
            );
        }

        this.volume = volume;
        this.changed = changed;

        // ⚠ Resolved to instances *now*, while the addresses are still valid. By the time an undo
        // runs, everything after them has shifted.
        added = new FoliageInstance[placed.Count];
        addedTypes = new int[placed.Count];

        for (var index = 0; index < placed.Count; index++) {
            added[index] = volume.At(placed[index]) ?? default;
            addedTypes[index] = placed[index].Type;
        }

        removed = [.. erased];
        removedTypes = [.. erasedTypes];

        Name = name;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <summary>How many instances the stroke added.</summary>
    public int Added => added.Length;

    /// <summary>And how many it took away.</summary>
    public int Removed => removed.Length;

    /// <inheritdoc />
    public void Do(EditorContext context) {
        Take(removed, removedTypes);
        Give(added, addedTypes);

        changed?.Invoke(volume);
    }

    /// <inheritdoc />
    public void Undo(EditorContext context) {
        Take(added, addedTypes);
        Give(removed, removedTypes);

        changed?.Invoke(volume);
    }

    void Give(FoliageInstance[] instances, int[] types) {
        for (var index = 0; index < instances.Length; index++) {
            volume.Add(types[index], instances[index]);
        }
    }

    /// <summary>Removes instances by matching them, because their addresses have moved.</summary>
    /// <remarks>
    ///     ⚠ <b>By position, and the position is exact.</b> An instance was written by this command
    ///     and read back unchanged, so a float comparison is not a tolerance question — and matching
    ///     by <em>nearest</em> would remove somebody else's tree when two of them coincide, which is
    ///     what happens on the boundary between two strokes.
    /// </remarks>
    void Take(FoliageInstance[] instances, int[] types) {
        for (var index = 0; index < instances.Length; index++) {
            var wanted = instances[index];
            var chunk = volume.ChunkOf(types[index], volume.Grid.CellOf(wanted.Position));

            if (chunk is null) {
                continue;
            }

            for (var at = 0; at < chunk.Count; at++) {
                if (chunk.Instances[at].Position == wanted.Position
                    && chunk.Instances[at].Scale == wanted.Scale) {
                    volume.Remove([new(types[index], chunk.Cell, at)]);
                    break;
                }
            }
        }
    }
}

/// <summary>Moving selected instances, as one entry.</summary>
/// <remarks>
///     ⚠ <b>A move can re-cell, so the addresses change and the command hands the new ones back.</b>
///     A gizmo still holding the old ones would move a different tree on the next drag — which is the
///     failure that looks like the gizmo drifting.
/// </remarks>
public sealed class FoliageMoveCommand : IEditorCommand {
    readonly FoliageVolume volume;
    readonly FoliageInstance[] before;
    readonly int[] types;
    readonly Vector3 offset;
    readonly Action<IReadOnlyList<FoliageAddress>>? rebound;

    FoliageAddress[] addresses;

    /// <summary>Records a move that has not been applied yet.</summary>
    /// <param name="volume">The volume.</param>
    /// <param name="addresses">What is selected.</param>
    /// <param name="before">Where each of them was.</param>
    /// <param name="offset">How far to move them.</param>
    /// <param name="rebound">Handed the addresses after each apply, so a gizmo can follow.</param>
    public FoliageMoveCommand(
        FoliageVolume volume,
        FoliageAddress[] addresses,
        FoliageInstance[] before,
        Vector3 offset,
        Action<IReadOnlyList<FoliageAddress>>? rebound = null
    ) {
        ArgumentNullException.ThrowIfNull(volume);
        ArgumentNullException.ThrowIfNull(addresses);
        ArgumentNullException.ThrowIfNull(before);

        if (addresses.Length != before.Length) {
            throw new ArgumentException(
                $"{addresses.Length} addresses came with {before.Length} instances.",
                nameof(before)
            );
        }

        this.volume = volume;
        this.addresses = addresses;
        this.before = before;
        this.offset = offset;
        this.rebound = rebound;

        types = [.. addresses.Select(address => address.Type)];
    }

    /// <inheritdoc />
    public string Name => "Move Foliage";

    /// <summary>How many instances moved.</summary>
    public int Count => addresses.Length;

    /// <inheritdoc />
    public void Do(EditorContext context) => Applied(offset);

    /// <inheritdoc />
    public void Undo(EditorContext context) => Applied(-offset);

    /// <remarks>
    ///     ⚠ <b>Moved one at a time and the addresses re-read after each</b>, because a move that
    ///     re-cells shifts the ones behind it in the old chunk. Applying them all against the
    ///     addresses taken at the start would move the first one and then four of its neighbours.
    /// </remarks>
    void Applied(Vector3 by) {
        var next = new FoliageAddress[addresses.Length];

        for (var index = 0; index < addresses.Length; index++) {
            var instance = volume.At(addresses[index]);

            if (instance is null) {
                next[index] = addresses[index];
                continue;
            }

            next[index] = volume.Move(
                addresses[index],
                instance.Value with { Position = instance.Value.Position + by }
            );

            // Everything after this one in the same chunk has shifted if the move re-celled.
            for (var later = index + 1; later < addresses.Length; later++) {
                if (addresses[later].Cell == addresses[index].Cell
                    && addresses[later].Type == addresses[index].Type
                    && addresses[later].Index > addresses[index].Index
                    && next[index].Cell != addresses[index].Cell) {
                    addresses[later] = addresses[later] with { Index = addresses[later].Index - 1 };
                }
            }
        }

        addresses = next;
        rebound?.Invoke(addresses);
    }

    /// <summary>Where the instances were before the move.</summary>
    internal IReadOnlyList<FoliageInstance> Before => before;

    /// <summary>Which types they are.</summary>
    internal IReadOnlyList<int> Types => types;
}
