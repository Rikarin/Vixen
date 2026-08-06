// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Animation;
using Vixen.Animation.StateMachine;
using Vixen.Core.Mathematics;
using Vixen.Editor.AnimationGraph;
using Vixen.Editor.Core;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;

namespace Vixen.Editor.AssetEditors.Animation;

/// <summary>The states of one layer, drawn as boxes with arrows between them.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Drawn rather than built out of elements, for <c>NodeMinimap</c>'s reason.</b> A state
///         is a rounded box with a label and an arrow is a line with a head; as elements they would
///         be a style node, a layout box and a rebind each, for a picture whose only interactions are
///         click and drag. Hit-testing two dozen rectangles is cheaper than laying out two dozen
///         elements, and it is the whole of what this control needs.
///     </para>
///     <para>
///         ⚠ <b>The drag commits once, on release.</b> Moving a state writes its position into the
///         document, and a command per pointer-move would be forty undo entries for one drag — so the
///         box follows the pointer in this control's own field and the document is told when the
///         button comes up. That is the same bargain <c>Timeline</c> makes with its keys.
///     </para>
/// </remarks>
public sealed class StateMapView : UiElement {
    readonly PathBuilder path = new();

    AnimationStateData? dragging;
    Vector2 grab;
    Vector2 offset;

    /// <inheritdoc />
    protected override string TagName => "state-map";

    /// <summary>The document it draws.</summary>
    public AnimationGraphDocument? Graph { get; set; }

    /// <summary>Which layer, by index.</summary>
    public int LayerIndex { get; set; }

    /// <summary>Which state is selected, or <see langword="null" />.</summary>
    public AnimationStateData? Selected { get; set; }

    /// <summary>Raised after the selection changes.</summary>
    public event Action<StateMapView>? SelectionChanged;

    /// <summary>Whether a click adds a transition from the selection instead of selecting.</summary>
    /// <remarks>
    ///     ⚠ <b>A mode rather than a drag from a port, and the reason is that a state has no
    ///     ports.</b> Every transition leaves the whole box and arrives at the whole box; a drag that
    ///     started anywhere on a box could not be told apart from a move.
    /// </remarks>
    public bool Connecting { get; set; }

    /// <summary>How wide a state's box is.</summary>
    public const float BoxWidth = 150f;

