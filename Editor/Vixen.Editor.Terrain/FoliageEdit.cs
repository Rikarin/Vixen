// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Editor.Core;
using Vixen.Editor.Inspector;
using Vixen.Foliage;
using Vixen.Terrain;

namespace Vixen.Editor.Terrain;

/// <summary>What a drag in the foliage mode does.</summary>
/// <remarks>
///     Six, and the list is Unreal's — [docs/plan/31 § The foliage tools]. Five of them change
///     instances and the sixth selects them, which is why the sixth is the one that hands the
///     transform gizmo something to hold.
/// </remarks>
public enum FoliageTool {
    /// <summary>Adds instances of every selected type at the brush's density; <c>Shift</c> erases.</summary>
    Paint,

    /// <summary>One instance at the cursor, of the selected type.</summary>
    Single,

    /// <summary>Fills the surface under the cursor out to the brush.</summary>
    Fill,

    /// <summary>Removes, filtered to the selected types.</summary>
    Erase,

    /// <summary>
    ///     Re-runs a chosen subset of a type's settings over instances that already exist.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The one to get right.</b> It is what turns foliage from place-and-regret into an
    ///     editable thing: changing a type's scale range afterwards should be able to re-roll the
    ///     scale of existing trees <em>without moving them</em>, and re-rolling everything is not the
    ///     same operation. Which properties it touches is <see cref="FoliageSettings.Reapply" />.
    /// </remarks>
    Reapply,

    /// <summary>Selects instances, so the gizmo can move, rotate and scale them.</summary>
    Select
}

/// <summary>Which of a type's settings a Reapply stroke re-rolls.</summary>
/// <remarks>
///     ⚠ <b>Flags, and a checkbox per property is exactly how Unreal does it.</b> "Re-roll the scale
///     and leave everything else" is the operation an artist wants after changing a scale range, and
///     a Reapply that re-rolled the position too would move a forest somebody had already thinned by
///     hand.
/// </remarks>
[Flags]
public enum FoliageReapply {
    /// <summary>Nothing, which is a stroke that does nothing and says so.</summary>
    None = 0,

    /// <summary>Re-roll each instance's scale from the type's current range.</summary>
    Scale = 1 << 0,

    /// <summary>Re-roll its heading.</summary>
    Yaw = 1 << 1,

    /// <summary>Re-align it to the surface it stands on, at the type's current strength.</summary>
    Alignment = 1 << 2,

    /// <summary>Drop instances the type's filters would now refuse.</summary>
    /// <remarks>
    ///     Separately from the rest, because it <em>removes</em> things. An artist tightening a slope
    ///     range expects to be asked before a third of their forest disappears.
    /// </remarks>
    Filters = 1 << 3
}

/// <summary>
///     The foliage panel's settings: the brush, the palette selection and the tool parameters.
/// </summary>
/// <remarks>
///     <b>[docs/plan/31 § The palette] and § The foliage tools.</b> The brush is
///     <see cref="TerrainBrushSettings" />'s — [§ D12]'s one service — and what is here is what
///     foliage adds to it.
/// </remarks>
[DataContract("FoliageSettings")]
public sealed class FoliageSettings {
    /// <summary>Which tool a drag runs.</summary>
    public FoliageTool Tool { get; set; } = FoliageTool.Paint;

    /// <summary>How much of each type's density a stroke places, 0…1.</summary>
    [Inspector]
    [Range(0f, 1f)]
    [Tooltip("Scales every selected type's own density, so the palette's balance is kept.")]
    public float Density { get; set; } = 1f;

    /// <summary>Which of a type's settings a Reapply stroke re-rolls.</summary>
    [Inspector]
    [ShowIf(nameof(IsReapplying))]
    public FoliageReapply Reapply { get; set; } = FoliageReapply.Scale;

    /// <summary>Whether a stroke may land on the terrain.</summary>
    [Inspector]
    [Header("Filters")]
    public bool OnTerrain { get; set; } = true;

