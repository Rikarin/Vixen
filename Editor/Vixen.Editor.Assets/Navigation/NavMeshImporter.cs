// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core;
using Vixen.Core.IO;
using Vixen.Core.Mathematics;
using Vixen.Core.Serialization;
using Vixen.Core.Yaml;
using Vixen.Editor.Assets.Models;
using Vixen.Navigation;
using Vixen.Navigation.Baking;
using Vixen.Rendering;

namespace Vixen.Editor.Assets.Navigation;

/// <summary>Bakes a navmesh from the geometry a <c>.vxnavmesh</c> names.</summary>
/// <remarks>
///     <para>
///         This is what makes baking a build step rather than something a game does at start-up. The
///         asset is two files, as everything else in the pipeline is: a <c>.vxnavmesh</c> saying
///         <i>what</i> to bake, and its <c>.meta</c> saying <i>how</i>. What comes out is a
///         <see cref="NavMeshAsset" /> — inert arrays a player loads and hands to
///         <see cref="NavMeshAsset.ToNavMesh" />.
///     </para>
///     <para>
///         <b>The geometry is a declared dependency, so re-exporting it rebakes the navmesh.</b> That
///         is the entire reason this is an importer rather than a menu item: a navmesh that silently
///         describes the level as it was last week is worse than no navmesh, because nothing about it
///         looks wrong until an agent walks into a wall that was added on Tuesday.
///     </para>
///     <para>
///         <b>The collision geometry is its own file, deliberately.</b> Baking from the visual mesh
///         means voxelising every leaf, every railing and every decorative moulding, which is slower
///         and worse: navigation wants the shape a body collides with. The file named here is
///         whatever the project uses for that, read through the same <see cref="ModelReader" /> the
///         model importer uses.
///     </para>
///     <para>
///         The document is small enough to read by hand rather than through the YAML object binder:
///         one path, and a list of area boxes. Doing it by hand is also what lets each mistake be
///         reported against the line that made it, rather than as a binder exception naming a type.
///     </para>
/// </remarks>
/// <example>
///     <code>
///     geometry: level_collision.obj
///     areas:
///       - area: 9
///         min: [8, -1, 0]
///         max: [12, 3, 20]
///     links:
///       - start: [9, 0, 5]
///         end: [17, 0, 5]
///         radius: 2
///         userId: 42
///     </code>
/// </example>
[Importer(".vxnavmesh")]
public sealed class NavMeshImporter : AssetImporter<NavMeshImportSettings> {
    /// <inheritdoc />
    public override int Version => 1;

    /// <inheritdoc />
    protected override async ValueTask<ImportResult> ImportAsync(
        ImportContext context,
        NavMeshImportSettings settings,
        CancellationToken cancellationToken
    ) {
        string text;

        await using (var source = await context.OpenSourceAsync(cancellationToken).ConfigureAwait(false)) {
            using var reader = new StreamReader(source);
            text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }

        YamlNode document;

        try {
            document = YamlReader.Read(text);
        } catch (YamlParseException failure) {
            context.Report(ImportSeverity.Error, $"It is not valid YAML: {failure.Message}");

            return context.Finish();
        }

        if (document is not YamlMapping root) {
            context.Report(ImportSeverity.Error, "Its root is not a mapping. A navmesh asset is a mapping with a `geometry` field.");

            return context.Finish();
        }

        if (root["geometry"] is not YamlScalar { Value.Length: > 0 } geometry) {
            context.Report(ImportSeverity.Error, "It has no `geometry` field naming the collision mesh to bake.");

            return context.Finish();
        }

        var path = Resolve(context.SourcePath, geometry.Value);

        // Declared before it is opened, which is both the rule and the point: this is what makes the
        // navmesh re-bake when the collision mesh is re-exported.
        context.DependsOnFile(path);

        if (!context.Files.Exists(path)) {
            context.Report(ImportSeverity.Error, $"The geometry it names is not there: {path}.");

            return context.Finish();
        }

        byte[] bytes;

        await using (var stream = await context.Files.OpenReadAsync(path, cancellationToken).ConfigureAwait(false)) {
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            bytes = buffer.ToArray();
        }

        ReadModel read;

        try {
            read = ModelReader.Read(bytes, Path.GetExtension(path.ToString()), "Collision", new ModelImportSettings());
        } catch (ModelFormatException failure) {
            context.Report(ImportSeverity.Error, $"The geometry it names could not be read: {failure.Message}");

            return context.Finish();
        }

        var (vertices, indices) = Combine(read);

        if (indices.Length == 0) {
            context.Report(ImportSeverity.Error, $"The geometry it names has no triangles in it: {path}.");

            return context.Finish();
        }

        if (!TryReadAreas(root, context, out var volumes) || !TryReadLinks(root, context, out var links)) {
            return context.Finish();
        }

        var build = settings.ToBuildSettings();

        try {
            build.Validate();
        } catch (ArgumentException failure) {
            context.Report(ImportSeverity.Error, $"Its settings cannot produce a mesh: {failure.Message}");

            return context.Finish();
        }

        var bounds = NavMeshBaker.Volume(vertices, build);

        var asset = settings.TileSize > 0
            ? NavMeshAsset.FromBake(NavMeshBaker.BakeTiles(vertices, indices, bounds, build, settings.TileSize, volumes, links))
            : Single(NavMeshBaker.Bake(vertices, indices, bounds, build, volumes, links));

        if (asset.Tiles.Length == 0) {
            // Not an error: an author who has just set the agent radius too wide for a corridor wants
            // to be told, and a level whose collision is genuinely all walls is a level with no
            // navmesh rather than a broken build.
            context.Report(
                ImportSeverity.Warning,
                "Nothing in the geometry is walkable for an agent this size, so the navmesh is empty."
            );
        } else {
            context.Report(
                ImportSeverity.Information,
                $"Baked {asset.PolyCount} polygons across {asset.Tiles.Length} tile(s) from {vertices.Length} vertices."
            );
        }

        context.Write(SubAssetId.Main, "NavMesh", Serializer.ToBytes(asset));

        return context.Finish();
    }

