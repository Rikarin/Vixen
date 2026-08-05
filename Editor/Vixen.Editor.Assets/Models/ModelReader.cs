// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Silk.NET.Assimp;
using Vixen.Core.Mathematics;
using Vixen.Rendering;
using AssimpApi = Silk.NET.Assimp.Assimp;
using AssimpMesh = Silk.NET.Assimp.Mesh;
using AssimpNode = Silk.NET.Assimp.Node;
using AssimpScene = Silk.NET.Assimp.Scene;
using NumericsMatrix = System.Numerics.Matrix4x4;
using NumericsVector = System.Numerics.Vector3;

namespace Vixen.Editor.Assets.Models;

/// <summary>A file that says it is a model and is not readable as one.</summary>
/// <param name="message">What is wrong with it.</param>
public sealed class ModelFormatException(string message) : Exception(message);

/// <summary>Everything one model file turned into.</summary>
/// <param name="Model">The model itself: the hierarchy, and what hangs off it.</param>
/// <param name="Meshes">Its meshes, in the order its parts name them.</param>
/// <param name="Skeleton">The skeleton its skinned meshes share, or <see langword="null" />.</param>
/// <param name="Animations">Its clips.</param>
public sealed record ReadModel(
    ModelData Model,
    MeshData[] Meshes,
    SkeletonData? Skeleton,
    AnimationClipData[] Animations
);

/// <summary>Reads a model file through Assimp and converts what comes out into engine data.</summary>
/// <remarks>
///     <para>
///         Separate from <see cref="ModelImporter" /> so that the conversion — which is where all the
///         decisions and all the ways to be wrong are — can be tested against a file without an
///         import context, a virtual file system or a settings binder in the way.
///     </para>
///     <para>
///         <b>Every matrix is transposed on the way in, and that is not a stylistic detail.</b>
///         Assimp's <c>aiMatrix4x4</c> is row-major storage of a <em>column-vector</em> matrix, so a
///         node's translation sits in its fourth column. Vixen is row-major storage of a
///         <em>row-vector</em> matrix, where translation sits in the fourth row. Copying field for
///         field puts every offset into the wrong place, and the symptom is a hierarchy that is
///         subtly and consistently wrong rather than obviously broken — a model that assembles
///         itself inside out.
///     </para>
///     <para>
///         <b>No axis conversion.</b> Assimp's own convention is right-handed and Y-up, which is
///         <see cref="Vixen.Core.Mathematics.Vector3" />'s. A file authored Z-up therefore arrives
///         Z-up, and correcting it is a rotation on the root node that an artist can see rather than
///         a silent transform in a build step. <c>MakeLeftHanded</c> and <c>FlipWindingOrder</c> are
///         deliberately not among the post-processing flags.
///     </para>
/// </remarks>
public static class ModelReader {
    /// <summary>How many joints can influence one vertex.</summary>
    /// <remarks>
    ///     Four, which is what the hardware wants: one <c>uint8x4</c> of indices and one
    ///     <c>unorm8x4</c> of weights is a byte-aligned eight bytes a vertex. Eight influences is a
    ///     real thing for faces and cloth and doubles that cost for every vertex in the project;
    ///     doc 06 can raise it when something needs it, and the reader says when it dropped one.
    /// </remarks>
    public const int MaximumInfluences = 4;

    /// <summary>The Assimp instance, which owns a native library and outlives any one import.</summary>
    /// <remarks>
    ///     Static because loading it is the expensive part and it holds no per-file state; the C API
    ///     is re-entrant for separate imports. Never disposed, deliberately — it lives as long as the
    ///     tool does, and disposing it while another import is in flight is the one way to turn a
    ///     malformed mesh into a crash the pipeline cannot catch.
    /// </remarks>
    static readonly Lazy<AssimpApi> Api = new(AssimpApi.GetApi, isThreadSafe: true);