    /// <summary>Whether it may land on static meshes.</summary>
    [Inspector]
    public bool OnStaticMeshes { get; set; } = true;

    /// <summary>Whether it may land on blockout meshes.</summary>
    [Inspector]
    public bool OnBlockout { get; set; }

    /// <summary>Whether it may land on other foliage.</summary>
    [Inspector]
    public bool OnFoliage { get; set; }

    /// <summary>Whether the Reapply rows apply.</summary>
    public bool IsReapplying => Tool == FoliageTool.Reapply;

    /// <summary>What a stroke will accept, as one value the probe can be asked with.</summary>
    public FoliageFilters Filters =>
        (OnTerrain ? FoliageFilters.Terrain : 0)
        | (OnStaticMeshes ? FoliageFilters.StaticMeshes : 0)
        | (OnBlockout ? FoliageFilters.Blockout : 0)
        | (OnFoliage ? FoliageFilters.Foliage : 0);
}

/// <summary>What kinds of surface accept a foliage stroke.</summary>
/// <remarks>
///     ⚠ <b>A stroke ray-tests through the probe, which is already the seam <c>ScenePlacement</c>
///     uses to answer "where does this go".</b> So painting onto a blockout wall works on the day
///     blockout meshes are probeable, with no foliage-specific code — which is [§ The foliage
///     tools]'s claim and the reason these are a filter over an existing question rather than four
///     new ones.
/// </remarks>
[Flags]
public enum FoliageFilters {
    /// <summary>Nothing, which is a stroke that lands nowhere.</summary>
    None = 0,

    /// <summary>The terrain.</summary>
    Terrain = 1 << 0,

    /// <summary>Static meshes.</summary>
    StaticMeshes = 1 << 1,

    /// <summary>Blockout meshes.</summary>
    Blockout = 1 << 2,

    /// <summary>Other foliage.</summary>
    Foliage = 1 << 3
}

/// <summary>
///     The volume being painted, and the drag in flight over it.
/// </summary>
/// <remarks>
///     <para>
///         <b>What <see cref="TerrainEdit" /> is to a heightfield.</b> The mode owns keys, a strip and
///         a claim on the viewport; this owns the volume, the palette selection and the stroke — which
///         is the split that lets a test drive a whole stroke with world points and assert the trees,
///         with no shell and no window.
///     </para>
///     <para>
///         ⚠ <b>Selection is by address and is re-resolved after every edit.</b> An address is only
///         valid until its chunk changes: removing an instance shifts the ones after it, so a
///         selection held across an erase stroke points at somebody else's tree.
///     </para>
/// </remarks>
public sealed class FoliageEdit {
    readonly List<FoliageAddress> selection = [];
    readonly HashSet<int> chosen = [];
    readonly List<FoliageAddress> placed = [];
    readonly List<FoliageAddress> erased = [];

    List<FoliageInstance>? removedInstances;
    List<int>? removedTypes;
    uint strokeSeed;
    bool stroking;

    /// <summary>The volume being painted, or <see langword="null" /> while none is.</summary>
    public FoliageVolume? Volume {
        get;
        set {
            if (ReferenceEquals(field, value)) {
                return;
            }

            Cancel();
            field = value;
            selection.Clear();
            chosen.Clear();

            if (value is { Palette.Count: > 0 }) {
                chosen.Add(0);
            }
        }
    }

    /// <summary>The brush every tool in both modes shares.</summary>
    public TerrainBrushSettings Brush { get; } = new();

    /// <summary>What foliage adds to it.</summary>
    public FoliageSettings Settings { get; } = new();

    /// <summary>What answers "what is the ground here", or null while nothing does.</summary>
    /// <remarks>
    ///     ⚠ <b>Null is a mode that cannot paint, and it says so rather than placing nothing.</b> A
    ///     brush that silently does nothing is the version of this that gets reported as broken.
    /// </remarks>
    public IFoliageSurface? Surface { get; set; }

    /// <summary>Which palette entries a stroke places, by index.</summary>
    public IReadOnlySet<int> Chosen => chosen;

