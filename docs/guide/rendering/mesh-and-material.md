---
title: Meshes and materials, type by type
slug: rendering/mesh-and-material
kind: concept
area: Rendering
summary: Neither word names a type — each names a chain of them, one per stage, plus four subsystems that borrowed the same word.
api: [T:Vixen.Rendering.MeshData, T:Vixen.Rendering.MeshDraw, T:Vixen.Rendering.Material, T:Vixen.Rendering.MeshPrimitives, T:Vixen.Rendering.MeshRenderer, T:Vixen.Rendering.MeshInstanceRenderer, T:Vixen.Rendering.MaterialRecords, T:Vixen.Rendering.Materials.MaterialDescriptor, T:Vixen.Rendering.Materials.MaterialContent, T:Vixen.Rendering.Materials.MaterialTexture, T:Vixen.Rendering.Materials.MaterialCompiler, T:Vixen.Rendering.Materials.MaterialCompilation, T:Vixen.Rendering.Materials.IMaterialFeature, T:Vixen.Rendering.Materials.TexturedNormalMapFeature, T:Vixen.Rendering.Materials.TexturedOrmFeature, T:Vixen.Rendering.Materials.TexturedEmissiveFeature, T:Vixen.Rendering.Materials.TexturedOpacityFeature, T:Vixen.Rendering.Materials.TexturedMaterialLayersFeature, T:Vixen.Rendering.Materials.GraphSurfaceFeature, T:Vixen.Rendering.Materials.GraphSurfaceNumber, T:Vixen.Rendering.Materials.GraphSurfaceVector, T:Vixen.Rendering.Materials.GraphSurfaceMap, T:Vixen.Rendering.Materials.IMaterialShading, T:Vixen.Rendering.Materials.MaterialShading, T:Vixen.Rendering.Materials.MaterialSurface, T:Vixen.Rendering.Ecs.MeshRenderable, T:Vixen.Rendering.Ecs.MeshRenderables, T:Vixen.Rendering.Ecs.PrimitiveShape, T:Vixen.Rendering.Ecs.MeshExtractionSystem, T:Vixen.Rendering.Ecs.IMeshSource, T:Vixen.Rendering.Ecs.IMaterialSource, T:Vixen.Rendering.Ecs.ISurfaceSource, T:Vixen.Rendering.Features.MeshRenderFeature, T:Vixen.Rendering.Features.MaterialRenderFeature, T:Vixen.Rendering.Features.PermutationKeyDictionary, T:Vixen.Engine.Renderer.AssetMeshSource, T:Vixen.Engine.Renderer.AssetMaterialSource, T:Vixen.Editor.Assets.Content.ProjectMeshSource, T:Vixen.Editor.Assets.Content.ProjectSurfaceSource, T:Vixen.Editor.Assets.Materials.MaterialImporter, T:Vixen.Editor.AssetEditors.Materials.MaterialAsset]
tags: [rendering, materials, meshes, assets, naming]
since: 0.1
status: stable
related: [rendering/lit-path, rendering/blend-shapes, rendering/texture-streaming, ecs/components, assets/content-in-a-game]
---

## What it is

A map of the two most overloaded words in the tree. Neither *mesh* nor *material* names a type in
Vixen; each names a **chain** of types — one link per stage of the same journey, from the file an
artist saved to the bytes a draw call binds — and every link is a different shape because every stage
has a different constraint. Most of the confusion in reading this code is mistaking two links of one
chain for two competing designs. The rest of it is that four unrelated subsystems use the same two
English words for something else entirely, and their types sort next to these ones alphabetically.

| Stage | Question it answers | Mesh side | Material side |
|---|---|---|---|
| **Authored** | What did a person edit? | a model file → `ModelData` | `.vxmat` → `MaterialAsset` |
| **Content** | What did the build write, and what does an address resolve to? | `MeshData` | `MaterialContent` |
| **Scene** | What does this entity say it draws? | `MeshRenderable`, `PrimitiveShape` | the same component's `Material` reference |
| **Frame** | What does a draw call need? | `MeshDraw` + a `GeometrySlice` | `Material` (a variant, a parameter block, a descriptor set) |
| **Shader** | What runs on the device? | `Mesh.rvn` | `IMaterialSurface` and `IShadingModel` in `Raven/Library/Material` |

