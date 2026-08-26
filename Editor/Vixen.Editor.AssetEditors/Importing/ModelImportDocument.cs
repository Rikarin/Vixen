// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Yaml.Meta;
using Vixen.Editor.Assets.Models;
using Vixen.Editor.Core;
using Vixen.Editor.Inspector;

namespace Vixen.Editor.AssetEditors.Importing;

/// <summary>The editable mirror of <see cref="ModelImportSettings" />.</summary>
/// <inheritdoc cref="TextureImportEdits" path="/remarks" />
[DataContract("ModelImportEdits")]
public sealed class ModelImportEdits {
    /// <summary>What each of the model's parts should be called instead of what the file calls it.</summary>
    /// <remarks>
    ///     ⚠ <b>The editable half of the part list below.</b> A <c>.glb</c> with two meshes called
    ///     <c>Cube</c> imports them as <c>Cube</c> and <c>Cube_1</c> — a name the importer invented,
    ///     which depends on the order they appear in the file — and the Parts list shows exactly that.
    ///     A row here, keyed by what the file calls it, is what turns one of them into a name somebody
    ///     chose. Renaming changes the sub-asset's id and so breaks existing references to it, which is
    ///     the same trade as renaming in the DCC tool and is worth making early.
    /// </remarks>
    [Inspector]
    [Tooltip("Rename a part: Source is what the file calls it, Name is what it is addressed as. Applies on the next import.")]
    public List<SubAssetRename> SubAssetNames { get; set; } = [];

    /// <summary>What to multiply every length by.</summary>
    [Inspector]
    [Tooltip("An FBX out of Max is centimetres and a glTF is metres. Applied to positions and to node translations.")]
    public float Scale { get; set; } = 1f;

    /// <summary>Whether to compute normals for meshes that have none.</summary>
    [Inspector]
    [Tooltip("Only for meshes that have none — recomputing authored normals throws away the shading.")]
    public bool GenerateNormals { get; set; } = true;

    /// <summary>Whether to compute tangents for meshes that have UVs and no tangents.</summary>
    [Inspector]
    [Tooltip("Needs texture coordinates. A mesh with no UVs gets none and no complaint.")]
    public bool GenerateTangents { get; set; } = true;

    /// <summary>Whether to import the animation clips the file carries.</summary>
    [Inspector]
    [Tooltip("Off for a character exported once per animation, where every file repeats the same skeleton.")]
    public bool ImportAnimations { get; set; } = true;

    /// <summary>Whether to import the blend shapes the file carries.</summary>
    /// <remarks>
    ///     ⚠ <b>Added to the record and not to this mirror when blend shapes landed</b>, so the two
    ///     settings that decide whether a face can animate at all were reachable only by hand-editing a
    ///     <c>.meta</c>. <c>ImportSettingsMirrorTests</c> is what noticed — the same way it noticed the
    ///     five cluster settings above — which is the whole reason that test compares the two by
    ///     reflection rather than against a written-down list.
    /// </remarks>
    [Inspector]
    [Tooltip("Off for the mirror of Import Animations: a character exported once per outfit ships the same facial shapes in every file.")]
    public bool ImportBlendShapes { get; set; } = true;

    /// <summary>How far a vertex must move to be kept in a blend shape, in the model's own units.</summary>
    [Inspector]
    [Tooltip("What makes a shape sparse. In the model's units after Scale, not the file's. An exporter writes a delta for every vertex and most of them are rounding noise.")]
    public float BlendShapeThreshold { get; set; } = 1e-4f;

    /// <summary>Whether to bake a signed distance field for each of the model's meshes.</summary>
    [Inspector]
    [Tooltip("What distance-field shadows and occlusion read. The most expensive part of the import; off for a project not lighting this way.")]
    public bool GenerateDistanceFields { get; set; } = true;

    /// <summary>How many samples along the longest axis of each field.</summary>
    [Inspector]
    [Tooltip("Quality and cost at once — doubling it is eight times the samples. Other axes follow the bounds so cells stay near-cubic.")]
    public int DistanceFieldResolution { get; set; } = 32;

