// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Assets;
using Vixen.Core;
using Vixen.Core.Serialization;
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
///         bytes and read here; a <c>.vxgrass</c> ships as its serialized <see cref="GrassType" />
///         and is opened the same way, because a typed load cannot reach it:
///         <see cref="AssetManager.Load{T}(string, CancellationToken)" /> requires a class and the
///         rule is deliberately a struct. <c>Load&lt;object&gt;</c> was the first attempt at this
///         seam, and it can never answer — the database checks the chunk's type id against the type
///         being asked for, and no chunk was written by <c>System.Object</c>.
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
    readonly Dictionary<string, FoliageEntry> foliage = [];
    readonly Dictionary<string, VolumeEntry> volumes = [];

    /// <summary>Builds a source over a content manager.</summary>
    /// <param name="assets">Where the bytes come from.</param>
    /// <exception cref="ArgumentNullException"><paramref name="assets" /> is null.</exception>
    public AssetTerrainSource(AssetManager assets) {
        ArgumentNullException.ThrowIfNull(assets);
        this.assets = assets;
    }

    /// <summary>How many references have been asked for.</summary>
    public int Requested => terrains.Count + grasses.Count + foliage.Count + volumes.Count;

    /// <summary>How many reads are on their way and have not finished.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>For a test that has to wait for the work rather than for a clock.</b> Every read
    ///         above is <see cref="Task.Run{TResult}(Func{Task{TResult}})" />, so how long a fixture
    ///         waits for one is decided by the thread pool and not by the read. See
    ///         <see cref="AssetWaterSource.Reading" /> and <see cref="AssetTextureSource.Reading" />,
    ///         which are the same hook in the two neighbouring sources, and the remarks on the first
    ///         of them for why a clock cannot be the giving-up condition.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Counted on <see cref="Task.IsCompleted" /> rather than on the handle being
    ///         non-null</b>, so it falls the moment a read finishes rather than when the next ask
    ///         takes its result up — a count that stayed up until take-up would turn a settle's
    ///         giving-up condition into one that never fires, and a regression would hang rather
    ///         than fail.
    ///     </para>
    /// </remarks>
    internal int Reading {
        get {
            var count = 0;

            foreach (var entry in terrains.Values) {
                if (entry.Loading is { IsCompleted: false }) {
                    count++;
                }
            }

            foreach (var entry in grasses.Values) {
                if (entry.Loading is { IsCompleted: false }) {
                    count++;
                }
            }

            foreach (var entry in foliage.Values) {
                if (entry.Loading is { IsCompleted: false }) {
                    count++;
                }
            }

            foreach (var entry in volumes.Values) {
                if (entry.Loading is { IsCompleted: false }) {
                    count++;
                }
            }

            return count;
        }
    }

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

            foreach (var entry in foliage.Values) {
                if (entry.Failed) {
                    count++;
                }
            }

            foreach (var entry in volumes.Values) {
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
            grasses[reference] = entry = new() { Loading = LoadGrass(reference) };
        }

        if (entry.Type is { } type) {
            return type;
        }

        if (entry.Failed) {
            return null;
        }

        if (entry.Loading is null) {
            entry.Failed = true;
            return null;
        }

        if (!entry.Loading.IsCompleted) {
            return null;
        }

        var loading = entry.Loading;

        entry.Loading = null;

        if (loading.IsCompletedSuccessfully) {
            entry.Type = loading.Result;
            return loading.Result;
        }

        // A chunk that is not a serialized grass rule — text out of an older build, some other
        // record's bytes — throws from the read, which is the same content problem a missing file
        // is, and retrying would give the same answer.
        _ = loading.Exception;
        entry.Failed = true;

        return null;
    }

    /// <inheritdoc />
    public FoliageType? Foliage(string reference) {
        if (string.IsNullOrEmpty(reference)) {
            return null;
        }

        if (!foliage.TryGetValue(reference, out var entry)) {
            foliage[reference] = entry = new() { Loading = LoadFoliage(reference) };
        }

        if (entry.Type is { } type) {
            return type;
        }

        if (entry.Failed) {
            return null;
        }

        if (entry.Loading is null) {
            entry.Failed = true;
            return null;
        }

        if (!entry.Loading.IsCompleted) {
            return null;
        }

        var loading = entry.Loading;

        entry.Loading = null;

        if (loading.IsCompletedSuccessfully) {
            entry.Type = loading.Result;
            return loading.Result;
        }

        // The grass path's own outcome, one asset kind over: text out of an older build, some other
        // record's bytes — a content problem retrying cannot fix.
        _ = loading.Exception;
        entry.Failed = true;

        return null;
    }

    /// <inheritdoc />
    public FoliageVolume? Volume(string reference, IReadOnlyList<FoliageType> palette) {
        ArgumentNullException.ThrowIfNull(palette);

        if (string.IsNullOrEmpty(reference)) {
            return null;
        }

        if (!volumes.TryGetValue(reference, out var entry)) {
            volumes[reference] = entry = new() { Loading = LoadVolume(reference) };
        }

        if (entry.Failed) {
            return null;
        }

        if (entry.Bytes is null) {
            if (entry.Loading is null) {
                entry.Failed = true;
                return null;
            }

            if (!entry.Loading.IsCompleted) {
                return null;
            }

            var loading = entry.Loading;

            entry.Loading = null;

            if (!loading.IsCompletedSuccessfully) {
                _ = loading.Exception;
                entry.Failed = true;

                return null;
            }

            entry.Bytes = loading.Result;
        }

        // Rebuilt only when the palette's content moved — the volume's identity is what the
        // compositor node keys its device state by, so a rebuild per ask would rebuild all of it
        // per frame. The copy is the cache's own: the caller's list is a scratch it refills.
        if (entry.Volume is { } built && entry.Palette is { } dressed && dressed.SequenceEqual(palette)) {
            return built;
        }

        var volume = new FoliageVolume(new(32f));

        foreach (var type in palette) {
            volume.AddType(type);
        }

        try {
            FoliageStore.Read(volume, entry.Bytes);
        } catch (ArgumentException failure) {
            // Bytes that are not a foliage file — the store refuses rather than interprets, and so
            // does this. The refusal reads once, not per frame.
            _ = failure;
            entry.Failed = true;

            return null;
        }

        entry.Volume = volume;
        entry.Palette = [.. palette];

        return volume;
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

    /// <summary>Starts one grass rule's read, off the frame's thread.</summary>
    /// <remarks>
    ///     Opened as bytes and deserialized here rather than loaded through a typed handle —
    ///     <c>TerrainAssetImporter</c> writes the chunk as the serialized record, and the class
    ///     remarks say why no <c>Load&lt;T&gt;</c> can be the reader.
    /// </remarks>
    Task<GrassType>? LoadGrass(string reference) {
        var address = AddressOf(reference);

        if (address is null) {
            return null;
        }

        return Task.Run(async () => {
            await using var stream = await assets.OpenAsync(address).ConfigureAwait(false);

            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory).ConfigureAwait(false);

            return Serializer.Read<GrassType>(memory.ToArray());
        });
    }

    /// <summary>The address a component's reference resolves to, or null for one the catalog lacks.</summary>
    string? AddressOf(string reference) {
        try {
            return AssetReference.TryParse(reference, out var parsed) ? assets.AddressOf(parsed) : reference;
        } catch (ReferenceNotFoundException) {
            return null;
        }
    }

    /// <summary>Starts one foliage type's read, on the grass rule's terms.</summary>
    Task<FoliageType>? LoadFoliage(string reference) {
        var address = AddressOf(reference);

        if (address is null) {
            return null;
        }

        return Task.Run(async () => {
            await using var stream = await assets.OpenAsync(address).ConfigureAwait(false);

            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory).ConfigureAwait(false);

            return Serializer.Read<FoliageType>(memory.ToArray());
        });
    }

    /// <summary>Starts one volume's read: raw bytes, because the palette arrives separately.</summary>
    /// <remarks>
    ///     The bytes rather than the volume, deliberately: <see cref="FoliageStore.Read" /> drops
    ///     chunks past the palette, and the palette is the caller's — resolved from its own
    ///     references on its own schedule — so the build happens at the ask that has both.
    /// </remarks>
    Task<byte[]>? LoadVolume(string reference) {
        var address = AddressOf(reference);

        if (address is null) {
            return null;
        }

        return Task.Run(async () => {
            await using var stream = await assets.OpenAsync(address).ConfigureAwait(false);

            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory).ConfigureAwait(false);

            return memory.ToArray();
        });
    }

    sealed class TerrainEntry {
        public Task<TerrainMap?>? Loading;
        public TerrainMap? Map;
        public bool Failed;
    }

    sealed class GrassEntry {
        public Task<GrassType>? Loading;
        public GrassType? Type;
        public bool Failed;
    }

    sealed class FoliageEntry {
        public Task<FoliageType>? Loading;
        public FoliageType? Type;
        public bool Failed;
    }

    sealed class VolumeEntry {
        public Task<byte[]>? Loading;
        public byte[]? Bytes;
        public FoliageVolume? Volume;
        public FoliageType[]? Palette;
        public bool Failed;
    }
}