## What it is for

**The chain exists because no one form can satisfy every stage.** A file cannot hold a descriptor set
or a compiled shader variant, so the authored form is names and numbers. A shipping runtime links no
YAML parser — that is a deliberate exclusion, recorded in `Vixen.Rendering`'s project file — so the
content form is a baked chunk rather than the text. A frame reads per-object data out of native
arrays with no indirection, so the frame form is a value type holding handles. Collapsing any two of
those into one type does not simplify anything: it makes one of the stages carry a field it cannot
fill.

**The word "material" splits once more than "mesh" does**, and that split is the one worth learning
first. `MaterialDescriptor` is what `MaterialCompiler` consumes; `MaterialContent` is what an asset
address resolves to. They look like duplicates and are not — content names its shading model as a
*string* so a document can carry a model this build has never heard of and be told so by name, and it
carries the texture assignments, which the compiler has no use for and a host does.

**You do not need most of this.** Putting a crate in a level is three types — `MeshRenderable`,
`AssetReference`, and whichever material the artist made. Everything below the scene row is the
engine's own plumbing, and the only reason to read it is that you are changing it, or that a name
collided with one you were looking for.

## Using it

### The naming rules, which are consistent once you know them

| Suffix | Means | Examples |
|---|---|---|
| `…Data` | CPU arrays, as an authoring tool produced them | `MeshData`, `ModelData`, `SkeletonData` |
| `…Descriptor` | The authored, serialisable model a compiler consumes | `MaterialDescriptor` |
| `…Content` | The baked chunk an asset address resolves to | `MaterialContent` |
| `…Asset` / `…Document` / `…View` | Editor-only: the file's shape, the open document, the panel | `MaterialAsset`, `MaterialDocument`, `MaterialView` |
| `…Importer` | A build step that reads a file and writes artefacts | `MaterialImporter`, `NavMeshImporter` |
| `…Compiler` | A pure function with no device and no I/O in it | `MaterialCompiler`, `ModelCompiler` |
| `…Source` | An interface the renderer asks, so it need not know what a bundle is | `IMeshSource`, `IMaterialSource`, `ISurfaceSource` |
| `…RenderFeature` | A slice of the render pipeline, holding one array per object | `MeshRenderFeature`, `MaterialRenderFeature` |
| `…Renderer` | An immediate-mode drawer that owns its own pipeline and ring buffer | `MeshRenderer`, `MeshInstanceRenderer` |
| bare `Material`, `MeshDraw` | The frame form — handles, not names | `Vixen.Rendering.Material` |

⚠ **"Feature" means two unrelated things, and they are one namespace apart.** An `IMaterialFeature` in
`Vixen.Rendering.Materials` is a *contribution to a surface* — a normal map, a clear coat — that
becomes one link in a shader's composition chain. A `RenderFeature` in `Vixen.Rendering.Features` is a
*slice of the renderer* that owns one parallel array per render object. `MaterialRenderFeature` is the
second kind, and it is not a material feature. Nothing is going to rename either of them; the
namespace is the discriminator.

⚠ **The device-side geometry store is called `Geometry`, not `Mesh`.** `GeometryBuffer`,
`GeometrySlice`, `GeometryResidency` and `GeometryKey` are where a `MeshData` ends up once it is on
the device, and `SurfaceGeometry` is the one function that converts between the two. If you are
looking for "the vertex buffer my mesh lives in", it is not in a file with `Mesh` in its name.

### The mesh chain

