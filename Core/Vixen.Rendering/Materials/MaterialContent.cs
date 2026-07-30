// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Rendering.Materials;

/// <summary>One texture a material assigns, by the name its features sample it under.</summary>
/// <param name="Parameter">
///     What the material calls it — <c>baseColorMap</c>, as
///     <see cref="TexturedMetalRoughnessFeature.BaseColorMap" /> names it.
/// </param>
/// <param name="Texture">Which texture, as the identity that survives a rename.</param>
/// <remarks>
///     <para>
///         <b>A name and a reference, and deliberately not a handle.</b> A feature carries the name it
///         wants sampled and nothing else — that is what "materials are values, not resources" means —
///         so this is the other half of the pairing, and it is as serialisable as the feature is. What
///         turns it into something a shader can index is a host with a
///         <see cref="Graphics.BindlessTable" />; see
///         <see cref="Features.MaterialRenderFeature.TextureIndices" />.
///     </para>
///     <para>
///         ⚠ <b>The name is the material's, not the shader's.</b> A shader's parameter is the composed
///         path — <c>ForwardPlus.CompositeSurface.TexturedMetalRoughnessSurface.baseColorIndex</c> —
///         and depends on which slot the feature landed in. This one does not, which is the whole
///         reason a feature names a texture indirectly: moving a feature up the chain must not rename
///         the texture an artist assigned.
///     </para>
/// </remarks>
[DataContract("MaterialTexture")]
public sealed record MaterialTexture(string Parameter, AssetReference Texture) {
    /// <summary>For the binder, which builds a value and then fills it.</summary>
    public MaterialTexture()
        : this(string.Empty, AssetReference.Null) { }

    /// <summary>What the material calls it.</summary>
    public string Parameter { get; init; } = Parameter;

    /// <summary>Which texture.</summary>
    public AssetReference Texture { get; init; } = Texture;
}

/// <summary>A material as a build wrote it and a frame reads it.</summary>
/// <remarks>
///     <para>
///         <b>The content form of <see cref="MaterialDescriptor" />, and the reason the two are not one
///         type.</b> A descriptor holds a resolved <see cref="IMaterialShading" /> and says nothing
///         about textures, because it is what <see cref="MaterialCompiler" /> consumes. This is what an
///         <em>asset</em> is: a shading model named rather than constructed, so a document can carry a
///         model this build has never heard of and be told so by name, and the texture assignments
///         beside it, which the compiler has no use for and a host does.
///     </para>
///     <para>
///         <b>Read from YAML by the importer and as a chunk by the runtime</b> — the arrangement
///         <c>GraphicsCompositorAsset</c> established, and for the same reason: this assembly links no
///         YAML parser, so which file format an asset was authored in belongs to whoever authored it.
///         A shipping build reads the baked record graph and never sees a <c>.vxmat</c>.
///     </para>
///     <para>
///         ⚠ <b>One declaration, two readers, and that is the part worth watching.</b> The editor's
///         <c>MaterialAsset</c> binds the same file to draw it in an inspector, and the two agree only
///         so long as neither drops a key the other writes — which is why the editor's carries
///         <c>features</c> and <c>textures</c> it does not yet edit. A binder skips a key it does not
///         know, so the failure is not an error: it is a material saved by the editor with its features
///         quietly gone.
///     </para>
/// </remarks>
/// <remarks>
///     ⚠ The alias is <c>MaterialContent</c> and not <c>MaterialAsset</c>, which is not a naming
///     preference: the editor's authoring type already claims that name, and a name is what data and
///     tools refer to a type by — the registry refuses the collision rather than letting two types
///     answer to one tag. Neither is ever written with a tag, both being the root of their document, so
///     the alias here names only the chunk.
/// </remarks>
[DataContract("MaterialContent")]
public sealed record MaterialContent {
    /// <summary>The version this reader speaks.</summary>
    public const int Current = 1;