    /// <summary>How many rays each sample casts to decide which side of the surface it is on.</summary>
    [Inspector]
    [Tooltip("What makes the sign survive meshes that are not closed. Noisy below sixteen; stops changing above sixty-four.")]
    public int DistanceFieldSignRays { get; set; } = 32;

    /// <summary>How far a field's volume is grown past its mesh, as a fraction of the mesh's size.</summary>
    [Inspector]
    [Tooltip("Room outside the surface for a ray to slow down in. With none, the surface lies on the volume's own face.")]
    public float DistanceFieldBoundsExpansion { get; set; } = 0.2f;

    /// <summary>Whether to build a cluster hierarchy for each of the model's meshes.</summary>
    /// <remarks>
    ///     <b>The five below are what the virtualized path is built from, and until now none of them
    ///     reached the inspector.</b> The settings record grew them and this mirror did not, so a mesh
    ///     could be cut into clusters or not — the most expensive decision this importer makes — only by
    ///     hand-editing a <c>.meta</c>. <c>ImportSettingsMirrorTests</c> is what noticed, which is the
    ///     whole reason that test compares the two by reflection rather than by a written-down list.
    /// </remarks>
    [Inspector]
    [Tooltip("What the virtualized path draws: every level of detail at once, plus a fallback mesh. Off for a mesh that is already a hundred triangles.")]
    public bool GenerateMeshlets { get; set; } = true;

    /// <summary>The most triangles one cluster may hold.</summary>
    [Inspector]
    [Tooltip("The unit of culling and of streaming both. A hundred and twenty-eight is about where the per-cluster overhead stops mattering.")]
    public int MeshletTriangles { get; set; } = 128;

    /// <summary>The most distinct vertices one cluster may reference.</summary>
    [Inspector]
    [Tooltip("At most 256, because a cluster's triangles index its own vertex list with a byte. Where it binds, the cluster is split rather than the mesh refused.")]
    public int MeshletVertices { get; set; } = 128;

    /// <summary>How many clusters are simplified together as a group.</summary>
    [Inspector]
    [Tooltip("How much a level of detail can actually remove. A group's outer boundary is locked, so a small group has little interior to collapse.")]
    public int MeshletGroupSize { get; set; } = 16;

    /// <summary>How many triangles the generated fallback mesh may have.</summary>
    [Inspector]
    [Tooltip("A cut through the finished hierarchy at a fixed budget. What WebGL2 draws and what the physics cook reads.")]
    public int MeshletFallbackTriangles { get; set; } = 4096;

    /// <summary>Whether every mesh in the model is retopologised into quads on the way in.</summary>
    /// <remarks>
    ///     ⚠ <b>Off, and unlike the two expensive settings above it this one is destructive.</b>
    ///     docs/plan/41 § D16: an artist's topology is a decision, and this replaces it. It is the AI
    ///     pipeline's hook — a generated blob with no coordinates comes out as a quad cage with an
    ///     atlas — and it is the wrong thing to turn on for a hand-modelled asset.
    /// </remarks>
    [Inspector]
    [Tooltip("Replace the file's topology with all-quads. Destructive by definition, and the point of it for a generated mesh.")]
    public bool Retopologize { get; set; }

    /// <summary>How many quads to aim for per mesh.</summary>
    [Inspector]
    [Tooltip("A budget rather than a guarantee: a patch's quad count is a product of two sides, so a snaky partition overshoots and the report says by how much.")]
    public int RetopologyQuads { get; set; } = 5000;

    /// <summary>Zero for uniform squares, one to let curvature decide the density.</summary>
    [Inspector]
    [Tooltip("Weighted by anisotropy rather than by curvature, so a sphere is left smooth instead of chasing noise.")]
    public float RetopologyAdaptivity { get; set; } = 0.5f;

