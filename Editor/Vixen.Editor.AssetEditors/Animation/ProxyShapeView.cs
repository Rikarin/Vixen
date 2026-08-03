// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Animation;
using Vixen.Animation.Constraints;
using Vixen.Core.Mathematics;
using Vixen.Editor.Core;
using Vixen.Editor.SceneView;
using Vixen.Engine.Diagnostics;
using Vixen.Ui;
using Vixen.Ui.Controls;

// `Viewport` is a rectangle of the interface here and a rectangle of a render target elsewhere, and
// `SceneViewport` brings both names into scope — the same disambiguation that file makes.
using ViewportControl = Vixen.Ui.Controls.Advanced.Viewport;

namespace Vixen.Editor.AssetEditors.Animation;

/// <summary>A body's proxy shapes: the list, one shape's fields, and what the audit says.</summary>
/// <remarks>
///     <para>
///         The three things D13 asks of this panel are the ones that cannot be got any other way:
///         the coarse set generated with a per-shape override, the symmetry toggle, and the
///         validation pass — including the check nobody can make by reading either file, which is a
///         name present in one body's set and missing from another's.
///     </para>
///     <para>
///         The numbers here are one of two ways to size a shape and the slower one.
///         <see cref="ProxyShapeGizmoTarget" /> is the other: the shapes draw through
///         <see cref="ConstraintGizmos.DrawShapes" /> and a viewport's gizmo takes hold of one through
///         the same <c>IGizmoTarget</c> an entity goes through. A panel that could only type numbers
///         would be one nobody uses for the first pass; a viewport with no numbers would be one nobody
///         can use for the last centimetre.
///     </para>
/// </remarks>
public sealed class ProxyShapeView : Control, IDisposable {
    static readonly Color4 Bone = new(0.55f, 0.58f, 0.63f, 0.8f);
    static readonly Color4 Highlight = new(1f, 0.72f, 0.2f, 0.9f);

    readonly List<(UiElement Row, ProxyShapeRecord Shape)> rows = [];

    ProxyShapeDocument? document;
    ProxyShapeRecord? selected;

    /// <summary>The target the gizmo is holding, so a finished drag's result can be read back.</summary>
    ProxyShapeGizmoTarget? handle;

    /// <inheritdoc />
    protected override string TagName => "shape-editor";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>The bar across the top.</summary>
    public UiElement Bar { get; private set; } = null!;

    /// <summary>Adds a shape.</summary>
    public Button AddShape { get; private set; } = null!;

    /// <summary>Removes the selected shape.</summary>
    public Button RemoveShape { get; private set; } = null!;

    /// <summary>Puts the selected shape on the other side of the body.</summary>
    public Button MirrorShape { get; private set; } = null!;

    /// <summary>Marks the coarse subset from a generated one.</summary>
    public Button Coarsen { get; private set; } = null!;

    /// <summary>Runs the validation pass.</summary>
    public Button Check { get; private set; } = null!;

    /// <summary>Where the body and its shapes are drawn.</summary>
    public ViewportControl Stage { get; private set; } = null!;

    /// <summary>The camera, the gizmo and the navigation over it.</summary>
    public SceneViewport Scene { get; private set; } = null!;

    /// <summary>The rig and the shapes, as lines for whoever renders the stage.</summary>
    /// <remarks>
    ///     ⚠ <b>Filled here and drained by the host, which is the same contract every viewport in
    ///     this editor has.</b> A panel cannot render itself — it has no device, no render target and
    ///     no renderer — so what it can do is say what it wants drawn. The application points its
    ///     <c>DebugDrawRenderer</c> at this and at <see cref="SceneViewport.View" />.
    /// </remarks>
    public DebugDraw Draw { get; } = new();

    /// <summary>The shapes.</summary>
    public UiElement List { get; private set; } = null!;

    /// <summary>The selected shape's fields.</summary>
    public UiElement Fields { get; private set; } = null!;