    /// <summary>And how tall.</summary>
    public const float BoxHeight = 42f;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        AddHandler<PointerEvent>(static (element, args) => ((StateMapView) element).Pointed(args));
    }

    void Pointed(PointerEvent args) {
        if (Graph?.Layer(LayerIndex) is not { } layer) {
            return;
        }

        var local = new Vector2(args.X - AbsoluteLeft, args.Y - AbsoluteTop);

        switch (args.Action) {
            case PointerAction.Pressed: {
                var hit = At(layer, local);

                if (Connecting && Selected is { } source && hit is { } target && !ReferenceEquals(source, target)) {
                    Graph.AddTransition(source, target.Name);
                    Connecting = false;

                    return;
                }

                Selected = hit;
                SelectionChanged?.Invoke(this);

                if (hit is not null) {
                    dragging = hit;
                    grab = local;
                    offset = new(hit.X, hit.Y);
                }

                break;
            }

            case PointerAction.Moved when dragging is { } state:
                state.X = offset.X + (local.X - grab.X);
                state.Y = offset.Y + (local.Y - grab.Y);

                break;

            case PointerAction.Released when dragging is { } state: {
                var (x, y) = (state.X, state.Y);

                // Put back before the command runs, so the undo entry's "before" is where the box
                // was when the drag started rather than where it already is.
                state.X = offset.X;
                state.Y = offset.Y;

                if (Math.Abs(x - offset.X) > 0.5f || Math.Abs(y - offset.Y) > 0.5f) {
                    Graph.MoveState(state, x, y);
                }

                dragging = null;
                break;
            }

            default:
                break;
        }
    }

    /// <summary>The state whose box contains a point, or <see langword="null" />.</summary>
    /// <param name="layer">The layer.</param>
    /// <param name="point">The point, relative to this control.</param>
    /// <returns>The state.</returns>
    public static AnimationStateData? At(AnimationLayerData layer, Vector2 point) {
        ArgumentNullException.ThrowIfNull(layer);

        // Backwards, so the box drawn on top is the one that is hit.
        for (var index = layer.States.Count - 1; index >= 0; index--) {
            var state = layer.States[index];

            if (point.X >= state.X && point.X <= state.X + BoxWidth
                && point.Y >= state.Y && point.Y <= state.Y + BoxHeight) {
                return state;
            }
        }

        return null;
    }

    readonly List<UiElement> boxes = [];

    /// <summary>Puts an element over each state's box, so the names can be read.</summary>
    /// <remarks>
    ///     ⚠ <b>The boxes are elements and the arrows are drawn, and the split is forced by
    ///     text.</b> <c>DrawContext</c> fills, strokes and blits images and deliberately does not draw
    ///     text — text in this framework is an element with a layout box, because that is what makes
    ///     it selectable, styleable and measurable. So a state, which is a rectangle with two lines in
    ///     it, has to be an element; an arrow, which has no text at all, must not be one.
    /// </remarks>
    public void Refresh() {
        if (Graph?.Layer(LayerIndex) is not { } layer) {
            foreach (var box in boxes) {
                box.AddClass("parked");
            }

            return;
        }

        while (boxes.Count < layer.States.Count) {
            var box = Add("state-box");

            box.Add("state-box-name");
            box.Add("state-box-motion");

            boxes.Add(box);
        }

        for (var index = 0; index < boxes.Count; index++) {
            var box = boxes[index];

            if (index >= layer.States.Count) {
                box.AddClass("parked");

                continue;
            }

            var state = layer.States[index];

            box.RemoveClass("parked");
            box.SetClass("selected", ReferenceEquals(state, Selected));
            box.SetClass("default", string.Equals(layer.Default, state.Name, StringComparison.Ordinal));

            box.Children[0].Text = state.Name;
            box.Children[1].Text = Motion(state);

            Place(box, state.X, state.Y);
        }
    }

    /// <summary>Puts a box where its state says, as inline style over the absolute layout.</summary>
    /// <remarks>
    ///     ⚠ <b>Inline style rather than a layout property, which is what an absolutely-positioned
    ///     element in this framework takes.</b> <c>Vixen.Ui</c>'s layout is CSS's, so "where" is
    ///     <c>left</c> and <c>top</c> against a positioned ancestor — the map itself, which the theme
    ///     gives <c>position: relative</c>.
    /// </remarks>
    static void Place(UiElement box, float x, float y) {
        box.SetStyle("left", x.ToString("0.##", CultureInfo.InvariantCulture) + "px");
        box.SetStyle("top", y.ToString("0.##", CultureInfo.InvariantCulture) + "px");
    }

    /// <inheritdoc />
    protected override void OnDraw(DrawContext context) {
        base.OnDraw(context);

        if (Graph?.Layer(LayerIndex) is not { } layer) {
            return;
        }

        // ⚠ Placed from here rather than from a handler on the document, because a drag moves the
        // model without going through one — the box has to follow the pointer on the frame the
        // pointer moved, and this is the frame.
        Refresh();

        var origin = new Vector2(context.Bounds.X, context.Bounds.Y);

        foreach (var state in layer.States) {
            foreach (var transition in state.Transitions) {
                if (layer.States.Find(entry => string.Equals(entry.Name, transition.To, StringComparison.Ordinal))
                    is not { } destination) {
                    continue;
                }

                Arrow(context, origin, state, destination, transition);
            }
        }
    }

    void Arrow(
        DrawContext context,
        Vector2 origin,
        AnimationStateData from,
        AnimationStateData to,
        AnimationTransitionData transition
    ) {
        var start = origin + new Vector2(from.X + (BoxWidth * 0.5f), from.Y + (BoxHeight * 0.5f));
        var end = origin + new Vector2(to.X + (BoxWidth * 0.5f), to.Y + (BoxHeight * 0.5f));

        var direction = end - start;
        var length = direction.Length();

        if (length < 1e-3f) {
            return;
        }

        direction /= length;

        // ⚠ Offset sideways, so the two arrows of a pair — idle→walk and walk→idle — do not lie on
        // top of each other. A state machine has more pairs than singles and a picture that drew one
        // line for both would be a picture that cannot say which conditions belong to which way.
        var side = new Vector2(-direction.Y, direction.X) * 6f;

        var a = start + (direction * BoxHeight * 0.6f) + side;
        var b = end - (direction * BoxHeight * 0.6f) + side;

        path.Clear().MoveTo(a).LineTo(b);
        context.Stroke(path, WireColour, transition.Conditions.Count > 0 ? 1.6f : 1f);

        // The head, as two short strokes rather than a filled triangle: `PathBuilder`'s fill has no
        // winding rule, and two lines say "this way" as clearly at this size.
        var back = -direction * 8f;
        var wing = new Vector2(-direction.Y, direction.X) * 4f;

        path.Clear().MoveTo(b).LineTo(b + back + wing).MoveTo(b).LineTo(b + back - wing);
        context.Stroke(path, WireColour, 1.6f);
    }

    static string Motion(AnimationStateData state) => state.Motion.Kind switch {
        AnimationMotionKind.Blend1D => $"blend 1D · {state.Motion.Children.Count} clip(s)",
        AnimationMotionKind.Blend2D => $"blend 2D · {state.Motion.Children.Count} clip(s)",
        _ => state.Motion.Clip.IsEmpty ? "no clip" : state.Motion.Clip.ToString()
    };

    static Color4 WireColour => new(0.55f, 0.58f, 0.63f, 1f);
}

