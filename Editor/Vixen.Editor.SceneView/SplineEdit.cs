// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using Vixen.Core.Mathematics;
using Vixen.Editor.Core;

namespace Vixen.Editor.SceneView;

/// <summary>Which part of a control point a handle is.</summary>
public enum SplineElement : byte {
    /// <summary>The point itself.</summary>
    Point,

    /// <summary>The handle for the tangent the curve arrives on.</summary>
    TangentIn,

    /// <summary>And the one it leaves on.</summary>
    TangentOut
}

/// <summary>One draggable thing on a spline.</summary>
/// <param name="Point">Which control point.</param>
/// <param name="Element">Which part of it.</param>
public readonly record struct SplineHandle(int Point, SplineElement Element);

/// <summary>
///     Editing a spline in the viewport: pick a handle, drag it through the gizmo, insert on the
///     curve, delete, join and split.
/// </summary>
/// <remarks>
///     <para>
///         <b>[docs/plan/31 § T8]'s viewport editing, on the gizmo and <see cref="SnapContext" />
///         that already exist.</b> A control point is a position and nothing else, so dragging one is
///         what <see cref="IGizmoTarget" /> already does — which is why this is a target and a
///         command rather than a mode.
///     </para>
///     <para>
///         ⚠ <b>Tangent handles are selectable in their own right, and they have to be.</b> A tangent
///         is where the curve leaves a point, and the only way to author a corner is to move one of
///         them without the other — so a handle set that held only positions could express a smooth
///         road and nothing else.
///     </para>
///     <para>
///         ⚠ <b>The undo record is the whole point list.</b> A stroke on a heightfield records a rect
///         because a terrain is megabytes; a spline is a hundred points and about three kilobytes, so
///         the machinery that makes a rect worth having buys nothing here and costs a class of bug —
///         an edit that inserts or removes a point changes every index after it, which a
///         per-point delta would have to reason about and a whole-list snapshot does not.
///     </para>
/// </remarks>
public sealed class SplineEdit {
    readonly HashSet<SplineHandle> selection = [];

    SplinePoint[]? started;

    /// <summary>Which spline is being edited.</summary>
    public SplineAsset? Asset { get; set; }

    /// <summary>What snapping applies to a drag, or none.</summary>
    public SnapContext? Snapping { get; set; }

    /// <summary>How close a pick has to be to a handle, in metres.</summary>
    /// <remarks>
    ///     World metres rather than screen pixels, because this class has no viewport — a pane that
    ///     wants a pixel radius converts at the point it knows the distance to the camera.
    /// </remarks>
    public float PickRadius { get; set; } = 0.5f;

    /// <summary>How far a tangent handle sits from its point, as a fraction of the tangent.</summary>
    /// <remarks>
    ///     One, so the handle is exactly at the tangent's tip. It is a setting because a road whose
    ///     tangents are forty metres long puts its handles off screen, and halving them is the usual
    ///     answer — but the default is the honest one, where the handle is the value.
    /// </remarks>
    public float HandleScale { get; set; } = 1f;

    /// <summary>Which handles are selected.</summary>
    public IReadOnlyCollection<SplineHandle> Selection => selection;

    /// <summary>Whether anything is selected.</summary>
    public bool HasSelection => selection.Count > 0;

    /// <summary>Where a handle is, in world space.</summary>
    /// <param name="handle">Which handle.</param>
    /// <returns>The position, or null if there is no such handle.</returns>
    public Vector3? PositionOf(SplineHandle handle) {
        if (Asset is null || handle.Point < 0 || handle.Point >= Asset.Count) {
            return null;
        }

        var point = Asset[handle.Point];

        return handle.Element switch {
            SplineElement.TangentIn => point.Position + (point.TangentIn * HandleScale),
            SplineElement.TangentOut => point.Position + (point.TangentOut * HandleScale),
            _ => point.Position
        };
    }

