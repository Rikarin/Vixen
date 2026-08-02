// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Editor.Core;
using Vixen.Terrain;
using TerrainMap = Vixen.Terrain.Terrain;

namespace Vixen.Editor.Terrain;

/// <summary>
///     The terrain being sculpted, and the drag in flight over it.
/// </summary>
/// <remarks>
///     <para>
///         <b>What <c>MeshEdit</c> is to a blockout mesh.</b> The mode owns keys, a toolbar and a
///         claim on the viewport; this owns the terrain, the layer being written and the stroke —
///         which is the split that lets a test drive a whole stroke with world points and assert the
///         ground, with no shell and no window.
///     </para>
///     <para>
///         <b>Pointer down, drag, pointer up is one command — [§ D11].</b>
///         <see cref="Begin" /> starts a <see cref="TerrainStroke" />, every
///         <see cref="Extend" /> records before it writes and applies the kernel immediately, and
///         <see cref="Commit" /> hands back one <see cref="IEditorCommand" />. The composite is
///         resolved as the drag goes, so the viewport shows the ground moving under the brush rather
///         than at pointer-up.
///     </para>
///     <para>
///         ⚠ <b>The brush is snapshotted at <see cref="Begin" /> and not read again.</b> The panel
///         can change while a drag is in flight — a pen with a barrel wheel, a key press, another
///         window — and a stroke whose radius changed halfway through has an undo record sized to a
///         footprint that no longer matches what it wrote.
///     </para>
///     <para>
///         ⚠ <b>The colliders are rebuilt at <see cref="Commit" />, not per stamp.</b> A stroke marks
///         one or two tiles over and over; building a Jolt height field per stamp is the version of
///         this that makes the brush stutter. What is per stamp is the composite, because that is
///         what the artist is looking at.
///     </para>
/// </remarks>
public sealed class TerrainEdit {
    readonly List<BrushStamp> stamps = [];

    TerrainStroke? stroke;
    TerrainHoleStroke? holes;
    TerrainWeightStroke? weights;
    BrushStroke? path;
    TerrainBrush held;
    TerrainCategory category;
    TerrainTool tool;
    TerrainPaintTool paintTool;
    int target;
    bool inverted;
    float flattenTarget;
    Vector2 rampFrom;
    Vector2 rampTo;

    /// <summary>The terrain being edited, or <see langword="null" /> while none is.</summary>
    /// <remarks>
    ///     ⚠ <b>Setting it drops whatever drag was in flight.</b> A stroke holds a layer of the
    ///     terrain it started on, so carrying one across a change of terrain would commit an undo
    ///     entry that restores samples of a different heightfield.
    /// </remarks>
    public TerrainMap? Terrain {
        get;
        set {
            if (ReferenceEquals(field, value)) {
                return;
            }

            Cancel();
            field = value;
            Layer = value?.Layers.LastOrDefault(layer => layer.AcceptsBrush);
            Tools.Target = value is { Weights.LayerCount: > 0 } ? 0 : -1;
        }
    }

    /// <summary>Which edit layer the tools write, or <see langword="null" /> for none.</summary>
    public TerrainEditLayer? Layer { get; set; }

    /// <summary>The brush every tool in both modes shares.</summary>
    public TerrainBrushSettings Brush { get; } = new();

    /// <summary>What each tool does beyond what the brush says.</summary>
    public TerrainToolSettings Tools { get; } = new();

    /// <summary>What rebuilds collision when the ground moves, or null for a scene with no physics.</summary>
    public ITerrainColliders? Colliders { get; set; }

    /// <summary>Told which samples moved, whenever any do. Where a renderer hangs its upload.</summary>
    public event Action<TerrainRect>? Changed;

    /// <summary>Whether a drag is in flight.</summary>
    public bool IsStroking => stroke is not null || holes is not null || weights is not null;

    /// <summary>Which tool the drag in flight is running.</summary>
    public TerrainTool HeldTool => tool;

    /// <summary>Which half of the toolset the drag in flight belongs to.</summary>
    public TerrainCategory HeldCategory => category;

    /// <summary>Which paint layer the paint tools write, or −1 for none.</summary>
    /// <remarks>
    ///     ⚠ <b>Clamped rather than trusted, because a paint layer is a slot and slots move.</b>
    ///     Removing the second of six shifts the rest down; a target left pointing past the end
    ///     paints nothing and says nothing, which reads as the brush being broken.
    /// </remarks>
    public int Target {
        get {
            var count = Terrain?.Weights.LayerCount ?? 0;

            return count == 0 ? -1 : Math.Clamp(Tools.Target, 0, count - 1);
        }
        set => Tools.Target = value;
    }

