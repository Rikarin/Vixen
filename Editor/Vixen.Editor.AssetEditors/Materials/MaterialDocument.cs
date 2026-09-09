// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Core.Yaml;
using Vixen.Editor.Assets.Shading;
using Vixen.Editor.Core;
using Vixen.Editor.Inspector;
using Vixen.Editor.ShaderGraph;
using Vixen.Rendering.Materials;

namespace Vixen.Editor.AssetEditors.Materials;

/// <summary>The header of a material — what it is drawn with — as something an inspector can edit.</summary>
/// <remarks>
///     A mirror of the three scalar members of <see cref="MaterialAsset" />, kept beside the
///     parameter list rather than inspecting the asset itself, because inspecting the asset would
///     also offer <c>Version</c> and <c>Parameters</c> as rows — a version field an author can type
///     into is a way to make a file this build refuses to open.
/// </remarks>
[DataContract("MaterialHeaderEdits")]
public sealed class MaterialHeaderEdits {
    /// <summary>Which shader it is drawn with.</summary>
    [Inspector]
    [Tooltip("The effect's name. A material authored in a graph names the shader the graph compiles to.")]
    public string Shader { get; set; } = "ForwardPlus";

    /// <summary>Which shading model.</summary>
    [Inspector]
    [Tooltip("Resolved by name against the shading models the renderer registers.")]
    public string Shading { get; set; } = "StandardShading";

    /// <summary>The shader graph it was authored in, if it was.</summary>
    [Inspector]
    [Tooltip("Empty for a material written against a hand-authored shader.")]
    public AssetId Graph { get; set; }
}

/// <summary>Adding or removing one of a material's parameters.</summary>
/// <remarks>
///     The parameter list is the one part of a material an inspector cannot express — a member's
///     editor edits a value, and this changes which values there are — so it is a command of its
///     own. Everything <em>inside</em> a parameter is an ordinary inspector edit on the object.
/// </remarks>
public sealed class MaterialParameterCommand : IEditorCommand {
    readonly MaterialDocument document;
    readonly IMaterialParameter parameter;
    readonly int index;
    readonly bool adding;

    /// <inheritdoc />
    public string Name => adding ? "Add Parameter" : "Remove Parameter";

    /// <summary>Describes adding or removing a parameter.</summary>
    /// <param name="document">The material.</param>
    /// <param name="parameter">The parameter.</param>
    /// <param name="index">Where it sits, so an undo puts it back where it was.</param>
    /// <param name="adding">Whether it is being added.</param>
    public MaterialParameterCommand(
        MaterialDocument document,
        IMaterialParameter parameter,
        int index,
        bool adding
    ) {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(parameter);

        this.document = document;
        this.parameter = parameter;
        this.index = index;
        this.adding = adding;
    }

    /// <inheritdoc />
    public void Do(EditorContext context) {
        ArgumentNullException.ThrowIfNull(context);

        Apply(adding);
        context.Touch(document);
    }

    /// <inheritdoc />
    public void Undo(EditorContext context) {
        ArgumentNullException.ThrowIfNull(context);

        Apply(!adding);
        context.Touch(document);
    }

    /// <inheritdoc />
    /// <remarks>Two parameters added a minute apart are two decisions, so nothing merges.</remarks>
    public bool TryMergeWith(IEditorCommand previous, [NotNullWhen(true)] out IEditorCommand? merged) {
        merged = null;
        return false;
    }

    void Apply(bool add) {
        var parameters = document.Material.Parameters;

        if (add) {
            parameters.Insert(Math.Clamp(index, 0, parameters.Count), parameter);
        } else {
            parameters.Remove(parameter);
        }

        document.RaiseParametersChanged();
    }
}