/// <summary>An animation graph, open for editing: layers, states, transitions and parameters.</summary>
public sealed class AnimationGraphView : Control {
    AnimationGraphDocument? document;
    AnimationTransitionData? transition;

    /// <inheritdoc />
    protected override string TagName => "animgraph-editor";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>The state map.</summary>
    public StateMapView Map { get; private set; } = null!;

    /// <summary>The column beside it.</summary>
    public UiElement Side { get; private set; } = null!;

    /// <summary>The layers.</summary>
    public Select Layers { get; private set; } = null!;

    /// <summary>The selected state's or transition's fields.</summary>
    public UiElement Fields { get; private set; } = null!;

    /// <summary>The parameters.</summary>
    public UiElement Parameters { get; private set; } = null!;

    /// <summary>What compiling said.</summary>
    public UiElement Diagnostics { get; private set; } = null!;

    /// <summary>Adds a state.</summary>
    public Button AddState { get; private set; } = null!;

    /// <summary>Latches connect mode.</summary>
    public ToggleButton Connect { get; private set; } = null!;

    /// <summary>Adds a layer.</summary>
    public Button AddLayer { get; private set; } = null!;

    /// <summary>Adds a parameter.</summary>
    public Button AddParameter { get; private set; } = null!;

    /// <summary>Compiles the graph.</summary>
    public Button Build { get; private set; } = null!;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        var bar = Part("animgraph-bar");

        bar.Add("fact-name").Text = "Layer";
        Layers = bar.Add<Select>();

        AddLayer = Push(bar, "Add Layer");
        AddState = Push(bar, "Add State");

        Connect = bar.Add<ToggleButton>();
        Connect.Label = "Transition";

        AddParameter = Push(bar, "Add Parameter");
        Build = Push(bar, "Compile");

        var body = Part("animgraph-body");

        Map = body.Add<StateMapView>();
        Side = body.Add("animgraph-side");
        Fields = Side.Add("animgraph-fields");
        Parameters = Side.Add("animgraph-parameters");
        Diagnostics = Side.Add("analysis-list");

