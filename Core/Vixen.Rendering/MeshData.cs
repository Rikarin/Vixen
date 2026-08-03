// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;

namespace Vixen.Rendering;

/// <summary>The geometry of one mesh, as it came out of an authoring tool.</summary>
/// <remarks>
///     <para>
///         The CPU side of <see cref="MeshDraw" />. That struct holds buffer handles and a range,
///         because it is read once per object in a frame; this holds arrays, because it is read once
///         per build by something deciding what those buffers should contain.
///     </para>
///     <para>
///         <b>Parallel arrays, and typed ones.</b> A vertex is not a struct here, because the
///         <em>layout</em> is a compile-time decision — which attributes are present, in what order,
///         at what precision, interleaved or not — and doc 08 puts that in <c>ModelCompiler</c>
///         where the whole model is in hand. Separate arrays let a compiler drop an attribute
///         nothing samples without rewriting the ones it keeps.
///     </para>
///     <para>
///         An attribute the file did not have is an <b>empty array</b> rather than a null or a
///         defaulted one. Empty says "this mesh has no tangents"; an array of zeros says "this mesh
///         has tangents and they are all degenerate", and a compiler cannot tell the second from a
///         bug in the first.
///     </para>
/// </remarks>
[DataContract("MeshData")]
public sealed record MeshData {
    /// <summary>What the mesh is called. Its sub-asset name, and part of its address.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>One position per vertex.</summary>
    public Vector3[] Positions { get; set; } = [];

    /// <summary>One normal per vertex, or empty.</summary>
    public Vector3[] Normals { get; set; } = [];

    /// <summary>
    ///     One tangent per vertex, or empty. <c>W</c> is the bitangent's sign.
    /// </summary>
    /// <remarks>
    ///     The sign rather than the bitangent itself: it is ±1, it is the only part not recoverable
    ///     from the normal and the tangent, and storing the third vector costs twelve bytes a vertex
    ///     to say what one bit says.
    /// </remarks>
    public Vector4[] Tangents { get; set; } = [];

    /// <summary>One texture coordinate per vertex, or empty.</summary>
    public Vector2[] TexCoords { get; set; } = [];

    /// <summary>A second texture coordinate per vertex, or empty.</summary>
    /// <remarks>
    ///     <para>
    ///         What a lightmap, a detail map or a trim sheet is addressed by while the first set goes
    ///         on doing the material. An exporter writes it as UV1 and every DCC has a channel for it;
    ///         dropping it on import means an artist's second unwrap silently does not arrive, which
    ///         is not something that shows up as an error anywhere downstream.
    ///     </para>
    ///     <para>
    ///         Only a second, not an array of N. Two is what the reference engines expose by default
    ///         and what the formats carry in practice; an array would make the vertex layout's
    ///         attribute count dynamic, which is a decision <c>ModelCompiler</c> would then have to
    ///         make per mesh rather than once.
    ///     </para>
    /// </remarks>
    public Vector2[] TexCoords1 { get; set; } = [];

    /// <summary>One colour per vertex, or empty. Linear, straight alpha.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Rarely a colour.</b> It is four interpolated channels an artist paints per vertex,
    ///         and what they mean is the material's business: ambient-occlusion bake, blend mask
    ///         between two layers, wear, and — the case this landed for — <b>wind weights on
    ///         foliage</b>, where the convention every vegetation tool writes is stiffness in one
    ///         channel and phase offset in another. See [docs/plan/31 § B4].
    ///     </para>
    ///     <para>
    ///         <see cref="Vector4" /> rather than a packed <c>uint</c>, for the reason the whole type
    ///         is parallel typed arrays: this is the authored form, and which of the four channels
    ///         survive at what precision is <c>ModelCompiler</c>'s decision with the whole model in
    ///         hand.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Carried, not yet drawn.</b> <see cref="SurfaceVertex" /> has no colour attribute,
    ///         so a mesh with this still renders through the four-attribute layout and the channel
    ///         goes as far as the compiler. Widening the shared vertex format costs sixteen bytes on
    ///         every mesh in the engine to serve the meshes that use it, so the consumer is a foliage
    ///         vertex layout of its own — [docs/plan/31 § T5] — and this is the half that has to exist
    ///         first, because an importer that discarded the data would leave nothing to consume.
    ///     </para>
    /// </remarks>
    public Vector4[] Colors { get; set; } = [];

    /// <summary>Three indices per triangle.</summary>
    public int[] Indices { get; set; } = [];

    /// <summary>Four joint indices per vertex, or empty if the mesh is not skinned.</summary>
    public int[] BoneIndices { get; set; } = [];

    /// <summary>Four weights per vertex, summing to one, or empty.</summary>
    public float[] BoneWeights { get; set; } = [];

    /// <summary>Which of the model's materials this is drawn with.</summary>
    public int MaterialIndex { get; set; }

    /// <summary>Everything the mesh occupies, in the model's space.</summary>
    public BoundingBox Bounds { get; set; }

    /// <summary>This mesh's cluster hierarchy and page records, or null if it has none.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Written down rather than derived, because a runtime cannot derive it.</b> A sub-asset's
    ///         id comes from <c>SubAssets.Derive(importer, kind, name)</c> — the importer's name, the kind
    ///         and the mesh's name — and a frame holding a mesh reference knows none of the three. So the
    ///         link is a field, and the mesh that has clusters is the one thing that can say where they
    ///         are.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Null is the ordinary case and not a failure.</b> A mesh imported with
    ///         <c>GenerateMeshlets</c> off has none, and so does a mesh the cluster builder refused —
    ///         both draw through the vertex-buffer path, which is what this <see cref="MeshData" /> is
    ///         for. It is also what a virtualized mesh falls back to everywhere the virtualized path does
    ///         not reach: WebGL2, the physics cook, a ray query.
    ///     </para>
    /// </remarks>
    public AssetReference Clusters { get; set; }

