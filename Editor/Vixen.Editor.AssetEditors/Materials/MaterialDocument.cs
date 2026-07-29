// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using Vixen.Core;
using Vixen.Core.Yaml;
using Vixen.Editor.Core;
using Vixen.Editor.Inspector;

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
