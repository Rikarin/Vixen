// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Assets;
using Vixen.Core;
using Vixen.Rendering.Ecs;
using Vixen.Rendering.Vfx;
using Vixen.Vfx;

namespace Vixen.Engine.Renderer;

/// <summary>Resolves a <c>.vxvfx</c> reference into the graph a simulation runs.</summary>
/// <remarks>
///     <para>
///         <b><see cref="AssetMaterialSource" />'s shape, one asset kind along.</b> A reference becomes
///         an address, an address becomes a <see cref="VfxEffectContent" /> chunk, and the chunk
///         becomes a <see cref="VfxCompiledGraph" /> exactly once. The layering is the same and for
///         the same reason: <c>Vixen.Rendering</c> links no asset manager, so the interface lives
///         there and the implementation lives here.
///     </para>
///     <para>
///         <b>The graph is built once and shared.</b> <c>ToGraph</c> re-runs the validation that
///         refuses an updater reading what nothing writes — cheap for one effect, wasteful once per
///         emitter per frame — and a compiled graph is immutable, which is the property the dual
///         backend already rests on. So one asset is one graph however many emitters name it, and each
///         emitter's own state lives in its <c>VfxSystem</c>.
///     </para>
///     <para>
///         ⚠ <b>A reference that will not arrive is recorded rather than thrown for.</b> One entity in
///         a level naming an effect nothing shipped should not take the frame with it — the same
///         decision <see cref="AssetMaterialSource" /> makes, and the reason <see cref="Failed" /> is
///         a number worth watching.
///     </para>
/// </remarks>
public sealed class AssetVfxEffectSource : IVfxEffectSource {
    readonly AssetManager assets;
    readonly Dictionary<AssetReference, Entry> entries = [];

    /// <summary>Builds a source over a content manager.</summary>
    /// <param name="assets">Where the effects come from.</param>
    /// <exception cref="ArgumentNullException"><paramref name="assets" /> is null.</exception>
    public AssetVfxEffectSource(AssetManager assets) {
        ArgumentNullException.ThrowIfNull(assets);

        this.assets = assets;
    }

    /// <summary>How many distinct effects have been asked for.</summary>
    public int Requested => entries.Count;

    /// <summary>How many have compiled.</summary>
    public int Loaded {
        get {
            var count = 0;

            foreach (var entry in entries.Values) {
                if (entry.Graph is not null) {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>How many will not arrive.</summary>
    /// <remarks>
    ///     A reference nothing shipped, a chunk that is not an effect, or content whose graph does not
    ///     compile. The last of those should have been caught at import — <c>VfxImporter</c> compiles
    ///     every graph to find out whether it is one — so a number here that is not zero is content
    ///     built by an older engine.
    /// </remarks>
    public int Failed {
        get {
            var count = 0;

            foreach (var entry in entries.Values) {
                if (entry.Failed) {
                    count++;
                }
            }

            return count;
        }
    }

    /// <inheritdoc />
    public bool TryGet(AssetReference reference, out VfxCompiledGraph graph) {
        graph = null!;

        if (reference == AssetReference.Null) {
            // Not a failure and not worth an entry: an emitter with no effect chosen yet is the
            // ordinary state of one just dropped onto an entity.
            return false;
        }

        if (!entries.TryGetValue(reference, out var entry)) {
            entries[reference] = entry = Begin(reference);
        }

        if (entry.Graph is { } compiled) {
            graph = compiled;

            return true;
        }

        if (entry.Failed || entry.Handle is not { Status: AssetStatus.Loaded }) {
            if (entry.Handle is { Status: AssetStatus.Failed }) {
                entry.Failed = true;
            }

            return false;
        }

        try {
            entry.Graph = entry.Handle.Result.ToGraph();
        } catch (ArgumentException) {
            // Content the importer would have refused. Recorded so the frame keeps drawing and the
            // count says how many, rather than throwing out of a system that runs every frame.
            entry.Failed = true;

            return false;
        }

        graph = entry.Graph;

        return true;
    }

    Entry Begin(AssetReference reference) {
        var entry = new Entry();

        try {
            entry.Handle = assets.LoadAsync<VfxEffectContent>(reference);
        } catch (ReferenceNotFoundException) {
            entry.Failed = true;
        }

        return entry;
    }

    sealed class Entry {
        public AssetHandle<VfxEffectContent>? Handle;
        public VfxCompiledGraph? Graph;
        public bool Failed;
    }
}
