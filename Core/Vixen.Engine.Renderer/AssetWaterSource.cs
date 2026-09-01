// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Assets;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Core.Serialization;
using Vixen.Rendering.Water;
using Vixen.Water;

namespace Vixen.Engine.Renderer;

/// <summary>The curves and sea states a scene's water names, out of the asset system.</summary>
/// <remarks>
///     <para>
///         <b><see cref="AssetTerrainSource" />'s arrangement, one subsystem over, and this assembly
///         is where it belongs for the same reason:</b> <c>WaterZoneSystem</c> takes its splines and
///         its waves through seams rather than holding an <see cref="AssetManager" />, because a
///         test's spline is a literal, an editor's is a document being dragged and a game's is a
///         loaded file. Something has to be the game's answer, and it needs one of the asset system
///         and one of the render system.
///     </para>
///     <para>
///         <b>Nothing here waits</b> — <c>AssetMeshSource</c>'s contract, kept. A load starts on the
///         first ask and the answer is null until it lands; the fold asks again next frame and counts
///         what it did not get. A reference that fails stays failed, so a scene naming a spline
///         somebody deleted is one counter rather than a retry per frame for the life of the run.
///     </para>
///     <para>
///         ⚠ <b>The spline is built once per <em>placement</em>, not once per name.</b> A curve is
///         geometry and is asked for in world space, so two lakes sharing one <c>.vxspline</c> at
///         different transforms are two curves — and a cache keyed by name alone would give the
///         second one the first one's position. The transform is part of the key.
///     </para>
///     <para>
///         ⚠ <b>The sea state has no placement and must not acquire one.</b>
///         [35 § D7](../../docs/plan/35-water.md#d7-waves-are-a-spectrum-summed-as-gerstner-and-the-fft-is-deferred-with-arithmetic)
///         makes determinism an exit criterion: a spectrum that varied with where a zone entity sat
///         is one a client and a server could disagree about, which is a boat in a different place on
///         each.
///     </para>
/// </remarks>
public sealed class AssetWaterSource : IWaterSplineSource, IWaterWaveSource {
    readonly AssetManager assets;
    readonly Dictionary<string, SplineEntry> splines = [];
    readonly Dictionary<string, WavesEntry> waves = [];

    /// <summary>Builds a source over a content manager.</summary>
    /// <param name="assets">Where the bytes come from.</param>
    /// <exception cref="ArgumentNullException"><paramref name="assets" /> is null.</exception>
    public AssetWaterSource(AssetManager assets) {
        ArgumentNullException.ThrowIfNull(assets);
        this.assets = assets;
    }

    /// <summary>How many references have been asked for.</summary>
    public int Requested => splines.Count + waves.Count;