| Type | Namespace | What it is |
|---|---|---|
| `ModelData` | `Vixen.Rendering` | A whole authored model: a node hierarchy and what hangs off it. Editor-domain |
| `ModelNode` | `Vixen.Rendering` | One node of that hierarchy, flat with a parent index |
| `ModelPart` | `Vixen.Rendering` | One drawable piece: a mesh *named*, at a node, with a material |
| `MeshData` | `Vixen.Rendering` | One mesh's geometry as parallel typed arrays. The chunk a mesh reference resolves to |
| `SkeletonData`, `SkeletonJoint` | `Vixen.Rendering` | The joints a skinned `MeshData` is deformed by |
| `AnimationClipData`, `AnimationChannel` | `Vixen.Rendering` | What an animation moves, beside the mesh it moves |
| `MeshRenderable` | `Vixen.Rendering.Ecs` | The component: which mesh, which material, whether it casts shadows |
| `MeshRenderables` | `Vixen.Rendering.Ecs` | Attach/read helpers, and the non-zero defaults a zeroed struct cannot have |
| `PrimitiveShape`, `PrimitiveShapes` | `Vixen.Rendering.Ecs` | The other drawable component: a built-in shape and nothing else |
| `PrimitiveKind` | `Vixen.Rendering` | Which built-in shape. The enum's order is a file format |
| `MeshPrimitives` | `Vixen.Rendering` | Builds those eight shapes on demand, each fitting the unit cube |
| `RenderHandle` | `Vixen.Rendering.Ecs` | Written by extraction: the render object and the residency claim an entity holds. Never serialised |
| `MeshExtractionSystem` | `Vixen.Rendering.Ecs` | Reconciles drawable entities into render objects, once per frame |
| `IMeshSource` | `Vixen.Rendering.Ecs` | Where geometry comes from. Asks rather than waits — false means "not yet" |
| `AssetMeshSource` | `Vixen.Engine.Renderer` | `IMeshSource` over the content manager, for a running game |
| `ProjectMeshSource` | `Vixen.Editor.Assets.Content` | `IMeshSource` over the editor's own import cache |
| `SurfaceGeometry` | `Vixen.Rendering` | Turns a `MeshData` into the vertices a `GeometryBuffer` holds |
| `GeometryBuffer`, `GeometrySlice` | `Vixen.Rendering` | Many meshes in one vertex and one index buffer, each at its own offset |
| `GeometryResidency`, `GeometryKey` | `Vixen.Rendering.Ecs` | Which geometry is uploaded, and the claim that keeps it there |
| `MeshDraw` | `Vixen.Rendering` | One indexed draw: buffers, range, layout. One submesh, never a list |
| `MeshRenderFeature` | `Vixen.Rendering.Features` | Owns one `MeshDraw` per object and records the draw calls |
| `IDrawSubFeature` | `Vixen.Rendering.Features` | Something that contributes commands to each of those draws |
| `MeshRenderer` | `Vixen.Rendering` | Immediate-mode world-space triangles for a block-out, a hull, a preview. Not the frame path |
| `MeshVertex`, `MeshShaders` | `Vixen.Rendering` | That renderer's vertex and its two stages |
| `MeshInstanceRenderer` | `Vixen.Rendering` | Shapes held on the device, drawn once per entity from a transform — the editor viewport's path |
| `MeshShapeVertex`, `MeshInstance`, `MeshShapeGeometry`, `MeshInstanceBatch`, `MeshInstanceShaders`, `MeshInstanceView` | `Vixen.Rendering` | That renderer's vertex, per-entity record, registered shape, draw run, shaders and camera |
| `SceneMeshes`, `SceneShape`, `ShapeBatch` | `Vixen.Editor.SceneView` | Collects a scene's shaped entities into instanced runs, device-free |
| `MeshElements`, `MeshEdge` | `Vixen.Editor.SceneView` | A mesh's vertices, edges and faces as things a pointer can hit |
| `MeshletPagePool` | `Vixen.Rendering` | A device buffer of fixed-size virtual-geometry page slots |
| `IMeshletPageSource`, `MemoryMeshletPageSource`, `StreamMeshletPageSource` | `Vixen.Rendering` | Where a page's bytes are read from: memory, or a build's blobs |
| `ClusterMesh`, `RasterMesh` | `Vixen.Rendering` | Where a registered mesh's records live in the scene-wide GPU buffers, and its quantization grid |
| `MeshKeys`, `MeshInstancedKeys` | `Vixen.Shaders.Generated` | Generated parameter keys for `Mesh.rvn` and `MeshInstanced.rvn` |

### The material chain

