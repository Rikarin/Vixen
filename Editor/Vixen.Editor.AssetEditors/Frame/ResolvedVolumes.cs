// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Reflection;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Rendering;
using Vixen.Rendering.Ecs;

namespace Vixen.Editor.AssetEditors.Frame;

/// <summary>One parameter of the volume fold: who claimed it, and what the last claimant won with.</summary>
/// <param name="Parameter">The document name of the parameter — <c>fogDensity</c>, <c>ev100</c>.</param>
/// <param name="Value">What the fold resolved it to, formatted as a document writes it.</param>
/// <param name="Weight">The weight the winning layer carried, 0…1.</param>
/// <param name="Layers">
///     Every layer that had an opinion, in application order — <c>look</c> first, then each
///     contributing volume by priority. The last is the one on screen.
/// </param>
public readonly record struct ResolvedVolumeParameter(
    string Parameter,
    string Value,
    float Weight,
    IReadOnlyList<string> Layers
) {
    /// <summary>The layer whose value is the one being drawn.</summary>
    public string Winner => Layers.Count > 0 ? Layers[^1] : "—";

    /// <summary>Whether more than one layer claimed it, which is where a surprise usually lives.</summary>
    public bool IsContested => Layers.Count > 1;
}

/// <summary>What the volume fold resolved to for one camera.</summary>
/// <param name="Volumes">How many volumes the fold saw — doc 39's <c>M</c>.</param>
/// <param name="Contributing">How many of them reached the camera and said something — its <c>N</c>.</param>
/// <param name="HasLook">Whether a project look profile was laid down as the base layer.</param>
/// <param name="Parameters">Every parameter anything had an opinion about, in document order.</param>
public sealed record ResolvedVolumeReport(
    int Volumes,
    int Contributing,
    bool HasLook,
    IReadOnlyList<ResolvedVolumeParameter> Parameters
) {
    /// <summary>The sentence doc 39 asks the editor to answer in one glance.</summary>
    /// <remarks>
    ///     ⚠ <b>A volume that is placed and not contributing is this feature's commonest failure</b>
    ///     — a zero weight, zero extents, or a camera outside the blend radius — <b>and it looks
    ///     exactly like one that is not wired up at all.</b> Which is why the pair of numbers is
    ///     shown even when they agree.
    /// </remarks>
    public string Summary => string.Create(
        CultureInfo.InvariantCulture,
        $"{Contributing} of {Volumes} volumes reaching the camera, and the fold "
        + $"{(Parameters.Count == 0 ? "says nothing" : "has an opinion")}."
    );
}

/// <summary>Doc 39's per-camera resolved volume stack, read out of the engine's own fold.</summary>
/// <remarks>
///     <para>
///         <b>Nothing here recomputes anything.</b> <see cref="PostProcessVolumeSystem" /> is what
///         the frame actually folds with, its <c>Fold</c> is public precisely "so a test, a tool or
///         an editor can fold without standing up a runner", and <c>Contributions</c> is documented
///         as "the resolved-stack view doc 39 promises the editor". This reads those three — the two
///         counts, the pairs, and the folded <see cref="PostProcessOverlay" /> — and arranges them.
///         A second implementation of the precedence would be a second answer to "why does it look
///         like this", which is the one question this panel exists to answer.
///     </para>
///     <para>
///         ⚠ <b>The camera is the editor's, and that is a real difference from the running
///         game.</b> The system reads <c>RenderView.Position</c> because
///         <c>CameraExtractionSystem</c> has already decided which of a scene's cameras won; in the
///         editor nothing has, so the viewport's eye is the honest stand-in and the panel says which
///         eye it used. Flying the viewport camera into a volume is then the gesture that makes the
///         panel change, which is exactly the check somebody with a volume that "is not working"
///         needs to make.
///     </para>
///     <para>
///         ⚠ <b>The pairs say which layers spoke and the overlay says what won, and neither alone is
///         enough.</b> <c>Contributions</c> lists <c>(layer, parameter)</c> in application order but
///         carries no values; the overlay carries the winning value and its weight but has forgotten
///         who supplied it. Joined on the parameter name they are the whole answer — and the join is
///         exact, because both sides spell a parameter the way a scene file does.
///     </para>
/// </remarks>
public sealed class ResolvedVolumes {
    /// <summary>The overlay's fields, which are the parameters a fold can decide.</summary>
    static readonly PropertyInfo[] Decided = typeof(PostProcessOverlay)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(property => Nullable.GetUnderlyingType(property.PropertyType) is not null)
        .ToArray();

