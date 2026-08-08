// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core.Reflection;
using Vixen.Input;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;

namespace Vixen.Samples.HelloUi;

/// <summary>What the sample is a picture of: a small editor, built out of the control set.</summary>
/// <remarks>
///     <para>
///         <b>No platform, no device, no window.</b> This file constructs a
///         <see cref="UiDocument" /> and nothing else, which is the point doc 02 makes about this
///         sample existing at all — the UI framework has to be usable, and testable, without any of
///         the machinery that eventually puts it on a screen. <c>Program.cs</c> is the half that
///         needs a GPU, and the two share exactly one type.
///     </para>
///     <para>
///         It is an editor shell rather than a gallery, because a gallery proves the controls draw
///         and an editor shell proves they <i>work together</i>: a menu bar over a docking host, a
///         virtualised tree in one panel, a property grid in another, a form of standard controls in
///         a third, and toasts over the lot. Doc 09's perf gate is the editor shell for the same
///         reason.
///     </para>
/// </remarks>
public sealed class Shell : IDisposable {
    readonly ToastHost toasts;
    readonly DockingHost docking;
    readonly TreeView tree;
    readonly PropertyGrid grid;
    readonly ProgressBar progress;
    readonly Spinner spinner;
    readonly Material material = new();

    float phase;

    /// <summary>Builds the interface into a new document.</summary>
    /// <param name="width">The surface's width in device-independent pixels.</param>
    /// <param name="height">Its height.</param>
    public Shell(float width, float height) {
        Document = new UiDocument(width, height);

        ControlTheme.Install(Document);
        AdvancedTheme.Install(Document);

        // The sample's own sheet, loaded as an author stylesheet — which is the arrangement the
        // whole theme is designed around. Nothing below out-specifies anything; a plain `root { … }`
        // beats the user-agent rule because of where it came from.
        Document.Load(Style);

        var shell = Document.Root.Add<Panel>();
        shell.AddClass("shell");

        BuildMenus(shell);

        docking = shell.Add<DockingHost>();
        tree = BuildHierarchy();
        grid = BuildInspector();

        BuildControls(out progress, out spinner);

        // ⚠ After the panels, not before. `AddPanel` puts a panel the arrangement does not already
        // mention into the first group, so registering three panels against an empty layout gives
        // one group of three tabs — correct, and not an editor. Applying the arrangement afterwards
        // is also the order an application uses when it has no saved layout to restore.
        docking.SetLayout(DefaultLayout());

        toasts = Document.Root.Add<ToastHost>();
        toasts.Show("Welcome to Vixen", ControlVariant.Primary, TimeSpan.FromSeconds(6));
    }

    /// <summary>The document the host lays out, draws and dispatches into.</summary>
    public UiDocument Document { get; }

    /// <summary>The docking host, so the host can save the layout on the way out.</summary>
    public DockingHost Docking => docking;

    /// <summary>Advances whatever moves by itself.</summary>
    /// <param name="now">The time since the application started.</param>
    /// <param name="delta">How long the last frame took.</param>
    /// <remarks>
    ///     ⚠ <b>The host drives the clock, and every timed control in the set is built that way.</b>
    ///     Nothing in <c>Vixen.Ui</c> knows what time it is except through an input event, so a
    ///     spinner that spins and a toast that expires both need telling — see
    ///     <c>ProgressBar.Phase</c> and <c>ToastHost.Tick</c>. It is the same bargain
    ///     <c>GestureRecognizer.Tick</c> makes, and it is what keeps a golden image of a control
    ///     from depending on what time it was taken.
    /// </remarks>
    public void Tick(TimeSpan now, TimeSpan delta) {
        phase += (float) delta.TotalSeconds;

        spinner.Phase = phase % 1f;
        progress.Value = ((MathF.Sin(phase) * 0.5f) + 0.5f) * progress.Maximum;

        // ⚠ The document's tick, not the recogniser's — the same correction `EditorShell.Tick`
        // needed and for the same reasons. It raises `UiDocument.Ticked`, which is what an `Overlay`
        // and a `Toasts` expire on, and it advances the animator, without which a declared CSS
        // transition sticks at the value it was leaving rather than jumping to the new one. A
        // template's frame loop is copied, so the sample getting it wrong is every game getting it
        // wrong.
        Document.Tick(now);
        toasts.Tick(now);
    }

