// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Editor.Core;
using Vixen.Editor.Inspector;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;

namespace Vixen.Editor.AssetEditors.Materials;

/// <summary>What shape the material preview is drawn on.</summary>
/// <remarks>
///     A sphere is the default because it shows a whole BRDF at once — every angle between the
///     normal and the eye is somewhere on it, which is exactly what a material is a function of. A
///     plane is what a tiling or a decal material needs, and a cube is what shows a normal map's
///     hard edges. The shapes are what the host renders; nothing here draws.
/// </remarks>
public enum MaterialPreviewShape {
    /// <summary>A sphere. Every view angle at once.</summary>
    Sphere,

    /// <summary>A plane, for tiling and decals.</summary>
    Plane,

    /// <summary>A cube, for the hard edges a sphere hides.</summary>
    Cube
}

/// <summary>A material, open for editing: a preview, what it is drawn with, and what it sets.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The preview is a request, not a renderer.</b> Drawing a lit sphere needs a device, a
///         compositor and a compiled effect, none of which this assembly has — so the view owns the
///         <see cref="Shape" /> and the <see cref="Preview" /> image, raises
///         <see cref="PreviewChanged" /> when either the shape or the material moves, and the
///         application renders into a target and puts its number on the image. The same split the
///         scene panel and the texture editor already have.
///     </para>
///     <para>
///         <b>The shader-graph link is a button and a state.</b> A material naming a graph offers
///         "Open graph"; one that names a graph the project no longer has says so rather than
///         offering a button that opens nothing. What happens when it is pressed is the shell's —
///         <see cref="OpenGraphRequested" /> carries the asset, and the registry is what turns an
///         asset into a document.
///     </para>
/// </remarks>
public sealed class MaterialView : Control {
    readonly Dictionary<UiElement, IMaterialParameter> removals = [];

    MaterialDocument? document;

    /// <inheritdoc />
    protected override string TagName => "material-editor";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>Where the preview is drawn once something has rendered it.</summary>
    public Image Preview { get; private set; } = null!;

    /// <summary>Which shape the preview asks for.</summary>
    public MaterialPreviewShape Shape { get; private set; } = MaterialPreviewShape.Sphere;

    /// <summary>The shape picker.</summary>
    public Select Shapes { get; private set; } = null!;

    /// <summary>The shader, the shading model and the graph link.</summary>
    public InspectorView HeaderView { get; private set; } = null!;

    /// <summary>The button that opens the graph this material came from.</summary>
    public Button OpenGraph { get; private set; } = null!;

    /// <summary>What the material sets, one expander a parameter.</summary>
    public UiElement Parameters { get; private set; } = null!;

    /// <summary>Which kind of parameter the add button will add.</summary>
    public Select NewKind { get; private set; } = null!;

    /// <summary>What the new parameter will be called.</summary>
    public TextBox NewName { get; private set; } = null!;

    /// <summary>The button that adds it.</summary>
    public Button AddParameter { get; private set; } = null!;

    /// <summary>Shown when the file did not read.</summary>
    public Alert Broken { get; private set; } = null!;

    /// <summary>Raised when the preview's subject changes — a different shape, or an edited value.</summary>
    public event Action<MaterialView>? PreviewChanged;

    /// <summary>Raised when the author asks for the shader graph this material was authored in.</summary>
    public event Action<MaterialView, AssetId>? OpenGraphRequested;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        Broken = Part<Alert>();
        Broken.AddClass("hidden");
        Broken.Title = "This material did not read";

        Preview = Part<Image>();
        Preview.Description = "The material on a preview shape";

        Shapes = Part<Select>();
        Shapes.AddOption(nameof(MaterialPreviewShape.Sphere), "Sphere");
        Shapes.AddOption(nameof(MaterialPreviewShape.Plane), "Plane");
        Shapes.AddOption(nameof(MaterialPreviewShape.Cube), "Cube");
        Shapes.Value = nameof(MaterialPreviewShape.Sphere);

        Shapes.SelectionChanged += (_, value) => {
            Shape = Enum.TryParse<MaterialPreviewShape>(value, out var shape) ? shape : MaterialPreviewShape.Sphere;
            PreviewChanged?.Invoke(this);
        };

        HeaderView = Part<InspectorView>();
        HeaderView.Search.SetStyle("display", "none");
        HeaderView.ValueChanged += (_, _) => Restate();

        OpenGraph = Part<Button>();
        OpenGraph.Label = "Open shader graph";
        OpenGraph.AddClass("hidden");

        Parameters = Part("material-parameters");

        var bar = Part("material-bar");

        NewKind = bar.Add<Select>();
        NewKind.AddOption("Scalar");
        NewKind.AddOption("Colour");
        NewKind.AddOption("Vector");
        NewKind.AddOption("Texture");
        NewKind.AddOption("Flag");
        NewKind.Value = "Scalar";

        NewName = bar.Add<TextBox>();
        NewName.Placeholder = "Parameter name";

        AddParameter = bar.Add<Button>();
        AddParameter.Label = "Add";

