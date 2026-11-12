// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Vixen.Core.Imaging;
using Vixen.Core.Mathematics;
using Vixen.Geometry;
using Vixen.Interchange;
using Xunit;

namespace Vixen.Editor.Assets.Tests;

/// <summary>
///     Against real files, read by the real Assimp. The fixtures are OBJ and glTF because both are
///     text and can be written in the test that needs them — a binary fixture checked in beside the
///     tests is a thing nobody can edit and nobody can read the diff of.
/// </summary>
public sealed class ModelReaderTests {
    static readonly ModelReadOptions Default = new();

    [Fact]
    public void ATriangleComesOutAsOneMeshWithItsAttributes() {
        var read = ModelReader.Read(Obj(Triangle), ".obj", "Shape", Default);

        var mesh = Assert.Single(read.Meshes);

        Assert.Equal(3, mesh.VertexCount);
        Assert.Equal(1, mesh.TriangleCount);
        Assert.Equal(3, mesh.Normals.Length);
        Assert.Equal(3, mesh.TexCoords.Length);
        Assert.False(mesh.IsSkinned);
    }

    /// <summary>glTF's v runs down the image as a coordinate here does, so a file keeps its numbers both ways.</summary>
    /// <remarks>
    ///     ⚠ This writer used to turn v over, as it must for OBJ. A written file then read back upside down
    ///     here and, with a texture, in every other tool.
    /// </remarks>
    [Fact]
    public void ACoordinateWrittenToGlbComesBackAsItWas() {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.glb");
        var mesh = new MeshData {
            Name = "Shape",
            Positions = [new(0f, 0f, 0f), new(1f, 0f, 0f), new(0f, 1f, 0f)],
            Normals = [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
            TexCoords = [new(0.1f, 0.2f), new(0.3f, 0.2f), new(0.1f, 0.4f)],
            Indices = [0, 1, 2],
            Bounds = new(new(0f), new(1f))
        };

        try {
            ModelWriter.Write(path, [mesh], "Shape");

            var read = ModelReader.Read(File.ReadAllBytes(path), ".glb", "Shape", Default);

            var coordinates = Assert.Single(read.Meshes).TexCoords;

            foreach (var expected in mesh.TexCoords) {
                Assert.Contains(coordinates, coordinate => (coordinate - expected).Length() < 1e-5f);
            }
        } finally {
            File.Delete(path);
        }
    }

    /// <summary>A file's images arrive by slot, as bytes when they are inside it and as a path when they are not.</summary>
    [Fact]
    public void AGltfMaterialArrivesWithItsImages() {
        var png = Convert.ToBase64String(PngCodec.Encode(new(1, 1, [10, 20, 30, 255])));
        var json = Encoding.UTF8.GetString(Gltf(0f, 0f, 0f))
            .Replace(
                "\"primitives\": [{ \"attributes\": { \"POSITION\": 0 }, \"indices\": 1 }]",
                "\"primitives\": [{ \"attributes\": { \"POSITION\": 0 }, \"indices\": 1, \"material\": 0 }]",
                StringComparison.Ordinal
            )
            .Replace(
                "\"asset\": { \"version\": \"2.0\" },",
                $$"""
                  "asset": { "version": "2.0" },
                  "images": [
                    { "uri": "data:image/png;base64,{{png}}" },
                    { "uri": "maps/normal.png" },
                    { "uri": "maps/orm.png" }
                  ],
                  "textures": [{ "source": 0 }, { "source": 1 }, { "source": 2 }],
                  "materials": [{
                    "name": "Worn",
                    "pbrMetallicRoughness": {
                      "baseColorTexture": { "index": 0 },
                      "metallicRoughnessTexture": { "index": 2 }
                    },
                    "normalTexture": { "index": 1, "scale": 0.5 },
                    "occlusionTexture": { "index": 2 }
                  }],
                  """,
                StringComparison.Ordinal
            );

        var read = ModelReader.Read(Encoding.UTF8.GetBytes(json), ".gltf", "Shape", Default);
        var textures = read.MaterialTextures[read.Model.Parts[0].Material];

        Assert.Equal(".png", textures.BaseColor!.Extension);
        Assert.Equal(PngCodec.Encode(new(1, 1, [10, 20, 30, 255])), textures.BaseColor.Embedded);
        Assert.Equal("maps/normal.png", textures.Normal!.Path);
        Assert.Equal("maps/orm.png", textures.MetallicRoughness!.Path);
        Assert.Equal("maps/orm.png", textures.Occlusion!.Path);
        Assert.Equal(0.5f, textures.NormalScale);
    }

    /// <summary>A viewer with no material assets reads the file's own metallic-roughness answer.</summary>
    [Fact]
    public void AGltfMaterialArrivesWithItsMetallicRoughnessFactors() {
        var json = Encoding.UTF8.GetString(Gltf(0f, 0f, 0f))
            .Replace(
                "\"primitives\": [{ \"attributes\": { \"POSITION\": 0 }, \"indices\": 1 }]",
                "\"primitives\": [{ \"attributes\": { \"POSITION\": 0 }, \"indices\": 1, \"material\": 0 }]",
                StringComparison.Ordinal
            )
            .Replace(
                "\"asset\": { \"version\": \"2.0\" },",
                """
                "asset": { "version": "2.0" },
                "materials": [{
                  "name": "Gold",
                  "pbrMetallicRoughness": {
                    "baseColorFactor": [1.0, 0.75, 0.25, 0.5],
                    "metallicFactor": 0.8,
                    "roughnessFactor": 0.3
                  }
                }],
                """,
                StringComparison.Ordinal
            );

        var read = ModelReader.Read(Encoding.UTF8.GetBytes(json), ".gltf", "Shape", Default);

        // Through the mesh rather than Assert.Single: Assimp appends a default material of its own.
        var material = read.Meshes[0].MaterialIndex;
        Assert.Equal("Gold", read.Model.Materials[material]);
        Assert.Equal(read.Model.Materials.Length, read.MaterialFactors.Length);

        var factors = read.MaterialFactors[material];
        Assert.Equal(new(1f, 0.75f, 0.25f, 0.5f), factors.BaseColor);
        Assert.Equal(0.8f, factors.Metallic, 3);
        Assert.Equal(0.3f, factors.Roughness, 3);
    }

    [Fact]
    public void TheModelNamesItsPartsRatherThanNumberingThem() {
        var read = ModelReader.Read(Obj(Triangle), ".obj", "Shape", Default);

        var part = Assert.Single(read.Model.Parts);

        Assert.Equal(read.Meshes[0].Name, part.Mesh);
        Assert.InRange(part.Node, 0, read.Model.Nodes.Length - 1);
    }

    /// <summary>
    ///     Assimp names the root of a memory import <c>$$$___magic___$$$</c>, which is an artefact of
    ///     how it was handed the bytes rather than anything in the file. Nobody wants that in an
    ///     inspector.
    /// </summary>
    [Fact]
    public void TheRootNodeIsCalledAfterTheAssetRatherThanAfterAssimpsPlaceholder() {
        var read = ModelReader.Read(Obj(Triangle), ".obj", "Shape", Default);

        Assert.Equal("Shape", read.Model.Nodes[0].Name);
        Assert.Equal(-1, read.Model.Nodes[0].Parent);
    }

    [Fact]
    public void ScaleMultipliesEveryLength() {
        var read = ModelReader.Read(Obj(Triangle), ".obj", "Shape", Default with { Scale = 100f });

        Assert.Equal(100f, read.Meshes[0].Positions.Max(static position => position.X), 3);
        Assert.Equal(100f, read.Model.Bounds.Maximum.X, 3);
    }

    /// <summary>
    ///     The one that would be quietly wrong for ever. Assimp stores a column-vector matrix
    ///     row-major, so a node's translation is in its fourth <em>column</em>; Vixen's row-vector
    ///     matrix keeps it in the fourth <em>row</em>. A field-for-field copy compiles, runs, and
    ///     assembles every hierarchy inside out.
    /// </summary>
    [Fact]
    public void ANodeTranslationLandsInTheRowVixenKeepsItIn() {
        var read = ModelReader.Read(Gltf(3f, 4f, 5f), ".gltf", "Offset", Default);

        // Whichever node the mesh hangs off. Assimp collapses a single-node glTF scene onto the
        // root, so naming an index here would pin an Assimp detail rather than the conversion.
        var node = read.Model.Nodes[Assert.Single(read.Model.Parts).Node];

        Assert.Equal(3f, node.Transform.M41, 3);
        Assert.Equal(4f, node.Transform.M42, 3);
        Assert.Equal(5f, node.Transform.M43, 3);
    }

    /// <summary>
    ///     Bounds are the union of each part put through its node's world transform. A mesh at the
    ///     origin hanging off a node three metres along X occupies a box three metres along X, and
    ///     bounds that ignored the hierarchy would cull it away.
    /// </summary>
    [Fact]
    public void BoundsFollowTheHierarchyRatherThanTheMeshesAsTheySit() {
        var read = ModelReader.Read(Gltf(3f, 0f, 0f), ".gltf", "Offset", Default);

        Assert.Equal(3f, read.Model.Bounds.Minimum.X, 3);
        Assert.Equal(4f, read.Model.Bounds.Maximum.X, 3);
    }

    /// <summary>
    ///     Sub-asset ids are derived from names, so two meshes called the same thing derive one id
    ///     and the import is refused outright. An exporter naming every mesh after one source object
    ///     is ordinary, so this is the common case rather than the pathological one.
    /// </summary>
    [Fact]
    public void TwoMeshesWithOneNameAreGivenDistinctOnes() {
        var read = ModelReader.Read(GltfTwins(), ".gltf", "Shape", Default);

        Assert.Equal(2, read.Meshes.Length);
        Assert.Equal(2, read.Meshes.Select(static mesh => mesh.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(2, read.Model.Parts.Select(static part => part.Mesh).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void SomethingThatIsNotAModelSaysSoRatherThanReturningAnEmptyOne() {
        var failure = Assert.Throws<ModelFormatException>(static () => ModelReader.Read(
                "{ this is not glTF"u8,
                ".gltf",
                "Shape",
                Default
            )
        );

        Assert.NotEmpty(failure.Message);
    }

    /// <summary>
    ///     Assimp's OBJ reader ignores lines it does not recognise, so prose with an <c>.obj</c>
    ///     extension parses successfully into a scene with nothing in it. That is not an error the
    ///     reader can invent — the file really did parse — so it comes out as a model with no meshes,
    ///     and the importer is what says so.
    /// </summary>
    [Fact]
    public void AFileThatParsesIntoNothingIsAModelWithNoMeshesRatherThanAFailure() {
        var read = ModelReader.Read("this is not a model"u8, ".obj", "Shape", Default);

        Assert.Empty(read.Meshes);
    }

    [Fact]
    public void AnEmptyFileIsRefusedBeforeAssimpSeesIt() {
        Assert.Throws<ModelFormatException>(static () => ModelReader.Read([], ".obj", "Shape", Default));
    }

    [Fact]
    public void TurningTangentsOffLeavesTheArrayEmptyRatherThanZeroed() {
        var read = ModelReader.Read(Obj(Triangle), ".obj", "Shape", Default with { GenerateTangents = false });

        Assert.Empty(read.Meshes[0].Tangents);
        Assert.NotEmpty(read.Meshes[0].Normals);
    }

    // --- Blend shapes -------------------------------------------------------

    /// <summary>
    ///     ⚠ A glTF morph target arrives as a <em>delta</em>, and the delta is the one the file names.
    /// </summary>
    /// <remarks>
    ///     <b>The assertion the whole importer half reduces to, and it is numeric on purpose.</b>
    ///     Assimp hands back a whole replacement vertex array rather than a delta — glTF's relative
    ///     targets have already been added to the base by the time <c>aiAnimMesh</c> exists — so the
    ///     subtraction is ours to get right, and getting it backwards produces a face that doubles its
    ///     expression at weight one and un-expresses at weight zero. Both of those look like a
    ///     <em>rig</em> problem rather than an import one.
    /// </remarks>
    [Fact]
    public void AGltfMorphTargetArrivesAsTheDeltaTheFileNames() {
        var read = ModelReader.Read(GltfMorphed(), ".gltf", "Face", Default);

        var mesh = Assert.Single(read.Meshes);

        Assert.True(mesh.IsMorphed);
        Assert.Equal(2, mesh.MorphTargets.Length);

        var jaw = mesh.MorphTargets[0];

        // One vertex moved, and it is the one the target's accessor is non-zero at.
        Assert.Equal([1], jaw.Indices);

        var delta = jaw.PositionDelta(0);
        var quantum = jaw.PositionScale / MorphTargetData.Quantum;

        Assert.Equal(0f, delta.X, quantum);
        Assert.Equal(0f, delta.Y, quantum);
        Assert.Equal(2f, delta.Z, quantum);
    }

    /// <summary>
    ///     ⚠ And it is scaled with the mesh, because it is added to positions that were.
    /// </summary>
    /// <remarks>
    ///     A delta left in file units and applied to a mesh in metres is a shape a hundred times too
    ///     large on anything out of Max or Maya, and it is invisible until somebody moves a slider.
    /// </remarks>
    [Fact]
    public void ABlendShapeDeltaIsScaledWithTheMeshItBelongsTo() {
        var read = ModelReader.Read(GltfMorphed(), ".gltf", "Face", Default with { Scale = 100f });

        var jaw = read.Meshes[0].MorphTargets[0];
        var quantum = jaw.PositionScale / MorphTargetData.Quantum;

        Assert.Equal(200f, jaw.PositionDelta(0).Z, quantum);
    }

    /// <summary>Only the vertices a shape actually moves are stored.</summary>
    /// <remarks>
    ///     The whole point of the format. An exporter writes a delta for every vertex of the mesh and
    ///     all but one of them here is zero; a target that kept them would cost a full vertex array
    ///     per shape.
    /// </remarks>
    [Fact]
    public void OnlyTheVerticesAShapeMovesAreStored() {
        var read = ModelReader.Read(GltfMorphed(), ".gltf", "Face", Default);
        var mesh = read.Meshes[0];

        Assert.Equal(3, mesh.VertexCount);
        Assert.All(mesh.MorphTargets, static target => Assert.Equal(1, target.Count));
    }

    [Fact]
    public void TurningBlendShapesOffLeavesNoneRatherThanEmptyOnes() {
        var read = ModelReader.Read(GltfMorphed(), ".gltf", "Face", Default with { ImportBlendShapes = false });

        Assert.Empty(read.Meshes[0].MorphTargets);
        Assert.False(read.Meshes[0].IsMorphed);
    }

    // --- Blend-shape weight tracks ------------------------------------------

    /// <summary>
    ///     ⚠ A morph-weight sampler becomes one scalar channel per shape, named after the shape.
    /// </summary>
    /// <remarks>
    ///     <b>The half of an animation that was being dropped by omission.</b> Assimp puts node
    ///     transforms in <c>mChannels</c> and morph weights in <c>mMeshMorphChannels</c>, and a reader
    ///     that walked only the first imported a character whose body moved and whose face did not —
    ///     with no warning anywhere, because nothing was asked for and nothing failed. The names are
    ///     the assertion that matters: a source file addresses a shape by its slot, and the slots are
    ///     not <c>MeshData.MorphTargets</c>' slots, so a curve stored against an index would silently
    ///     re-target itself on the next export.
    /// </remarks>
    [Fact]
    public void AMorphWeightSamplerBecomesOneNamedScalarChannelPerShape() {
        var read = ModelReader.Read(GltfMorphAnimated(), ".gltf", "Face", Default);

        var clip = Assert.Single(read.Animations);
        var weighted = clip.Channels.Where(static channel => channel.WeightTimes.Length > 0).ToArray();

        Assert.Equal(2, weighted.Length);
        Assert.Equal(["jawOpen", "browRaise"], weighted.Select(static channel => channel.Shape));

        // Key-major in the file — two keys of two targets — and one curve per shape out of it.
        Assert.Equal([0f, 1f], weighted[0].WeightTimes);
        Assert.Equal([0f, 1f], weighted[0].Weights);
        Assert.Equal([0f, 1f], weighted[1].WeightTimes);
        Assert.Equal([0f, 0.5f], weighted[1].Weights);
    }

    /// <summary>
    ///     The names the curves carry are the names the deltas carry, and one table makes both.
    /// </summary>
    [Fact]
    public void AWeightChannelNamesTheShapeTheMeshStored() {
        var read = ModelReader.Read(GltfMorphAnimated(), ".gltf", "Face", Default);

        var shapes = read.Meshes[0].MorphTargets.Select(static target => target.Name).ToArray();
        var driven = read.Animations[0]
            .Channels
            .Where(static channel => channel.WeightTimes.Length > 0)
            .Select(static channel => channel.Shape);

        Assert.Equal(shapes, driven);
    }

    /// <summary>A weight channel carries no transform keys, and vice versa.</summary>
    /// <remarks>
    ///     Nothing produces both, which is what lets the bake tell them apart without a discriminator
    ///     member — and what keeps a face's curves out of the unresolved-channel count.
    /// </remarks>
    [Fact]
    public void AWeightChannelAndATransformChannelAreNeverTheSameChannel() {
        var read = ModelReader.Read(GltfMorphAnimated(), ".gltf", "Face", Default);

        Assert.Contains(read.Animations[0].Channels, static channel => channel.WeightTimes.Length > 0);

        foreach (var channel in read.Animations[0].Channels) {
            var transform =
                channel.PositionTimes.Length + channel.RotationTimes.Length + channel.ScaleTimes.Length;

            Assert.True(
                channel.WeightTimes.Length == 0 || transform == 0,
                $"'{channel.Target}' carries both a weight track and {transform} transform key(s)."
            );
        }
    }

    /// <summary>A model with no shapes carries an empty array rather than paying for one.</summary>
    [Fact]
    public void AMeshWithNoBlendShapesCarriesNone() {
        var read = ModelReader.Read(Obj(Triangle), ".obj", "Shape", Default);

        Assert.Empty(read.Meshes[0].MorphTargets);
    }

    const string Triangle = """
                            o Tri
                            v 0 0 0
                            v 1 0 0
                            v 0 1 0
                            vt 0 0
                            vt 1 0
                            vt 0 1
                            f 1/1 2/2 3/3
                            """;

    static byte[] Obj(string text) => Encoding.UTF8.GetBytes(text);

    /// <summary>
    ///     A glTF with one triangle under one translated node, assembled here rather than checked in.
    /// </summary>
    /// <remarks>
    ///     The buffer is built and base64'd at run time, so the fixture reads as the geometry it is
    ///     rather than as a wall of encoded bytes nobody can verify by looking.
    /// </remarks>
    static byte[] Gltf(float x, float y, float z) {
        float[] positions = [0, 0, 0, 1, 0, 0, 0, 1, 0];
        ushort[] indices = [0, 1, 2];

        var buffer = new byte[positions.Length * 4 + indices.Length * 2];
        Buffer.BlockCopy(positions, 0, buffer, 0, positions.Length * 4);
        Buffer.BlockCopy(indices, 0, buffer, positions.Length * 4, indices.Length * 2);

        var json = $$"""
                     {
                       "asset": { "version": "2.0" },
                       "scene": 0,
                       "scenes": [{ "nodes": [0] }],
                       "nodes": [{ "name": "Offset", "translation": [{{F(x)}}, {{F(y)}}, {{F(z)}}], "mesh": 0 }],
                       "meshes": [{
                         "name": "Tri",
                         "primitives": [{ "attributes": { "POSITION": 0 }, "indices": 1 }]
                       }],
                       "buffers": [{
                         "byteLength": {{buffer.Length}},
                         "uri": "data:application/octet-stream;base64,{{Convert.ToBase64String(buffer)}}"
                       }],
                       "bufferViews": [
                         { "buffer": 0, "byteOffset": 0, "byteLength": {{positions.Length * 4}}, "target": 34962 },
                         { "buffer": 0, "byteOffset": {{positions.Length * 4}}, "byteLength": {{indices.Length * 2}}, "target": 34963 }
                       ],
                       "accessors": [
                         {
                           "bufferView": 0, "componentType": 5126, "count": 3, "type": "VEC3",
                           "min": [0, 0, 0], "max": [1, 1, 0]
                         },
                         { "bufferView": 1, "componentType": 5123, "count": 3, "type": "SCALAR" }
                       ]
                     }
                     """;

        return Encoding.UTF8.GetBytes(json);
    }

    /// <summary>
    ///     A glTF triangle with two morph targets, one moving each of two vertices.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>glTF stores a target as a delta, and one accessor per target per attribute.</b> Both
    ///     targets here are mostly zeros, which is the shape of every real one — a brow-raise's
    ///     accessor is forty thousand vertices of nothing and a few hundred of something — and it is
    ///     what makes the sparsify step the difference between sixteen bytes and a vertex array.
    /// </remarks>
    static byte[] GltfMorphed() {
        float[] positions = [0, 0, 0, 1, 0, 0, 0, 1, 0];
        ushort[] indices = [0, 1, 2];

        // Vertex 1 moves two along Z; vertex 2 moves half a unit along X. Nothing else moves.
        float[] jaw = [0, 0, 0, 0, 0, 2, 0, 0, 0];
        float[] brow = [0, 0, 0, 0, 0, 0, 0.5f, 0, 0];

        var buffer = new byte[positions.Length * 4 + indices.Length * 2 + jaw.Length * 4 + brow.Length * 4];
        var at = 0;

        Buffer.BlockCopy(positions, 0, buffer, at, positions.Length * 4);
        var indicesAt = at += positions.Length * 4;

        Buffer.BlockCopy(indices, 0, buffer, at, indices.Length * 2);
        var jawAt = at += indices.Length * 2;

        Buffer.BlockCopy(jaw, 0, buffer, at, jaw.Length * 4);
        var browAt = at += jaw.Length * 4;

        Buffer.BlockCopy(brow, 0, buffer, at, brow.Length * 4);

        var json = $$"""
                     {
                       "asset": { "version": "2.0" },
                       "scene": 0,
                       "scenes": [{ "nodes": [0] }],
                       "nodes": [{ "name": "Face", "mesh": 0 }],
                       "meshes": [{
                         "name": "Head",
                         "weights": [0, 0],
                         "extras": { "targetNames": ["jawOpen", "browRaise"] },
                         "primitives": [{
                           "attributes": { "POSITION": 0 },
                           "indices": 1,
                           "targets": [{ "POSITION": 2 }, { "POSITION": 3 }]
                         }]
                       }],
                       "buffers": [{
                         "byteLength": {{buffer.Length}},
                         "uri": "data:application/octet-stream;base64,{{Convert.ToBase64String(buffer)}}"
                       }],
                       "bufferViews": [
                         { "buffer": 0, "byteOffset": 0, "byteLength": {{positions.Length * 4}}, "target": 34962 },
                         { "buffer": 0, "byteOffset": {{indicesAt}}, "byteLength": {{indices.Length * 2}}, "target": 34963 },
                         { "buffer": 0, "byteOffset": {{jawAt}}, "byteLength": {{jaw.Length * 4}}, "target": 34962 },
                         { "buffer": 0, "byteOffset": {{browAt}}, "byteLength": {{brow.Length * 4}}, "target": 34962 }
                       ],
                       "accessors": [
                         {
                           "bufferView": 0, "componentType": 5126, "count": 3, "type": "VEC3",
                           "min": [0, 0, 0], "max": [1, 1, 0]
                         },
                         { "bufferView": 1, "componentType": 5123, "count": 3, "type": "SCALAR" },
                         {
                           "bufferView": 2, "componentType": 5126, "count": 3, "type": "VEC3",
                           "min": [0, 0, 0], "max": [0, 0, 2]
                         },
                         {
                           "bufferView": 3, "componentType": 5126, "count": 3, "type": "VEC3",
                           "min": [0, 0, 0], "max": [0.5, 0, 0]
                         }
                       ]
                     }
                     """;

        return Encoding.UTF8.GetBytes(json);
    }

    /// <summary>
    ///     The same head, with an animation that drives its two shapes through a <c>weights</c>
    ///     sampler.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A glTF <c>weights</c> sampler holds <em>every</em> target's weight at every key</b>,
    ///     laid out key-major — two keys of two targets is four floats, not two curves of two. Assimp
    ///     hands that back as one <c>aiMeshMorphAnim</c> whose keys carry a vector of
    ///     <c>(slot, weight)</c> pairs, and turning it into a curve per shape is the importer's job.
    ///     The values are halves, so a sample between the keys is exact.
    /// </remarks>
    static byte[] GltfMorphAnimated() {
        float[] positions = [0, 0, 0, 1, 0, 0, 0, 1, 0];
        ushort[] indices = [0, 1, 2];
        float[] jaw = [0, 0, 0, 0, 0, 2, 0, 0, 0];
        float[] brow = [0, 0, 0, 0, 0, 0, 0.5f, 0, 0];

        // Two keys, a second apart. At the first the face is at rest; at the second the jaw is fully
        // open and the brow is half raised.
        float[] times = [0, 1];
        float[] outputs = [0, 0, 1, 0.5f];

        var buffer = new byte[
            positions.Length * 4
            + indices.Length * 2
            + jaw.Length * 4
            + brow.Length * 4
            + times.Length * 4
            + outputs.Length * 4
        ];

        var at = 0;

        Buffer.BlockCopy(positions, 0, buffer, at, positions.Length * 4);
        var indicesAt = at += positions.Length * 4;

        Buffer.BlockCopy(indices, 0, buffer, at, indices.Length * 2);
        var jawAt = at += indices.Length * 2;

        Buffer.BlockCopy(jaw, 0, buffer, at, jaw.Length * 4);
        var browAt = at += jaw.Length * 4;

        Buffer.BlockCopy(brow, 0, buffer, at, brow.Length * 4);
        var timesAt = at += brow.Length * 4;

        Buffer.BlockCopy(times, 0, buffer, at, times.Length * 4);
        var outputsAt = at += times.Length * 4;

        Buffer.BlockCopy(outputs, 0, buffer, at, outputs.Length * 4);

        var json = $$"""
                     {
                       "asset": { "version": "2.0" },
                       "scene": 0,
                       "scenes": [{ "nodes": [0] }],
                       "nodes": [{ "name": "Face", "mesh": 0 }],
                       "meshes": [{
                         "name": "Head",
                         "weights": [0, 0],
                         "extras": { "targetNames": ["jawOpen", "browRaise"] },
                         "primitives": [{
                           "attributes": { "POSITION": 0 },
                           "indices": 1,
                           "targets": [{ "POSITION": 2 }, { "POSITION": 3 }]
                         }]
                       }],
                       "animations": [{
                         "name": "Talk",
                         "samplers": [{ "input": 4, "output": 5, "interpolation": "LINEAR" }],
                         "channels": [{ "sampler": 0, "target": { "node": 0, "path": "weights" } }]
                       }],
                       "buffers": [{
                         "byteLength": {{buffer.Length}},
                         "uri": "data:application/octet-stream;base64,{{Convert.ToBase64String(buffer)}}"
                       }],
                       "bufferViews": [
                         { "buffer": 0, "byteOffset": 0, "byteLength": {{positions.Length * 4}}, "target": 34962 },
                         { "buffer": 0, "byteOffset": {{indicesAt}}, "byteLength": {{indices.Length * 2}}, "target": 34963 },
                         { "buffer": 0, "byteOffset": {{jawAt}}, "byteLength": {{jaw.Length * 4}}, "target": 34962 },
                         { "buffer": 0, "byteOffset": {{browAt}}, "byteLength": {{brow.Length * 4}}, "target": 34962 },
                         { "buffer": 0, "byteOffset": {{timesAt}}, "byteLength": {{times.Length * 4}} },
                         { "buffer": 0, "byteOffset": {{outputsAt}}, "byteLength": {{outputs.Length * 4}} }
                       ],
                       "accessors": [
                         {
                           "bufferView": 0, "componentType": 5126, "count": 3, "type": "VEC3",
                           "min": [0, 0, 0], "max": [1, 1, 0]
                         },
                         { "bufferView": 1, "componentType": 5123, "count": 3, "type": "SCALAR" },
                         {
                           "bufferView": 2, "componentType": 5126, "count": 3, "type": "VEC3",
                           "min": [0, 0, 0], "max": [0, 0, 2]
                         },
                         {
                           "bufferView": 3, "componentType": 5126, "count": 3, "type": "VEC3",
                           "min": [0, 0, 0], "max": [0.5, 0, 0]
                         },
                         {
                           "bufferView": 4, "componentType": 5126, "count": 2, "type": "SCALAR",
                           "min": [0], "max": [1]
                         },
                         { "bufferView": 5, "componentType": 5126, "count": 4, "type": "SCALAR" }
                       ]
                     }
                     """;

        return Encoding.UTF8.GetBytes(json);
    }

    /// <summary>Two meshes, both called <c>Tri</c>, which is what an exporter does all the time.</summary>
    static byte[] GltfTwins() {
        var text = Encoding.UTF8.GetString(Gltf(0f, 0f, 0f))
            .Replace(
                "\"nodes\": [{ \"name\": \"Offset\", \"translation\": [0.0, 0.0, 0.0], \"mesh\": 0 }]",
                "\"nodes\": [{ \"name\": \"A\", \"mesh\": 0 }, { \"name\": \"B\", \"mesh\": 1 }]",
                StringComparison.Ordinal
            )
            .Replace("\"nodes\": [0]", "\"nodes\": [0, 1]", StringComparison.Ordinal)
            .Replace(
                "\"meshes\": [{\n    \"name\": \"Tri\",\n    \"primitives\": [{ \"attributes\": { \"POSITION\": 0 }, \"indices\": 1 }]\n  }]",
                "\"meshes\": [{ \"name\": \"Tri\", \"primitives\": [{ \"attributes\": { \"POSITION\": 0 }, \"indices\": 1 }] },"
                + """ { "name": "Tri", "primitives": [{ "attributes": { "POSITION": 0 }, "indices": 1 }] }]""",
                StringComparison.Ordinal
            );

        return Encoding.UTF8.GetBytes(text);
    }

    static string F(float value) => value.ToString("0.0###", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>A triangle in red, as a file with a colour attribute has it.</summary>
    const string ColouredTriangle = """
                                    {
                                      "asset": { "version": "2.0" },
                                      "scenes": [{ "nodes": [0] }],
                                      "nodes": [{ "mesh": 0 }],
                                      "meshes": [{ "primitives": [{ "attributes": { "POSITION": 0, "COLOR_0": 1 } }] }],
                                      "accessors": [
                                        { "bufferView": 0, "componentType": 5126, "count": 3, "type": "VEC3", "min": [0, 0, 0], "max": [1, 1, 0] },
                                        { "bufferView": 1, "componentType": 5126, "count": 3, "type": "VEC4" }
                                      ],
                                      "bufferViews": [
                                        { "buffer": 0, "byteLength": 36 },
                                        { "buffer": 0, "byteOffset": 36, "byteLength": 48 }
                                      ],
                                      "buffers": [{ "byteLength": 84, "uri": "data:application/octet-stream;base64,AAAAAAAAAAAAAAAAAACAPwAAAAAAAAAAAAAAAAAAgD8AAAAAAACAPwAAAAAAAAAAAACAPwAAgD8AAAAAAAAAAAAAgD8AAIA/AAAAAAAAAAAAAIA/" }]
                                    }
                                    """;

    [Fact]
    public void VertexColoursAreReadWhenAskedFor() {
        var read = ModelReader.Read(
            "{\n  \"asset\": { \"version\": \"2.0\" },\n  \"scenes\": [{ \"nodes\": [0] }],\n  \"nodes\": [{ \"mesh\": 0 }],\n  \"meshes\": [{ \"primitives\": [{ \"attributes\": { \"POSITION\": 0, \"COLOR_0\": 1 } }] }],\n  \"accessors\": [\n    { \"bufferView\": 0, \"componentType\": 5126, \"count\": 3, \"type\": \"VEC3\", \"min\": [0, 0, 0], \"max\": [1, 1, 0] },\n    { \"bufferView\": 1, \"componentType\": 5126, \"count\": 3, \"type\": \"VEC4\" }\n  ],\n  \"bufferViews\": [\n    { \"buffer\": 0, \"byteLength\": 36 },\n    { \"buffer\": 0, \"byteOffset\": 36, \"byteLength\": 48 }\n  ],\n  \"buffers\": [{ \"byteLength\": 84, \"uri\": \"data:application/octet-stream;base64,AAAAAAAAAAAAAAAAAACAPwAAAAAAAAAAAAAAAAAAgD8AAAAAAACAPwAAAAAAAAAAAACAPwAAgD8AAAAAAAAAAAAAgD8AAIA/AAAAAAAAAAAAAIA/\" }]\n}"u8,
            ".gltf",
            "coloured",
            new ModelReadOptions { ImportVertexColours = true }
        );

        Assert.Equal(new(1f, 0f, 0f, 1f), read.Meshes[0].Colours[0]);
    }

    [Fact]
    public void VertexColoursCostNothingUnlessAskedFor() {
        var read = ModelReader.Read(
            "{\n  \"asset\": { \"version\": \"2.0\" },\n  \"scenes\": [{ \"nodes\": [0] }],\n  \"nodes\": [{ \"mesh\": 0 }],\n  \"meshes\": [{ \"primitives\": [{ \"attributes\": { \"POSITION\": 0, \"COLOR_0\": 1 } }] }],\n  \"accessors\": [\n    { \"bufferView\": 0, \"componentType\": 5126, \"count\": 3, \"type\": \"VEC3\", \"min\": [0, 0, 0], \"max\": [1, 1, 0] },\n    { \"bufferView\": 1, \"componentType\": 5126, \"count\": 3, \"type\": \"VEC4\" }\n  ],\n  \"bufferViews\": [\n    { \"buffer\": 0, \"byteLength\": 36 },\n    { \"buffer\": 0, \"byteOffset\": 36, \"byteLength\": 48 }\n  ],\n  \"buffers\": [{ \"byteLength\": 84, \"uri\": \"data:application/octet-stream;base64,AAAAAAAAAAAAAAAAAACAPwAAAAAAAAAAAAAAAAAAgD8AAAAAAACAPwAAAAAAAAAAAACAPwAAgD8AAAAAAAAAAAAAgD8AAIA/AAAAAAAAAAAAAIA/\" }]\n}"u8,
            ".gltf",
            "coloured",
            new ModelReadOptions()
        );

        Assert.Empty(read.Meshes[0].Colours);
    }
}
