// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Core.Yaml;
using Vixen.Editor.SceneView;
using Vixen.Water;

namespace Vixen.Editor.Water;

/// <summary>What the viewport draws this module's water from.</summary>
/// <remarks>
///     <para>
///         <b>The other half of the gesture, and the half that was missing.</b> The draw tool wrote a
///         real <c>.vxspline</c> and created a body naming it, and nothing anywhere turned that name
///         back into a curve for the pane to draw — so an author laid a lake and the viewport showed
///         the same dry ground it had before. <c>TerrainModule</c>'s <c>ITerrainScene</c> arrangement
///         exactly, one document on.
///     </para>
///     <para>
///         ⚠ <b>It reads the files off disk rather than going through the asset database</b>, and the
///         reason is the write it has to keep up with. <see cref="WaterModule.Placed" /> writes the
///         curve beside the scene with <c>File.WriteAllText</c>, and an import is a scan away — so a
///         source that asked the database would show the lake on whichever frame the watcher happened
///         to catch up, which reads as the draw tool being unreliable. Cached by name and re-read when
///         the file's timestamp moves, so a curve edited outside the editor still lands.
///     </para>
/// </remarks>
public sealed partial class WaterModule {
    /// <summary>What a drawn curve is written as, and therefore what one is looked for as.</summary>
    /// <remarks>
    ///     Here rather than on <c>SplineAsset</c> because that type does not declare one — the
    ///     extension is a convention of this toolset's, and <see cref="SplinePathFor" /> is the other
    ///     half of it. One constant so the write and the read cannot drift apart.
    /// </remarks>
    public const string SplineExtension = ".vxspline";

    WaterSceneSource? waterScene;

    /// <summary>What the viewport draws the water from. Built on first use.</summary>
    /// <remarks>
    ///     Built lazily for <c>TerrainModuleSession.TerrainScene</c>'s reason: it takes the module and
    ///     <c>this</c> is not available in a field initialiser.
    /// </remarks>
    internal IWaterScene WaterScene => waterScene ??= new(this);

    /// <summary>Every directory a named curve or sea state might be in, nearest first.</summary>
    /// <remarks>
    ///     ⚠ <b>The scene's own directory first, because that is where the draw tool writes.</b> A
    ///     project-wide walk is the fallback for a curve an author moved or authored by hand; putting
    ///     it first would let a stale copy elsewhere in the project shadow the one just drawn.
    /// </remarks>
    IEnumerable<string> SearchRoots() {
        if (Scene.Writer is SceneFileWriter { Path: { Length: > 0 } path }
            && Path.GetDirectoryName(path) is { Length: > 0 } beside) {
            yield return beside;
        }

        if (Project.Paths.Assets is { Length: > 0 } assets && Directory.Exists(assets)) {
            yield return assets;
        }
    }

    /// <summary>The module, seen as the thing that answers "what does this name mean".</summary>
    /// <remarks>
    ///     A nested type rather than the module implementing the interface itself, on
    ///     <c>SceneTerrains</c>' terms: the module is already several partials wide and a member
    ///     called <c>SplineFor</c> on it would sit beside <c>SplinePathFor</c>, which means the
    ///     opposite thing — where a curve is <em>written</em> rather than where one is read.
    /// </remarks>
    sealed class WaterSceneSource(WaterModule editor) : IWaterScene {
        readonly Dictionary<string, Cached<SplineAsset>> splines = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, Cached<WaterWavesAsset>> waves = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, (Spline Curve, Matrix4x4 Placement)> built = new(StringComparer.OrdinalIgnoreCase);

        /// <inheritdoc />
        public Spline? SplineFor(string name, in Matrix4x4 placement) {
            if (Load(splines, name, SplineExtension) is not { } asset || !asset.CanBuild) {
                return null;
            }

            // ⚠ Built at a placement and cached against it, for `AssetWaterSource`'s reason: building
            // a curve precomputes an arc-length table, and the fold asks once per body per frame.
            if (built.TryGetValue(name, out var cached) && cached.Placement.Equals(placement)) {
                return cached.Curve;
            }

            var transform = placement;
            var points = new SplinePoint[asset.Count];

            for (var index = 0; index < points.Length; index++) {
                var point = asset[index];

                points[index] = point with {
                    Position = Matrix4x4.TransformPosition(point.Position, transform),

                    // A tangent is a direction: rotation and scale, never the translation. See
                    // `AssetWaterSource.SplineFor`, whose arithmetic this is.
                    TangentIn = Matrix4x4.TransformDirection(point.TangentIn, transform),
                    TangentOut = Matrix4x4.TransformDirection(point.TangentOut, transform)
                };
            }

            var curve = new Spline(points, asset.IsClosed);

            built[name] = (curve, placement);

            return curve;
        }

        /// <inheritdoc />
        public WaterWaveSpectrum? SpectrumFor(string name) {
            if (Load(waves, name, WaterWavesAsset.Extension) is not { } asset || asset.Validate() is not null) {
                return null;
            }

            return asset.Spectrum;
        }

        /// <inheritdoc />
        /// <remarks>
        ///     ⚠ <b>Flat at zero, which is right for an ocean and visibly wrong for a lake in a
        ///     valley.</b> The ground the runtime uses is the terrain's, and this module may not
        ///     reference the terrain one — the two are independent plugins and either may be absent.
        ///     What it costs is a shoreline drawn where the body's own falloff puts it rather than
        ///     where the hill is; what it buys is a water toolset that works in a project with no
        ///     terrain in it at all. See the module's README.
        /// </remarks>
        public float GroundAt(Vector2 ground) => 0f;

        /// <summary>A named file under one of the search roots, re-read when it changes.</summary>
        T? Load<T>(Dictionary<string, Cached<T>> into, string name, string extension) where T : class {
            if (string.IsNullOrEmpty(name)) {
                return null;
            }

            if (Find(name + extension) is not { } path) {
                // ⚠ Forgotten rather than kept, so a curve that is written a moment later is picked
                // up. A body whose spline is not there yet is exactly what the fold counts into
                // `UnresolvedBodies`, which is a number rather than a failure.
                into.Remove(name);

                return null;
            }

            var stamp = File.GetLastWriteTimeUtc(path);

            if (into.TryGetValue(name, out var cached) && cached.Path == path && cached.Stamp == stamp) {
                return cached.Value;
            }

            T? value = null;

            try {
                if (YamlReader.Read(File.ReadAllText(path)) is { } node) {
                    value = YamlSerializer.Deserialize<T>(node);
                }
            } catch (IOException) {
                // A file being written as it is read. Cached as a miss with this timestamp so the
                // next frame tries again rather than spinning on the exception.
            } catch (UnauthorizedAccessException) {
                // Same.
            } catch (YamlParseException) {
                // A curve somebody is hand-editing. The viewport draws no body rather than throwing
                // out of a frame — see `IWaterScene.SplineFor`.
            }

            into[name] = new(path, stamp, value);

            return value;
        }

        /// <summary>Where a named file is, nearest search root first.</summary>
        string? Find(string file) {
            foreach (var root in editor.SearchRoots()) {
                var direct = Path.Combine(root, file);

                if (File.Exists(direct)) {
                    return direct;
                }

                try {
                    foreach (var found in Directory.EnumerateFiles(root, file, SearchOption.AllDirectories)) {
                        return found;
                    }
                } catch (IOException) {
                    // A directory that went away between the check and the walk.
                } catch (UnauthorizedAccessException) {
                    // A directory this process may not read.
                }
            }

            return null;
        }

        readonly record struct Cached<TValue>(string Path, DateTime Stamp, TValue? Value) where TValue : class;
    }
}