| Type | Namespace | What it is |
|---|---|---|
| `MaterialAsset` | `Vixen.Editor.AssetEditors.Materials` | A material as a `.vxmat` holds it — the editor's binding of the file |
| `IMaterialParameter` | `Vixen.Editor.AssetEditors.Materials` | One value the file sets, tagged by kind |
| `ScalarParameter`, `FlagParameter`, `VectorParameter`, `ColourParameter`, `TextureParameter` | `Vixen.Editor.AssetEditors.Materials` | The five kinds. The YAML tag is the discriminator |
| `MaterialDocument` | `Vixen.Editor.AssetEditors.Materials` | That asset, open for editing, with undo |
| `MaterialHeaderEdits`, `MaterialParameterCommand` | `Vixen.Editor.AssetEditors.Materials` | The header as something an inspector can edit; adding or removing one parameter, undoably |
| `MaterialView`, `MaterialPreviewShape`, `MaterialEditorFactory` | `Vixen.Editor.AssetEditors.Materials` | The panel, what the preview is drawn on, and what opens it |
| `MaterialImporter`, `MaterialImportSettings` | `Vixen.Editor.Assets.Materials` | Compiles a `.vxmat` into the chunk a player loads. Runs the compiler only to find out whether it is a material |
| `MaterialContent` | `Vixen.Rendering.Materials` | That chunk: shader, shading model *by name*, features, textures |
| `MaterialTexture` | `Vixen.Rendering.Materials` | One texture assignment, as a parameter name and a reference — never a handle |
| `MaterialDescriptor` | `Vixen.Rendering.Materials` | The authored model `MaterialCompiler` consumes: shader name, features, a resolved shading model |
| `IMaterialFeature` | `Vixen.Rendering.Materials` | One contribution to the surface. Order is load-bearing: each reads what the last one wrote |
| `MetalRoughnessFeature`, `TexturedMetalRoughnessFeature`, `SpecularGlossinessFeature` | `Vixen.Rendering.Materials` | The base workflows |
| `NormalMapFeature`, `EmissiveFeature`, `OcclusionFeature`, `AnisotropyFeature`, `ClearCoatFeature`, `ClearCoatNormalMapFeature`, `SheenFeature`, `SubsurfaceFeature` | `Vixen.Rendering.Materials` | The optional features that switch on a lobe |
| `TexturedNormalMapFeature` | `Vixen.Rendering.Materials` | A tangent-space normal read from a **map**, where `NormalMapFeature` carries one constant vector. Needs a table and a pairing, exactly as the textured base colour does |
| `TexturedOrmFeature` | `Vixen.Rendering.Materials` | Occlusion, roughness and metalness from one packed map — R, G, B. ⚠ Reads the base albedo back out of the surface, so the base feature must be authored at metalness 0 |
| `GraphSurfaceFeature`, `GraphSurfaceNumber`, `GraphSurfaceVector`, `GraphSurfaceMap` | `Vixen.Rendering.Materials` | A surface authored as a shader graph rather than chosen from the library. ⚠ The only feature whose `ShaderName` is **data** — every other one names a file somebody committed, this names a shader a build generated from a `.vxshadergraph` |
| `TexturedEmissiveFeature` | `Vixen.Rendering.Materials` | Emission from a **map**, where `EmissiveFeature` emits over the whole surface. ⚠ Its colour defaults to white rather than black, because here the colour tints the map instead of being the emission |
| `TexturedOpacityFeature` | `Vixen.Rendering.Materials` | Coverage from a mask. ⚠ Reads the map's **red** channel: a one-channel mask samples alpha as 1, so reading alpha would make every mask opaque. A base colour's own alpha already reaches coverage through `TexturedMetalRoughnessFeature` |
| `MaterialLayersFeature`, `MaterialLayerValue`, `BlendFeature` | `Vixen.Rendering.Materials` | Layering and mixing: rock under moss under snow; two surfaces by a weight |
| `TexturedMaterialLayersFeature` | `Vixen.Rendering.Materials` | The same stack with its weights **painted**: one splat map, R G B A as layers 0 to 3. ⚠ `MaterialLayerValue.Weight` becomes a *scale* on the painted channel; one is the map exactly |
| ⤷ `PaintedChannels` | same | ⚠ How many channels that map really has, **three by default**. A one- or three-channel texture samples alpha as 1, so a fourth layer read from `.a` would weigh 1 at every texel and, normalised, be the whole surface. A four-channel map says so and gets its fourth layer; a stack deeper than the count is a compiler warning and an unpainted layer |
| `IMaterialShading` | `Vixen.Rendering.Materials` | What the material does with light. The second slot on a shading pass |
| `StandardShading`, `AnisotropicShading`, `ClearCoatShading`, `SheenShading`, `SubsurfaceShading`, `HairShading`, `CelShading` | `Vixen.Rendering.Materials` | The seven models |
| `MaterialShading` | `Vixen.Rendering.Materials` | The name → model table, so a document can name one |
| `MaterialCompiler` | `Vixen.Rendering.Materials` | Descriptor → `Material`. No shader compiler runs: the names are predicted from Raven's qualification rule |
| `MaterialCompilation` | `Vixen.Rendering.Materials` | What that produced — the material, or null, and the diagnostics either way |
| `MaterialCompilationContext` | `Vixen.Rendering.Materials` | What a feature writes into while it is being compiled |
| `MaterialDiagnostic`, `MaterialDiagnosticId` | `Vixen.Rendering.Materials` | One thing the compiler has to say, and the closed set of things it can say |
| `MaterialKeys` | `Vixen.Rendering.Materials` | The permutation keys the material model sets, such as a layered material's layer count |
| `Material` | `Vixen.Rendering` | **The frame form.** A shader name, a parameter collection, a composition and a descriptor set. The one reference type on the per-object path |
| `IMaterialSource` | `Vixen.Rendering.Ecs` | Where a compiled, shared `Material` comes from. Asks rather than waits |
| `AssetMaterialSource` | `Vixen.Engine.Renderer` | `IMaterialSource` over the content manager: load, compile, paint textures in as they land |
| `ISurfaceSource` | `Vixen.Rendering.Ecs` | `IMaterialSource`'s cheap sibling, for a caller with no compositor. False means "no material", not "not yet" |
| `ProjectSurfaceSource` | `Vixen.Editor.Assets.Content` | `ISurfaceSource` over the editor's import cache |
| `MaterialSurface` | `Vixen.Rendering.Materials` | A material flattened to four numbers a viewport can shade with. Lossy on purpose |
| `MaterialRenderFeature` | `Vixen.Rendering.Features` | Which material each object uses, and which shader variant that resolves to |
| `IPermutationSubFeature` | `Vixen.Rendering.Features` | How skinning or instancing changes the variant without being a material setting |
| `MaterialRecords` | `Vixen.Rendering` | Every material of one effect as records of one buffer — what replaces a descriptor set per material |
| `ResolveMaterial` | `Vixen.Rendering` | What one material's resolve dispatch needs, on the GPU cluster path |
| `ISurfaceMaterial` | `Vixen.Rendering.SurfaceCache` | What a surface is made of, for the surface cache's reference capture to ask |
| `…PerMaterialConstants` | `Vixen.Shaders.Generated` | One generated constant block per shading pass — `ForwardPlusPerMaterialConstants` and its siblings |