/// <summary>Setting one of a graph's properties on the material that composes it.</summary>
/// <remarks>
///     <para>
///         <b>The feature is a record and the values are <c>init</c>, so an edit is a replacement.</b>
///         <see cref="GraphSurfaceFeature" /> holds <c>Numbers</c> and <c>Vectors</c> as arrays a
///         caller may not write into, which is what makes an undo cheap here: the before-image is the
///         whole feature, held by reference, and putting it back is one list assignment.
///     </para>
///     <para>
///         ⚠ <b>A property the author has not touched is left out of the feature rather than written
///         at zero</b>, which is the only decision in this file that is not mechanical. A generated
///         surface shader declares each property with the graph's own default; a
///         <see cref="GraphSurfaceNumber" /> entry <em>overrides</em> that default, so writing every
///         property out at its type's zero the moment the panel opens would silently replace every
///         graph default with black. That is this renderer's standing "zero looks like a valid value"
///         trap, and it is the same call <c>LayerStackView</c> makes one panel over when a lane
///         returns to its port's default: the key comes out.
///     </para>
///     <para>
///         ⚠ <b><c>internal</c>, unlike <see cref="MaterialParameterCommand" /> beside it, and
///         deliberately.</b> Nothing outside this assembly constructs one —
///         <see cref="MaterialDocument.SetGraphValue" /> is the only caller and the only thing that
///         could sensibly build the before-image — so publishing it would be a public type owing a
///         guide page for a shape no other assembly can use. <c>MaterialGraphLink</c> in this same
///         folder made the same call.
///     </para>
/// </remarks>
internal sealed class MaterialGraphValueCommand : IEditorCommand {
    readonly MaterialDocument document;
    readonly GraphSurfaceFeature? before;
    readonly GraphSurfaceFeature after;
    readonly string property;

    /// <inheritdoc />
    public string Name => "Set Graph Property";

    /// <summary>Describes replacing the material's graph feature.</summary>
    /// <param name="document">The material.</param>
    /// <param name="before">The feature as it stands, or null when the material has none yet.</param>
    /// <param name="after">The feature the edit produces.</param>
    /// <param name="property">Which property moved, so that a drag merges and two names do not.</param>
    public MaterialGraphValueCommand(
        MaterialDocument document,
        GraphSurfaceFeature? before,
        GraphSurfaceFeature after,
        string property
    ) {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentException.ThrowIfNullOrEmpty(property);

        this.document = document;
        this.before = before;
        this.after = after;
        this.property = property;
    }

    /// <inheritdoc />
    public void Do(EditorContext context) {
        ArgumentNullException.ThrowIfNull(context);

        document.Replace(before, after);
        context.Touch(document);
    }

    /// <inheritdoc />
    public void Undo(EditorContext context) {
        ArgumentNullException.ThrowIfNull(context);

        document.Replace(after, before);
        context.Touch(document);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Merged by property name, and the merged entry keeps the <em>earlier</em> command's
    ///     before-image.</b> A number field dragged across a range raises one of these per frame, and
    ///     an undo stack that recorded each would make a single gesture forty presses to undo. Two
    ///     different properties are two decisions and do not merge, which is what stops a drag on one
    ///     row from swallowing the edit made on the row above it.
    /// </remarks>
    public bool TryMergeWith(IEditorCommand previous, [NotNullWhen(true)] out IEditorCommand? merged) {
        if (previous is MaterialGraphValueCommand earlier
            && ReferenceEquals(earlier.document, document)
            && string.Equals(earlier.property, property, StringComparison.Ordinal)) {
            merged = new MaterialGraphValueCommand(document, earlier.before, after, property);

            return true;
        }

        merged = null;

        return false;
    }
}

/// <summary>A material, open for editing.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The header is a mirror and the parameters are not.</b> A parameter object is already
///         a class with settable members and its own descriptor, so the inspector edits it in place
///         and every edit is a <c>SetMembersCommand</c> without anything here being involved. The
///         header is mirrored only to keep <c>Version</c> and <c>Parameters</c> off the rows, and
///         <see cref="SaveCore" /> copies the three fields back — which is three lines rather than a
///         second schema.
///     </para>
///     <para>
///         ⚠ <b>A material whose file will not parse opens empty and says so through
///         <see cref="LoadError" />.</b> Refusing to open it would leave the one panel that could
///         show what is wrong unreachable, and the file is untouched until a save.
///     </para>
/// </remarks>
public sealed class MaterialDocument : EditorDocument {
    /// <summary>Where the file is, absolute.</summary>
    public string AssetPath { get; }