    /// <summary>Selects the handle nearest a point, if one is close enough.</summary>
    /// <param name="at">Where the pointer landed, in world space.</param>
    /// <param name="add">Whether to add to the selection rather than replace it.</param>
    /// <returns>What was picked, or null.</returns>
    /// <remarks>
    ///     ⚠ <b>Tangent handles win ties against the point they belong to.</b> A tangent of zero
    ///     length sits exactly on its point, and a pick that preferred the point would make a corner
    ///     authored by collapsing a tangent impossible to undo — the handle would be unreachable
    ///     precisely once it mattered.
    /// </remarks>
    public SplineHandle? Pick(Vector3 at, bool add = false) {
        if (Asset is null) {
            return null;
        }

        var best = default(SplineHandle);
        var found = false;
        var nearest = PickRadius * PickRadius;

        for (var index = 0; index < Asset.Count; index++) {
            foreach (var element in (ReadOnlySpan<SplineElement>)[
                SplineElement.TangentIn, SplineElement.TangentOut, SplineElement.Point
            ]) {
                var handle = new SplineHandle(index, element);
                var position = PositionOf(handle);

                if (position is null) {
                    continue;
                }

                var distance = Vector3.DistanceSquared(position.Value, at);

                // Strictly nearer, and tangents are tested first — so a tangent sitting exactly on
                // its point keeps the tie.
                if (distance < nearest) {
                    nearest = distance;
                    best = handle;
                    found = true;
                }
            }
        }

        if (!found) {
            if (!add) {
                selection.Clear();
            }

            return null;
        }

        if (!add) {
            selection.Clear();
        }

        selection.Add(best);

        return best;
    }

    /// <summary>Deselects everything.</summary>
    public void Deselect() => selection.Clear();

    /// <summary>Takes a snapshot, so the drag that follows becomes one undo entry.</summary>
    public void Begin() => started = Asset is null ? null : [.. Asset.Points];

    /// <summary>Moves every selected handle by a world-space delta.</summary>
    /// <param name="delta">How far.</param>
    /// <param name="mirror">Whether moving one tangent handle mirrors the other.</param>
    /// <remarks>
    ///     ⚠ <b>Moving a point carries its tangents; moving a tangent handle does not move the
    ///     point.</b> They are offsets from the point, so the first is free and the second is the
    ///     whole difference between bending a road and moving it.
    /// </remarks>
    public void Move(Vector3 delta, bool mirror = true) {
        if (Asset is null || selection.Count == 0) {
            return;
        }

        foreach (var handle in selection) {
            if (handle.Point < 0 || handle.Point >= Asset.Count) {
                continue;
            }

            var point = Asset[handle.Point];
            var scale = MathF.Max(HandleScale, 1e-3f);

            switch (handle.Element) {
                case SplineElement.TangentIn:
                    Asset.SetTangentIn(handle.Point, point.TangentIn + (delta / scale), mirror);
                    break;

                case SplineElement.TangentOut:
                    Asset.SetTangentOut(handle.Point, point.TangentOut + (delta / scale), mirror);
                    break;

                default:
                    Asset.MoveTo(handle.Point, Snap(point.Position + delta));
                    break;
            }
        }
    }

    /// <summary>Appends a control point at the end of the path and selects it.</summary>
    /// <param name="at">Where, in world space.</param>
    /// <returns>Its index, or −1 if there is no asset.</returns>
    public int Append(Vector3 at) {
        if (Asset is null) {
            return -1;
        }

        var index = Asset.Add(SplinePoint.At(Snap(at)));

        // Tangents from the neighbours, so a path drawn point by point is smooth as it is drawn
        // rather than a polyline the author has to smooth afterwards.
        Asset.Smooth();

        selection.Clear();
        selection.Add(new(index, SplineElement.Point));

        return index;
    }

    /// <summary>Inserts a control point on the curve nearest a place, without changing its shape.</summary>
    /// <param name="near">Where, in world space.</param>
    /// <returns>Its index, or −1 if there was nowhere to insert.</returns>
    public int InsertOn(Vector3 near) {
        if (Asset is not { CanBuild: true }) {
            return -1;
        }

        Asset.Build().DistanceTo(near, out var parameter);

        var index = Asset.InsertOn(parameter);

        if (index >= 0) {
            selection.Clear();
            selection.Add(new(index, SplineElement.Point));
        }

        return index;
    }