The shader half lives in Raven rather than in C#:

| Declaration | File | What it is |
|---|---|---|
| `MaterialData` | `Raven/Library/Material/MaterialSurface.rvn` | Everything a lighting model needs about a point on a surface |
| `IMaterialSurface` | same | The protocol a feature satisfies. `inout`, so a feature contributes rather than replaces |
| `MetalRoughnessSurface`, `TexturedMetalRoughnessSurface`, `SpecularGlossinessSurface`, `NormalMapSurface`, `TexturedNormalMapSurface`, `TexturedOrmSurface`, `EmissiveSurface`, `TexturedEmissiveSurface`, `TexturedOpacitySurface`, `OcclusionSurface` | same | The base and always-available features |
| `MaterialTextures` | same | The sampling the textured workflow inherits |
| `AnisotropySurface`, `ClearCoatSurface`, `ClearCoatNormalMapSurface`, `SheenSurface`, `SubsurfaceSurface`, `MaterialLayersSurface`, `TexturedMaterialLayersSurface`, `BlendSurface` | `MaterialFeatures.rvn` | The optional features |
| `CompositeSurface` | `MaterialFeatures.rvn` | The eight-slot chain a material's features are composed into |
| `IdentitySurface` | `MaterialFeatures.rvn` | What an unfilled slot takes. Raven rejects an empty slot, so every slot is always bound |
| `IShadingModel` and its seven shaders | `ShadingModels.rvn` | The C# shading models' counterparts, one per name |