    /// <summary>Reads a model.</summary>
    /// <param name="bytes">The file.</param>
    /// <param name="extension">Its extension, with the leading dot, which tells Assimp what to expect.</param>
    /// <param name="name">What to call the model and to name its parts after.</param>
    /// <param name="settings">How to import it.</param>
    /// <param name="report">Where to say things worth knowing. May be <see langword="null" />.</param>
    /// <returns>The model and its parts.</returns>
    /// <exception cref="ModelFormatException">Assimp would not read it.</exception>
    public static unsafe ReadModel Read(
        ReadOnlySpan<byte> bytes,
        string extension,
        string name,
        ModelImportSettings settings,
        Action<ImportSeverity, string>? report = null
    ) {
        ArgumentNullException.ThrowIfNull(settings);

        if (bytes.Length == 0) {
            throw new ModelFormatException("It is empty.");
        }

        var assimp = Api.Value;
        var hint = extension.TrimStart('.').ToLowerInvariant();
        AssimpScene* scene;

        fixed (byte* data = bytes) {
            scene = assimp.ImportFileFromMemory(data, (uint)bytes.Length, Flags(settings), hint);
        }

        if (scene is null || scene->MRootNode is null) {
            var reason = assimp.GetErrorStringS();

            throw new ModelFormatException(
                hint == "blend"
                    // Doc 08's own footnote. Assimp's Blender reader handles a narrow range of
                    // versions and fails opaquely outside it, and the fix is a step the author can
                    // take rather than one this tool can.
                    ? $"Assimp could not read this .blend: {reason} Blender's own format changes with its "
                    + "versions and is only readable for some of them. Export it as .fbx or .gltf."
                    : $"Assimp could not read it: {reason}"
            );
        }

        try {
            return Convert(assimp, scene, name, settings, report);
        } finally {
            // Always, including on the way out of an exception. The scene is native memory Assimp
            // allocated; an import that threw halfway and leaked it would lose a few megabytes per
            // broken file, which on a project with a directory of them is the editor's whole heap.
            assimp.ReleaseImport(scene);
        }
    }

    static uint Flags(ModelImportSettings settings) {
        var flags = PostProcessSteps.Triangulate
            | PostProcessSteps.JoinIdenticalVertices
            | PostProcessSteps.ImproveCacheLocality
            | PostProcessSteps.SortByPrimitiveType
            | PostProcessSteps.FindInvalidData
            | PostProcessSteps.LimitBoneWeights
            | PostProcessSteps.FlipUVs;

        if (settings.GenerateNormals) {
            // Smooth rather than flat, and only where there are none: the flag is documented as a
            // no-op on a mesh that already has normals, which is what keeps an artist's hand-adjusted
            // shading from being thrown away.
            flags |= PostProcessSteps.GenerateSmoothNormals;
        }

        if (settings.GenerateTangents) {
            flags |= PostProcessSteps.CalculateTangentSpace;
        }

        return (uint)flags;
    }

    static unsafe ReadModel Convert(
        AssimpApi assimp,
        AssimpScene* scene,
        string name,
        ModelImportSettings settings,
        Action<ImportSeverity, string>? report
    ) {
        var nodes = new List<ModelNode>();
        var parts = new List<ModelPart>();
        var world = new List<Matrix4x4>();

        Flatten(scene->MRootNode, parent: -1, name, settings.Scale, nodes, world, parts, scene);

        var materials = new string[scene->MNumMaterials];

        for (var index = 0u; index < scene->MNumMaterials; index++) {
            AssimpString material = default;
            assimp.GetMaterialString(scene->MMaterials[index], AssimpApi.MaterialNameBase, 0, 0, ref material);
            materials[index] = material.AsString;
        }

        var joints = Joints(scene, settings.Scale);
        var meshes = new MeshData[scene->MNumMeshes];
        var taken = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0u; index < scene->MNumMeshes; index++) {
            meshes[index] = ReadMesh(scene->MMeshes[index], Unique(taken, Name(scene->MMeshes[index]->MName, $"{name}_{index}")), joints, settings, report);
        }

        // Each part names its mesh rather than indexing it, so the names have to be settled before
        // the parts are. Flatten recorded the index; this is where it becomes an address.
        for (var index = 0; index < parts.Count; index++) {
            parts[index] = parts[index] with { Mesh = meshes[int.Parse(parts[index].Mesh, System.Globalization.CultureInfo.InvariantCulture)].Name };
        }

        var skeleton = joints.Count == 0
            ? null
            : new SkeletonData { Name = $"{name}_Skeleton", Joints = [.. joints.Values.OrderBy(joint => joint.Order).Select(joint => joint.Joint)] };

        var animations = settings.ImportAnimations ? ReadAnimations(scene, report) : [];

