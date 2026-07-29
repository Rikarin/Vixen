// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Core.Yaml;
using Vixen.Editor.Core.Scenes;
using Vixen.Editor.Inspector;

namespace Vixen.Editor.AssetEditors.Materials;

/// <summary>One value a material sets, by the name a shader knows it under.</summary>
/// <remarks>
///     <para>
///         <b>An interface with a <c>[DataContract]</c> name per implementation</b>, which is how the
///         rest of the engine does polymorphism in a file: the contract name is the YAML tag, so
///         <c>- !Colour</c> selects the type and nothing keeps a registration table in sync. It is
///         the same shape <c>ISceneRendererAsset</c> and a scene's component list already have.
///     </para>
///     <para>
///         ⚠ <b>Five kinds rather than one with a type field.</b> A single parameter type carrying a
///         <c>Vector4</c>, an <c>AssetId</c> and a <c>Kind</c> saying which of them is real would put
///         four dead fields in every line of every material file, and would give the inspector one
///         editor to draw for five different things. The tag is the discriminator the format already
///         has.
///     </para>
/// </remarks>
public interface IMaterialParameter {
    /// <summary>What the shader calls it.</summary>
    string Name { get; }
}

/// <summary>A single number.</summary>
[DataContract("Scalar")]
public sealed class ScalarParameter : IMaterialParameter {
    /// <inheritdoc />
    [Inspector]
    public string Name { get; set; } = string.Empty;

    /// <summary>Its value.</summary>
    [Inspector]
    public float Value { get; set; }
}

/// <summary>A flag, which a shader reads as a permutation or as zero and one.</summary>
[DataContract("Flag")]
public sealed class FlagParameter : IMaterialParameter {
    /// <inheritdoc />
    [Inspector]
    public string Name { get; set; } = string.Empty;

    /// <summary>Its value.</summary>
    [Inspector]
    public bool Value { get; set; }
}

/// <summary>Four numbers that are not a colour — a tiling, a set of thresholds, a packed mask.</summary>
[DataContract("Vector")]
public sealed class VectorParameter : IMaterialParameter {
    /// <inheritdoc />
    [Inspector]
    public string Name { get; set; } = string.Empty;

    /// <summary>Its value.</summary>
    [Inspector]
    public Vector4 Value { get; set; }
}

/// <summary>A colour, edited with a picker rather than as four numbers.</summary>
[DataContract("Colour")]
public sealed class ColourParameter : IMaterialParameter {
    /// <inheritdoc />
    [Inspector]
    public string Name { get; set; } = string.Empty;

    /// <summary>Its value.</summary>
    /// <remarks>
    ///     HDR, because an emissive tint is the case a material colour most often is: values above
    ///     one are meaningful and a picker clamped to one would make an emissive material impossible
    ///     to author. An albedo tint edited through an HDR picker is merely a picker with an
    ///     intensity control nobody has to move.
    /// </remarks>
    [Inspector]
    [ColorUsage(hdr: true)]
    public Color4 Value { get; set; } = Color4.White;
}

/// <summary>A texture, by the identity that survives a rename.</summary>
[DataContract("Texture")]
public sealed class TextureParameter : IMaterialParameter {
    /// <inheritdoc />
    [Inspector]
    public string Name { get; set; } = string.Empty;

    /// <summary>Which texture.</summary>
    /// <remarks>
    ///     A GUID, which is what makes moving the texture into another folder change nothing here.
    ///     It is also what <c>NativeFormatImporter</c> finds when it walks the file, so replacing
    ///     the texture re-imports the material.
    /// </remarks>
    [Inspector]
    public AssetId Value { get; set; }
}