    /// <summary>Changes the surface's size.</summary>
    /// <param name="width">The new width.</param>
    /// <param name="height">Its height.</param>
    public void Resize(float width, float height) {
        Document.Resize(width, height);

        // ⚠ Explicit, and this is the gap the advanced README names: nothing tells an element that
        // its box changed, so a virtualiser realises against the previous size until something asks
        // it to look again. A "layout finished" callback on UiDocument closes it; until then the
        // caller who resized is the one who knows.
        Document.Update();
        tree.Refresh();
    }

    /// <inheritdoc />
    public void Dispose() => Document.Dispose();

    void BuildMenus(UiElement shell) {
        var bar = shell.Add<MenuBar>();

        var file = Listen(bar.AddMenu("File"));
        file.AddItem("New").ShowShortcut(InputKey.N, ModifierKeys.Control);
        file.AddItem("Open…").ShowShortcut(InputKey.O, ModifierKeys.Control);
        file.AddSeparator();

        var recent = Listen(file.AddSubmenu("Open Recent"));
        recent.AddItem("shader-graph.vixen");
        recent.AddItem("terrain.vixen");

        file.AddSeparator();
        file.AddItem("Save").ShowShortcut(InputKey.S, ModifierKeys.Control);

        var view = Listen(bar.AddMenu("View"));
        view.AddItem("Reset Layout");
        view.AddItem("Toggle Dark");
    }

    /// <summary>Makes a menu report the command chosen from it.</summary>
    /// <param name="menu">The menu, as returned by <see cref="MenuBar.AddMenu" /> or <see cref="Menu.AddSubmenu" />.</param>
    /// <returns>The same menu, so it reads as a wrapper around the call that made it.</returns>
    /// <remarks>
    ///     ⚠ <b>On the menu, not on the bar</b> — one handler per menu rather than one for the whole
    ///     bar, which is not the arrangement a routed event usually wants but is the only one that
    ///     works here. <see cref="MenuBar.AddMenu" /> hangs its dropdown off <c>Document.Root</c>, and
    ///     <see cref="Menu.AddSubmenu" /> does the same, precisely so a menu can spill past whatever
    ///     dropped it without being clipped by it. The bar is therefore <i>not</i> an ancestor of its
    ///     own items, and a <see cref="ClickEvent" /> from a <see cref="MenuItem" /> bubbles to the
    ///     menu and then straight to the root — never through the bar. A handler on the bar sees the
    ///     <see cref="MenuBarItem" /> clicks that open the menus and nothing else, so the commands do
    ///     nothing at all, silently.
    ///     <para>
    ///         Per <i>menu</i> is still not per item: a menu rebuilt at run time is one subscription
    ///         to redo, not one per line, which is the part of the routed event that survives the
    ///         overlay's parenting.
    ///     </para>
    /// </remarks>
    Menu Listen(Menu menu) {
        menu.AddHandler<ClickEvent>(
            (_, args) => {
                // An item that opens a submenu was not a command being chosen — the click is what
                // opens the submenu, and reporting it would announce "Open Recent" as if it were.
                if (args.Source is MenuItem { Submenu: null, Label: { } label }) {
                    Chose(label);
                }
            }
        );

        return menu;
    }

    void Chose(string label) {
        switch (label) {
            case "Reset Layout":
                docking.SetLayout(DefaultLayout());
                toasts.Show("Layout reset");

                break;

            case "Toggle Dark":
                if (!Document.Root.RemoveClass("dark")) {
                    Document.Root.AddClass("dark");
                }

                break;

            default:
                toasts.Show(label);
                break;
        }
    }

    static DockLayout DefaultLayout() =>
        new() {
            Root = new DockSplitNode(
                Orientation.Horizontal,
                new DockGroupNode("hierarchy"),
                new DockSplitNode(
                    Orientation.Horizontal,
                    new DockGroupNode("controls"),
                    new DockGroupNode("inspector"),
                    0.65f
                ),
                0.22f
            )
        };

    TreeView BuildHierarchy() {
        var panel = docking.AddPanel("hierarchy", "Hierarchy");
        var view = panel.Add<TreeView>();

        var scene = view.Root.Add("Scene");
        var lights = scene.Add("Lights");

        lights.Add("Key");
        lights.Add("Fill");
        lights.Add("Rim");

        var geometry = scene.Add("Geometry");

        // ⚠ Deliberately more rows than anybody will scroll through, because that is the claim being
        // demonstrated: the tree holds a thousand nodes and realises about thirty rows.
        for (var i = 0; i < 1_000; i++) {
            geometry.Add($"Rock_{i:D4}");
        }

        view.Refresh();

        // ⚠ Through the view rather than by setting `IsExpanded`. Expanding is what makes a node's
        // children visible, and what is visible is what the virtualiser realises — so it is the
        // view's operation, and the model's flag is the view's to write.
        view.Expand(scene);
        view.Expand(lights);
        view.Activated += (_, node) => toasts.Show($"Opened {node.Text}");

        return view;
    }