        var model = new ModelData {
            Name = name,
            Nodes = [.. nodes],
            Parts = [.. parts],
            Materials = materials,
            Skeleton = skeleton?.Name ?? string.Empty,
            Animations = [.. animations.Select(clip => clip.Name)],
            Bounds = Bounds(meshes, parts, world)
        };

        return new(model, meshes, skeleton, animations);
    }

    /// <summary>Walks the node tree into a flat array, parents before children.</summary>
    /// <remarks>
    ///     Depth-first with an explicit parent index, so composing world transforms downstream is one
    ///     forward pass. The mesh column of each part holds the Assimp mesh <em>index</em> as text at
    ///     this point and is rewritten to the mesh's name once the names are settled — the names are
    ///     deduplicated, and a part cannot name a mesh before that has happened.
    /// </remarks>
    static unsafe void Flatten(
        AssimpNode* node,
        int parent,
        string modelName,
        float scale,
        List<ModelNode> nodes,
        List<Matrix4x4> world,
        List<ModelPart> parts,
        AssimpScene* scene
    ) {
        var local = Convert(node->MTransformation, scale);
        var index = nodes.Count;

        nodes.Add(
            new() {
                // Assimp names the root of a memory import `$$$___magic___$$$`, which is an artefact
                // of how it was handed the bytes rather than anything in the file. Nobody wants to
                // see that in an inspector.
                Name = parent < 0 && node->MName.AsString.StartsWith("$$$", StringComparison.Ordinal)
                    ? modelName
                    : Name(node->MName, $"Node{index}"),
                Parent = parent,
                Transform = local
            }
        );

        world.Add(parent < 0 ? local : local * world[parent]);

        for (var mesh = 0u; mesh < node->MNumMeshes; mesh++) {
            var meshIndex = node->MMeshes[mesh];

            parts.Add(
                new() {
                    Mesh = meshIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Node = index,
                    Material = (int)scene->MMeshes[meshIndex]->MMaterialIndex
                }
            );
        }

        for (var child = 0u; child < node->MNumChildren; child++) {
            Flatten(node->MChildren[child], index, modelName, scale, nodes, world, parts, scene);
        }
    }

    /// <summary>One joint, and where it sits in the skeleton's order.</summary>
    readonly record struct OrderedJoint(SkeletonJoint Joint, int Order, int Index);

    /// <summary>
    ///     The joints every skinned mesh in the file shares, in hierarchy order.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Collected across <em>all</em> meshes, because a character's body, coat and hat deform
    ///         by one skeleton and three copies of it would be three things to keep in step and three
    ///         chunks where the object database wants one.
    ///     </para>
    ///     <para>
    ///         Ordered by the node tree rather than by the order bones happen to appear in the first
    ///         mesh, so a joint always precedes its children and a pose can be composed in one
    ///         forward pass. A parent is the nearest ancestor node that is <em>also</em> a bone —
    ///         the intermediate nodes an exporter inserts are not joints and would leave holes in the
    ///         indices.
    ///     </para>
    /// </remarks>
    static unsafe Dictionary<string, OrderedJoint> Joints(AssimpScene* scene, float scale) {
        var bones = new Dictionary<string, NumericsMatrix>(StringComparer.Ordinal);

        for (var mesh = 0u; mesh < scene->MNumMeshes; mesh++) {
            for (var bone = 0u; bone < scene->MMeshes[mesh]->MNumBones; bone++) {
                var entry = scene->MMeshes[mesh]->MBones[bone];
                bones[entry->MName.AsString] = entry->MOffsetMatrix;
            }
        }

        var joints = new Dictionary<string, OrderedJoint>(StringComparer.Ordinal);

        if (bones.Count == 0) {
            return joints;
        }

        Visit(scene->MRootNode, -1);
        return joints;

        void Visit(AssimpNode* node, int parent) {
            var own = parent;
            var nodeName = node->MName.AsString;

            if (bones.TryGetValue(nodeName, out var offset)) {
                own = joints.Count;

                joints[nodeName] = new(
                    new SkeletonJoint {
                        Name = nodeName,
                        Parent = parent,
                        InverseBindPose = Convert(offset, scale)
                    },
                    own,
                    own
                );
            }

            for (var child = 0u; child < node->MNumChildren; child++) {
                Visit(node->MChildren[child], own);
            }
        }
    }

    static unsafe MeshData ReadMesh(
        AssimpMesh* mesh,
        string name,
        Dictionary<string, OrderedJoint> joints,
        ModelImportSettings settings,
        Action<ImportSeverity, string>? report
    ) {
        var count = (int)mesh->MNumVertices;
        var positions = new Vector3[count];
        var minimum = new Vector3(float.PositiveInfinity);
        var maximum = new Vector3(float.NegativeInfinity);

        for (var index = 0; index < count; index++) {
            var position = Convert(mesh->MVertices[index]) * settings.Scale;
            positions[index] = position;
            minimum = Vector3.Min(minimum, position);
            maximum = Vector3.Max(maximum, position);
        }

        var normals = mesh->MNormals is null ? [] : new Vector3[count];

        for (var index = 0; index < normals.Length; index++) {
            normals[index] = Convert(mesh->MNormals[index]);
        }

        var uvs = mesh->MTextureCoords[0] is null ? [] : new Vector2[count];

        for (var index = 0; index < uvs.Length; index++) {
            var coordinate = mesh->MTextureCoords[0][index];
            uvs[index] = new(coordinate.X, coordinate.Y);
        }

        // ⚠ Assimp's second coordinate set and its colour channel are deliberately not read.
        // MeshData carried both for a while and nothing ever drew either — SurfaceGeometry.Pack
        // dropped them on the way to the only vertex layout the engine has — and its own remarks
        // record why they come back with the change that draws them rather than ahead of it. Reading
        // them here is ten lines; carrying them is bytes in every mesh chunk and a signature that
        // reads as support.

        // Tangents need the bitangent to recover the handedness, and Assimp produces both together
        // or neither. The sign is cross(normal, tangent) · bitangent, which is ±1 for an orthonormal
        // frame and is the one bit a shader cannot work out for itself.
        var tangents = mesh->MTangents is null || mesh->MBitangents is null || mesh->MNormals is null
            ? []
            : new Vector4[count];

        for (var index = 0; index < tangents.Length; index++) {
            var normal = Convert(mesh->MNormals[index]);
            var tangent = Convert(mesh->MTangents[index]);
            var bitangent = Convert(mesh->MBitangents[index]);
            var sign = Vector3.Dot(Vector3.Cross(normal, tangent), bitangent) < 0f ? -1f : 1f;

            tangents[index] = new(tangent.X, tangent.Y, tangent.Z, sign);
        }

        var indices = new List<int>((int)mesh->MNumFaces * 3);

        for (var face = 0u; face < mesh->MNumFaces; face++) {
            // Triangulate ran, so every face should have three. A degenerate face that survived
            // FindInvalidData is dropped rather than emitted as a line, because a draw call with two
            // indices is not a thing.
            if (mesh->MFaces[face].MNumIndices != 3) {
                continue;
            }

            for (var corner = 0u; corner < 3; corner++) {
                indices.Add((int)mesh->MFaces[face].MIndices[corner]);
            }
        }

        var (boneIndices, boneWeights) = ReadSkin(mesh, count, joints, name, report);

        return new() {
            Name = name,
            Positions = positions,
            Normals = normals,
            Tangents = tangents,
            TexCoords = uvs,
            Indices = [.. indices],
            BoneIndices = boneIndices,
            BoneWeights = boneWeights,
            MaterialIndex = (int)mesh->MMaterialIndex,
            Bounds = count == 0 ? default : new BoundingBox(minimum, maximum)
        };
    }

    /// <summary>The four strongest influences on each vertex, normalised.</summary>
    /// <remarks>
    ///     <para>
    ///         Assimp's <c>LimitBoneWeights</c> has already capped this, so the sort is a guard
    ///         rather than the mechanism; what is done here is the <b>renormalisation</b>, which
    ///         matters because dropping a fifth influence leaves the remaining weights summing to
    ///         less than one and a vertex whose weights sum to 0.9 is drawn ten per cent of the way
    ///         towards the model's origin.
    ///     </para>
    ///     <para>
    ///         A bone whose name is not a joint is skipped and reported. That happens when an
    ///         exporter writes a weight against a node the skeleton walk did not reach, and silently
    ///         indexing joint 0 instead would attach part of a mesh to the root.
    ///     </para>
    /// </remarks>
    static unsafe (int[] Indices, float[] Weights) ReadSkin(
        AssimpMesh* mesh,
        int vertexCount,
        Dictionary<string, OrderedJoint> joints,
        string meshName,
        Action<ImportSeverity, string>? report
    ) {
        if (mesh->MNumBones == 0 || vertexCount == 0) {
            return ([], []);
        }

        var influences = new List<(int Joint, float Weight)>[vertexCount];
        var missing = 0;

        for (var bone = 0u; bone < mesh->MNumBones; bone++) {
            var entry = mesh->MBones[bone];

            if (!joints.TryGetValue(entry->MName.AsString, out var joint)) {
                missing++;
                continue;
            }

            for (var weight = 0u; weight < entry->MNumWeights; weight++) {
                var influence = entry->MWeights[weight];

                if (influence.MVertexId >= vertexCount || influence.MWeight <= 0f) {
                    continue;
                }

                (influences[influence.MVertexId] ??= []).Add((joint.Index, influence.MWeight));
            }
        }

        if (missing > 0) {
            report?.Invoke(
                ImportSeverity.Warning,
                $"{meshName} is weighted to {missing} bone(s) that are not in the node hierarchy, and those "
                + "weights are dropped. An exporter that writes them usually also writes the joints; check "
                + "whether the skeleton was exported with the mesh."
            );
        }

        var indices = new int[vertexCount * MaximumInfluences];
        var weights = new float[vertexCount * MaximumInfluences];
        var dropped = 0;

        for (var vertex = 0; vertex < vertexCount; vertex++) {
            if (influences[vertex] is not { Count: > 0 } list) {
                continue;
            }

            if (list.Count > MaximumInfluences) {
                dropped++;
                list.Sort(static (left, right) => right.Weight.CompareTo(left.Weight));
            }

            var total = 0f;
            var kept = Math.Min(list.Count, MaximumInfluences);

            for (var slot = 0; slot < kept; slot++) {
                total += list[slot].Weight;
            }

            for (var slot = 0; slot < kept; slot++) {
                indices[(vertex * MaximumInfluences) + slot] = list[slot].Joint;
                weights[(vertex * MaximumInfluences) + slot] = total > 0f ? list[slot].Weight / total : 0f;
            }
        }

        if (dropped > 0) {
            report?.Invoke(
                ImportSeverity.Information,
                $"{meshName} has {dropped} vertex(es) with more than {MaximumInfluences} influences. The weakest "
                + "are dropped and the rest renormalised, which is what keeps the mesh from shrinking towards "
                + "the origin."
            );
        }

        return (indices, weights);
    }

    static unsafe AnimationClipData[] ReadAnimations(AssimpScene* scene, Action<ImportSeverity, string>? report) {
        var clips = new AnimationClipData[scene->MNumAnimations];
        var taken = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0u; index < scene->MNumAnimations; index++) {
            var animation = scene->MAnimations[index];

            // A file that leaves the rate at zero is common enough that treating it as "the duration
            // is already in seconds" is the only reading that does not produce a clip of infinite
            // length. Assimp's own documentation calls the field "may be 0".
            var rate = animation->MTicksPerSecond > 0 ? animation->MTicksPerSecond : 1;
            var channels = new AnimationChannel[animation->MNumChannels];

            for (var channel = 0u; channel < animation->MNumChannels; channel++) {
                var track = animation->MChannels[channel];
                var positions = new Vector3[track->MNumPositionKeys];
                var positionTimes = new float[track->MNumPositionKeys];

                for (var key = 0u; key < track->MNumPositionKeys; key++) {
                    positionTimes[key] = (float)(track->MPositionKeys[key].MTime / rate);
                    positions[key] = Convert(track->MPositionKeys[key].MValue);
                }

                var rotations = new Quaternion[track->MNumRotationKeys];
                var rotationTimes = new float[track->MNumRotationKeys];

                for (var key = 0u; key < track->MNumRotationKeys; key++) {
                    var value = track->MRotationKeys[key].MValue;
                    rotationTimes[key] = (float)(track->MRotationKeys[key].MTime / rate);
                    rotations[key] = new(value.X, value.Y, value.Z, value.W);
                }

                var scales = new Vector3[track->MNumScalingKeys];
                var scaleTimes = new float[track->MNumScalingKeys];

                for (var key = 0u; key < track->MNumScalingKeys; key++) {
                    scaleTimes[key] = (float)(track->MScalingKeys[key].MTime / rate);
                    scales[key] = Convert(track->MScalingKeys[key].MValue);
                }

                channels[channel] = new() {
                    Target = track->MNodeName.AsString,
                    PositionTimes = positionTimes,
                    Positions = positions,
                    RotationTimes = rotationTimes,
                    Rotations = rotations,
                    ScaleTimes = scaleTimes,
                    Scales = scales
                };
            }

            clips[index] = new() {
                Name = Unique(taken, Name(animation->MName, $"Clip{index}")),
                Duration = (float)(animation->MDuration / rate),
                Channels = channels
            };
        }

        if (clips.Length > 0) {
            report?.Invoke(ImportSeverity.Information, $"{clips.Length} animation clip(s).");
        }

        return clips;
    }

    /// <summary>Everything the model occupies, in its own space.</summary>
    /// <remarks>
    ///     The union of each part's bounds put through its node's world transform, rather than of the
    ///     meshes as they sit. A mesh reused at four nodes occupies four places, and bounds that
    ///     ignored the hierarchy would cull three of them away.
    /// </remarks>
    static BoundingBox Bounds(MeshData[] meshes, List<ModelPart> parts, List<Matrix4x4> world) {
        var minimum = new Vector3(float.PositiveInfinity);
        var maximum = new Vector3(float.NegativeInfinity);
        var any = false;

        foreach (var part in parts) {
            var mesh = meshes.FirstOrDefault(candidate => candidate.Name == part.Mesh);

            if (mesh is null || mesh.VertexCount == 0) {
                continue;
            }

            var transform = world[part.Node];

            foreach (var corner in Corners(mesh.Bounds)) {
                var placed = Matrix4x4.TransformPosition(corner, transform);
                minimum = Vector3.Min(minimum, placed);
                maximum = Vector3.Max(maximum, placed);
                any = true;
            }
        }

        return any ? new BoundingBox(minimum, maximum) : default;
    }

    static IEnumerable<Vector3> Corners(BoundingBox box) {
        for (var index = 0; index < 8; index++) {
            yield return new(
                (index & 1) == 0 ? box.Minimum.X : box.Maximum.X,
                (index & 2) == 0 ? box.Minimum.Y : box.Maximum.Y,
                (index & 4) == 0 ? box.Minimum.Z : box.Maximum.Z
            );
        }
    }

    static Vector3 Convert(NumericsVector value) => new(value.X, value.Y, value.Z);

    /// <summary>
    ///     Assimp's matrix in Vixen's convention, with the translation scaled.
    /// </summary>
    /// <remarks>
    ///     <b>Transposed.</b> Assimp stores a column-vector matrix row-major, so translation is in
    ///     the fourth column; Vixen stores a row-vector matrix row-major, so translation is in the
    ///     fourth row. A field-for-field copy puts every offset in the wrong place and produces a
    ///     hierarchy that is consistently, quietly wrong.
    /// </remarks>
    static Matrix4x4 Convert(NumericsMatrix value, float scale) =>
        new(
            value.M11, value.M21, value.M31, value.M41,
            value.M12, value.M22, value.M32, value.M42,
            value.M13, value.M23, value.M33, value.M43,
            value.M14 * scale, value.M24 * scale, value.M34 * scale, value.M44
        );

    static string Name(AssimpString value, string fallback) =>
        value.AsString is { Length: > 0 } text ? text : fallback;

    /// <summary>
    ///     A name nothing else has taken.
    /// </summary>
    /// <remarks>
    ///     Sub-asset ids are derived from names, so two meshes called <c>Cube</c> would derive one id
    ///     and the import would be refused outright by the collision check. An exporter naming every
    ///     mesh after the same source object is ordinary, so this is the common case rather than the
    ///     pathological one.
    /// </remarks>
    static string Unique(HashSet<string> taken, string name) {
        if (taken.Add(name)) {
            return name;
        }

        for (var suffix = 1; ; suffix++) {
            var candidate = $"{name}_{suffix}";

            if (taken.Add(candidate)) {
                return candidate;
            }
        }
    }
}