⚠ **A feature writes channels; a shading model reads them.** That split is what keeps both sets small,
and it is why the C# and Raven sides have the same seven names on one axis and thirteen on the other.

#### Which permutations a variant is selected by, and the one key a host cannot drop

`MaterialRenderFeature.PermutationKeys` is a `PermutationKeyDictionary`: shader name → the keys that
shader's effect key is built from. A host states a pass's set in one line, with the array the shader's
own reflection produced:

```csharp no-compile="one line of a host's own setup, against a renderer it has already built"
renderer.Materials.PermutationKeys["ForwardPlus"] = ForwardPlusKeys.UsedPermutationKeys;
```

⚠ **A key set under a name that list does not carry reaches no compiler at all** — the variant resolves
to whatever the `.rvn` declares as its default, an effect resolves, a pipeline binds and a frame draws.
That is the trap the whole type exists around.

⚠ **And a *composed* surface's permutation is in no pass's reflection.** `LayerCount` is declared by
`TexturedMaterialLayersSurface`, not by `ForwardPlus`, so the generated array above cannot carry it and
a host assigning that array is not making a mistake by leaving it out. The engine registers it with
`PermutationKeys.Register(shader, key)` when the renderer is built — and **a later assignment cannot
take a registered key away**, which is the reason this is a type of its own rather than a
`Dictionary`. It was a dictionary once, the host line ran after the engine's, and every host that drew
compiled three-layer materials as two-layer ones without a word.

### Types called Mesh or Material that are not on either chain

Same word, unrelated meaning. Sorting alphabetically next to a rendering type is the whole reason they
are confusing.

| Types | Namespace | What "mesh" means there |
|---|---|---|
| `NavMesh`, `NavMeshAsset`, `NavMeshTile`, `NavMeshTileData`, `NavMeshParams`, `NavMeshPolyData`, `NavMeshDetailData`, `NavOffMeshConnectionData`, `NavMeshQuery` | `Vixen.Navigation` | The **walkable surface** an agent paths across — convex polygons in tiles. Never drawn |
| `NavMeshBaker`, `NavMeshBakeResult`, `NavMeshBuildSettings`, `NavMeshPartitioning`, `TilePlacement`, `PolyMesh`, `PolyMeshDetail`, `PolyDetail` | `Vixen.Navigation.Baking` | The bake that produces it. `PolyMesh` is a navmesh stage, not geometry |
| `NavMeshDebugDraw`, `NavMeshDrawStyle` | `Vixen.Navigation.Diagnostics` | Drawing that surface for a human |
| `NavMeshImporter`, `NavMeshImportSettings` | `Vixen.Editor.Assets.Navigation` | Baking one from a `.vxnavmesh` |
| `CrowdOffMeshTraversal` | `Vixen.Navigation.Agents` | An agent part-way across an off-mesh link |
| `Meshlet`, `MeshletGroup`, `MeshletMesh`, `MeshletBuilder`, `MeshletBuildInput`, `MeshletBuildSettings`, `MeshletCut`, `MeshletValidator`, `MeshSimplifier` | `Vixen.Rendering.VirtualGeometry` | A **cluster** of ~128 triangles, and the LOD DAG built from them. A `MeshletMesh` is a DAG, not a `MeshData` |
| `MeshletPage`, `MeshletPageSet`, `MeshletPageCluster`, `MeshletPageBuilder`, `MeshletPageSettings` | `Vixen.Rendering.VirtualGeometry` | That DAG packed into fixed-size streamable pages |
| `MeshDistanceField`, `MeshDistanceFieldBaker` | `Vixen.Rendering.DistanceFields` | How far every point in a box is from a mesh's surface — a volume texture, not geometry |