/// <summary>A material, as a <c>.vxmat</c> holds it.</summary>
/// <remarks>
///     <para>
///         <b>The authoring format, not <c>MaterialDescriptor</c>.</b> The runtime's material is a
///         tree of features with a shading model and a compiled pipeline; this is what a person
///         edits and what git diffs, and turning one into the other is <c>MaterialCompiler</c>'s job
///         — the one doc 08 names and nothing has written. <c>NativeFormatImporter</c> carries this
///         document forward unchanged for exactly that reason, so the file the editor writes is the
///         file the pipeline reads.
///     </para>
///     <para>
///         <b><see cref="Shading" /> is a name, not a type.</b> An <c>IMaterialShading</c> is a
///         runtime object with a <c>Compile</c> method on it, and a document that named the type
///         would be a document that cannot be read without loading the renderer. The compiler
///         resolves the name against the shading models it has and reports one it does not know,
///         which is a diagnostic an author can act on rather than a binder failure.
///     </para>
///     <para>
///         <b>The version is written and is read</b>, on <see cref="SceneFile" />'s terms: a file
///         from a newer editor is refused by number rather than bound as far as it goes, because a
///         material half-read is a material that will be saved back with the other half gone.
///     </para>
/// </remarks>
[DataContract("MaterialAsset")]
public sealed class MaterialAsset {
    /// <summary>The version this reader and writer speak.</summary>
    public const int Current = 1;

    /// <summary>What a material is written as.</summary>
    public const string Extension = ".vxmat";

    /// <summary>
    ///     Teaches the binder how a vector and a colour read before anything asks it to read one.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The scene format's converters, not a second set.</b> A material and a scene are the
    ///     same YAML dialect, and two registrations of <c>Vector4</c> that disagreed about spacing
    ///     would make a round trip through one of them a diff. A static constructor rather than a
    ///     module initializer, for the reason <see cref="SceneScalars.Register" /> gives.
    /// </remarks>
    static MaterialAsset() => SceneScalars.Register();

    /// <summary>Which version of the format this file is.</summary>
    public int Version { get; set; } = Current;

    /// <summary>Which shader it is drawn with.</summary>
    public string Shader { get; set; } = "ForwardPlus";

    /// <summary>Which shading model, by the name the renderer registers it under.</summary>
    public string Shading { get; set; } = "StandardShading";

    /// <summary>The shader graph this material was authored in, if it was.</summary>
    /// <remarks>
    ///     ⚠ <b>A link, not a source of truth.</b> The graph compiles to a shader and the shader's
    ///     name is above; this is what lets the editor offer "open the graph" rather than making an
    ///     author find it in the browser. A material whose graph has been deleted still draws — it
    ///     names a shader — and the editor says the link is broken rather than refusing the file.
    /// </remarks>
    public AssetId Graph { get; set; }

    /// <summary>What it sets, in the order the file lists them.</summary>
    /// <remarks>
    ///     Settable for the reason <c>SceneEntityData.Children</c> gives: the YAML binder takes part
    ///     only in members it can write, on both sides, so a get-only list is written and then
    ///     silently skipped on load.
    /// </remarks>
    public List<IMaterialParameter> Parameters { get; set; } = [];

    /// <summary>Reads YAML into a material.</summary>
    /// <param name="yaml">The text.</param>
    /// <returns>The material.</returns>
    /// <exception cref="NotSupportedException">The file is from a newer editor.</exception>
    public static MaterialAsset FromYaml(string yaml) {
        ArgumentNullException.ThrowIfNull(yaml);

        if (yaml.Trim().Length == 0) {
            // An empty file is a new material rather than an error, which is what makes "create the
            // file and open it" the ordinary way to write the first one.
            return new();
        }

        var material = YamlSerializer.Parse<MaterialAsset>(yaml);

        return material.Version <= Current
            ? material
            : throw new NotSupportedException(
                $"This material is version {material.Version} and this build reads {Current}. Reading it "
                + "would bind the parts it recognises and drop the rest."
            );
    }

    /// <summary>Writes it as YAML.</summary>
    /// <returns>The text, ending in a newline.</returns>
    public string ToYaml() => YamlSerializer.ToYaml(this);

    /// <summary>The parameter of a given name, if it has one.</summary>
    /// <param name="name">The name.</param>
    /// <returns>The parameter, or <see langword="null" />.</returns>
    public IMaterialParameter? Find(string name) {
        ArgumentException.ThrowIfNullOrEmpty(name);

        foreach (var parameter in Parameters) {
            if (string.Equals(parameter.Name, name, StringComparison.Ordinal)) {
                return parameter;
            }
        }

        return null;
    }
}