    /// <summary>What is selected, for the gizmo.</summary>
    public IReadOnlyList<FoliageAddress> Selection => selection;

    /// <summary>Told which instances changed, whenever any do.</summary>
    public event Action<FoliageVolume>? Changed;

    /// <summary>Whether a drag is in flight.</summary>
    public bool IsStroking => stroking;

    /// <summary>What the last <see cref="Begin" /> refused for, or <see langword="null" />.</summary>
    public string? Refusal { get; private set; }

    /// <summary>Whether a stroke could start.</summary>
    public bool CanStroke => Reason() is null;

    /// <summary>Chooses a palette entry, or adds it to the choice.</summary>
    /// <param name="type">Which entry.</param>
    /// <param name="add">Whether to keep what was already chosen.</param>
    public void Choose(int type, bool add = false) {
        if (Volume is not { } volume || (uint)type >= (uint)volume.Palette.Count) {
            return;
        }

        if (!add) {
            chosen.Clear();
        }

        if (!chosen.Add(type) && add) {
            chosen.Remove(type);
        }
    }

    /// <summary>Starts a stroke at a point on the ground.</summary>
    /// <param name="ground">Where the pointer met a surface, in world XZ.</param>
    /// <param name="invert">Whether <c>Shift</c> was held, which erases.</param>
    /// <returns>Whether a stroke started.</returns>
    public bool Begin(Vector2 ground, bool invert = false) {
        Cancel();

        Refusal = Reason();

        if (Refusal is not null) {
            return false;
        }

        stroking = true;
        placed.Clear();
        erased.Clear();
        removedInstances = [];
        removedTypes = [];

        // ⚠ One seed for the whole stroke, mixed with the stamp's index inside it. A per-stamp seed
        // taken from a clock would make the stroke unrepeatable, which breaks both the undo record
        // and any hope of comparing this to the GPU scatter.
        strokeSeed = FoliageScatter.Hash(0x9E3779B9u, placed.Count ^ (int)(ground.X * 31f + ground.Y * 17f));

        Extend(ground, invert);

        return true;
    }

    /// <summary>Carries the stroke to a point.</summary>
    /// <param name="ground">Where the pointer is now, in world XZ.</param>
    /// <param name="invert">Whether <c>Shift</c> is held.</param>
    public void Extend(Vector2 ground, bool invert = false) {
        if (!stroking || Volume is not { } volume) {
            return;
        }

        var radius = Brush.Radius;
        var erasing = invert || Settings.Tool == FoliageTool.Erase;

        switch (Settings.Tool) {
            case FoliageTool.Select:
                return;

            // ⚠ Erasing before the surface is looked at, because it does not need one. Requiring a
            // surface for every tool made `Shift`-erase silently do nothing wherever nothing answered
            // — which is exactly where an artist is most likely to be cleaning up.
            case FoliageTool.Erase:
            case FoliageTool.Paint when erasing:
                Erase(volume, ground, radius);
                break;

            default:
                if (Surface is not { } surface) {
                    return;
                }

                switch (Settings.Tool) {
                    case FoliageTool.Single:
                        Single(volume, surface, ground);
                        break;

                    case FoliageTool.Fill:
                        Scatter(volume, surface, ground, radius, strength: 1f);
                        break;

                    case FoliageTool.Reapply:
                        Reapply(volume, surface, ground, radius);
                        break;

                    default:
                        Scatter(volume, surface, ground, radius, Brush.Strength * Settings.Density);
                        break;
                }

                break;
        }

        Changed?.Invoke(volume);
    }

    /// <summary>Ends the stroke and hands back the entry that undoes it.</summary>
    /// <returns>The command, or <see langword="null" /> if the stroke changed nothing.</returns>
    public IEditorCommand? Commit() {
        if (Volume is not { } volume || !stroking) {
            return null;
        }

        stroking = false;

        var added = placed.ToArray();
        var removed = removedInstances?.ToArray() ?? [];
        var types = removedTypes?.ToArray() ?? [];

        placed.Clear();
        erased.Clear();
        removedInstances = null;
        removedTypes = null;

        if (added.Length == 0 && removed.Length == 0) {
            return null;
        }

        return new FoliageStrokeCommand(volume, added, removed, types, NameOf(Settings.Tool), Notify);
    }