## Examples

**Building a material with no file at all**, which is what the compiler is for and what a test does:

```csharp compile
using Vixen.Core.Mathematics;
using Vixen.Rendering;
using Vixen.Rendering.Materials;

public static class BrassMaterial {
    public static Material? Compile() {
        var descriptor = new MaterialDescriptor {
            ShaderName = "ForwardPlus",
            Features = [
                new MetalRoughnessFeature {
                    BaseColor = new Vector3(0.85f, 0.68f, 0.28f),
                    Metalness = 1f,
                    Roughness = 0.28f
                },
                new NormalMapFeature { Strength = 0.6f }
            ],
            Shading = new StandardShading()
        };

        var compilation = MaterialCompiler.Compile(descriptor);

        return compilation.Failed ? null : compilation.Material;
    }
}
```

Nothing there touches a graphics device, a shader compiler or a frame — which is the property that
lets a material be authored and validated in an editor that has not opened a window yet.

**The same material as a file**, which is the form an artist actually produces. The tags are the
`[DataContract]` names of the feature records above:

```yaml
shader: ForwardPlus
shading: StandardShading
features:
  - !MetalRoughness
    baseColor: 0.85 0.68 0.28
    metalness: 1
    roughness: 0.28
  - !NormalMap
    strength: 0.6
```

`MaterialImporter` reads that at build time and writes a `MaterialContent` chunk. A shipping build
never sees the text.

**Putting a mesh and a material on an entity** — the scene row of the first table, and all most code
ever needs:

```csharp compile
using Vixen.Core;
using Vixen.Ecs;
using Vixen.Rendering.Ecs;

public static class Crates {
    public static void Place(World world, Entity entity, AssetReference mesh, AssetReference material) =>
        MeshRenderables.Attach(
            world,
            entity,
            new MeshRenderable { Mesh = mesh, Material = material, CastsShadows = true }
        );
}
```

⚠ **`CastsShadows` is spelled out because a zeroed struct says `false`.** `MeshRenderables.Default`
sets it for the same reason, and the editor's Add Component goes through that — but a renderable built
field-by-field, as above, casts nothing unless the line is there. What it costs when it is *not* set is
a stage bit, so it only bites in a project that has named its caster stages; see
[shadows](shadows.md) for that wiring and for why toggling the flag on a live scene needs
`MeshExtractionSystem.Resettle`.

⚠ **A null material reference is a usable value, not a mistake.** A block-out mesh dropped into a level
before anybody has made a material for it draws with `MeshExtractionSystem.Material`, the renderer's
neutral default. A null *mesh* reference is different: nothing is drawn, because an entity whose shape
depended on disk speed would be worse than one that is late.

**Joining the renderer to the content manager**, which is what makes either reference mean anything.
`Vixen.Rendering` deliberately knows nothing about bundles, so a host supplies the two sources:

```csharp compile
using Vixen.Engine.Renderer;
using Vixen.Rendering.Ecs;

public static class Sources {
    public static void Wire(MeshExtractionSystem extraction, AssetMeshSource meshes, AssetMaterialSource materials) {
        extraction.Meshes = meshes;
        extraction.Materials = materials;
    }
}
```

Leave either null and the corresponding entities are simply never extracted — which is what a project
with no content mounted is, and why an unwired renderer draws an empty frame rather than throwing.

## See also

- [Turning on dynamic global illumination](rendering/lit-path) — what lights the surfaces these
  materials describe.
- [Components](ecs/components) — what makes `MeshRenderable` a component, and why `RenderHandle` is
  not one.
- [Getting content into a running game](assets/content-in-a-game) — how a `MeshData` or a
  `MaterialContent` chunk reaches a build.

The design records are `docs/plan/06-rendering-pipeline.md` for the render features and the extraction
split, `docs/plan/07-raven-shader-pipeline.md` for composition and permutations,
`docs/plan/08-asset-pipeline-and-addressables.md` for the importer/compiler split every `…Importer`
here follows, and `docs/plan/23-bindless-materials.md` for why `MaterialRecords` exists at all.