    /// <summary>The material as it stands.</summary>
    public MaterialAsset Material { get; }

    /// <summary>The three header fields, as an inspector edits them.</summary>
    public MaterialHeaderEdits Header { get; }

    /// <summary>Why the file did not read, or <see langword="null" /> if it did.</summary>
    public string? LoadError { get; }

    /// <summary>The linked shader graph, compiled, or null when there is not one to compile.</summary>
    /// <remarks>
    ///     ⚠ <b>Compiled here rather than read from the built shader, because the property list is
    ///     the compiler's answer and nothing else's.</b> A <c>.vxshadergraph</c> is nodes; what a
    ///     material has to fill in is <see cref="ShaderGraphSource.Properties" />, which exists only
    ///     after the graph is compiled. <see cref="GraphProblem" /> is why it is null.
    /// </remarks>
    public ShaderGraphSource? GraphSource { get; private set; }

    /// <summary>Why the linked graph produced no properties, or null when it did.</summary>
    /// <remarks>
    ///     Null on a material that names no graph at all — the ordinary case, and not a problem —
    ///     which is why the panel reads <see cref="MaterialAsset.Graph" /> for whether to show the
    ///     section and this only for what to say inside it.
    /// </remarks>
    public string? GraphProblem { get; private set; }

    /// <summary>The one feature that carries this material's graph values, if it has one yet.</summary>
    /// <remarks>
    ///     ⚠ <b>The first, not the only.</b> Nothing refuses a second <see cref="GraphSurfaceFeature" />
    ///     in a hand-written file, and a panel that edited whichever it reached last would move a
    ///     different one each time the list was reordered.
    /// </remarks>
    public GraphSurfaceFeature? Surface =>
        Material.Features.OfType<GraphSurfaceFeature>().FirstOrDefault();

    /// <summary>Raised when a parameter is added or removed.</summary>
    /// <remarks>
    ///     What the parameter list rebuilds from. Not raised for a value edit — that changes a row's
    ///     contents rather than which rows exist.
    /// </remarks>
    public event Action<MaterialDocument>? ParametersChanged;

    /// <summary>Opens a material.</summary>
    /// <param name="project">The project it belongs to.</param>
    /// <param name="asset">Its identity.</param>
    /// <param name="path">Where the file is, absolute.</param>
    public MaterialDocument(EditorProject project, AssetId asset, string path)
        : base(project, asset, Path.GetFileName(path)) {
        ArgumentException.ThrowIfNullOrEmpty(path);

        AssetPath = path;

        try {
            Material = MaterialAsset.FromYaml(AssetFile.Read(path));
        } catch (Exception exception) when (exception is YamlBindingException
            or YamlParseException or NotSupportedException or FormatException) {
            Material = new();
            LoadError = exception.Message;
        }

        Header = new() {
            Shader = Material.Shader,
            Shading = Material.Shading,
            Graph = Material.Graph
        };

        ReadGraph();
    }

    /// <summary>Adds a parameter, undoably.</summary>
    /// <param name="parameter">The parameter, already named.</param>
    /// <returns>The parameter, for a caller that wants to select it.</returns>
    /// <exception cref="InvalidOperationException">Something is already called that.</exception>
    public IMaterialParameter Add(IMaterialParameter parameter) {
        ArgumentNullException.ThrowIfNull(parameter);
        ArgumentException.ThrowIfNullOrEmpty(parameter.Name);

        if (Material.Find(parameter.Name) is not null) {
            throw new InvalidOperationException(
                $"This material already sets '{parameter.Name}'. Two parameters of one name mean the shader "
                + "gets whichever the compiler reached last."
            );
        }

        Stack.Execute(new MaterialParameterCommand(this, parameter, Material.Parameters.Count, adding: true));
        Stack.Seal();

        return parameter;
    }

