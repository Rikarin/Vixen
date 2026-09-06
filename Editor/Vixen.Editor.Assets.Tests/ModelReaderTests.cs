// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Vixen.Core.Mathematics;
using Vixen.Editor.Assets.Models;
using Vixen.Rendering;
using Xunit;

namespace Vixen.Editor.Assets.Tests;

/// <summary>
///     Against real files, read by the real Assimp. The fixtures are OBJ and glTF because both are
///     text and can be written in the test that needs them — a binary fixture checked in beside the
///     tests is a thing nobody can edit and nobody can read the diff of.
/// </summary>
public sealed class ModelReaderTests {
    static readonly ModelImportSettings Default = new();

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

        Assert.Equal(100f, read.Meshes[0].Positions.Max(position => position.X), 3);
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
        Assert.Equal(2, read.Meshes.Select(mesh => mesh.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(2, read.Model.Parts.Select(part => part.Mesh).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void SomethingThatIsNotAModelSaysSoRatherThanReturningAnEmptyOne() {
        var failure = Assert.Throws<ModelFormatException>(
            () => ModelReader.Read("{ this is not glTF"u8, ".gltf", "Shape", Default)
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
        Assert.Throws<ModelFormatException>(() => ModelReader.Read([], ".obj", "Shape", Default));
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
        Assert.All(mesh.MorphTargets, target => Assert.Equal(1, target.Count));
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
        var weighted = clip.Channels.Where(channel => channel.WeightTimes.Length > 0).ToArray();

        Assert.Equal(2, weighted.Length);
        Assert.Equal(["jawOpen", "browRaise"], weighted.Select(channel => channel.Shape));

        // Key-major in the file — two keys of two targets — and one curve per shape out of it.
        Assert.Equal<float>([0f, 1f], weighted[0].WeightTimes);
        Assert.Equal<float>([0f, 1f], weighted[0].Weights);
        Assert.Equal<float>([0f, 1f], weighted[1].WeightTimes);
        Assert.Equal<float>([0f, 0.5f], weighted[1].Weights);
    }

    /// <summary>
    ///     The names the curves carry are the names the deltas carry, and one table makes both.
    /// </summary>
    [Fact]
    public void AWeightChannelNamesTheShapeTheMeshStored() {
        var read = ModelReader.Read(GltfMorphAnimated(), ".gltf", "Face", Default);

        var shapes = read.Meshes[0].MorphTargets.Select(target => target.Name).ToArray();
        var driven = read.Animations[0].Channels
            .Where(channel => channel.WeightTimes.Length > 0)
            .Select(channel => channel.Shape);

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

        Assert.Contains(read.Animations[0].Channels, channel => channel.WeightTimes.Length > 0);

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

        var buffer = new byte[(positions.Length * 4) + (indices.Length * 2)];
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

        var buffer = new byte[(positions.Length * 4) + (indices.Length * 2) + (jaw.Length * 4) + (brow.Length * 4)];
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
            (positions.Length * 4) + (indices.Length * 2) + (jaw.Length * 4) + (brow.Length * 4)
            + (times.Length * 4) + (outputs.Length * 4)
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
                + " { \"name\": \"Tri\", \"primitives\": [{ \"attributes\": { \"POSITION\": 0 }, \"indices\": 1 }] }]",
                StringComparison.Ordinal
            );

        return Encoding.UTF8.GetBytes(text);
    }

    static string F(float value) => value.ToString("0.0###", System.Globalization.CultureInfo.InvariantCulture);
}