    /// <summary>What the last validation pass said.</summary>
    public UiElement Report { get; private set; } = null!;

    /// <summary>Which shape is selected, or <see langword="null" />.</summary>
    public ProxyShapeRecord? Selected => selected;

    /// <summary>Another body's set to compare names against, or <see langword="null" />.</summary>
    /// <remarks>
    ///     ⚠ <b>The most valuable of the three checks and the one an author cannot make alone.</b> A
    ///     clip naming <c>left-palm</c> works on the body that has one and silently does nothing on
    ///     the body that calls it <c>palm-l</c> — and nothing about either set, read on its own, says
    ///     so. The host supplies the other set, because this document cannot reach one.
    /// </remarks>
    public ProxyShapeSetContent? Against { get; set; }

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        Bar = Part("shape-bar");

        AddShape = Bar.Add<Button>();
        AddShape.Label = "Add Shape";

        RemoveShape = Bar.Add<Button>();
        RemoveShape.Label = "Remove";

        MirrorShape = Bar.Add<Button>();
        MirrorShape.Label = "Mirror";

        Coarsen = Bar.Add<Button>();
        Coarsen.Label = "Generate Coarse";

        Check = Bar.Add<Button>();
        Check.Label = "Check";

        var body = Part("shape-body");
        var left = body.Add("shape-stage");

        Stage = left.Add<ViewportControl>();
        List = left.Add("shape-list");

        var side = body.Add("shape-side");

        Fields = side.Add("shape-fields");
        Report = side.Add("shape-report");

        // ⚠ The scene's viewport, not a second one written here. Orbit, pan, dolly, the gizmo, its
        // three modes, the snapping and the world/parent toggle are all substantial and all already
        // tested; a preview pane that reimplemented them would be a second set of camera controls
        // that behaves almost like the real one, which is worse than none.
        Scene = new(Stage, new Selection<Vixen.Core.Entity>(), "ProxyShapes") {
            TargetsFactory = Targets
        };

        Scene.Transformed += Followed;