    PropertyGrid BuildInspector() {
        var panel = docking.AddPanel("inspector", "Inspector");
        var inspector = panel.Add<PropertyGrid>();

        // A hand-written descriptor, because `Vixen.Core.Reflection.Generator` runs over types that
        // carry `[DataContract]` and this sample deliberately references nothing that would bring
        // the attribute in. The shape is the generator's, member for member.
        TypeRegistry.Register(Material.Describe());
        inspector.Inspect(material);

        return inspector;
    }

    void BuildControls(out ProgressBar bar, out Spinner wheel) {
        var panel = docking.AddPanel("controls", "Controls");

        // The gallery brings its own scroller, and a panel scrolls by default — so this says which of
        // the two does it, rather than shipping a sample with one inside the other.
        panel.Scrolls = false;

        var scroller = panel.Add<ScrollView>();
        scroller.AddClass("gallery");

        var content = scroller.Content;

        var card = content.Add<Card>();
        card.Header.Add<TextBlock>().Text = "Buttons";

        var row = card.Body.Add<Panel>();
        row.AddClass("row");

        foreach (var variant in (ReadOnlySpan<ControlVariant>) [
            ControlVariant.Default,
            ControlVariant.Primary,
            ControlVariant.Success,
            ControlVariant.Danger,
            ControlVariant.Subtle
        ]) {
            var button = row.Add<Button>();
            button.Label = variant.ToString();
            button.Variant = variant;
            button.Clicked += control => toasts.Show(
                $"{((Button) control).Label} pressed",
                variant == ControlVariant.Danger ? ControlVariant.Danger : ControlVariant.Default
            );
        }

        var toggles = content.Add<Card>();
        toggles.Header.Add<TextBlock>().Text = "State";

        var check = toggles.Body.Add<CheckBox>();
        check.Label = "Cast shadows";
        check.IsChecked = true;

        var partial = toggles.Body.Add<CheckBox>();
        partial.Label = "Receive shadows";
        partial.IsIndeterminate = true;

        var flip = toggles.Body.Add<Switch>();
        flip.Label = "Wireframe";

        var quality = toggles.Body.Add<RadioGroup>();
        quality.AddOption("low", "Low");
        quality.AddOption("medium", "Medium");
        quality.AddOption("high", "High");
        quality.Value = "medium";

        var fields = content.Add<Card>();
        fields.Header.Add<TextBlock>().Text = "Fields";

        var name = fields.Body.Add<TextBox>();
        name.Placeholder = "Name";
        name.Value = "Standard Material";

        var search = fields.Body.Add<SearchBox>();
        search.Placeholder = "Filter assets";

        var count = fields.Body.Add<NumericInput>();
        count.Minimum = 0;
        count.Maximum = 64;
        count.Number = 8;

        var pick = fields.Body.Add<Select>();
        pick.Placeholder = "Blend mode";
        pick.AddOption("opaque", "Opaque");
        pick.AddOption("cutout", "Cutout");
        pick.AddOption("blend", "Blend");
        pick.Value = "opaque";

        var slider = fields.Body.Add<Slider>();
        slider.Value = 0.35f;

        var range = fields.Body.Add<RangeSlider>();
        range.Low = 0.2f;
        range.High = 0.8f;

        var feedback = content.Add<Card>();
        feedback.Header.Add<TextBlock>().Text = "Feedback";

        bar = feedback.Body.Add<ProgressBar>();
        wheel = feedback.Body.Add<Spinner>();

        var alert = feedback.Body.Add<Alert>();
        alert.Variant = ControlVariant.Danger;
        alert.Title = "Shader failed to compile";
        alert.Message = "Ui/Msdf.rvn(42): expected ';'";

        var tabs = content.Add<Tabs>();
        tabs.AddTab("Surface").Panel.Add<TextBlock>().Text = "Metallic-roughness";
        tabs.AddTab("Shading").Panel.Add<TextBlock>().Text = "Standard";
        tabs.AddTab("Layers").Panel.Add<TextBlock>().Text = "None";

        var section = content.Add<Accordion>();
        section.AddSection("Advanced").Content.Add<TextBlock>().Text = "Nothing here yet.";

        var trail = content.Add<Breadcrumb>();
        trail.AddStep("Assets");
        trail.AddStep("Materials");
        trail.AddStep("Standard");

        var pages = content.Add<Pagination>();
        pages.PageCount = 12;
    }

