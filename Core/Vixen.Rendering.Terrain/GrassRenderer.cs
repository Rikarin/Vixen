// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Foliage;
using Vixen.Rendering.Features;

namespace Vixen.Rendering.Terrain;

/// <summary>One grass type and the templates its levels are drawn with.</summary>
/// <param name="Type">The type.</param>
/// <param name="Levels">One draw template per level, nearest first.</param>
/// <param name="Distances">Where each level takes over, ascending, one fewer than the levels.</param>
/// <remarks>
///     <see cref="FoliageDraw" />'s shape, deliberately: a field of grass is culled and binned by the
///     same <see cref="InstanceCuller" />, so it is described the same way. Most grass has one level
///     and passes an empty <paramref name="Distances" />.
/// </remarks>
public readonly record struct GrassDraw(GrassType Type, DrawCommand[] Levels, float[] Distances);

/// <summary>What one resident cell contributed for one field.</summary>
/// <param name="Field">Which entry of the frame's field list.</param>
/// <param name="Cell">Which cell.</param>
/// <param name="Slot">Which ring slot holds its blades.</param>
/// <param name="First">Where its survivors begin in the frame's compacted buffer.</param>
/// <param name="Count">How many survived.</param>
/// <param name="Commands">One indirect command per level, at that level's index.</param>
public readonly record struct GrassBatch(
    int Field,
    FoliageCellKey Cell,
    int Slot,
    int First,
    int Count,
    DrawCommand[] Commands
);

/// <summary>
///     A field of grass: cells scattered as they come into range, culled per blade every frame.
/// </summary>
/// <remarks>
///     <para>
///         <b>[docs/plan/31 § T6] and [§ D8].</b> Nothing here is loaded and nothing here is saved. A
///         cell entering range is scattered into one of a fixed number of pooled buffers; a cell
///         leaving hands its buffer back; a cell coming back is re-scattered to the identical blades,
///         because <see cref="GrassScatter.Hash" /> depends on the cell coordinate and the slot and on
///         nothing else.
///     </para>
///     <para>
///         ⚠ <b>The scatter happens on entry and the cull happens every frame, and keeping those
///         apart is the whole shape of the feature.</b> Scattering per frame would probe the surface
///         for every blade of every cell every frame — which is the cost the ring exists to pay once —
///         and culling on entry would draw the far half of every cell for as long as it stayed
///         resident.
///     </para>
///     <para>
///         ⚠ <b>Residency is per cell and scatter is per cell per field.</b> One ring, because the
///         cell is the unit of everything foliage does and a ring per field would probe the same
///         ground several times; but a field with a 20 m cull distance does not scatter into a cell
///         held resident by a field with an 80 m one, or the short field would pay for the long
///         field's range.
///     </para>
///     <para>
///         ⚠ <b>This is the CPU half.</b> <c>GrassScatter.rvn</c> is the dispatch that mirrors it, and
///         <c>GrassScatterParityTests</c> is what holds the two together — the same arrangement
///         <see cref="FoliageRenderer" /> and <see cref="InstanceCuller" /> have, for the same reason:
///         a scatter fails silently in both directions.
///     </para>
/// </remarks>
public sealed class GrassRenderer {
    readonly InstanceCuller culler = new();
    readonly List<GrassBatch> batches = [];
    readonly List<Matrix4x4> transforms = [];
    readonly List<InstanceParameters> parameters = [];
    readonly List<InstanceBounds> bounds = [];
    readonly List<InstanceParameters> source = [];

    // [slot][field]. Rectangular and allocated once, because the ring is fixed and the field list is
    // the frame's — a jagged structure keyed by cell would allocate on every cell that enters range,
    // which is the allocation a ring exists to remove.
    List<GrassBlade>[,] blades;
    BoundingBox[,] extents;