    /// <summary>What the last <see cref="Begin" /> refused for, or <see langword="null" />.</summary>
    /// <remarks>
    ///     ⚠ <b>A reason rather than an exception, and it is what the panel shows.</b> "The layer
    ///     Splines is managed by its generator" is an ordinary thing to try, and a mode that threw
    ///     would take the frame down with the scene unsaved. What is not ordinary is a brush that
    ///     silently does nothing, which is the version of this that gets reported as the tool being
    ///     broken.
    /// </remarks>
    public string? Refusal { get; private set; }

    /// <summary>Whether the terrain and layer are in a state a stroke could start from.</summary>
    public bool CanStroke => Reason() is null;

    /// <summary>Starts a stroke at a point on the ground.</summary>
    /// <param name="ground">Where the pointer met the terrain, in world XZ.</param>
    /// <param name="invert">Whether <c>Shift</c> was held, which reverses the tool.</param>
    /// <returns>Whether a stroke started.</returns>
    public bool Begin(Vector2 ground, bool invert = false) {
        Cancel();

        Refusal = Reason();

        if (Refusal is not null || Terrain is not { } terrain) {
            return false;
        }

        category = Tools.Category;
        tool = Tools.Tool;
        paintTool = Tools.PaintTool;
        target = Target;
        inverted = invert;
        held = Brush.ToBrush();
        path = new(held);

        if (category == TerrainCategory.Paint) {
            weights = new(terrain);
        } else if (tool == TerrainTool.Holes) {
            holes = new(terrain);
        } else {
            stroke = new(terrain, Layer!);
        }

        if (category == TerrainCategory.Sculpt && tool == TerrainTool.Flatten && Tools.PickTarget) {
            Tools.FlattenTarget = TerrainPick.HeightAt(terrain, ground.X, ground.Y);
        }

        flattenTarget = tool switch {
            TerrainTool.Flatten => Tools.FlattenTarget,

            // Clay accumulates against a plane a fixed distance from where the stroke started,
            // rather than pushing along the normal — so holding the brush down builds a mesa
            // instead of sharpening a spike.
            TerrainTool.Sculpt when Tools.Clay =>
                TerrainPick.HeightAt(terrain, ground.X, ground.Y) + (invert ? -Tools.Metres : Tools.Metres),
            _ => 0f
        };

        rampFrom = ground;
        rampTo = ground;

        Extend(ground);

        return true;
    }

    /// <summary>Carries the stroke to a point.</summary>
    /// <param name="ground">Where the pointer is now, in world XZ.</param>
    /// <remarks>
    ///     Does nothing when no stroke is in flight, so a host that reports moves it did not report a
    ///     press for is not a special case.
    /// </remarks>
    public void Extend(Vector2 ground) {
        if (Terrain is not { } terrain || !IsStroking) {
            return;
        }

        if (category == TerrainCategory.Sculpt && tool == TerrainTool.Ramp) {
            rampTo = ground;
            Preview(terrain);
            return;
        }

        stamps.Clear();
        path!.MoveTo(ground, stamps);

        var touched = TerrainRect.Empty;

        foreach (var stamp in stamps) {
            touched = touched.Union(Stamped(terrain, stamp));
        }

        Resolved(terrain, touched);
    }

    /// <summary>Ends the stroke and hands back the entry that undoes it.</summary>
    /// <param name="rebuildColliders">
    ///     Whether to rebuild collision now. False is for a test that has no physics to check.
    /// </param>
    /// <returns>The command, or <see langword="null" /> if the stroke touched nothing.</returns>
    /// <remarks>
    ///     ⚠ <b>Hands the command back rather than executing it.</b> The stack belongs to the
    ///     document and this type has never heard of one — the same reason
    ///     <c>BlockoutMode.Export</c> is a callback. It also means a caller can put the stroke inside
    ///     a transaction with something else, which is what a tool that both sculpts and paints will
    ///     want.
    /// </remarks>
    public IEditorCommand? Commit(bool rebuildColliders = true) {
        if (Terrain is not { } terrain) {
            return null;
        }

        IEditorCommand? command = null;
        var rect = TerrainRect.Empty;

        if (stroke is { IsEmpty: false } sculpted) {
            rect = sculpted.Rect;
            command = new TerrainStrokeCommand(terrain, sculpted, NameOf(tool), Recomposited);
        } else if (holes is { IsEmpty: false } punched) {
            rect = punched.Rect;
            command = new TerrainHoleCommand(terrain, punched, NameOf(tool), Recomposited);
        } else if (weights is { IsEmpty: false } painted) {
            rect = painted.Rect;
            command = new TerrainPaintCommand(terrain, painted, NameOf(paintTool), Repainted);
        }

        stroke = null;
        holes = null;
        weights = null;
        path = null;
        stamps.Clear();

        if (command is not null) {
            terrain.Resolve();

            // ⚠ Only when a height moved. A paint stroke changes which *material* each quad is, and
            // that is read from the weights when it is asked rather than baked into the shape — so
            // rebuilding here would be a Jolt height field built to hold the heights it already has,
            // once per stroke, for nothing.
            if (rebuildColliders && category == TerrainCategory.Sculpt) {
                Colliders?.Rebuild(terrain, rect);
            }
        }

        return command;
    }