    /// <summary>Where this mesh's page blob is, or null if it has no clusters.</summary>
    /// <remarks>
    ///     A second reference and not a second field of the first chunk, because the two are read in
    ///     opposite ways: the records are deserialised in full at load and stay resident, and the blob is
    ///     seeked into one page at a time by whatever is looking at the mesh. One chunk carrying both
    ///     would be a chunk whose deserialisation reads every page of every mesh in the level, which is
    ///     the one thing paging exists to avoid.
    /// </remarks>
    public AssetReference ClusterPages { get; set; }

    /// <summary>Whether it was built with a cluster hierarchy.</summary>
    public bool IsClustered => !Clusters.IsNull && !ClusterPages.IsNull;

    /// <summary>How many vertices it has.</summary>
    public int VertexCount => Positions.Length;

    /// <summary>How many triangles it has.</summary>
    public int TriangleCount => Indices.Length / 3;

    /// <summary>Whether it carries skinning weights.</summary>
    public bool IsSkinned => BoneWeights.Length > 0;

    /// <summary>Whether it carries per-vertex colours.</summary>
    public bool HasColors => Colors.Length > 0;

    /// <summary>Whether it carries a second texture coordinate set.</summary>
    public bool HasTexCoords1 => TexCoords1.Length > 0;
}

/// <summary>One joint of a skeleton.</summary>
[DataContract("SkeletonJoint")]
public sealed record SkeletonJoint {
    /// <summary>What the joint is called. Animation channels name it.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Its parent's index, or −1 for the root.</summary>
    public int Parent { get; set; } = -1;

    /// <summary>
    ///     The transform from model space into this joint's space at bind time.
    /// </summary>
    /// <remarks>
    ///     Stored inverted, because that is the direction skinning uses: a vertex is taken out of
    ///     model space, into joint space, and back out through the joint's animated transform.
    ///     Inverting it per joint per frame would be a matrix inverse in the hot path to recover a
    ///     value that never changes.
    /// </remarks>
    public Matrix4x4 InverseBindPose { get; set; } = Matrix4x4.Identity;

    /// <summary>Whether this joint has a range of motion, or turns freely.</summary>
    /// <remarks>
    ///     ⚠ <b>A flag, rather than reading a zero limit as "no limit".</b> Zero swing and zero twist
    ///     is a joint welded to its bind pose — a legitimate thing to author, and also exactly what
    ///     every rig exported before this field existed deserialises to. Without the flag, adding the
    ///     three below would freeze every character in every project on the day the format changed.
    /// </remarks>
    public bool Limited { get; set; }

    /// <summary>How far the bone may lean from its bind direction, in degrees.</summary>
    public float Swing { get; set; }

    /// <summary>How far it may turn about its own length, in degrees, either way.</summary>
    public float Twist { get; set; }

    /// <summary>Which way the bone points in the joint's own space. Zero means <c>+Y</c>.</summary>
    public Vector3 TwistAxis { get; set; }
}

/// <summary>The joints a skinned mesh is deformed by.</summary>
/// <remarks>
///     Its own sub-asset rather than a field on each mesh, because a character's meshes share one
///     skeleton — a body, a coat and a hat all deform by the same joints, and three copies of it
///     would be three things to keep in step and three chunks where the object database wants one.
/// </remarks>
[DataContract("Skeleton")]
public sealed record SkeletonData {
    /// <summary>What the skeleton is called.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The joints, parents before children.</summary>
    public SkeletonJoint[] Joints { get; set; } = [];
}

/// <summary>What one animation does to one node, as three independent tracks.</summary>
/// <remarks>
///     <para>
///         Position, rotation and scale keep their own time arrays, because an exporter emits keys
///         only where a track actually changes. A character that translates for two seconds while
///         rotating on every frame would otherwise carry two hundred position keys that all say the
///         same thing.
///     </para>
///     <para>
///         Rotation is a quaternion and is deliberately not decomposed into Euler angles: a track
///         sampled between two Euler triples takes a different path than one sampled between the
///         rotations they represent, and the difference is the gimbal artefact everybody has seen.
///     </para>
/// </remarks>
[DataContract("AnimationChannel")]
public sealed record AnimationChannel {
    /// <summary>Which node or joint it drives, by name.</summary>
    public string Target { get; set; } = string.Empty;

    /// <summary>When each position key is, in seconds.</summary>
    public float[] PositionTimes { get; set; } = [];

    /// <summary>The position keys.</summary>
    public Vector3[] Positions { get; set; } = [];

    /// <summary>When each rotation key is, in seconds.</summary>
    public float[] RotationTimes { get; set; } = [];

    /// <summary>The rotation keys.</summary>
    public Quaternion[] Rotations { get; set; } = [];

    /// <summary>When each scale key is, in seconds.</summary>
    public float[] ScaleTimes { get; set; } = [];

    /// <summary>The scale keys.</summary>
    public Vector3[] Scales { get; set; } = [];
}

/// <summary>One animation: how long it is and what it moves.</summary>
[DataContract("AnimationClip")]
public sealed record AnimationClipData {
    /// <summary>What the clip is called. Its sub-asset name, and part of its address.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>How long it plays for, in seconds.</summary>
    /// <remarks>
    ///     In seconds, converted on import. A file stores its duration in ticks against a ticks-per-
    ///     second it also stores, and a clip that carried both would make every consumer redo that
    ///     division — and get it wrong for the exporters that leave the rate at zero.
    /// </remarks>
    public float Duration { get; set; }

    /// <summary>What it moves.</summary>
    public AnimationChannel[] Channels { get; set; } = [];
}