    /// <summary>Abandons the stroke, putting the instances back the way they were.</summary>
    public void Cancel() {
        if (!stroking || Volume is not { } volume) {
            stroking = false;
            return;
        }

        stroking = false;

        if (placed.Count > 0) {
            volume.Remove(placed);
        }

        if (removedInstances is { Count: > 0 } && removedTypes is not null) {
            for (var index = 0; index < removedInstances.Count; index++) {
                volume.Add(removedTypes[index], removedInstances[index]);
            }
        }

        placed.Clear();
        erased.Clear();
        removedInstances = null;
        removedTypes = null;

        Changed?.Invoke(volume);
    }

    /// <summary>Selects everything of the chosen types within the brush.</summary>
    /// <param name="ground">Where the pointer is, in world XZ.</param>
    /// <param name="add">Whether to keep what was already selected.</param>
    /// <returns>How many are selected now.</returns>
    /// <remarks>
    ///     ⚠ <b>Re-resolved rather than accumulated, because an address is not a reference.</b> Any
    ///     edit between two selections shifts the indices, so keeping the old ones would hand the
    ///     gizmo somebody else's trees.
    /// </remarks>
    public int Select(Vector2 ground, bool add = false) {
        if (Volume is not { } volume) {
            return 0;
        }

        if (!add) {
            selection.Clear();
        }

        foreach (var address in volume.Within(ground, Brush.Radius, chosen)) {
            if (!selection.Contains(address)) {
                selection.Add(address);
            }
        }

        return selection.Count;
    }

    /// <summary>Drops the selection.</summary>
    public void Deselect() => selection.Clear();

    /// <summary>Moves the selected instances, as a gizmo drag does.</summary>
    /// <param name="offset">How far, in world space.</param>
    /// <returns>The command that undoes it, or null if nothing was selected.</returns>
    /// <remarks>
    ///     ⚠ <b>The selection is re-resolved afterwards, because a move can re-cell.</b> An instance
    ///     dragged over a cell boundary is removed from one chunk and added to another, so its
    ///     address changes — and a gizmo still holding the old one would move a different tree next
    ///     frame.
    /// </remarks>
    public IEditorCommand? MoveSelection(Vector3 offset) {
        if (Volume is not { } volume || selection.Count == 0) {
            return null;
        }

        var before = new FoliageInstance[selection.Count];
        var addresses = selection.ToArray();

        for (var index = 0; index < addresses.Length; index++) {
            before[index] = volume.At(addresses[index]) ?? default;
        }

        var command = new FoliageMoveCommand(volume, addresses, before, offset, Rebind);

        return command;
    }

    /// <summary>Why a stroke cannot start.</summary>
    string? Reason() {
        if (Volume is null) {
            return "There is no foliage volume in this scene.";
        }

        if (Volume.Palette.Count == 0) {
            return "The palette is empty. Add a foliage type before painting.";
        }

        if (chosen.Count == 0) {
            return "No type is chosen in the palette.";
        }

        if (Surface is null && Settings.Tool is not (FoliageTool.Erase or FoliageTool.Select)) {
            return "There is nothing to paint onto: no surface answers where the ground is.";
        }

        if (Settings.Filters == FoliageFilters.None) {
            return "Every surface filter is off, so a stroke would land nowhere.";
        }

        return null;
    }

    void Scatter(FoliageVolume volume, IFoliageSurface surface, Vector2 ground, float radius, float strength) {
        foreach (var type in chosen) {
            FoliageScatter.Stamp(
                volume,
                type,
                surface,
                ground,
                radius,
                strength,
                FoliageScatter.Hash(strokeSeed, (type * 977) + placed.Count),
                placed
            );
        }
    }