    /// <summary>Creates a renderer with a ring of a fixed size.</summary>
    /// <param name="grid">The cell grid grass is scattered over.</param>
    /// <param name="capacity">How many cells may hold blades at once.</param>
    /// <param name="fields">How many grass types the ring makes room for per cell.</param>
    /// <exception cref="ArgumentOutOfRangeException">The capacity or the field count is not positive.</exception>
    public GrassRenderer(FoliageCellGrid grid, int capacity = 256, int fields = 4) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fields);

        Residency = new(grid, capacity);
        FieldCapacity = fields;

        blades = new List<GrassBlade>[capacity, fields];
        extents = new BoundingBox[capacity, fields];

        Reset();
    }

    /// <summary>Which cells hold blades and which slot each of them is in.</summary>
    public GrassResidency Residency { get; }

    /// <summary>How many grass types the ring has room for.</summary>
    public int FieldCapacity { get; private set; }

    /// <summary>What each resident cell drew, in the order they were gathered.</summary>
    public IReadOnlyList<GrassBatch> Batches => batches;

    /// <summary>Every surviving blade's transform, compacted across every cell and field.</summary>
    public ReadOnlySpan<Matrix4x4> Transforms => CollectionsMarshal.AsSpan(transforms);

    /// <summary>And their parameters, at the same indices.</summary>
    public ReadOnlySpan<InstanceParameters> Parameters => CollectionsMarshal.AsSpan(parameters);

    /// <summary>How many blades the ring is holding.</summary>
    public int BladesResident { get; private set; }

    /// <summary>How many blades the last scatter produced.</summary>
    public int BladesScattered { get; private set; }

    /// <summary>How many it dropped.</summary>
    public int BladesDropped { get; private set; }

    /// <summary>How many cell-and-field pairs the last cull tested.</summary>
    public int CellsConsidered { get; private set; }

    /// <summary>How many survived the first stage.</summary>
    public int CellsDrawn { get; private set; }

    /// <summary>How many blades those cells held.</summary>
    public int BladesConsidered { get; private set; }

    /// <summary>And how many survived the second.</summary>
    public int BladesDrawn { get; private set; }

    /// <summary>How many indirect draws the frame issues, empty ones included.</summary>
    public int Draws { get; private set; }

    /// <summary>Brings the ring up to date and scatters whatever entered range.</summary>
    /// <param name="fields">The grass types this level has, in a stable order.</param>
    /// <param name="surface">What answers "what is the ground here".</param>
    /// <param name="viewPosition">Where the view is.</param>
    /// <param name="densityScale">A runtime scalar over every field, 0…1.</param>
    /// <returns>How many blades the ring now holds.</returns>
    /// <exception cref="ArgumentNullException">There are no fields or no surface.</exception>
    /// <exception cref="ArgumentException">There are more fields than the ring has room for.</exception>
    /// <remarks>
    ///     ⚠ <b>The residency range is the largest field's cull distance</b>, because one ring serves
    ///     every field. A field whose own range is shorter simply does not scatter into the far cells
    ///     — see <see cref="Wanted" /> — so the cost of the long field is paid by the long field.
    /// </remarks>
    public int Scatter(
        IReadOnlyList<GrassDraw> fields,
        IFoliageSurface surface,
        Vector3 viewPosition,
        float densityScale = 1f
    ) {
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(surface);

        if (fields.Count > FieldCapacity) {
            throw new ArgumentException(
                $"This ring has room for {FieldCapacity} fields per cell and {fields.Count} were "
                + "given. Grow the renderer rather than dropping the tail, because which field the "
                + "tail is depends on the order somebody happened to list them in.",
                nameof(fields)
            );
        }

        BladesScattered = 0;
        BladesDropped = 0;

        var range = 0f;

        foreach (var field in fields) {
            range = MathF.Max(range, field.Type.EndCullDistance);
        }

        var change = Residency.Update(viewPosition, range);

        foreach (var gone in change.Evicted) {
            for (var field = 0; field < FieldCapacity; field++) {
                BladesDropped += blades[gone.Slot, field].Count;
                Clear(gone.Slot, field);
            }
        }

        // Every resident cell, not only the ones that just arrived: a field whose own range the cell
        // has just come inside has to fill in, and one whose range it has just left has to empty.
        // Walking only `change.Created` would leave a near field permanently unscattered in a cell
        // that entered while it was still far away.
        var at = new Vector2(viewPosition.X, viewPosition.Z);

        foreach (var resident in Residency.Resident) {
            var distance = Residency.DistanceTo(resident.Cell, at);

            for (var field = 0; field < fields.Count; field++) {
                var type = fields[field].Type;
                var has = blades[resident.Slot, field].Count > 0;
                var wanted = Wanted(distance, type);

                if (wanted && !has) {
                    BladesScattered += Fill(resident, field, type, surface, densityScale);
                } else if (!wanted && has) {
                    BladesDropped += blades[resident.Slot, field].Count;
                    Clear(resident.Slot, field);
                }
            }

            // A field that was dropped from the list keeps nothing.
            for (var field = fields.Count; field < FieldCapacity; field++) {
                BladesDropped += blades[resident.Slot, field].Count;
                Clear(resident.Slot, field);
            }
        }

        BladesResident += BladesScattered - BladesDropped;

        return BladesResident;
    }

    /// <summary>Culls what the ring holds against a view and builds its indirect draws.</summary>
    /// <param name="fields">The same fields, in the same order.</param>
    /// <param name="frustum">What the camera can see.</param>
    /// <param name="viewPosition">Where it is.</param>
    /// <param name="densityScale">How much of the field to draw, 0…1.</param>
    /// <returns>How many blades will be drawn.</returns>
    /// <exception cref="ArgumentNullException">There are no fields.</exception>
    /// <remarks>
    ///     ⚠ <b>A resident cell can survive the frustum and still draw nothing</b>, when every blade
    ///     in it is past the cull distance — <see cref="FoliageRenderer" />'s third rejection, and it
    ///     is more common here because a cell is held resident to the <em>largest</em> field's range.
    /// </remarks>
    public int Cull(
        IReadOnlyList<GrassDraw> fields,
        in BoundingFrustum frustum,
        Vector3 viewPosition,
        float densityScale = 1f
    ) {
        ArgumentNullException.ThrowIfNull(fields);

        batches.Clear();
        transforms.Clear();
        parameters.Clear();

        CellsConsidered = 0;
        CellsDrawn = 0;
        BladesConsidered = 0;
        BladesDrawn = 0;
        Draws = 0;

        var view = frustum;

        foreach (var resident in Residency.Resident) {
            for (var field = 0; field < Math.Min(fields.Count, FieldCapacity); field++) {
                var held = blades[resident.Slot, field];

                if (held.Count == 0) {
                    continue;
                }

                CellsConsidered++;

                if (!view.Intersects(extents[resident.Slot, field])) {
                    continue;
                }

                var draw = fields[field];

                CellsDrawn++;
                BladesConsidered += held.Count;

                Fill(held, draw.Type);

                var culled = culler.Cull(
                    CollectionsMarshal.AsSpan(bounds),
                    CollectionsMarshal.AsSpan(source),
                    new InstanceCullSettings {
                        Frustum = view,
                        ViewPosition = viewPosition,
                        StartCullDistance = draw.Type.StartCullDistance,
                        EndCullDistance = draw.Type.EndCullDistance,
                        DensityScale = densityScale,
                        Fade = true
                    },
                    draw.Distances
                );

                if (culled == 0) {
                    continue;
                }

                var first = transforms.Count;
                var survivors = culler.Survivors;
                var faded = culler.Parameters;

                for (var index = 0; index < culled; index++) {
                    transforms.Add(held[(int)survivors[index]].Instance.Transform);
                    parameters.Add(faded[index]);
                }

                var commands = new DrawCommand[culler.LevelCount];
                culler.FillCommands(draw.Levels, (uint)first, commands);

                batches.Add(new(field, resident.Cell, resident.Slot, first, culled, commands));

                BladesDrawn += culled;
                Draws += commands.Length;
            }
        }

        return BladesDrawn;
    }

    /// <summary>Drops every blade and returns every slot.</summary>
    /// <remarks>What a teleport or a change of grass types does: nothing resident is still right.</remarks>
    public void Reset() {
        Residency.Clear();

        for (var slot = 0; slot < Residency.Capacity; slot++) {
            for (var field = 0; field < FieldCapacity; field++) {
                blades[slot, field] ??= [];
                Clear(slot, field);
            }
        }

        BladesResident = 0;
        BladesScattered = 0;
        BladesDropped = 0;

        batches.Clear();
        transforms.Clear();
        parameters.Clear();
    }

    /// <summary>Makes room for more grass types per cell.</summary>
    /// <param name="fields">How many.</param>
    /// <exception cref="ArgumentOutOfRangeException">The count is not positive.</exception>
    /// <remarks>
    ///     Drops everything, because a field's index is what a slot's blades are filed under and
    ///     growing the array does not tell anybody which index each field now has.
    /// </remarks>
    public void Grow(int fields) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fields);

        if (fields <= FieldCapacity) {
            return;
        }

        blades = new List<GrassBlade>[Residency.Capacity, fields];
        extents = new BoundingBox[Residency.Capacity, fields];
        FieldCapacity = fields;

        Reset();
    }

    /// <summary>Whether a field wants a cell at this distance.</summary>
    /// <remarks>
    ///     The same hysteresis the ring uses, and it has to be the same: a field whose own boundary
    ///     had no dead band would scatter and drop the same cell every frame while the camera stood
    ///     on it, which is precisely the cost the ring exists to avoid — moved one level down.
    /// </remarks>
    bool Wanted(float distance, in GrassType type) =>
        distance <= type.EndCullDistance * (1f + MathF.Max(Residency.Hysteresis, 0f));

    int Fill(GrassSlot resident, int field, in GrassType type, IFoliageSurface surface, float densityScale) {
        var held = blades[resident.Slot, field];
        var placed = GrassScatter.Scatter(in type, resident.Cell, Residency.Grid, surface, held, densityScale);

        extents[resident.Slot, field] = Extent(held, type);

        return placed;
    }

    void Clear(int slot, int field) {
        blades[slot, field].Clear();
        extents[slot, field] = new(new(float.PositiveInfinity), new(float.NegativeInfinity));
    }

    /// <summary>What a cell's blades are, as the culler wants them.</summary>
    /// <remarks>
    ///     ⚠ <b>The per-blade parameters are the scatter's and only the fade is the cull's.</b> The
    ///     tint and the wind phase were drawn from the blade's own hash when it was placed; deriving
    ///     them here would be a second answer to "which way is this blade out of phase", and the two
    ///     would disagree the first time the cull ran at a different density scale.
    /// </remarks>
    void Fill(List<GrassBlade> held, in GrassType type) {
        bounds.Clear();
        source.Clear();

        var reach = Reach(type);

        foreach (var blade in held) {
            bounds.Add(new(blade.Instance.Position, reach * blade.Instance.Scale));

            source.Add(
                new() {
                    Tint = blade.Tint,
                    WindPhase = blade.WindPhase,
                    Scale = 1f,
                    Fade = 1f
                }
            );
        }
    }

    /// <summary>
    ///     How far a blade reaches from its own origin, at unit scale.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Derived from the candidate spacing, because this assembly has no mesh to ask.</b> One
    ///     grid step is the honest guess at how big a blade is — a denser field is a smaller mesh —
    ///     and it errs upwards by a whole step, because a radius that is too small culls a blade while
    ///     part of it is on screen. It is also what <see cref="GrassType.Wind" /> displaces within, so
    ///     a bound that ignored it would clip the sway at the edge of a cell.
    /// </remarks>
    float Reach(in GrassType type) {
        var step = Residency.Grid.CellSize / type.GridOf(Residency.Grid.CellSize);

        return MathF.Max(step + MathF.Abs(type.Wind.Strength), 0.25f);
    }

    static BoundingBox Extent(List<GrassBlade> held, in GrassType type) {
        var bounds = new BoundingBox(new(float.PositiveInfinity), new(float.NegativeInfinity));

        foreach (var blade in held) {
            // The vertical reach is the blade's own height rather than the horizontal step, which is
            // the one direction grass is bigger than its spacing. Scaled, because a blade drawn at
            // 1.3 is 1.3 tall.
            var extent = new Vector3(type.MaxScale) * blade.Instance.Scale;

            bounds = new(
                Vector3.Min(bounds.Minimum, blade.Instance.Position - extent),
                Vector3.Max(bounds.Maximum, blade.Instance.Position + extent)
            );
        }

        return bounds;
    }
}