    /// <summary>The sample's own stylesheet, over the two the controls bring.</summary>
    const string Style = """
        root { padding: 0px; background-color: var(--surface-sunken); }
        .shell { flex-direction: column; flex-grow: 1; }

        menu-bar {
            flex-shrink: 0;
            padding: 2px 4px;
            border-width: 0px 0px 1px 0px;
            border-color: var(--border);
            background-color: var(--surface);
        }

        .gallery { flex-grow: 1; flex-basis: 0px; padding: 10px; }
        .gallery scroll-content { flex-direction: column; gap: 10px; padding-right: 12px; }

        .row { flex-direction: row; gap: 8px; }
        card-body { gap: 8px; }

        tree-view { flex-grow: 1; }
        property-grid { flex-grow: 1; padding: 8px; }
        """;
}

/// <summary>Something for the inspector to inspect.</summary>
/// <remarks>
///     Its descriptor is written by hand rather than generated, for the reason
///     <c>Shell.BuildInspector</c> gives — and writing it out is itself worth seeing,
///     because it is exactly what <c>Vixen.Core.Reflection.Generator</c> emits: a name, a type, two
///     lambdas over a cast, and what the inspector should make of it.
/// </remarks>
public sealed class Material {
    /// <summary>Whether it is drawn at all.</summary>
    public bool Visible { get; set; } = true;

    /// <summary>What it is called.</summary>
    public string Name { get; set; } = "Standard";

    /// <summary>How rough the surface is, from nothing to entirely.</summary>
    public float Roughness { get; set; } = 0.4f;

    /// <summary>How metallic it is.</summary>
    public float Metallic { get; set; }

    /// <summary>How many samples the shading takes.</summary>
    public int Samples { get; set; } = 4;

    /// <summary>Describes it the way the generator would.</summary>
    /// <returns>The descriptor.</returns>
    public static TypeDescriptor Describe() =>
        new(
            typeof(Material),

            // ⚠ Not "Material". The registry is process-wide and `Vixen.Rendering` already claims
            // that alias for `MaterialDescriptor` — it refused this one at start-up and said so,
            // which is the conflict detection earning its keep: an alias is what saved data and
            // tooling refer to a type by, so two types answering to one name is a corruption waiting
            // for somebody to load a file.
            "SampleMaterial",
            TypeTraits.DataContract | TypeTraits.EditorVisible,
            [
                Member("Visible", typeof(bool), m => m.Visible, (m, v) => m.Visible = (bool) v!),
                Member("Name", typeof(string), m => m.Name, (m, v) => m.Name = (string) v!),
                Member(
                    "Roughness",
                    typeof(float),
                    m => m.Roughness,
                    (m, v) => m.Roughness = (float) v!,
                    new MemberPresentation(Minimum: 0, Maximum: 1, Step: 0.01, IsEditorVisible: true)
                ),
                Member(
                    "Metallic",
                    typeof(float),
                    m => m.Metallic,
                    (m, v) => m.Metallic = (float) v!,
                    new MemberPresentation(Minimum: 0, Maximum: 1, Step: 0.01, IsEditorVisible: true)
                ),
                Member("Samples", typeof(int), m => m.Samples, (m, v) => m.Samples = (int) v!)
            ],
            () => new Material()
        );

    /// <remarks>
    ///     ⚠ The presentation is spelled out. <c>MemberPresentation</c> is a record struct, so
    ///     <c>default</c> zeroes <c>IsEditorVisible</c> however the parameter is declared — a member
    ///     handed a defaulted presentation is hidden from the inspector, silently.
    /// </remarks>
    static MemberDescriptor Member(
        string name,
        Type type,
        Func<Material, object?> get,
        Action<Material, object?>? set,
        MemberPresentation? presentation = null
    ) =>
        new(
            name,
            type,
            0,
            instance => get((Material) instance),
            set is null ? null : (instance, value) => set((Material) instance, value),
            presentation ?? new MemberPresentation(IsEditorVisible: true)
        );

    /// <inheritdoc />
    public override string ToString() => Name + " (" + Roughness.ToString("0.00", CultureInfo.InvariantCulture) + ")";
}