    /// <summary>The angle, in degrees, above which a shared edge is a hard feature.</summary>
    [Inspector]
    [Tooltip("A crease above this becomes a boundary of the layout, so it comes out as a chain of edges rather than something snapped to.")]
    public float RetopologyFeatureAngle { get; set; } = 35f;

    /// <summary>Whether the source's coordinate seams are features the retopology keeps.</summary>
    [Inspector]
    [Tooltip("So a retexture-then-remesh round trip does not shred an atlas. Needs the source to carry coordinates.")]
    public bool RetopologyKeepUvSeams { get; set; }

    /// <summary>Which axis plane through the origin to mirror across.</summary>
    [Inspector]
    [Tooltip("Exact: the seam snaps to zero and the mirror is a sign flip, so vertex k and its mirror correspond bit for bit. The model's own space.")]
    public SymmetryAxis RetopologySymmetry { get; set; } = SymmetryAxis.None;

    /// <summary>Guide curves the retopology's edge flow should follow.</summary>
    [Inspector]
    [Tooltip("Paths to .vxspline assets. A curve saved beside the mesh survives the mesh being regenerated, which a painted guide does not.")]
    public List<RetopologyGuideReference> RetopologyGuides { get; set; } = [];

    /// <summary>When to generate texture coordinates for the model's meshes.</summary>
    [Inspector]
    [Tooltip("Never, only for meshes that arrived without any, or always. The middle one fixes generated meshes and leaves an artist's atlas alone.")]
    public UnwrapMode Unwrap { get; set; } = UnwrapMode.Never;

    /// <summary>The atlas resolution the unwrap packs for.</summary>
    [Inspector]
    [Tooltip("The packer needs it because the margin is counted in texels, and a margin in UV units means a different number of texels on every island.")]
    public int UnwrapResolution { get; set; } = 1024;

    /// <summary>How many texels of empty space each island keeps around it.</summary>
    [Inspector]
    [Tooltip("What stops one island's mip bleeding into the next. Four is enough for a full mip chain at most resolutions.")]
    public int UnwrapMargin { get; set; } = 4;

    /// <summary>Texels per world unit to hold across every chart, or zero to fill the atlas instead.</summary>
    [Inspector]
    [Tooltip("Set it and every chart gets the same density — no more a face at half the resolution of the boots. Zero packs for efficiency instead.")]
    public float UnwrapTexelDensity { get; set; }
}

/// <summary>A model's import settings, open for editing.</summary>
/// <remarks>
///     <para>
///         The part list doc 11 asks for — a mesh per material, a skeleton, a clip per animation —
///         is <see cref="ImportSettingsDocument.SubAssets" />, read out of the sidecar rather than by
///         re-importing. That is the difference between opening a model and waiting for Assimp.
///     </para>
///     <para>
///         ⚠ <b>No LOD preview, and the reason is no longer the viewport.</b> <c>ModelCompiler</c>
///         writes a <c>Clusters</c> sub-asset per mesh — the hierarchy of
///         <c>docs/plan/22-virtualized-geometry.md</c>, which is every level at once rather than a
///         chain — and this list shows it as the sub-asset it is. The scene viewport now draws mesh
///         assets, so what is left is choosing a cut and drawing that, which is a control this panel
///         does not have rather than a renderer nobody wrote.
///     </para>
/// </remarks>
public sealed class ModelImportDocument : ImportSettingsDocument {
    /// <summary>The settings, typed.</summary>
    public ModelImportEdits Model => (ModelImportEdits) Settings;

    /// <inheritdoc />
    protected override Type SettingsType => typeof(ModelImportEdits);

    /// <inheritdoc />
    protected override string ImporterTag => "ModelImporter";

    /// <summary>Opens a model's import settings.</summary>
    /// <param name="project">The project it belongs to.</param>
    /// <param name="asset">Its identity.</param>
    /// <param name="path">Where the file is, absolute.</param>
    public ModelImportDocument(EditorProject project, AssetId asset, string path)
        : base(project, asset, path) {
    }
}