    /// <summary>Removes every selected control point.</summary>
    /// <returns>How many went.</returns>
    /// <remarks>
    ///     ⚠ <b>Descending, so the indices below a removal do not shift under it.</b> The same trap
    ///     <c>FoliageVolume.Remove</c> guards against, one subsystem over — and here the selection is
    ///     a set of indices, so a caller cannot even hand them over in a safe order by accident.
    /// </remarks>
    public int Delete() {
        if (Asset is null) {
            return 0;
        }

        var points = selection
            .Where(handle => handle.Element == SplineElement.Point)
            .Select(handle => handle.Point)
            .Distinct()
            .OrderByDescending(index => index)
            .ToArray();

        var removed = 0;

        foreach (var index in points) {
            if (Asset.RemoveAt(index)) {
                removed++;
            }
        }

        selection.Clear();

        return removed;
    }

    /// <summary>Ends the drag, as one entry for the undo stack.</summary>
    /// <returns>The command, or null if nothing changed.</returns>
    public SplineCommand? Commit() {
        if (Asset is null || started is null) {
            return null;
        }

        var before = started;
        var after = Asset.Points.ToArray();

        started = null;

        if (before.AsSpan().SequenceEqual(after)) {
            return null;
        }

        return new(Asset, before, after);
    }

    /// <summary>Abandons the drag, putting the points back.</summary>
    public void Cancel() {
        if (Asset is null || started is null) {
            return;
        }

        Restore(Asset, started);
        started = null;
    }

    /// <summary>Puts a control point list back into an asset.</summary>
    /// <param name="asset">The asset.</param>
    /// <param name="points">The points.</param>
    internal static void Restore(SplineAsset asset, ReadOnlySpan<SplinePoint> points) {
        asset.Clear();

        foreach (var point in points) {
            asset.Add(point);
        }
    }

    Vector3 Snap(Vector3 position) => Snapping is { } snapping ? snapping.Position(position) : position;
}

/// <summary>One spline edit, as something the undo stack can take back.</summary>
/// <remarks>
///     ⚠ <b>The whole point list, before and after.</b> An edit that inserts or removes a point moves
///     every index after it, so a per-point record would have to reason about that; a snapshot of a
///     hundred points is three kilobytes and reasons about nothing. It is the same trade
///     <c>TerrainStrokeCommand</c> makes in the other direction, for the other reason.
/// </remarks>
public sealed class SplineCommand : IEditorCommand {
    readonly SplineAsset asset;
    readonly SplinePoint[] before;
    readonly SplinePoint[] after;

    /// <summary>Records an edit that has already been applied to the asset.</summary>
    /// <param name="asset">Which spline.</param>
    /// <param name="before">Its points before.</param>
    /// <param name="after">And after.</param>
    public SplineCommand(SplineAsset asset, SplinePoint[] before, SplinePoint[] after) {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        this.asset = asset;
        this.before = before;
        this.after = after;
    }

    /// <inheritdoc />
    public string Name => before.Length == after.Length ? "Move Spline Points" : "Edit Spline";

    /// <inheritdoc />
    public void Do(EditorContext context) => SplineEdit.Restore(asset, after);

    /// <inheritdoc />
    public void Undo(EditorContext context) => SplineEdit.Restore(asset, before);

    /// <inheritdoc />
    /// <remarks>
    ///     Two drags of the same asset that neither inserted nor removed a point become one entry —
    ///     which is what makes nudging a control point with the arrow keys one undo rather than
    ///     thirty. An edit that changed the <em>count</em> never merges: inserting a point and then
    ///     moving it are two things an author did, and undoing them together loses the insertion.
    /// </remarks>
    public bool TryMergeWith(IEditorCommand previous, [NotNullWhen(true)] out IEditorCommand? merged) {
        merged = null;

        if (previous is not SplineCommand earlier
            || !ReferenceEquals(earlier.asset, asset)
            || earlier.before.Length != after.Length
            || before.Length != after.Length) {
            return false;
        }

        merged = new SplineCommand(asset, earlier.before, after);

        return true;
    }
}