    /// <summary>Abandons the stroke, putting the ground back the way it was.</summary>
    public void Cancel() {
        if (Terrain is { } terrain) {
            var rect = stroke?.Undo() ?? holes?.Undo() ?? weights?.Undo() ?? TerrainRect.Empty;

            if (!rect.IsEmpty) {
                terrain.Resolve();
                Changed?.Invoke(rect);
            }
        }

        stroke = null;
        holes = null;
        weights = null;
        path = null;
        stamps.Clear();
    }

    /// <summary>Why a stroke cannot start, or <see langword="null" /> if it can.</summary>
    string? Reason() {
        if (Terrain is null) {
            return "There is no terrain selected.";
        }

        if (Tools.Category == TerrainCategory.Paint) {
            // ⚠ A paint layer has no lock and no generator — that is the *edit* layer stack, a
            // different thing with a similar name. What refuses a paint stroke is having nothing
            // selected to paint.
            return Terrain!.Weights.LayerCount == 0
                ? "There are no target layers to paint. Add one in the terrain panel."
                : null;
        }

        if (Tools.Tool == TerrainTool.Holes) {
            return null;
        }

        if (Layer is not { } layer) {
            return "There is no edit layer to write. Add one in the terrain panel.";
        }

        if (layer.IsLocked) {
            return $"The layer '{layer.Name}' is locked.";
        }

        if (layer.Kind != TerrainLayerKind.Manual) {
            return $"The layer '{layer.Name}' is managed by the {layer.Kind} generator, so a hand "
                + "edit would be discarded the next time it ran.";
        }

        return null;
    }

    /// <summary>Applies one stamp of the held tool, recording the ground first.</summary>
    /// <remarks>
    ///     ⚠ <b><see cref="TerrainStroke.Record" /> before the kernel, every time.</b> A record holds
    ///     what the ground <em>was</em>; taking it from the kernel's return value takes it afterwards
    ///     and produces an undo that restores the stroke it was supposed to remove. The kernel's
    ///     rectangle is what the tool wrote and is unioned separately, for the renderer.
    /// </remarks>
    TerrainRect Stamped(TerrainMap terrain, BrushStamp stamp) {
        if (category == TerrainCategory.Paint) {
            weights!.Record(held, stamp);

            return paintTool switch {
                TerrainPaintTool.Smooth => TerrainPaint.Smooth(terrain, target, held, stamp),
                TerrainPaintTool.Flatten => TerrainPaint.Flatten(
                    terrain, target, held, stamp, Tools.TargetCoverage
                ),
                TerrainPaintTool.Noise => TerrainPaint.Noise(
                    terrain,
                    target,
                    held,
                    stamp,
                    inverted ? -Tools.CoverageNoise : Tools.CoverageNoise,
                    Tools.ToNoise()
                ),
                _ => TerrainPaint.Paint(
                    terrain, target, held, stamp, inverted ? -Tools.Coverage : Tools.Coverage
                )
            };
        }

        if (tool == TerrainTool.Holes) {
            holes!.Record(held, stamp);

            return TerrainSculpt.PaintHoles(terrain, held, stamp, !inverted, Tools.HoleThreshold);
        }

        var recorded = stroke!.Record(held, stamp);
        var layer = stroke.Layer;
        var touched = TerrainRect.Empty;

        for (var pass = 0; pass < Tools.PassesOf(tool); pass++) {
            touched = touched.Union(
                tool switch {
                    TerrainTool.Sculpt when Tools.Clay => TerrainSculpt.Flatten(
                        terrain,
                        layer,
                        held,
                        stamp,
                        flattenTarget,
                        direction: inverted ? FlattenDirection.Lower : FlattenDirection.Raise
                    ),
                    TerrainTool.Sculpt => TerrainSculpt.Sculpt(
                        terrain, layer, held, stamp, inverted ? -Tools.Metres : Tools.Metres
                    ),
                    TerrainTool.Smooth => TerrainSculpt.Smooth(terrain, layer, held, stamp),
                    TerrainTool.Flatten => TerrainSculpt.Flatten(terrain, layer, held, stamp, flattenTarget),
                    TerrainTool.Erosion => TerrainSculpt.Erode(
                        terrain, layer, held, stamp, Tools.Talus, Tools.ErosionRate
                    ),
                    TerrainTool.Hydro => TerrainSculpt.Hydro(terrain, layer, held, stamp, Tools.HydroRate),
                    TerrainTool.Noise => TerrainSculpt.Noise(
                        terrain,
                        layer,
                        held,
                        stamp,
                        inverted ? -Tools.Amplitude : Tools.Amplitude,
                        Tools.ToNoise()
                    ),
                    _ => TerrainRect.Empty
                }
            );
        }

        return touched.IsEmpty ? recorded : touched;
    }