    static NavMeshAsset Single(NavMeshTileData? tile) => tile is null ? new() : NavMeshAsset.FromTile(tile);

    /// <summary>The geometry path, which is relative to the asset that names it.</summary>
    static VirtualPath Resolve(VirtualPath source, string relative) {
        if (relative.StartsWith('/')) {
            return new(relative);
        }

        var directory = source.ToString();
        var slash = directory.LastIndexOf('/');

        return new(slash < 0 ? relative : string.Concat(directory.AsSpan(0, slash + 1), relative));
    }

    /// <summary>Every placed mesh in the file, concatenated into one triangle soup in world space.</summary>
    /// <remarks>
    ///     <para>
    ///         One soup rather than one bake per mesh, because a navmesh is about the level rather
    ///         than about the objects in it: a floor and the crate standing on it have to be voxelised
    ///         together or the crate is not an obstacle.
    ///     </para>
    ///     <para>
    ///         <b>Through the hierarchy, not as the meshes sit.</b> A mesh's positions are relative to
    ///         the node it hangs off, so concatenating them raw stacks the whole level at the origin —
    ///         which an OBJ hides, because Assimp collapses its single node onto the root, and which a
    ///         glTF or an FBX does not. Instanced meshes contribute once per part, which is right: two
    ///         crates are two obstacles.
    ///     </para>
    /// </remarks>
    static (Vector3[] Vertices, int[] Indices) Combine(ReadModel read) {
        var vertices = new List<Vector3>();
        var indices = new List<int>();

        var meshes = new Dictionary<string, MeshData>(StringComparer.Ordinal);

        foreach (var mesh in read.Meshes) {
            meshes[mesh.Name] = mesh;
        }

        var world = WorldTransforms(read.Model);

        foreach (var part in read.Model.Parts) {
            if (!meshes.TryGetValue(part.Mesh, out var mesh) || (uint)part.Node >= (uint)world.Length) {
                continue;
            }

            var transform = world[part.Node];
            var offset = vertices.Count;

            foreach (var position in mesh.Positions) {
                vertices.Add(Matrix4x4.TransformPosition(position, in transform));
            }

            foreach (var index in mesh.Indices) {
                indices.Add(offset + index);
            }
        }

        return ([.. vertices], [.. indices]);
    }