        AddHandler<ClickEvent>(static (element, args) => ((MaterialView) element).Chosen(args));
    }

    /// <summary>Shows a material.</summary>
    /// <param name="material">The document.</param>
    public void Show(MaterialDocument material) {
        ArgumentNullException.ThrowIfNull(material);

        if (document is { } previous) {
            previous.ParametersChanged -= Reload;
        }

        document = material;
        material.ParametersChanged += Reload;

        if (material.LoadError is { } error) {
            Broken.RemoveClass("hidden");
            Broken.Message = error;
        } else {
            Broken.AddClass("hidden");
        }

        HeaderView.EditedDocument = material;
        HeaderView.Inspect(material.Header);

        Rebuild();
    }

    /// <summary>Rebuilds the parameter list from the material as it stands.</summary>
    public void Rebuild() {
        while (Parameters.Children.Count > 0) {
            Parameters.Children[^1].Remove();
        }

        removals.Clear();

        if (document is not { } material) {
            return;
        }

        foreach (var parameter in material.Material.Parameters) {
            var expander = Parameters.Add<Expander>();

            // Labelled by the kind and the name, because the name alone does not say whether
            // `tint` is a colour or four numbers — which is the question an author asks when a
            // shader is not getting what they think they set.
            expander.Label = $"{Alias(parameter)}  {parameter.Name}";
            expander.IsExpanded = true;

            var rows = expander.Content.Add<InspectorView>();
            rows.Search.SetStyle("display", "none");
            rows.EditedDocument = material;
            rows.Inspect(parameter);
            rows.ValueChanged += (_, _) => PreviewChanged?.Invoke(this);

            var remove = expander.Content.Add<Button>();
            remove.Label = "Remove";
            remove.Variant = ControlVariant.Subtle;

            removals[remove] = parameter;
        }

        Restate();
        PreviewChanged?.Invoke(this);
    }

    /// <summary>Reads every editor back from the material, without rebuilding anything.</summary>
    public void Reload() {
        HeaderView.Reload();

        // ⚠ Through the expander's *content*, not its children. An `Expander` is a header and a
        // content element, and the rows are inside the second — a walk over its immediate children
        // finds the header and reloads nothing.
        foreach (var expander in Parameters.Children) {
            if (expander is not Expander section) {
                continue;
            }

            foreach (var child in section.Content.Children) {
                if (child is InspectorView rows) {
                    rows.Reload();
                }
            }
        }
    }

    void Reload(MaterialDocument changed) => Rebuild();

    void Restate() {
        if (document is not { } material) {
            return;
        }

        var graph = material.Header.Graph;

        if (graph.IsEmpty) {
            OpenGraph.AddClass("hidden");
            return;
        }

        OpenGraph.RemoveClass("hidden");

        // ⚠ Resolved against the database rather than assumed to exist. A material outlives the
        // graph it was generated from often enough — a graph deleted, a branch without it — and a
        // button that opened nothing is worse than one that says why it cannot.
        var known = material.Project.Assets.TryGetByGuid(graph, out _);

        OpenGraph.Disabled = !known;
        OpenGraph.Label = known ? "Open shader graph" : "Shader graph is missing from this project";
    }

    void Chosen(ClickEvent args) {
        if (document is not { } material) {
            return;
        }

        for (var element = args.Source; element is not null; element = element.Parent) {
            if (ReferenceEquals(element, AddParameter)) {
                Add(material);
                args.Handled = true;

                return;
            }

            if (ReferenceEquals(element, OpenGraph)) {
                OpenGraphRequested?.Invoke(this, material.Header.Graph);
                args.Handled = true;

                return;
            }

            if (removals.TryGetValue(element, out var parameter)) {
                material.Remove(parameter);
                args.Handled = true;

                return;
            }
        }
    }

    void Add(MaterialDocument material) {
        var name = (NewName.Value ?? string.Empty).Trim();

        if (name.Length == 0 || material.Material.Find(name) is not null) {
            return;
        }

        material.Add(Create(NewKind.Value, name));
        NewName.Value = string.Empty;
    }

    /// <summary>A fresh parameter of a named kind.</summary>
    /// <param name="kind">The kind, as the picker spells it.</param>
    /// <param name="name">What to call it.</param>
    /// <returns>The parameter.</returns>
    internal static IMaterialParameter Create(string? kind, string name) => kind switch {
        "Colour" => new ColourParameter { Name = name },
        "Vector" => new VectorParameter { Name = name },
        "Texture" => new TextureParameter { Name = name },
        "Flag" => new FlagParameter { Name = name },
        _ => new ScalarParameter { Name = name }
    };

    /// <summary>What a parameter's kind is called, which is its contract name and its YAML tag.</summary>
    /// <param name="parameter">The parameter.</param>
    /// <returns>The name.</returns>
    internal static string Alias(IMaterialParameter parameter) => parameter switch {
        ColourParameter => "Colour",
        VectorParameter => "Vector",
        TextureParameter => "Texture",
        FlagParameter => "Flag",
        _ => "Scalar"
    };
}

/// <summary>Opens a material.</summary>
public sealed class MaterialEditorFactory : IAssetEditorFactory {
    /// <inheritdoc />
    public string Name => "Material";

    /// <inheritdoc />
    public IReadOnlyList<string> Extensions { get; } = [MaterialAsset.Extension];

    /// <inheritdoc />
    public EditorDocument Open(AssetEditorRequest request) {
        ArgumentNullException.ThrowIfNull(request);
        return new MaterialDocument(request.Project, request.Asset, request.Path);
    }

    /// <inheritdoc />
    public UiElement CreateView(EditorDocument document, UiElement panel) {
        ArgumentNullException.ThrowIfNull(panel);

        var view = panel.Add<MaterialView>();
        view.Show((MaterialDocument) document);

        return view;
    }
}