    /// <summary>Redraws the ramp from scratch, so the drag previews it.</summary>
    /// <remarks>
    ///     ⚠ <b>Undo the stroke, then reapply — and this is what makes a two-point tool live rather
    ///     than a thing that appears when the button comes up.</b> A ramp is not a sequence of stamps
    ///     that accumulate; it is one shape between two points, and the second point moves. Undoing
    ///     puts the layer back to what it was before the preview, so the record's lazily-captured
    ///     before-image is still the right one and re-extending over new ground captures the original
    ///     values there too.
    /// </remarks>
    void Preview(TerrainMap terrain) {
        var previous = stroke!.Rect;
        var restored = stroke.Undo();

        var half = MathF.Max(Tools.RampWidth * 0.5f, 0.01f);
        var reach = new Vector2(half, half);
        var minimum = Vector2.Min(rampFrom, rampTo) - reach;
        var maximum = Vector2.Max(rampFrom, rampTo) + reach;

        stroke.Extend(SampleRect(terrain, minimum, maximum));

        var written = TerrainSculpt.Ramp(
            terrain,
            stroke.Layer,
            rampFrom,
            rampTo,
            TerrainPick.HeightAt(terrain, rampFrom.X, rampFrom.Y),
            TerrainPick.HeightAt(terrain, rampTo.X, rampTo.Y),
            half,
            Tools.RampFalloff
        );

        // Everything the last preview covered has to be recomposited as well, or the ground it left
        // behind stays on screen where the ramp used to be.
        Resolved(terrain, written.Union(restored).Union(previous));
    }

    /// <summary>Which samples a world-space rectangle covers.</summary>
    static TerrainRect SampleRect(TerrainMap terrain, Vector2 minimum, Vector2 maximum) {
        var scale = terrain.Description.MetresPerQuad;

        var x = Math.Max(0, (int)MathF.Floor(minimum.X / scale));
        var z = Math.Max(0, (int)MathF.Floor(minimum.Y / scale));
        var endX = Math.Min(terrain.Description.SamplesX, (int)MathF.Ceiling(maximum.X / scale) + 1);
        var endZ = Math.Min(terrain.Description.SamplesZ, (int)MathF.Ceiling(maximum.Y / scale) + 1);

        return endX <= x || endZ <= z ? TerrainRect.Empty : new(x, z, endX - x, endZ - z);
    }

    void Resolved(TerrainMap terrain, TerrainRect rect) {
        if (rect.IsEmpty) {
            return;
        }

        // ⚠ Per stamp, and it is what [§ D11]'s "applied to the composite for display as it happens"
        // means. The tiles are already marked, so this recomposites only what the stamp touched.
        terrain.Resolve();
        Changed?.Invoke(rect);
    }

    /// <summary>What an undo and a redo do, which is the same thing from either direction.</summary>
    void Recomposited(TerrainRect rect) {
        Changed?.Invoke(rect);

        if (Terrain is { } terrain) {
            Colliders?.Rebuild(terrain, rect);
        }
    }

    /// <summary>Tells the renderer a paint stroke moved, and does not recomposite.</summary>
    /// <remarks>
    ///     ⚠ <b>No collider rebuild either.</b> A paint stroke changes no height, so the shape is
    ///     the shape it was; what it changes is which <em>material</em> each quad is, and that is
    ///     read from the weights at the moment it is asked rather than baked into the shape. A
    ///     rebuild here would be a Jolt height field built to hold the same heights it already has.
    /// </remarks>
    void Repainted(TerrainRect rect) => Changed?.Invoke(rect);

    /// <summary>What the undo history calls a paint stroke.</summary>
    static string NameOf(TerrainPaintTool tool) =>
        tool switch {
            TerrainPaintTool.Smooth => "Smooth Terrain Layer",
            TerrainPaintTool.Flatten => "Flatten Terrain Layer",
            TerrainPaintTool.Noise => "Scatter Terrain Layer",
            _ => "Paint Terrain Layer"
        };

    /// <summary>What the undo history calls a stroke of a tool.</summary>
    static string NameOf(TerrainTool tool) =>
        tool switch {
            TerrainTool.Sculpt => "Sculpt Terrain",
            TerrainTool.Smooth => "Smooth Terrain",
            TerrainTool.Flatten => "Flatten Terrain",
            TerrainTool.Ramp => "Ramp Terrain",
            TerrainTool.Erosion => "Erode Terrain",
            TerrainTool.Hydro => "Erode Terrain (Hydraulic)",
            TerrainTool.Noise => "Add Terrain Noise",
            _ => "Paint Terrain Holes"
        };
}