        Layers.SelectionChanged += (_, _) => {
            Map.LayerIndex = Math.Max(0, Layers.IndexOfSelection());
            Map.Selected = null;
            transition = null;

            Restate();
        };

        Connect.CheckedChanged += (_, on) => Map.Connecting = on;

        Map.SelectionChanged += _ => {
            transition = null;
            Connect.IsChecked = Map.Connecting;

            Restate();
        };

        AddHandler<ClickEvent>(static (element, args) => ((AnimationGraphView) element).Chosen(args));
    }

    static Button Push(UiElement bar, string label) {
        var button = bar.Add<Button>();

        button.Label = label;
        button.Size = ControlSize.Small;
        button.Variant = ControlVariant.Subtle;

        return button;
    }

    /// <summary>Shows a graph.</summary>
    /// <param name="graph">The document.</param>
    public void Show(AnimationGraphDocument graph) {
        ArgumentNullException.ThrowIfNull(graph);

        if (document is { } previous) {
            previous.Changed -= Reload;
        }

        document = graph;
        graph.Changed += Reload;

        Map.Graph = graph;
        Reload(graph);

        // Compiled on open rather than waiting to be asked, for `VfxGraphView`'s reason: opening is
        // not an edit, and a diagnostics list that stayed empty until a button was found reads as a
        // graph with nothing wrong with it.
        Compile();
    }

    /// <summary>Rebuilds the panel from the document.</summary>
    /// <param name="graph">The document.</param>
    public void Reload(AnimationGraphDocument graph) {
        ArgumentNullException.ThrowIfNull(graph);

        var chosen = Layers.IndexOfSelection();

        Layers.ClearOptions();

        foreach (var layer in graph.Graph.Layers) {
            Layers.AddOption(layer.Name);
        }

        Map.LayerIndex = Math.Clamp(chosen < 0 ? 0 : chosen, 0, Math.Max(0, graph.Graph.Layers.Count - 1));

        if (graph.Graph.Layers.Count > 0) {
            Layers.Value = graph.Graph.Layers[Map.LayerIndex].Name;
        }

        Restate();
    }

    /// <summary>Compiles the graph and lists what it said.</summary>
    /// <returns>The number of complaints.</returns>
    public int Compile() {
        if (document is not { } graph) {
            return 0;
        }

        graph.Compile();

        while (Diagnostics.Children.Count > 0) {
            Diagnostics.Children[^1].Remove();
        }

        if (graph.LoadError is { } error) {
            Line("read", error, warning: false);
        }

        foreach (var diagnostic in graph.Diagnostics) {
            Line(diagnostic.Id, diagnostic.Message + Where(diagnostic), warning: true);
        }

        if (graph.Artefact is { } artefact) {
            Line(
                "graph",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{artefact.Layers.Count} layer(s), {artefact.Parameters.Count} parameter(s)."
                ),
                warning: false
            );
        }

        return graph.Diagnostics.Count;

        static string Where(AnimationGraphDiagnostic diagnostic) =>
            diagnostic.State.Length > 0 ? $"  ({diagnostic.Layer}/{diagnostic.State})"
                : diagnostic.Layer.Length > 0 ? $"  ({diagnostic.Layer})"
                : string.Empty;
    }

    void Line(string stage, string message, bool warning) {
        var row = Diagnostics.Add("analysis-row");

        if (warning) {
            row.AddClass("warning");
        }

        row.Add("analysis-stage").Text = stage;
        row.Add("analysis-message").Text = message;
    }

    /// <summary>Rebuilds the fields for whatever is selected, and the parameter list.</summary>
    public void Restate() {
        while (Fields.Children.Count > 0) {
            Fields.Children[^1].Remove();
        }

        while (Parameters.Children.Count > 0) {
            Parameters.Children[^1].Remove();
        }

        if (document is not { } graph) {
            return;
        }

        Parameters.Add("animgraph-title").Text = "Parameters";

        foreach (var parameter in graph.Graph.Parameters) {
            var row = Parameters.Add("fact-row");
            row.Add("fact-name").Text = parameter.Name;

            var value = row.Add("fact-value").Add("text");
            value.Text = parameter.Type.ToString();
        }

        if (graph.Layer(Map.LayerIndex) is not { } layer) {
            Fields.Add("text").Text = "This graph has no layers.";

            return;
        }

        LayerFields(graph, layer);

        if (Map.Selected is not { } state) {
            Fields.Add("text").Text = "Select a state.";

            return;
        }

        StateFields(graph, layer, state);
    }

    void LayerFields(AnimationGraphDocument graph, AnimationLayerData layer) {
        Fields.Add("animgraph-title").Text = "Layer";

        Number("Weight", layer.Weight, value => graph.Edit("Set Layer Weight", () => layer.Weight = value));

        Choice("Blend", layer.Blend, value => graph.Edit("Set Layer Blend", () => layer.Blend = value));

        var root = Fields.Add("fact-row");
        root.Add("fact-name").Text = "Root motion";

        var toggle = root.Add("fact-value").Add<CheckBox>();
        toggle.IsChecked = layer.ContributesRootMotion;

        toggle.CheckedChanged += (_, on) =>
            graph.Edit("Set Root Motion", () => layer.ContributesRootMotion = on);

        Line("Mask", string.Join(", ", layer.Mask), value => graph.Edit(
            "Set Layer Mask",
            () => layer.Mask = [.. value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]
        ));
    }

    void StateFields(AnimationGraphDocument graph, AnimationLayerData layer, AnimationStateData state) {
        Fields.Add("animgraph-title").Text = "State";

        Line("Name", state.Name, value => Rename(graph, layer, state, value));
        Number("Speed", state.Speed, value => graph.Edit("Set State Speed", () => state.Speed = value));
        Choice("Wrap", state.Wrap, value => graph.Edit("Set Wrap Mode", () => state.Wrap = value));

        var start = Fields.Add("fact-row");
        start.Add("fact-name").Text = "Default";

        var isDefault = start.Add("fact-value").Add<CheckBox>();
        isDefault.IsChecked = string.Equals(layer.Default, state.Name, StringComparison.Ordinal);

        isDefault.CheckedChanged += (_, on) => {
            if (on) {
                graph.Edit("Set Default State", () => layer.Default = state.Name);
            }
        };

        Choice("Motion", state.Motion.Kind, value => graph.Edit("Set Motion Kind", () => state.Motion.Kind = value));

        if (state.Motion.Kind == AnimationMotionKind.Clip) {
            Line(
                "Clip",
                state.Motion.Clip.IsEmpty ? string.Empty : state.Motion.Clip.ToString(),
                value => graph.Edit(
                    "Set Clip",
                    () => state.Motion.Clip = Vixen.Core.AssetId.TryParse(value, out var id) ? id : default
                )
            );
        } else {
            Line(
                "Parameter X",
                state.Motion.ParameterX,
                value => graph.Edit("Set Blend Parameter", () => state.Motion.ParameterX = value)
            );

            if (state.Motion.Kind == AnimationMotionKind.Blend2D) {
                Line(
                    "Parameter Y",
                    state.Motion.ParameterY,
                    value => graph.Edit("Set Blend Parameter", () => state.Motion.ParameterY = value)
                );
            }
        }

        Fields.Add("animgraph-title").Text = "Transitions";

        foreach (var leaving in state.Transitions.ToList()) {
            var row = Fields.Add("fact-row");

            row.SetClass("selected", ReferenceEquals(leaving, transition));
            row.Add("fact-name").Text = "→ " + (leaving.To.Length > 0 ? leaving.To : "(nowhere)");

            var pick = row.Add("fact-value").Add<Button>();

            pick.Label = ReferenceEquals(leaving, transition) ? "Editing" : "Edit";
            pick.Size = ControlSize.Small;
            pick.Variant = ControlVariant.Subtle;

            pick.Clicked += _ => {
                transition = ReferenceEquals(leaving, transition) ? null : leaving;
                Restate();
            };
        }

        if (transition is { } editing && state.Transitions.Contains(editing)) {
            TransitionFields(graph, state, editing);
        }
    }

    void TransitionFields(AnimationGraphDocument graph, AnimationStateData state, AnimationTransitionData editing) {
        Fields.Add("animgraph-title").Text = "Transition";

        Number("Duration", editing.Duration, value => graph.Edit("Set Transition Duration", () => editing.Duration = value));

        var exit = Fields.Add("fact-row");
        exit.Add("fact-name").Text = "Has exit time";

        var toggle = exit.Add("fact-value").Add<CheckBox>();
        toggle.IsChecked = editing.HasExitTime;
        toggle.CheckedChanged += (_, on) => graph.Edit("Set Exit Time", () => editing.HasExitTime = on);

        Number("Exit time", editing.ExitTime, value => graph.Edit("Set Exit Time", () => editing.ExitTime = value));
        Number("Offset", editing.Offset, value => graph.Edit("Set Transition Offset", () => editing.Offset = value));

        Choice(
            "Interruption",
            editing.Interruption,
            value => graph.Edit("Set Interruption", () => editing.Interruption = value)
        );

        Fields.Add("animgraph-title").Text = "Conditions";

        foreach (var condition in editing.Conditions.ToList()) {
            var row = Fields.Add("fact-row");
            var name = row.Add("fact-value").Add<Select>();

            foreach (var parameter in graph.Graph.Parameters) {
                name.AddOption(parameter.Name);
            }

            name.Value = condition.Parameter;
            name.SelectionChanged += (_, value) =>
                graph.Edit("Set Condition", () => condition.Parameter = value ?? string.Empty);

            var mode = row.Add("fact-value").Add<Select>();

            foreach (var option in Enum.GetValues<AnimationConditionMode>()) {
                mode.AddOption(option.ToString());
            }

            mode.Value = condition.Mode.ToString();

            mode.SelectionChanged += (_, value) => {
                if (Enum.TryParse<AnimationConditionMode>(value, out var parsed)) {
                    graph.Edit("Set Condition", () => condition.Mode = parsed);
                }
            };

            var threshold = row.Add("fact-value").Add<NumericInput>();

            threshold.Decimals = 3;
            threshold.Number = condition.Threshold;
            threshold.NumberChanged += (_, value) =>
                graph.Edit("Set Condition", () => condition.Threshold = (float) value);
        }

        var add = Fields.Add("fact-row").Add("fact-value").Add<Button>();

        add.Label = "Add condition";
        add.Size = ControlSize.Small;
        add.Variant = ControlVariant.Subtle;

        add.Clicked += _ => graph.Edit(
            "Add Condition",
            () => editing.Conditions.Add(new() {
                Parameter = graph.Graph.Parameters.Count > 0 ? graph.Graph.Parameters[0].Name : string.Empty
            })
        );

        var remove = Fields.Add("fact-row").Add("fact-value").Add<Button>();

        remove.Label = "Remove transition";
        remove.Size = ControlSize.Small;
        remove.Variant = ControlVariant.Subtle;

        remove.Clicked += _ => {
            graph.RemoveTransition(state, editing);
            transition = null;
        };
    }

    /// <summary>Renaming a state moves every transition that names it.</summary>
    /// <remarks>
    ///     ⚠ <b>Otherwise renaming a state silently breaks every arrow into it.</b> The document
    ///     cross-references by name — see <c>AnimationGraphAsset</c> for why — so a rename is a
    ///     rewrite, and it has to be the same undo entry as the rename or an undo would fix the name
    ///     and leave the arrows pointing at the old one.
    /// </remarks>
    static void Rename(AnimationGraphDocument graph, AnimationLayerData layer, AnimationStateData state, string name) {
        if (name.Length == 0 || string.Equals(state.Name, name, StringComparison.Ordinal)) {
            return;
        }

        var was = state.Name;

        graph.Edit(
            "Rename State",
            () => {
                state.Name = name;

                if (string.Equals(layer.Default, was, StringComparison.Ordinal)) {
                    layer.Default = name;
                }

                foreach (var source in layer.States) {
                    foreach (var leaving in source.Transitions) {
                        if (string.Equals(leaving.To, was, StringComparison.Ordinal)) {
                            leaving.To = name;
                        }
                    }
                }

                foreach (var any in layer.AnyState) {
                    if (string.Equals(any.To, was, StringComparison.Ordinal)) {
                        any.To = name;
                    }
                }
            }
        );
    }

    void Line(string label, string value, Action<string> write) {
        var row = Fields.Add("fact-row");
        row.Add("fact-name").Text = label;

        var box = row.Add("fact-value").Add<TextBox>();

        box.Value = value;
        box.ValueChanged += (_, text) => write(text ?? string.Empty);
    }

    void Number(string label, float value, Action<float> write) {
        var row = Fields.Add("fact-row");
        row.Add("fact-name").Text = label;

        var box = row.Add("fact-value").Add<NumericInput>();

        box.Decimals = 3;
        box.Number = value;
        box.NumberChanged += (_, number) => write((float) number);
    }

    void Choice<T>(string label, T value, Action<T> write) where T : struct, Enum {
        var row = Fields.Add("fact-row");
        row.Add("fact-name").Text = label;

        var select = row.Add("fact-value").Add<Select>();

        foreach (var option in Enum.GetValues<T>()) {
            select.AddOption(option.ToString());
        }

        select.Value = value.ToString();

        select.SelectionChanged += (_, chosen) => {
            if (Enum.TryParse<T>(chosen, out var parsed)) {
                write(parsed);
            }
        };
    }

    void Chosen(ClickEvent args) {
        if (document is not { } graph) {
            return;
        }

        for (var element = args.Source; element is not null; element = element.Parent) {
            if (ReferenceEquals(element, AddLayer)) {
                graph.AddLayer("Layer");
                args.Handled = true;

                return;
            }

            if (ReferenceEquals(element, AddState) && graph.Layer(Map.LayerIndex) is { } layer) {
                // Placed where there is room rather than at the origin, so pressing it twice does not
                // stack two boxes on each other.
                graph.AddState(layer, "State", 40f + (layer.States.Count % 4 * 180f), 40f + (layer.States.Count / 4 * 80f));
                args.Handled = true;

                return;
            }

            if (ReferenceEquals(element, AddParameter)) {
                graph.AddParameter("Parameter", AnimationParameterType.Float);
                args.Handled = true;

                return;
            }

            if (ReferenceEquals(element, Build)) {
                Compile();
                args.Handled = true;

                return;
            }
        }
    }
}

/// <summary>Opens an animation graph.</summary>
public sealed class AnimationGraphEditorFactory : IAssetEditorFactory {
    /// <inheritdoc />
    public string Name => "Animation Graph";

    /// <inheritdoc />
    public IReadOnlyList<string> Extensions { get; } = [AnimationGraphDocument.Extension];

    /// <inheritdoc />
    public EditorDocument Open(AssetEditorRequest request) {
        ArgumentNullException.ThrowIfNull(request);

        return new AnimationGraphDocument(request.Project, request.Asset, request.Path);
    }

    /// <inheritdoc />
    public UiElement CreateView(EditorDocument document, UiElement panel) {
        ArgumentNullException.ThrowIfNull(panel);

        // ⚠ `StateMapView` places its boxes absolutely inside a clipped box and hit-tests them with
        // `args.X - AbsoluteLeft`, so it fails the same way a node canvas would even though it has no
        // pan of its own: a scroll offset it does not know about moves every state under the cursor.
        DockPanel.Fills(panel);

        var view = panel.Add<AnimationGraphView>();
        view.Show((AnimationGraphDocument) document);

        return view;
    }
}