    /// <summary>Each node's local-to-world matrix, in one pass because parents come first.</summary>
    static Matrix4x4[] WorldTransforms(ModelData model) {
        var world = new Matrix4x4[model.Nodes.Length];

        for (var index = 0; index < model.Nodes.Length; index++) {
            var node = model.Nodes[index];

            // local * parent, read left to right — Vixen's row-vector convention.
            world[index] = node.Parent >= 0 && node.Parent < index
                ? node.Transform * world[node.Parent]
                : node.Transform;
        }

        return world;
    }

    static bool TryReadAreas(YamlMapping root, ImportContext context, out NavAreaVolume[] volumes) {
        volumes = [];

        if (root["areas"] is not YamlSequence sequence) {
            return true;
        }

        var read = new List<NavAreaVolume>();

        foreach (var node in sequence) {
            if (node is not YamlMapping entry) {
                context.Report(ImportSeverity.Error, "An entry under `areas` is not a mapping.");

                return false;
            }

            if (!TryReadByte(entry, "area", context, out var area) ||
                !TryReadVector(entry, "min", context, out var minimum) ||
                !TryReadVector(entry, "max", context, out var maximum)) {
                return false;
            }

            read.Add(NavAreaVolume.Box(new(Vector3.Min(minimum, maximum), Vector3.Max(minimum, maximum)), area));
        }

        volumes = [.. read];

        return true;
    }

    /// <summary>Reads the authored ways off the surface — the ladders and the jumps.</summary>
    static bool TryReadLinks(YamlMapping root, ImportContext context, out NavOffMeshConnectionData[] links) {
        links = [];

        if (root["links"] is not YamlSequence sequence) {
            return true;
        }

        var read = new List<NavOffMeshConnectionData>();

        foreach (var node in sequence) {
            if (node is not YamlMapping entry) {
                context.Report(ImportSeverity.Error, "An entry under `links` is not a mapping.");

                return false;
            }

            if (!TryReadVector(entry, "start", context, out var start) || !TryReadVector(entry, "end", context, out var end)) {
                return false;
            }

            var radius = Read(entry, "radius", 1f);

            if (radius <= 0f) {
                context.Report(ImportSeverity.Error, $"A link has a radius of {radius}, which cannot reach any ground.");

                return false;
            }

            var area = NavArea.Walkable;

            if (entry["area"] is not null && !TryReadByte(entry, "area", context, out area)) {
                return false;
            }

            read.Add(new() {
                Start = start,
                End = end,
                Radius = radius,
                // Two-way unless the author says otherwise: a ladder is climbed in both directions,
                // and a one-way link that was meant to be two-way is a bug nobody sees until an agent
                // walks the long way round.
                Bidirectional = entry["bidirectional"] is not YamlScalar bidirectional
                    || !bool.TryParse(bidirectional.Value, out var oneWay)
                    || oneWay,
                Area = area,
                Flags = NavPolyFlags.Walk | NavPolyFlags.Jump,
                UserId = (uint)Read(entry, "userId", 0f)
            });
        }

        links = [.. read];

        return true;
    }

    static float Read(YamlMapping entry, string key, float fallback) =>
        entry[key] is YamlScalar scalar && float.TryParse(scalar.Value, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    static bool TryReadByte(YamlMapping entry, string key, ImportContext context, out byte value) {
        value = 0;

        if (entry[key] is not YamlScalar scalar || !byte.TryParse(scalar.Value, CultureInfo.InvariantCulture, out value)) {
            context.Report(ImportSeverity.Error, $"An entry under `areas` has no readable `{key}`.");

            return false;
        }

        if (value == NavArea.Null) {
            context.Report(ImportSeverity.Error, "An entry under `areas` uses area 0, which is what unwalkable means.");

            return false;
        }

        return true;
    }

    static bool TryReadVector(YamlMapping entry, string key, ImportContext context, out Vector3 value) {
        value = Vector3.Zero;

        if (entry[key] is not YamlSequence sequence || sequence.Count != 3) {
            context.Report(ImportSeverity.Error, $"An entry under `areas` has no `{key}` of three numbers.");

            return false;
        }

        Span<float> parts = stackalloc float[3];

        for (var index = 0; index < 3; index++) {
            if (sequence[index] is not YamlScalar scalar ||
                !float.TryParse(scalar.Value, CultureInfo.InvariantCulture, out parts[index])) {
                context.Report(ImportSeverity.Error, $"An entry under `areas` has a `{key}` that is not three numbers.");

                return false;
            }
        }

        value = new(parts[0], parts[1], parts[2]);

        return true;
    }
}
