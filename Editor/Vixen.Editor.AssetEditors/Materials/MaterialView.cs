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
///         <c>Shape</c> and the <c>Preview</c> image, raises <c>PreviewChanged</c> when either the
///         shape or the material moves, and the application renders into a target and puts its
///         number on the image. The same split the scene panel and the texture editor already have.
///     </para>
///     <para>
///         ⚠ <b>And the second half of that sentence is a plan rather than a description: nothing
///         subscribes <c>PreviewChanged</c>, so the image is never given a texture number and the
///         preview is blank in every editor built so far</b>
///         (<a href="https://github.com/Rikarin/Vixen/issues/1204">#1204</a>). The texture editor's
///         equivalent is wired — <c>TexturePreviewImages</c>, from <c>EditorApplication.Joined</c> —
///         and it uploads decoded texels, which is a far smaller job than rendering a lit shape; a
///         material has no picture that is not a render of it, which is doc 20's E5 preview work.
///         ⚠ <b>The graph button is <em>not</em> in that state</b>, and an audit that said so was
///         wrong: <c>Joined</c> subscribes <c>OpenGraphRequested</c>, so "Open shader graph" opens
///         the graph.
///     </para>
///     <para>
///         <b>The shader-graph link is a button and a state.</b> A material naming a graph offers
///         "Open graph"; one that names a graph the project no longer has says so rather than
///         offering a button that opens nothing. What happens when it is pressed is the shell's —
///         <c>OpenGraphRequested</c> carries the asset, and the registry is what turns an asset into
///         a document.
///     </para>
///     <para>
///         The panel is <c>MaterialView.vxml</c>; this file is the accessibility modifier, the
///         preview-shape enumeration and the record the graph button is. ⚠ <b>The parameter list is
///         still built in C# and the ledger's sixth shape is why</b> — every row feeds a
///         <c>sealed</c> <c>InspectorView</c> by a method, inside a loop, which is the one thing no
///         escape this document records can express.
///     </para>
/// </remarks>
public sealed partial class MaterialView;

/// <summary>What the "Open shader graph" button is, all three facts at once.</summary>
/// <param name="Class"><c>hidden</c> when the material names no graph, and empty when it names one.</param>
/// <param name="Missing">Whether the graph it names is one the project no longer has.</param>
/// <param name="Label">What the button says, which is a sentence rather than a verb when it is missing.</param>
/// <remarks>
///     ⚠ <b>One record rather than three signals, which is <c>TextureImportView</c>'s finding.</b>
///     All three are functions of <c>material.Header.Graph</c> and the asset database — a plain
///     mutable object and a service, and no signal watches either — so the panel finds out the link
///     moved because <c>Restate</c> runs. Three signals over three facts would each depend on the
///     document and none of them on the edit, and the button would say "Open shader graph" while
///     sitting greyed out.
/// </remarks>
internal readonly record struct MaterialGraphLink(string Class, bool Missing, string Label);

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
