// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Assets;
using Vixen.Core;
using Vixen.Foliage;
using Vixen.Rendering.Terrain;
using Vixen.Terrain;
using TerrainMap = Vixen.Terrain.Terrain;

namespace Vixen.Engine.Renderer;

/// <summary>The terrains and grass rules a scene's components name, out of the asset system.</summary>
/// <remarks>
///     <para>
///         <b><see cref="ITerrainAssetSource" />'s implementation, beside
///         <see cref="AssetTerrainTextures" /> and for the same reason:</b> this assembly is where
///         the asset system and the render system meet. A <c>.vxterrain</c> ships as a raw payload
///         — <see cref="TerrainStore" />'s own binary, not a serialized object — so it is opened as
///         bytes and read here; a <c>.vxgrass</c> ships as its <c>[DataContract]</c> graph and
///         loads as an object.
///     </para>
///     <para>
///         <b>Nothing here waits</b> — <c>AssetMeshSource</c>'s contract. A load starts on the
///         first ask and the answer is null until it lands; the extraction system asks again next
///         frame. A reference that fails stays failed, so a level naming a terrain somebody deleted
///         is one counter rather than a retry per frame for the life of the run.
///     </para>
/// </remarks>
public sealed class AssetTerrainSource : ITerrainAssetSource {
    readonly AssetManager assets;
    readonly Dictionary<string, TerrainEntry> terrains = [];
    readonly Dictionary<string, GrassEntry> grasses = [];

    /// <summary>Builds a source over a content manager.</summary>
    /// <param name="assets">Where the bytes come from.</param>
    /// <exception cref="ArgumentNullException"><paramref name="assets" /> is null.</exception>
    public AssetTerrainSource(AssetManager assets) {
        ArgumentNullException.ThrowIfNull(assets);
        this.assets = assets;
    }

    /// <summary>How many references have been asked for.</summary>
    public int Requested => terrains.Count + grasses.Count;

    /// <summary>How many of them will never arrive.</summary>
    /// <remarks>
    ///     An address the catalog does not know, bytes that are not a terrain, a grass whose chunk
    ///     deserialises to something else. All content problems rather than frame problems, and all
    ///     invisible without a number: what they look like is ground that is simply not in the
    ///     level.
    /// </remarks>
    public int Failed {
        get {
            var count = 0;

            foreach (var entry in terrains.Values) {
                if (entry.Failed) {
                    count++;
                }
            }

            foreach (var entry in grasses.Values) {
                if (entry.Failed) {
                    count++;
                }
            }

            return count;
        }
    }

    /// <inheritdoc />
    public TerrainMap? Terrain(string reference) {
        if (string.IsNullOrEmpty(reference)) {
            return null;
        }

        if (!terrains.TryGetValue(reference, out var entry)) {
            terrains[reference] = entry = new() { Loading = LoadTerrain(reference) };
        }

        if (entry.Map is { } map) {
            return map;
        }

        if (entry.Failed) {
            return null;
        }

        if (entry.Loading is null) {
            // The read could not even start — an address the catalog lacks — which is as failed as
            // a read that threw, and must not be retried per frame.
            entry.Failed = true;
            return null;
        }

        if (!entry.Loading.IsCompleted) {
            return null;
        }

        var loading = entry.Loading;

        entry.Loading = null;

        if (loading.IsCompletedSuccessfully && loading.Result is { } loaded) {
            entry.Map = loaded;
            return loaded;
        }

        // Observed so a broken file is a counter here rather than an unobserved-task event later.
        _ = loading.Exception;
        entry.Failed = true;

        return null;
    }

    /// <inheritdoc />
    public GrassType? Grass(string reference) {
        if (string.IsNullOrEmpty(reference)) {
            return null;
        }

        if (!grasses.TryGetValue(reference, out var entry)) {
            grasses[reference] = entry = new() { Handle = LoadGrass(reference) };
        }

        if (entry.Type is { } type) {
            return type;
        }

        if (entry.Failed) {
            return null;
        }

        if (entry.Handle is not { } handle) {
            entry.Failed = true;
            return null;
        }

        switch (handle.Status) {
            case AssetStatus.Loaded when handle.Result is GrassType loaded:
                entry.Type = loaded;
                return loaded;

            case AssetStatus.Loaded:
            case AssetStatus.Failed:
                // Loaded-but-not-a-grass is a chunk whose alias names some other record — the same
                // content problem a failed load is, and retrying would give the same answer.
                entry.Failed = true;
                return null;

            default:
                return null;
        }
    }

    /// <summary>Starts one terrain's read, off the frame's thread.</summary>
    Task<TerrainMap?>? LoadTerrain(string reference) {
        var address = AddressOf(reference);

        if (address is null) {
            return null;
        }

        return Task.Run(async () => {
            await using var stream = await assets.OpenAsync(address).ConfigureAwait(false);

            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory).ConfigureAwait(false);

            var map = TerrainStore.Read(memory.ToArray());

            // Composited before the first frame reads it, so the renderer's first upload copies
            // ground rather than the zeroed base a freshly read terrain holds.
            map.Resolve();

            return (TerrainMap?)map;
        });
    }

    /// <summary>Starts one grass rule's load, or records that it cannot start.</summary>
    AssetHandle<object>? LoadGrass(string reference) {
        try {
            return AssetReference.TryParse(reference, out var parsed)
                ? assets.LoadAsync<object>(parsed)
                : assets.LoadAsync<object>(reference);
        } catch (Exception failure) when (failure is ReferenceNotFoundException or AddressNotFoundException) {
            return null;
        }
    }

    /// <summary>The address a component's reference resolves to, or null for one the catalog lacks.</summary>
    string? AddressOf(string reference) {
        try {
            return AssetReference.TryParse(reference, out var parsed) ? assets.AddressOf(parsed) : reference;
        } catch (ReferenceNotFoundException) {
            return null;
        }
    }

    sealed class TerrainEntry {
        public Task<TerrainMap?>? Loading;
        public TerrainMap? Map;
        public bool Failed;
    }

    sealed class GrassEntry {
        public AssetHandle<object>? Handle;
        public GrassType? Type;
        public bool Failed;
    }
}