    void Single(FoliageVolume volume, IFoliageSurface surface, Vector2 ground) {
        foreach (var type in chosen) {
            var settings = volume.Palette[type];
            var hit = surface.SampleAt(ground, settings.LayerFilter);

            if (!hit.Hit) {
                continue;
            }

            var hash = FoliageScatter.Hash(strokeSeed, placed.Count);

            placed.Add(volume.Add(type, FoliageScatter.Place(settings, hit, hash)));

            // One instance, whichever types are chosen — the tool is "put one here", not "put one of
            // each here".
            break;
        }
    }

    void Erase(FoliageVolume volume, Vector2 ground, float radius) {
        var doomed = volume.Within(ground, radius, chosen).ToArray();

        foreach (var address in doomed) {
            if (volume.At(address) is { } instance) {
                removedInstances!.Add(instance);
                removedTypes!.Add(address.Type);
            }
        }

        volume.Remove(doomed);
        erased.AddRange(doomed);
    }

    /// <summary>Re-rolls the chosen properties of everything under the brush.</summary>
    void Reapply(FoliageVolume volume, IFoliageSurface surface, Vector2 ground, float radius) {
        if (Settings.Reapply == FoliageReapply.None) {
            return;
        }

        // ⚠ The doomed are collected and removed in one call at the end, never inside the loop.
        // Removing an instance shifts every address after it in the same chunk, so a loop that
        // removed as it went would edit somebody else's tree on the very next iteration — which is
        // the trap `FoliageVolume.Remove` sorts descending to avoid, and the loop was walking round
        // it. A test caught it.
        var doomed = new List<FoliageAddress>();

        foreach (var address in volume.Within(ground, radius, chosen).ToArray()) {
            if (volume.At(address) is not { } instance) {
                continue;
            }

            var settings = volume.Palette[address.Type];
            var hit = surface.SampleAt(new(instance.Position.X, instance.Position.Z), settings.LayerFilter);

            if (Settings.Reapply.HasFlag(FoliageReapply.Filters) && Refused(settings, hit)) {
                removedInstances!.Add(instance);
                removedTypes!.Add(address.Type);
                doomed.Add(address);

                continue;
            }

            var hash = FoliageScatter.Hash(strokeSeed, address.Index * 31);
            var rolled = FoliageScatter.Place(settings, hit, hash);

            // ⚠ Property by property, and the position is never one of them. Re-rolling everything
            // is a different operation and it is the one that moves a forest somebody has already
            // thinned by hand.
            var next = instance with {
                Scale = Settings.Reapply.HasFlag(FoliageReapply.Scale) ? rolled.Scale : instance.Scale,
                Rotation = Settings.Reapply.HasFlag(FoliageReapply.Yaw)
                    || Settings.Reapply.HasFlag(FoliageReapply.Alignment)
                        ? rolled.Rotation
                        : instance.Rotation
            };

            // Safe in the loop because the position is never re-rolled, so a Reapply never re-cells
            // and never shifts an index.
            volume.Move(address, next);
        }

        volume.Remove(doomed);
    }

    static bool Refused(in FoliageType settings, in FoliageSurface hit) {
        if (!hit.Hit) {
            return true;
        }

        var slope = hit.Slope;

        return slope < settings.MinSlope
            || slope > settings.MaxSlope
            || hit.Position.Y < settings.MinAltitude
            || hit.Position.Y > settings.MaxAltitude
            || (settings.NeedsSurfaceWeight && hit.Weight < settings.LayerThreshold);
    }

    void Notify(FoliageVolume volume) => Changed?.Invoke(volume);

    void Rebind(IReadOnlyList<FoliageAddress> addresses) {
        selection.Clear();
        selection.AddRange(addresses);

        if (Volume is { } volume) {
            Changed?.Invoke(volume);
        }
    }

    static string NameOf(FoliageTool tool) =>
        tool switch {
            FoliageTool.Single => "Place Foliage",
            FoliageTool.Fill => "Fill Foliage",
            FoliageTool.Erase => "Erase Foliage",
            FoliageTool.Reapply => "Reapply Foliage",
            _ => "Paint Foliage"
        };
}