        AddHandler<ClickEvent>(static (element, args) => ((ProxyShapeView) element).Chosen(args));
        AddHandler<PointerEvent>(static (element, args) => ((ProxyShapeView) element).Pressed(args));
    }

    /// <summary>Advances the pane: the camera, the gizmo, and what is drawn.</summary>
    /// <param name="delta">How long the last frame took.</param>
    /// <remarks>
    ///     ⚠ <b>Called by the host once a frame, after the layout pass</b>, for the reason
    ///     <c>Viewport.Refresh</c> gives: nothing announces that an element's box changed, so a
    ///     splitter moving is something the application notices rather than something the viewport is
    ///     told. The scene panel is driven the same way.
    /// </remarks>
    public void Update(TimeSpan delta) {
        Stage.Refresh();
        Scene.Update(delta);

        Draw.Clear();

        if (document is not { Rig: { } rig } set) {
            return;
        }

        var model = new BoneTransform[rig.JointCount];

        SkeletonPose.ComputeModelSpace(rig, rig.BindPose, model);

        // The rig, so a shape can be seen against the body it hangs off rather than floating.
        for (var joint = 1; joint < rig.JointCount; joint++) {
            var parent = rig.ParentOf(joint);

            if (parent >= 0) {
                Draw.Line(model[joint].Translation, model[parent].Translation, Bone);
            }
        }

        if (set.Set.Bake(rig) is { } baked) {
            ConstraintGizmos.DrawShapes(Draw, new(baked), model, BoneTransform.Identity);
        }

        // ⚠ The selected shape a second time, in the selection colour. `DrawShapes` draws the whole
        // set in one colour deliberately — it is answering "where are this body's shapes" — and an
        // author dragging one needs to know which of thirty is moving.
        if (selected is { } chosen && set.Set.Bake(rig) is { } posed
            && posed.IndexOf(chosen.Name) is var index && index >= 0) {
            var shapes = new ProxyShapes(posed);

            if (shapes.TryPose(posed[index].Name, model, out var placed)) {
                Draw.Sphere(new(placed.Transform.Translation, Radius(placed)), Highlight);
            }
        }
    }

    static float Radius(in ProxyShapePose posed) {
        var extents = Vector3.Max(posed.Dimensions.Extents, posed.Dimensions.TopExtents) * posed.Transform.Scale;
        return MathF.Max(MathF.Max(extents.X, extents.Y), extents.Z);
    }

    /// <summary>What the gizmo is holding: the selected shape, or nothing.</summary>
    IReadOnlyList<IGizmoTarget> Targets() {
        if (document is not { Rig: { } rig } set || selected is not { } shape) {
            handle = null;
            return [];
        }

        // A fresh target per attach, which `SceneViewport` only does between drags — the one a drag
        // started on is the one the rest of it is applied to, which is what makes the whole gesture
        // one entry. Kept, because a record is replaced rather than mutated and the drag's result is
        // only reachable through the target that produced it.
        handle = new(set, shape, ProxyShapeGizmoTarget.JointOf(rig, [], shape, BoneTransform.Identity));

        return [handle];
    }

    /// <summary>Follows the shape a finished drag replaced, so the selection stays on it.</summary>
    /// <remarks>
    ///     ⚠ <b>A shape is a record, so an edit is a replacement.</b> The instance this view was
    ///     holding is no longer in the set the moment the drag is recorded, and a view that kept it
    ///     would highlight a shape that is not there and edit one nobody can see. The undo entry
    ///     itself is <c>ProxyShapeGizmoTarget.Record</c>'s business.
    /// </remarks>
    void Followed(SceneViewport viewport) {
        if (handle is { } target) {
            selected = target.Current;
        }
    }

    /// <inheritdoc />
    public void Dispose() {
        Scene?.Dispose();

        if (document is { } previous) {
            previous.Changed -= Reload;
        }
    }

    /// <summary>Shows a set.</summary>
    /// <param name="set">The document.</param>
    public void Show(ProxyShapeDocument set) {
        ArgumentNullException.ThrowIfNull(set);

        if (document is { } previous) {
            previous.Changed -= Reload;
        }

        document = set;
        set.Changed += Reload;

        Reload(set);
    }

    /// <summary>Rebuilds from the document.</summary>
    /// <param name="set">The document.</param>
    public void Reload(ProxyShapeDocument set) {
        ArgumentNullException.ThrowIfNull(set);
        Reload();
    }

    /// <summary>Rebuilds the list.</summary>
    public void Reload() {
        List.Empty();
        rows.Clear();

        if (document is not { } set) {
            Restate();
            return;
        }

        var header = List.Add("shape-row");
        header.AddClass("header");

        header.Add("shape-name").Text = "Shape";
        header.Add("shape-kind").Text = "Kind";
        header.Add("shape-joint").Text = "Joint";
        header.Add("shape-tags").Text = "Tags";
        header.Add("shape-kind").Text = "Coarse";

        foreach (var shape in set.Set.Shapes) {
            var row = List.Add("shape-row");

            row.Add("shape-name").Text = shape.Name;
            row.Add("shape-kind").Text = shape.Kind.ToString();
            row.Add("shape-joint").Text = shape.Joint;
            row.Add("shape-tags").Text = string.Join(' ', shape.Tags);
            row.Add("shape-kind").Text = shape.Coarse ? "yes" : string.Empty;

            // ⚠ A shape naming a joint this rig does not have is dead: it never poses, so every goal
            // anchored to it silently resolves to nothing. With no rig bound, nothing is claimed.
            row.SetClass("missing", set.Rig is { } rig && rig.IndexOf(shape.Joint) < 0);

            rows.Add((row, shape));
        }

        Restate();
    }

    void Restate() {
        Fields.Empty();

        foreach (var (element, shape) in rows) {
            element.SetClass("selected", ReferenceEquals(shape, selected));
        }

        if (document is not { } set) {
            return;
        }

        Fields.Add("shape-title").Text = set.Set.Name;

        Fields.Add("fact-row").Add("fact-name").Text = string.Create(
            CultureInfo.InvariantCulture,
            $"{set.Set.Shapes.Length} shape(s), {set.Set.Shapes.Count(static shape => shape.Coarse)} coarse"
        );

        if (set.LoadError is { } error) {
            Fields.Add("shape-error").Text = error;
        }

        // ⚠ The three references the file makes, at the top and editable. Which rig a set was
        // authored against is what everything needing a pose depends on, and a field only a text
        // editor could reach would make "no rig is bound" a message with no way to act on it.
        Line("Rig", set.Set.Rig, value => set.SetField("Rig", static content => content.Rig, static (content, text) => content.Rig = text, value));
        Line("Vocabulary", set.Set.Vocabulary, value => set.SetField("Vocabulary", static content => content.Vocabulary, static (content, text) => content.Vocabulary = text, value));
        Line("Class", set.Set.Class, value => set.SetField("Class", static content => content.Class, static (content, text) => content.Class = text, value));

        if (set.Rig is null) {
            Fields.Add("shape-error").Text = set.Set.Rig.Length == 0
                ? "No rig is bound. Name the model this body is, above; until then names, kinds, sizes and tags "
                + "are still editable and anything that needs a pose — the coarse set, the audit, the gizmos — is not."
                : $"'{set.Set.Rig}' has no skeleton this editor could read, so anything that needs a pose — the "
                + "coarse set, the audit, the gizmos — is unavailable.";
        }

        if (selected is not { } chosen) {
            Fields.Add("text").Text = "Select a shape.";
            return;
        }

        Fields.Add("shape-title").Text = chosen.Name;

        Line("Name", chosen.Name, value => Replace(set, "Rename Shape", shape => shape with { Name = value }));
        Line("Joint", chosen.Joint, value => Replace(set, "Reparent Shape", shape => shape with { Joint = value }));

        var kind = Fields.Add("fact-row");
        kind.Add("fact-name").Text = "Kind";

        var select = kind.Add("fact-value").Add<Select>();

        foreach (var option in Enum.GetValues<ShapeKind>()) {
            select.AddOption(option.ToString());
        }

        select.Value = chosen.Kind.ToString();

        select.SelectionChanged += (_, value) => {
            if (Enum.TryParse<ShapeKind>(value, out var picked)) {
                Replace(set, "Change Shape Kind", shape => shape with { Kind = picked });
            }
        };

        Vector("Offset", chosen.Position, value => Replace(set, "Move Shape", shape => shape with { Position = value }));
        Vector("Extents", chosen.Extents, value => Replace(set, "Resize Shape", shape => shape with { Extents = value }));
        Vector("Top extents", chosen.TopExtents, value => Replace(set, "Resize Shape", shape => shape with { TopExtents = value }));

        Line(
            "Tags",
            string.Join(' ', chosen.Tags),
            value => Replace(
                set,
                "Retag Shape",
                shape => shape with {
                    Tags = value.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries)
                }
            )
        );

        var coarse = Fields.Add("fact-row");
        coarse.Add("fact-name").Text = "Coarse";

        var toggle = coarse.Add("fact-value").Add<ToggleButton>();

        toggle.Label = chosen.Coarse ? "in the coarse set" : "full detail only";
        toggle.IsChecked = chosen.Coarse;

        // The per-shape override D13 asks for: a generated coarse set is a starting point, and this
        // is how somebody disagrees with it on one shape without regenerating anything.
        toggle.CheckedChanged += (_, on) => Replace(set, "Override Coarse Flag", shape => shape with { Coarse = on });
    }

    void Replace(ProxyShapeDocument set, string label, Func<ProxyShapeRecord, ProxyShapeRecord> edit) {
        if (selected is { } shape) {
            selected = set.Edit(shape, label, edit);
        }
    }

    void Audit() {
        Report.Empty();

        if (document is not { } set) {
            return;
        }

        Report.Add("shape-title").Text = "Check";

        var found = set.Audit(Against);

        if (found.Count == 0) {
            Report.Add("text").Text = "Nothing worth saying.";
            return;
        }

        // ⚠ None of these is an error and all of them are worth reading. A shape that never moves is
        // right on a prop; two shapes overlapping is right at a shoulder. What is wanted is a list an
        // author scans once, not a build that refuses.
        foreach (var entry in found) {
            Report.Add("shape-note").Text = entry.Message;
        }
    }

    void Line(string label, string value, Action<string> write) {
        var row = Fields.Add("fact-row");

        row.Add("fact-name").Text = label;

        var box = row.Add("fact-value").Add<TextBox>();

        box.Value = value;
        box.ValueChanged += (_, text) => write(text ?? string.Empty);
    }

    /// <summary>Three numbers on one row, because that is how somebody reads an extent.</summary>
    void Vector(string label, Vector3 value, Action<Vector3> write) {
        var row = Fields.Add("fact-row");

        row.Add("fact-name").Text = label;

        var holder = row.Add("fact-value");
        var current = value;

        Component(holder, current.X, number => write(new(number, current.Y, current.Z)));
        Component(holder, current.Y, number => write(new(current.X, number, current.Z)));
        Component(holder, current.Z, number => write(new(current.X, current.Y, number)));
    }

    static void Component(UiElement holder, float value, Action<float> write) {
        var box = holder.Add<NumericInput>();

        box.Decimals = 3;
        box.Number = value;
        box.NumberChanged += (_, number) => write((float) number);
    }

    void Chosen(ClickEvent args) {
        if (document is not { } set) {
            return;
        }

        for (var element = args.Source; element is not null; element = element.Parent) {
            if (ReferenceEquals(element, AddShape)) {
                selected = set.Add(
                    new() {
                        Name = $"shape {set.Set.Shapes.Length + 1}",
                        Kind = ShapeKind.Sphere,
                        Extents = new(0.1f)
                    }
                );

                args.Handled = true;
                return;
            }

            if (ReferenceEquals(element, RemoveShape)) {
                if (selected is { } shape && set.Remove(shape)) {
                    selected = null;
                }

                args.Handled = true;
                return;
            }

            if (ReferenceEquals(element, MirrorShape)) {
                if (selected is { } shape && set.Mirror(shape) is { } mirrored) {
                    selected = mirrored;
                }

                args.Handled = true;
                return;
            }

            if (ReferenceEquals(element, Coarsen)) {
                set.GenerateCoarse();
                args.Handled = true;

                return;
            }

            if (ReferenceEquals(element, Check)) {
                Audit();
                args.Handled = true;

                return;
            }
        }
    }

    void Pressed(PointerEvent args) {
        if (args.Action != PointerAction.Pressed || args.Button != PointerButton.Primary) {
            return;
        }

        foreach (var (element, shape) in rows) {
            if (element.Bounds.Contains(new Vector2(args.X, args.Y))) {
                selected = shape;
                Restate();

                args.Handled = true;
                return;
            }
        }
    }
}

/// <summary>Opens a body's proxy shapes.</summary>
public sealed class ProxyShapeEditorFactory : IAssetEditorFactory {
    /// <inheritdoc />
    public string Name => "Proxy Shapes";

    /// <inheritdoc />
    public IReadOnlyList<string> Extensions { get; } = [ProxyShapeDocument.Extension];

    /// <inheritdoc />
    public EditorDocument Open(AssetEditorRequest request) {
        ArgumentNullException.ThrowIfNull(request);

        return new ProxyShapeDocument(request.Project, request.Asset, request.Path);
    }

    /// <inheritdoc />
    public UiElement CreateView(EditorDocument document, UiElement panel) {
        ArgumentNullException.ThrowIfNull(panel);

        var view = panel.Add<ProxyShapeView>();
        view.Show((ProxyShapeDocument) document);

        return view;
    }
}