    readonly List<(string Layer, string Parameter)> pairs = [];

    /// <summary>The look profile laid down as the fold's base layer.</summary>
    public PostProcessSettings Look { get; set; } = PostProcessSettings.None;

    /// <summary>Where the camera is, which is what decides which volumes reach it.</summary>
    public Vector3 Camera { get; set; }

    /// <summary>Folds the world's volumes and reads the result out.</summary>
    /// <param name="world">The world the scene is in.</param>
    /// <returns>The report.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="world" /> is null.</exception>
    /// <remarks>
    ///     ⚠ <b>The system is built per fold rather than kept, which costs one object and buys the
    ///     panel not owning a lifetime.</b> A <c>SystemBase</c> is disposable, and one held on a
    ///     <c>Control</c> — which is not — is a resource whose release depends on somebody
    ///     remembering to call a method on a panel that is closed by the docking layout. Folding is
    ///     a gesture rather than a frame, so building it here is free; what it must not do is span
    ///     the two calls, because <c>Contributions</c> reads what <c>Fold</c> gathered.
    /// </remarks>
    public ResolvedVolumeReport Fold(World world) {
        ArgumentNullException.ThrowIfNull(world);

        using var system = new PostProcessVolumeSystem(new RenderView("editor.volumes") { Position = Camera }) {
            Look = Look
        };

        system.Fold(world);

        pairs.Clear();
        system.Contributions(pairs);

        var claims = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var (layer, parameter) in pairs) {
            if (!claims.TryGetValue(parameter, out var layers)) {
                claims[parameter] = layers = [];
            }

            layers.Add(layer);
        }

        var overlay = system.Overlay;
        var parameters = new List<ResolvedVolumeParameter>();

        foreach (var property in Decided) {
            var name = Spelled(property.Name);

            if (property.GetValue(overlay) is not { } blended) {
                continue;
            }

            var (value, weight) = Read(blended);

            parameters.Add(
                new(
                    name,
                    value,
                    weight,
                    claims.TryGetValue(name, out var layers) ? layers : []
                )
            );
        }

        return new(system.VolumeCount, system.ContributingCount, !Look.IsEmpty, parameters);
    }

    /// <summary>The <c>Value</c> and <c>Weight</c> off whichever <c>Blended*</c> struct this is.</summary>
    /// <remarks>
    ///     ⚠ <b>Reflected rather than switched over the four kinds</b>, because a fifth would
    ///     otherwise be a parameter this panel silently stopped showing — which is the same
    ///     already-drifted second declaration the quality stack avoids by walking its own schema.
    ///     They agree on the two member names by design: a blend is a value and the weight it won at.
    /// </remarks>
    static (string Value, float Weight) Read(object blended) {
        var type = blended.GetType();

        var value = type.GetProperty("Value")?.GetValue(blended);
        var weight = type.GetProperty("Weight")?.GetValue(blended);

        return (Format(value), weight is float number ? number : 0f);
    }

    static string Spelled(string name) =>
        name.Length == 0 ? name : string.Concat(char.ToLowerInvariant(name[0]).ToString(), name.AsSpan(1));

    static string Format(object? value) => value switch {
        null => "—",
        bool flag => flag ? "true" : "false",
        float number => number.ToString("0.###", CultureInfo.InvariantCulture),
        Vector3 vector => string.Create(
            CultureInfo.InvariantCulture,
            $"{vector.X:0.###} {vector.Y:0.###} {vector.Z:0.###}"
        ),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.GetType().Name
    };
}
