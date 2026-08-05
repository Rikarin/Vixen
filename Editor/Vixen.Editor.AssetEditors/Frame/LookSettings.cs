// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Editor.Inspector;
using Vixen.Rendering;
using Vixen.Rendering.Ecs;

namespace Vixen.Editor.AssetEditors.Frame;

/// <summary>The project look profile's artistic values, as something an inspector can write to.</summary>
/// <remarks>
///     <para>
///         <b>A <c>.vxlook</c> is one <see cref="PostProcessSettings" /> and this is the form over
///         it.</b> Doc 39's boundary with the quality preset is one rule — look changes the intent,
///         quality changes only the fidelity and cost of the same intent — so bloom threshold is
///         here and bloom pyramid levels are in the resolved-quality stack beside it.
///     </para>
///     <para>
///         ⚠ <b>Every member is nullable and that is the feature, not a convenience.</b> Doc 32's
///         "says nothing / has an opinion" distinction is the whole precedence model: a look that
///         sets <c>fogDensity: 0</c> has cleared the fog, and one that says nothing about fog lets
///         whatever is under it stand. <c>OptionalDrawer</c> draws exactly that — a tick beside the
///         value — so the two readings are one click apart and never confusable. Flattening them
///         onto a plain float is the <c>ColorGradingRange</c> zero-value trap restated, and it looks
///         like every volume in the project overriding to black.
///     </para>
///     <para>
///         ⚠ <b><c>Grading</c> is carried and not shown.</b> The colour decision list is one opinion
///         of twenty-two numbers with no drawer, and a mirror that dropped what it could not draw
///         would delete a colourist's grade the first time somebody nudged a vignette.
///         <see cref="ToSettings" /> copies the read struct and overwrites only what is here, so
///         everything this type is silent about survives a round trip — the same discipline
///         <see cref="StandardFrameSettings.ToAsset" /> follows.
///     </para>
/// </remarks>
[DataContract("LookSettings")]
public sealed class LookSettings {
    PostProcessSettings carried;

    /// <summary>Stops added to whatever exposure the camera or the meter arrived at.</summary>
    [Inspector]
    [Tooltip("Negative is darker. A compensation, not an exposure — the absolute is Ev100.")]
    public float? ExposureCompensation { get; set; }

    /// <summary>The exposure the frame is pinned to, as an EV at ISO 100.</summary>
    [Inspector]
    [Tooltip("The one absolute in a look. A metered frame ignores it; the meter's buffer outranks it.")]
    public float? Ev100 { get; set; }

    /// <summary>The darkest scene the meter may expose for.</summary>
    [Inspector]
    [Tooltip("The meter's clamps are how a look keeps auto-exposure inside the range its grade was authored for.")]
    public float? MeterMinimumEv { get; set; }

    /// <summary>And the brightest, so a dark level cannot be metered up to noon.</summary>
    [Inspector]
    public float? MeterMaximumEv { get; set; }

    /// <summary>How much the highlights are brought down by the local exposure.</summary>
    [Inspector]
    public float? LocalHighlightContrast { get; set; }

    /// <summary>And how much the shadows are brought up.</summary>
    [Inspector]
    public float? LocalShadowContrast { get; set; }

    /// <summary>How much of the pyramid is composited into the image.</summary>
    [Inspector]
    public float? BloomIntensity { get; set; }

    /// <summary>Luminance above which a pixel contributes.</summary>
    [Inspector]
    [Tooltip("Look, not quality. How many pyramid levels it is spread over is the tier's business.")]
    public float? BloomThreshold { get; set; }

    /// <summary>How soft the shoulder under the threshold is.</summary>
    [Inspector]
    public float? BloomKnee { get; set; }

    /// <summary>What colour the glow is tinted.</summary>
    [Inspector]
    public Vector3? BloomTint { get; set; }

    /// <summary>Contrast, applied around middle grey.</summary>
    [Inspector]
    public float? Contrast { get; set; }

    /// <summary>Saturation, 0 for greyscale.</summary>
    [Inspector]
    public float? Saturation { get; set; }

    /// <summary>A multiplier on the whole image before the curve.</summary>
    [Inspector]
    public Vector3? ColourFilter { get; set; }

    /// <summary>The colour temperature the scene is graded against, in kelvin.</summary>
    [Inspector]
    [Tooltip(
        "A white balance, so the direction inverts: it names the light being corrected for. "
        + "4000 K reads cold and 7800 K reads warm. Zero is 'do not white balance'."
    )]
    public float? Temperature { get; set; }

    /// <summary>Green against magenta, perpendicular to the temperature.</summary>
    [Inspector]
    public float? Tint { get; set; }

    /// <summary>Hue rotation, in degrees.</summary>
    [Inspector]
    public float? HueShift { get; set; }

    /// <summary>How thick the fog is.</summary>
    [Inspector]
    public float? FogDensity { get; set; }

    /// <summary>What colour it is.</summary>
    [Inspector]
    public Vector3? FogColour { get; set; }

    /// <summary>Whether the fog thins with altitude.</summary>
    [Inspector]
    public bool? FogHeightFalloff { get; set; }

    /// <summary>Whether looking toward the sun brightens the fog.</summary>
    [Inspector]
    public bool? FogSunScattering { get; set; }

    /// <summary>How thick the froxel medium is, per metre.</summary>
    [Inspector]
    [Tooltip("Unset is 'no opinion'. Zero is 'there is no fog here', which is how an interior is cleared.")]
    public float? VolumetricDensity { get; set; }

