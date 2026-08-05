// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Serialization;
using Vixen.Core.Yaml;
using Vixen.Core.Yaml.Meta;
using Vixen.Water;

namespace Vixen.Editor.Assets.Water;

/// <summary>How a sea state is imported.</summary>
/// <remarks>
///     Empty, like <c>TerrainAssetImportSettings</c> and for the same reason: a <c>.vxwaves</c> already
///     <em>is</em> engine data, so there is nothing about the conversion to decide.
/// </remarks>
[DataContract("WaterWavesImporter")]
public sealed record WaterWavesImportSettings : IImportSettings {
    /// <inheritdoc />
    public int Version { get; init; } = 1;
}

/// <summary>Imports a <c>.vxwaves</c> — the one asset kind water adds.</summary>
/// <remarks>
///     <para>
///         <b>[35 § D6](../../docs/plan/35-water.md#d6-a-water-body-is-a-spline-and-a-profile-and-there-is-no-new-spline)
///         admits exactly one new asset kind and this is it.</b> A water body is a spline reference and
///         eleven numbers, so it stays in the scene where the merge is; a sea state is shared between
///         every body in a region <em>and between levels</em>, so it does not.
///     </para>
///     <para>
///         <b>Written as its serialized record rather than carried forward as text</b> — the split
///         <c>TerrainAssetImporter</c> makes between a <c>.vxgrass</c> and a <c>.vxlayer</c>, on the
///         same rule. There is a runtime reader: a zone names this asset and
///         <c>IWaterWaveSource</c> supplies it, so the chunk has to hold the record a game
///         deserialises. A text chunk would be a sea state that quietly never arrives.
///     </para>
///     <para>
///         ⚠ <b>An unsummable spectrum is an <em>error</em>, and that is the one place this is
///         stricter than its terrain sibling.</b> A foliage type with no mesh is an author part-way
///         through and warns; a spectrum whose wavelength range runs backwards has no defensible
///         partial state — <see cref="WaterWaveSpectrum.Generate" /> throws on it, so what a build that
///         accepted it produces is a zone that renders no waves at all, which reads as the water being
///         broken rather than as the file being wrong.
///     </para>
///     <para>
///         ⚠ <b>What cannot be checked here is whether anything names it.</b> A zone referring to a
///         sea state by a name no asset carries falls back to its inline spectrum and counts into
///         <c>WaterZoneSystem.UnresolvedWaves</c> — a running frame with the wrong sea. The importer
///         sees one file and cannot know which scenes point at it; the count is where that shows.
///     </para>
/// </remarks>
[Importer(WaterWavesAsset.Extension)]
public sealed class WaterWavesImporter : AssetImporter<WaterWavesImportSettings> {
    /// <summary>The alias of the type this writes.</summary>
    /// <remarks>
    ///     ⚠ <b>The alias of the type actually written, which is what a chunk's reader resolves.</b>
    ///     <c>MaterialImporter</c>'s own remarks record what the other spelling costs: the bytes of one
    ///     record handed to the reader of another, thrown from inside the asset manager about content
    ///     the build had just declared good.
    /// </remarks>
    public const string WavesType = nameof(WaterWavesAsset);

    /// <summary>The vector forms a spectrum's document may write.</summary>
    /// <remarks>On <c>TerrainAssetImporter</c>'s terms: from a static constructor rather than a module
    ///     initializer, for the reason <see cref="MathScalars" /> gives about a process-wide table.</remarks>
    static WaterWavesImporter() => MathScalars.Register();

    /// <inheritdoc />
    public override int Version => 1;

    /// <inheritdoc />
    protected override async ValueTask<ImportResult> ImportAsync(
        ImportContext context,
        WaterWavesImportSettings settings,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(context);

        string text;

        await using (var source = await context.OpenSourceAsync(cancellationToken).ConfigureAwait(false)) {
            using var reader = new StreamReader(source);

            text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }

        WaterWavesAsset waves;

        try {
            waves = YamlSerializer.Parse<WaterWavesAsset>(text);
        } catch (Exception exception) when (exception is YamlBindingException or YamlParseException) {
            // Reported rather than thrown, so an author gets every broken file in one pass.
            context.Report(ImportSeverity.Error, exception.Message);

            return context.Finish();
        }

        if (waves.Name.Length == 0) {
            waves.Name = Path.GetFileNameWithoutExtension(context.SourcePath.Value);
        }

        if (waves.Validate() is { } problem) {
            context.Report(ImportSeverity.Error, problem);

            return context.Finish();
        }

        Advise(context, waves.Spectrum);

        context.Write(SubAssetId.Main, WavesType, Serializer.ToBytes(waves));

        return context.Finish();
    }

    /// <summary>The two seas that are legal, load, and are not what anybody meant.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Warnings and not errors, because both are things somebody may want.</b> A dead calm
    ///         is a legitimate lake and a perfectly ordered swell is a legitimate stylisation; what
    ///         they have in common is that they are also what a half-filled file looks like, and the
    ///         symptom — "the water is flat", "the water looks like corrugated iron" — sends people to
    ///         the renderer.
    ///     </para>
    ///     <para>
    ///         The spread one is <see cref="WaterWaveSpectrum.DirectionalSpread" />'s own remark,
    ///         raised here because a remark in a docstring is not read by the person whose file it is.
    ///     </para>
    /// </remarks>
    static void Advise(ImportContext context, in WaterWaveSpectrum spectrum) {
        if (spectrum.WindSpeed <= 0f || spectrum.AmplitudeScale <= 0f) {
            context.Report(
                ImportSeverity.Warning,
                "Every wave in this sea state has zero amplitude, so the surface is a mirror. That is "
                + "a legitimate dead calm and it is also what a file nobody finished looks like — the "
                + "wind speed or the amplitude scale is zero."
            );
        }

        if (spectrum.DirectionalSpread <= 0f) {
            context.Report(
                ImportSeverity.Warning,
                "Every wave travels in exactly one direction, so no crests cross and the surface reads "
                + "as corrugated iron from any height. Half a radian is a reasonable open sea."
            );
        }
    }
}