    /// <summary>How many reads are on their way and have not finished.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>For a test that has to wait for the work rather than for a clock.</b> Every read
    ///         above is <see cref="Task.Run{TResult}(Func{Task{TResult}})" />, so how long a fixture
    ///         waits for one is decided by the thread pool and not by the read — a suite that gave up
    ///         after an interval was measuring the runner, and did, on the Windows leg. This is how a
    ///         settle can say "something is still on its way" instead: while this is non-zero the work
    ///         exists and is worth another frame, and when it is zero for several frames running the
    ///         source has run out of things to do and no number of further frames can change the
    ///         answer. See <see cref="AssetTextureSource.Reading" />, which is the same hook one
    ///         subsystem over.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Counted on <see cref="Task.IsCompleted" /> rather than on the handle being
    ///         non-null</b>, so it falls the moment a read finishes rather than when the next ask
    ///         takes its result up. A count that stayed up until take-up would make "anything
    ///         outstanding" true on a source that had finished, and a fixture that gives up when
    ///         nothing is outstanding would hang on a real regression instead of failing. The gap
    ///         that opens the other way — finished, not yet taken up, so this reads zero for one
    ///         frame — is why a settle counts <em>consecutive</em> quiet frames.
    ///     </para>
    /// </remarks>
    internal int Reading {
        get {
            var count = 0;

            foreach (var entry in splines.Values) {
                if (entry.Loading is { IsCompleted: false }) {
                    count++;
                }
            }

            foreach (var entry in waves.Values) {
                if (entry.Loading is { IsCompleted: false }) {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>How many of them will never arrive.</summary>
    /// <remarks>
    ///     An address the catalog does not know, bytes that are not a spline, a sea state whose chunk
    ///     deserialises to something else. All content problems rather than frame problems, and each
    ///     invisible without a number: a missing curve is a lake that is simply not in the level, and
    ///     a missing sea state is water that looks entirely convincing and is the wrong sea.
    /// </remarks>
    public int Failed {
        get {
            var count = 0;

            foreach (var entry in splines.Values) {
                if (entry.Failed) {
                    count++;
                }
            }

            foreach (var entry in waves.Values) {
                if (entry.Failed) {
                    count++;
                }
            }

            return count;
        }
    }

    /// <inheritdoc />
    public Spline? SplineFor(string name, in Matrix4x4 placement) {
        if (string.IsNullOrEmpty(name)) {
            return null;
        }

        if (!splines.TryGetValue(name, out var entry)) {
            splines[name] = entry = new() { Loading = LoadSpline(name) };
        }

        if (entry.Asset is null) {
            if (entry.Failed) {
                return null;
            }

            if (entry.Loading is null) {
                // The read could not even start — an address the catalog lacks — which is as failed
                // as a read that threw, and must not be retried per frame.
                entry.Failed = true;

                return null;
            }

            if (!entry.Loading.IsCompleted) {
                return null;
            }

            var loading = entry.Loading;

            entry.Loading = null;

            if (!loading.IsCompletedSuccessfully) {
                // Observed so a broken file is a counter here rather than an unobserved-task event
                // later.
                _ = loading.Exception;
                entry.Failed = true;

                return null;
            }

            entry.Asset = loading.Result;
        }

        // ⚠ Built at a placement and cached against it, because the fold asks every frame and
        // building a curve — which precomputes an arc-length table — sixty times a second per body is
        // the cost `SplineAsset` exists as a separate type to avoid. A body that moved gets one
        // rebuild on the frame it moved.
        if (entry.Built is { } built && entry.Placement.Equals(placement)) {
            return built;
        }

        if (!entry.Asset.CanBuild) {
            // One control point is a legal asset and is not a curve — see `SplineAsset.CanBuild`. It
            // is the author's half-finished road rather than a broken file, so it is not counted as
            // failed and it is asked again next frame.
            return null;
        }

        var transform = placement;
        var points = new SplinePoint[entry.Asset.Count];

        for (var index = 0; index < points.Length; index++) {
            var point = entry.Asset[index];

            points[index] = point with {
                Position = Matrix4x4.TransformPosition(point.Position, transform),

                // ⚠ A tangent is a direction and takes the rotation and the scale but not the
                // translation. Transformed as a position, every tangent would point at wherever the
                // entity happens to be, which is a curve that folds into a knot at the origin.
                TangentIn = Matrix4x4.TransformDirection(point.TangentIn, transform),
                TangentOut = Matrix4x4.TransformDirection(point.TangentOut, transform)
            };
        }

        var curve = new Spline(points, entry.Asset.IsClosed);

        entry.Built = curve;
        entry.Placement = placement;

        return curve;
    }

    /// <inheritdoc />
    public WaterWaveSpectrum? SpectrumFor(string name) {
        if (string.IsNullOrEmpty(name)) {
            return null;
        }

        if (!waves.TryGetValue(name, out var entry)) {
            waves[name] = entry = new() { Loading = LoadWaves(name) };
        }

        if (entry.Spectrum is { } ready) {
            return ready;
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

        if (!loading.IsCompletedSuccessfully) {
            // A chunk that is not a serialized sea state — text out of an older build, some other
            // record's bytes — throws from the read, which is the same content problem a missing file
            // is, and retrying would give the same answer.
            _ = loading.Exception;
            entry.Failed = true;

            return null;
        }

        entry.Spectrum = loading.Result.Spectrum;

        return entry.Spectrum;
    }

    /// <summary>Starts one curve's read, off the frame's thread.</summary>
    /// <remarks>
    ///     Opened as bytes and deserialised here rather than loaded through a typed handle, on
    ///     <c>AssetTerrainSource.LoadGrass</c>'s terms: the importer writes the chunk and this reads
    ///     it, and the two have to be talking about one type.
    /// </remarks>
    Task<SplineAsset>? LoadSpline(string reference) {
        var address = AddressOf(reference);

        if (address is null) {
            return null;
        }

        return Task.Run(
            async () => {
                await using var stream = await assets.OpenAsync(address).ConfigureAwait(false);

                using var memory = new MemoryStream();
                await stream.CopyToAsync(memory).ConfigureAwait(false);

                return Serializer.Read<SplineAsset>(memory.ToArray());
            }
        );
    }

    /// <summary>Starts one sea state's read, on the same terms.</summary>
    Task<WaterWavesAsset>? LoadWaves(string reference) {
        var address = AddressOf(reference);

        if (address is null) {
            return null;
        }

        return Task.Run(
            async () => {
                await using var stream = await assets.OpenAsync(address).ConfigureAwait(false);

                using var memory = new MemoryStream();
                await stream.CopyToAsync(memory).ConfigureAwait(false);

                return Serializer.Read<WaterWavesAsset>(memory.ToArray());
            }
        );
    }

    /// <summary>The address a component's reference resolves to, or null for one the catalog lacks.</summary>
    string? AddressOf(string reference) {
        try {
            return AssetReference.TryParse(reference, out var parsed) ? assets.AddressOf(parsed) : reference;
        } catch (ReferenceNotFoundException) {
            return null;
        }
    }

    sealed class SplineEntry {
        public Task<SplineAsset>? Loading;
        public SplineAsset? Asset;
        public Spline? Built;
        public Matrix4x4 Placement;
        public bool Failed;
    }

    sealed class WavesEntry {
        public Task<WaterWavesAsset>? Loading;
        public WaterWaveSpectrum? Spectrum;
        public bool Failed;
    }
}