    /// <summary>Which version of the format this is.</summary>
    public int Version { get; init; } = Current;

    /// <summary>Which shading pass it is drawn with.</summary>
    public string Shader { get; init; } = "ForwardPlus";

    /// <summary>Which shading model, by the name the renderer registers it under.</summary>
    /// <remarks>
    ///     A name rather than the object, because a document that named the type would be a document
    ///     that cannot be read without loading the renderer — and because a model this build does not
    ///     have should be a message an author can act on rather than a binder failure.
    ///     <see cref="MaterialShading.TryResolve" /> is what turns one into the other.
    /// </remarks>
    public string Shading { get; init; } = MaterialCompiler.DefaultShadingShader;

    /// <summary>The features, in the order they contribute.</summary>
    public IMaterialFeature[] Features { get; init; } = [];

    /// <summary>The textures its features sample, by the names they sample them under.</summary>
    public MaterialTexture[] Textures { get; init; } = [];

    /// <summary>The descriptor <see cref="MaterialCompiler" /> compiles.</summary>
    /// <param name="shading">The model <see cref="Shading" /> named, or the default when it named none.</param>
    /// <returns>The descriptor.</returns>
    public MaterialDescriptor ToDescriptor(IMaterialShading shading) {
        ArgumentNullException.ThrowIfNull(shading);
        return new() { ShaderName = Shader, Features = Features, Shading = shading };
    }
}

/// <summary>The shading models a document can name, and how a name becomes one.</summary>
/// <remarks>
///     <para>
///         <b>Written out rather than discovered by reflection</b>, for the reason
///         <c>PrimitiveShapes.All</c> gives about its own list: what a build ships is a decision, and a
///         scan of the loaded assemblies makes it an accident of which ones happened to be loaded. It
///         also keeps the answer available to a trimmed build, where reflection over subtypes is
///         exactly what does not survive.
///     </para>
///     <para>
///         ⚠ <b>A model with parameters gets its defaults.</b> <see cref="SubsurfaceShading" /> has a
///         wrap width and <see cref="CelShading" /> has a band count, and a name cannot carry either —
///         so a material naming one of those is shaded by the model with the numbers it declares.
///         Carrying them needs the document to hold the model as an object rather than a name, which
///         is what <see cref="MaterialContent.Features" /> already does and what the authoring format
///         does not yet do for this one field.
///     </para>
/// </remarks>
public static class MaterialShading {
    /// <summary>Every model, by the name a document writes.</summary>
    public static IReadOnlyDictionary<string, Func<IMaterialShading>> All { get; } =
        new Dictionary<string, Func<IMaterialShading>>(StringComparer.Ordinal) {
            ["StandardShading"] = () => new StandardShading(),
            ["AnisotropicShading"] = () => new AnisotropicShading(),
            ["ClearCoatShading"] = () => new ClearCoatShading(),
            ["SheenShading"] = () => new SheenShading(),
            ["SubsurfaceShading"] = () => new SubsurfaceShading(),
            ["HairShading"] = () => new HairShading(),
            ["CelShading"] = () => new CelShading()
        };

    /// <summary>The model a name means.</summary>
    /// <param name="name">The name, or null or empty for the default.</param>
    /// <param name="shading">The model.</param>
    /// <returns>Whether the name was one this build has.</returns>
    /// <remarks>
    ///     Empty resolves to <see cref="StandardShading" /> and returns true, because a document that
    ///     said nothing chose the default rather than named something wrong. A name that is not a model
    ///     returns false with the default in hand, so a caller can report it and still draw.
    /// </remarks>
    public static bool TryResolve(string? name, out IMaterialShading shading) {
        if (string.IsNullOrWhiteSpace(name)) {
            shading = new StandardShading();
            return true;
        }

        if (All.TryGetValue(name.Trim(), out var build)) {
            shading = build();
            return true;
        }

        shading = new StandardShading();
        return false;
    }
}