    /// <summary>Removes a parameter, undoably.</summary>
    /// <param name="parameter">The parameter.</param>
    /// <returns>Whether it was there.</returns>
    public bool Remove(IMaterialParameter parameter) {
        ArgumentNullException.ThrowIfNull(parameter);

        var index = Material.Parameters.IndexOf(parameter);

        if (index < 0) {
            return false;
        }

        Stack.Execute(new MaterialParameterCommand(this, parameter, index, adding: false));
        Stack.Seal();

        return true;
    }

    /// <summary>Reads and compiles the graph <see cref="MaterialHeaderEdits.Graph" /> names.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Called when the panel opens and whenever the link moves</b>, which is the same
    ///         moment <c>MaterialView.Restate</c> works out what the "open graph" button is. Both
    ///         read a plain mutable field and a service that no signal watches.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Every failure is a sentence and none is an exception.</b> A material outlives the
    ///         graph it was generated from — deleted, on another branch, or edited into something
    ///         that no longer compiles — and this panel is the one place an author would find that
    ///         out. Throwing would take the parameter list and the preview with it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A standalone graph is reported rather than compiled into a feature.</b>
    ///         <c>ShaderGraphMaterial.Feature</c> refuses one, and the sentence it refuses with is
    ///         about a master node an author can add; reaching that refusal from a row of number
    ///         fields would report it as a failed edit instead.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What compiling a graph asset <em>means</em> is
    ///         <see cref="ShaderGraphSources" />' and no longer also this method's</b> —
    ///         <a href="https://github.com/Rikarin/Vixen/issues/1117">#1117</a>. This used to
    ///         re-spell the parse, the <c>NodeGraphDocument.Load</c>, the shader-graph registry and
    ///         the <c>DefaultName</c> rule, because the one production path that already did all
    ///         four packaged its result as generated <em>text</em> and let the compilation go out of
    ///         scope. Two places deciding that could disagree about the default name, about which
    ///         repairs are reported, and about whether a standalone graph is skipped or refused —
    ///         and only this one would have been looked at when a material's properties were wrong.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A file that will not parse now reaches the author through the same sentence as a
    ///         graph that will not compile</b>, where it used to have one of its own. Telling them
    ///         apart needs the parse, and doing the parse here is the duplication above; the
    ///         diagnostic itself still says which it was, and it now carries the compiler's id with
    ///         it because <see cref="ShaderGraphSourceFile.Diagnostics" /> is written for a build log.
    ///     </para>
    /// </remarks>
    public void ReadGraph() {
        GraphSource = null;
        GraphProblem = null;

        var graph = Header.Graph;

        if (graph.IsEmpty) {
            return;
        }

        if (!Project.Assets.TryGetByGuid(graph, out var entry)) {
            GraphProblem = "This material names a shader graph that is not in the project, so there is nothing "
                + "to read its properties from.";

            return;
        }

        string text;

        try {
            text = AssetFile.Read(Project.Paths.Absolute(entry.Path));
        } catch (IOException failure) {
            GraphProblem = $"The shader graph did not read: {failure.Message}";

            return;
        }

        var compiled = ShaderGraphSources.From(entry.Path, text);

        if (compiled.Source is not { } source) {
            var said = string.Join("; ", compiled.Diagnostics);

            GraphProblem = said.Length > 0
                ? "The shader graph does not compile, so it declares no properties: " + said
                : "The shader graph does not compile, so it declares no properties.";

            return;
        }

        if (source.Kind != ShaderGraphKind.Surface) {
            GraphProblem = $"'{source.Name}' compiles to a standalone shader, which a material cannot compose. "
                + "Give the graph a Master/Surface node and its properties appear here.";

            return;
        }

        GraphSource = source;
    }