    /// <summary>What fraction of that scatters rather than absorbs, per channel.</summary>
    [Inspector]
    public Vector3? VolumetricAlbedo { get; set; }

    /// <summary>Henyey–Greenstein anisotropy: 0 an even glow, 0.9 a searchlight beam.</summary>
    [Inspector]
    public float? VolumetricPhaseG { get; set; }

    /// <summary>0 is no darkening at the corners, 1 is fully dark.</summary>
    [Inspector]
    public float? VignetteIntensity { get; set; }

    /// <summary>How abrupt the vignette's falloff is.</summary>
    [Inspector]
    public float? VignetteSmoothness { get; set; }

    /// <summary>How much film grain there is.</summary>
    [Inspector]
    public float? GrainIntensity { get; set; }

    /// <summary>Channel offset at the screen edge, in UV units.</summary>
    [Inspector]
    public float? AberrationStrength { get; set; }

    /// <summary>How bright the lens flare's ghosts are.</summary>
    [Inspector]
    public float? FlareIntensity { get; set; }

    /// <summary>How wide the defocus may get, in pixels.</summary>
    [Inspector]
    [Tooltip("A ceiling, not a focus distance — the distance and the aperture stay on the camera.")]
    public float? MaximumDefocus { get; set; }

    /// <summary>Reads a look's values into the mirror.</summary>
    /// <param name="look">The profile, or null for one nothing has authored.</param>
    public void Read(LookAsset? look) {
        var settings = look?.Settings ?? PostProcessSettings.None;

        carried = settings;

        ExposureCompensation = settings.ExposureCompensation;
        Ev100 = settings.Ev100;
        MeterMinimumEv = settings.MeterMinimumEv;
        MeterMaximumEv = settings.MeterMaximumEv;
        LocalHighlightContrast = settings.LocalHighlightContrast;
        LocalShadowContrast = settings.LocalShadowContrast;
        BloomIntensity = settings.BloomIntensity;
        BloomThreshold = settings.BloomThreshold;
        BloomKnee = settings.BloomKnee;
        BloomTint = settings.BloomTint;
        Contrast = settings.Contrast;
        Saturation = settings.Saturation;
        ColourFilter = settings.ColourFilter;
        Temperature = settings.Temperature;
        Tint = settings.Tint;
        HueShift = settings.HueShift;
        FogDensity = settings.FogDensity;
        FogColour = settings.FogColour;
        FogHeightFalloff = settings.FogHeightFalloff;
        FogSunScattering = settings.FogSunScattering;
        VolumetricDensity = settings.VolumetricDensity;
        VolumetricAlbedo = settings.VolumetricAlbedo;
        VolumetricPhaseG = settings.VolumetricPhaseG;
        VignetteIntensity = settings.VignetteIntensity;
        VignetteSmoothness = settings.VignetteSmoothness;
        GrainIntensity = settings.GrainIntensity;
        AberrationStrength = settings.AberrationStrength;
        FlareIntensity = settings.FlareIntensity;
        MaximumDefocus = settings.MaximumDefocus;
    }

    /// <summary>The settings this mirror stands for, with everything it does not model carried over.</summary>
    /// <returns>The settings.</returns>
    public PostProcessSettings ToSettings() {
        var settings = carried;

        settings.ExposureCompensation = ExposureCompensation;
        settings.Ev100 = Ev100;
        settings.MeterMinimumEv = MeterMinimumEv;
        settings.MeterMaximumEv = MeterMaximumEv;
        settings.LocalHighlightContrast = LocalHighlightContrast;
        settings.LocalShadowContrast = LocalShadowContrast;
        settings.BloomIntensity = BloomIntensity;
        settings.BloomThreshold = BloomThreshold;
        settings.BloomKnee = BloomKnee;
        settings.BloomTint = BloomTint;
        settings.Contrast = Contrast;
        settings.Saturation = Saturation;
        settings.ColourFilter = ColourFilter;
        settings.Temperature = Temperature;
        settings.Tint = Tint;
        settings.HueShift = HueShift;
        settings.FogDensity = FogDensity;
        settings.FogColour = FogColour;
        settings.FogHeightFalloff = FogHeightFalloff;
        settings.FogSunScattering = FogSunScattering;
        settings.VolumetricDensity = VolumetricDensity;
        settings.VolumetricAlbedo = VolumetricAlbedo;
        settings.VolumetricPhaseG = VolumetricPhaseG;
        settings.VignetteIntensity = VignetteIntensity;
        settings.VignetteSmoothness = VignetteSmoothness;
        settings.GrainIntensity = GrainIntensity;
        settings.AberrationStrength = AberrationStrength;
        settings.FlareIntensity = FlareIntensity;
        settings.MaximumDefocus = MaximumDefocus;

        return settings;
    }

    /// <summary>The look asset this mirror stands for, or null where it has no opinions at all.</summary>
    /// <returns>The asset.</returns>
    /// <remarks>
    ///     ⚠ <b>Null rather than an empty profile, because the document's own <c>look:</c> is
    ///     nullable and the two mean different things to the host.</b> A node carrying an empty
    ///     <c>LookAsset</c> still out-votes <c>GraphicsOptions.Look</c> — see
    ///     <c>AppGraphics.LookFor</c> — so writing one for a profile nobody has authored would
    ///     silently disconnect the project's standalone <c>.vxlook</c>.
    /// </remarks>
    public LookAsset? ToAsset() {
        var settings = ToSettings();
        return settings.IsEmpty ? null : new LookAsset { Settings = settings };
    }
}