    /// <summary>Sets one of the linked graph's properties, undoably.</summary>
    /// <param name="property">The property, as the graph declares it.</param>
    /// <param name="value">Its value; a <c>float</c> property reads <c>X</c> and ignores the rest.</param>
    /// <returns>Whether there was a compiled graph to set it on.</returns>
    /// <exception cref="ArgumentException"><paramref name="property" /> is empty.</exception>
    /// <remarks>
    ///     ⚠ <b>This is the editor's first feature-editing path, and keeping every other feature is
    ///     the whole of what it has to get right.</b> <see cref="MaterialAsset.Features" /> was
    ///     carried and never written precisely so that opening a material with features and saving it
    ///     did not delete them; a write that rebuilt the list would turn that non-destructive read
    ///     into the loss it was guarding against. So this replaces one element in place and appends
    ///     when there is none.
    /// </remarks>
    public bool SetGraphValue(string property, Vector4 value) {
        ArgumentException.ThrowIfNullOrEmpty(property);

        if (GraphSource is not { } source) {
            return false;
        }

        var declared = ShaderGraphMaterial.Values(source)
            .FirstOrDefault(entry => string.Equals(entry.Name, property, StringComparison.Ordinal));

        if (declared.Name is null or "") {
            return false;
        }

        var before = Surface;

        // ⚠ Only a feature naming *this* graph carries anything forward. A material whose link has
        // been moved to another graph still holds the old one's feature, and keeping its entries
        // would write the previous graph's values and — worse — its texture *slots* under the new
        // shader's name: `AssetMaterialSource.Pair` keys the bindless table on
        // `{shader}.{chain}.{graph}.{slot}`, so the new graph's own slots would never be written and
        // every one of them would fall back to the table's placeholder view. A wrong picture, no
        // error.
        var carried = before is not null && string.Equals(before.Shader, source.Name, StringComparison.Ordinal);
        var numbers = (carried ? before!.Numbers : []).ToList();
        var vectors = (carried ? before!.Vectors : []).ToList();

        if (string.Equals(declared.Type, "float", StringComparison.Ordinal)) {
            numbers.RemoveAll(entry => string.Equals(entry.Name, property, StringComparison.Ordinal));
            numbers.Add(new(property, value.X));
        } else {
            vectors.RemoveAll(entry => string.Equals(entry.Name, property, StringComparison.Ordinal));
            vectors.Add(new(property, value));
        }

        GraphSurfaceFeature after = new() {
            // ⚠ The shader name comes from the compilation and not from `Header.Shader`. A material's
            // `Shader` is the effect it draws with — `ForwardPlus` — and a feature's is the generated
            // surface the graph compiled to; writing the first into the second is a composition Raven
            // cannot resolve, reported against a material whose author never saw the generated text.
            Shader = source.Name,
            Numbers = [.. numbers],
            Vectors = [.. vectors],
            // ⚠ The join from a compiled slot to a `GraphSurfaceMap` is the shader graph's own and
            // is asked for rather than spelled — #1117. `AssetMaterialSource.Pair` keys the bindless
            // table on `{shader}.{chain}.{graph}.{slot}`, so a second spelling that dropped a slot
            // would write nothing for it and read the table's placeholder view for ever.
            Maps = carried ? before!.Maps : ShaderGraphMaterial.Maps(source)
        };

        Stack.Execute(new MaterialGraphValueCommand(this, before, after, property));

        return true;
    }

    /// <summary>Puts one graph feature where another was, keeping every other feature in place.</summary>
    /// <param name="removing">What to take out, or null to only add.</param>
    /// <param name="adding">What to put in, or null to only remove.</param>
    internal void Replace(GraphSurfaceFeature? removing, GraphSurfaceFeature? adding) {
        var features = Material.Features;
        var index = removing is null ? -1 : features.IndexOf(removing);

        if (index >= 0) {
            if (adding is null) {
                features.RemoveAt(index);
            } else {
                features[index] = adding;
            }
        } else if (adding is not null) {
            features.Add(adding);
        }
    }

    /// <summary>The material as this document would write it, without writing it.</summary>
    /// <returns>The YAML.</returns>
    public string ToYaml() {
        Material.Shader = Header.Shader;
        Material.Shading = Header.Shading;
        Material.Graph = Header.Graph;

        return Material.ToYaml();
    }

    /// <inheritdoc />
    protected override void SaveCore() => AssetFile.Write(AssetPath, ToYaml());

    internal void RaiseParametersChanged() => ParametersChanged?.Invoke(this);
}
