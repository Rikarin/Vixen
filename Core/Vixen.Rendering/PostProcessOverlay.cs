// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Rendering.Ecs;

namespace Vixen.Rendering;

/// <summary>One field's contribution: what it wants to be, and how much of the way there.</summary>
/// <param name="Value">What the winning volume said.</param>
/// <param name="Weight">How much of the way from the authored value to that one, 0 to 1.</param>
/// <remarks>
///     <para>
///         ⚠ <b>The weight has to travel with the value, and this pair is why
///         <see cref="PostProcessOverlay" /> is a different type from
///         <see cref="PostProcessSettings" />.</b> A volume half faded in wants half its offset — but
///         half of the way from <em>what</em>? From the value the node was authored with, which lives
///         in the node and not in the volume. So the fold cannot finish the interpolation, and a
///         plain <c>float?</c> coming out of it would either lose the weight or bake it against a
///         number the fold had to invent.
///     </para>
///     <para>
///         <b>Different fields carry different weights</b>, which rules out one weight for the whole
///         struct: a volume fully inside can set the bloom while a second, half faded in, sets the
///         contrast, and both are correct at once.
///     </para>
/// </remarks>
public readonly record struct Blended(float Value, float Weight) {
    /// <summary>This contribution laid over the value a document authored.</summary>
    /// <param name="authored">What the node's own parameter says.</param>
    /// <returns>What it should be this frame.</returns>
    public float Over(float authored) => MathUtil.Lerp(authored, Value, Math.Clamp(Weight, 0f, 1f));
}

/// <summary>The same for a colour.</summary>
/// <param name="Value">What the winning volume said.</param>
/// <param name="Weight">How much of the way there.</param>
public readonly record struct BlendedColour(Vector3 Value, float Weight) {
    /// <summary>This contribution laid over the value a document authored.</summary>
    /// <param name="authored">What the node's own parameter says.</param>
    /// <returns>What it should be this frame.</returns>
    public Vector3 Over(Vector3 authored) => Vector3.Lerp(authored, Value, Math.Clamp(Weight, 0f, 1f));
}

/// <summary>And for a whole colour decision list.</summary>
/// <param name="Value">What the winning layer's grade is.</param>
/// <param name="Weight">How much of the way there.</param>
/// <remarks>
///     One weight for the whole grade, on <see cref="Ecs.PostProcessSettings.Grading" />'s terms: a
///     CDL is one artifact, so its twenty-two numbers travel together the way the white balance's
///     two halves do. The lerp itself is <see cref="ColorGrading.Lerp" />, which is well defined at
///     every weight because no component of a grade is a sentinel.
/// </remarks>
public readonly record struct BlendedGrading(ColorGrading Value, float Weight) {
    /// <summary>This contribution laid over the grade a document authored.</summary>
    /// <param name="authored">
    ///     What the node's own grade says — <see cref="ColorGrading.Neutral" /> for a node whose
    ///     grade is off, never <c>default</c>, whose zeroed gain is a black frame.
    /// </param>
    /// <returns>What it should be this frame.</returns>
    public ColorGrading Over(in ColorGrading authored) =>
        ColorGrading.Lerp(authored, Value, Math.Clamp(Weight, 0f, 1f));
}

/// <summary>One switch's contribution: what it wants, and how much of the fold agrees.</summary>
/// <param name="Value">What the winning layer said.</param>
/// <param name="Weight">How much of the way there, 0 to 1.</param>
/// <remarks>
///     <para>
///         ⚠ <b>A switch cannot crossfade, so it flips at half weight.</b> Fog's height falloff is
///         on or off — there is no picture that is forty percent of an atmosphere model — so the
///         honest reading of a partial weight is "which side of the blend are we closer to". The
///         flip lands at the blend's midpoint, where a walker expects the room to change, rather
///         than at its edge, where a one-texel step would toggle it per frame.
///     </para>
///     <para>
///         The look profile — the field's reason to exist — always contributes at weight 1, so the
///         rule only shows where a bounded volume claims a switch and fades, which is a level design
///         the smoothness-minded author avoids anyway.
///     </para>
/// </remarks>
public readonly record struct BlendedToggle(bool Value, float Weight) {
    /// <summary>This contribution laid over the value a document authored.</summary>
    /// <param name="authored">What the node's own parameter says.</param>
    /// <returns>What it should be this frame.</returns>
    public bool Over(bool authored) => Weight >= 0.5f ? Value : authored;
}

/// <summary>Several volumes folded into one set of contributions.</summary>
/// <remarks>
///     <para>
///         <b>What the frame actually consumes.</b> A <see cref="PostProcessSettings" /> is what one
///         volume authors; this is what a stack of them comes to, and it is a different type because
///         it carries a weight per field — see <see cref="Blended" /> for why that is unavoidable.
///     </para>
///     <para>
///         <b>The fold is <see cref="Add" />, applied in ascending priority order.</b> Each call lays
///         one volume over what is already there. The rule per field has three cases, and the third
///         is the one that makes overlapping volumes work:
///     </para>
///     <list type="number">
///         <item>The volume has no opinion — nothing changes.</item>
///         <item>Nothing underneath had one either — the field becomes the volume's value at the
///             volume's weight, and the node will lerp from its own authored value.</item>
///         <item>Both have one — the value lerps toward the new one by the new weight, and the
///             <em>weight</em> lerps toward 1 by the same amount. That second half is what makes a
///             half-faded volume over a fully-applied one come out half way between them rather than
///             half way back to the document.</item>
///     </list>
///     <para>
///         ⚠ <b>It is not commutative, and it must not be.</b> Priority is what decides which volume
///         is on top, so the fold is ordered and the caller sorts. Two volumes at the same priority
///         resolve in whatever order the world walks, which is arbitrary — and that is the honest
///         answer rather than a hidden tiebreak, because two volumes that both fully claim one field
///         at one priority is a level-design mistake rather than a case to define.
///     </para>
/// </remarks>
public struct PostProcessOverlay {
    /// <summary>Stops added to the exposure.</summary>
    public Blended? ExposureCompensation { get; set; }

    /// <summary>The EV the frame's authored exposure is pinned to.</summary>
    public Blended? Ev100 { get; set; }

    /// <summary>The darkest scene the meter may expose for, in EV.</summary>
    public Blended? MeterMinimumEv { get; set; }

    /// <summary>And the brightest.</summary>
    public Blended? MeterMaximumEv { get; set; }

    /// <summary>How much the local exposure brings highlights down.</summary>
    public Blended? LocalHighlightContrast { get; set; }

    /// <summary>And shadows up.</summary>
    public Blended? LocalShadowContrast { get; set; }

    /// <summary>How much of the bloom pyramid is composited.</summary>
    public Blended? BloomIntensity { get; set; }

    /// <summary>Luminance above which a pixel blooms.</summary>
    public Blended? BloomThreshold { get; set; }

    /// <summary>How soft the shoulder under that threshold is.</summary>
    public Blended? BloomKnee { get; set; }

    /// <summary>What colour the glow is.</summary>
    public BlendedColour? BloomTint { get; set; }

    /// <summary>Contrast around middle grey.</summary>
    public Blended? Contrast { get; set; }

    /// <summary>Saturation.</summary>
    public Blended? Saturation { get; set; }

    /// <summary>A multiplier on the image before the curve.</summary>
    public BlendedColour? ColourFilter { get; set; }

    /// <summary>The temperature the scene is graded against.</summary>
    public Blended? Temperature { get; set; }

    /// <summary>Green against magenta.</summary>
    public Blended? Tint { get; set; }

    /// <summary>Hue rotation, in degrees.</summary>
    public Blended? HueShift { get; set; }

    /// <summary>The whole colour decision list, under one weight.</summary>
    public BlendedGrading? Grading { get; set; }

    /// <summary>How thick the fog is.</summary>
    public Blended? FogDensity { get; set; }

    /// <summary>What colour it is.</summary>
    public BlendedColour? FogColour { get; set; }

    /// <summary>Whether the fog thins with altitude.</summary>
    public BlendedToggle? FogHeightFalloff { get; set; }

    /// <summary>Whether looking toward the sun brightens it.</summary>
    public BlendedToggle? FogSunScattering { get; set; }

    /// <summary>How dark the corners go.</summary>
    public Blended? VignetteIntensity { get; set; }

    /// <summary>How abrupt that falloff is.</summary>
    public Blended? VignetteSmoothness { get; set; }

    /// <summary>How much grain there is.</summary>
    public Blended? GrainIntensity { get; set; }

    /// <summary>Channel offset at the screen edge.</summary>
    public Blended? AberrationStrength { get; set; }

    /// <summary>How bright the flare's ghosts are.</summary>
    public Blended? FlareIntensity { get; set; }

    /// <summary>How wide the defocus may get.</summary>
    public Blended? MaximumDefocus { get; set; }

    /// <summary>Nothing has an opinion, which is what a frame with no volumes folds to.</summary>
    public static PostProcessOverlay None => default;

    /// <summary>Whether any field carries a contribution.</summary>
    /// <remarks>
    ///     What every node checks first. A frame with no volumes reaches this and stops, so the whole
    ///     feature costs one branch per node per frame when nobody is using it.
    /// </remarks>
    public readonly bool IsEmpty =>
        ExposureCompensation is null
        && Ev100 is null
        && MeterMinimumEv is null
        && MeterMaximumEv is null
        && LocalHighlightContrast is null
        && LocalShadowContrast is null
        && BloomIntensity is null
        && BloomThreshold is null
        && BloomKnee is null
        && BloomTint is null
        && Contrast is null
        && Saturation is null
        && ColourFilter is null
        && Temperature is null
        && Tint is null
        && HueShift is null
        && Grading is null
        && FogDensity is null
        && FogColour is null
        && FogHeightFalloff is null
        && FogSunScattering is null
        && VignetteIntensity is null
        && VignetteSmoothness is null
        && GrainIntensity is null
        && AberrationStrength is null
        && FlareIntensity is null
        && MaximumDefocus is null;

    /// <summary>Lays one volume's settings over this fold.</summary>
    /// <param name="settings">What the volume says.</param>
    /// <param name="weight">How much of it applies — its master weight times its distance falloff.</param>
    /// <remarks>
    ///     Called in ascending priority order, so the last call is the volume on top. A weight of zero
    ///     changes nothing, which is what a volume the camera is well outside of contributes.
    /// </remarks>
    public void Add(in PostProcessSettings settings, float weight) {
        var amount = Math.Clamp(weight, 0f, 1f);

        if (amount <= 0f || settings.IsEmpty) {
            return;
        }

        ExposureCompensation = Fold(ExposureCompensation, settings.ExposureCompensation, amount);
        Ev100 = Fold(Ev100, settings.Ev100, amount);
        MeterMinimumEv = Fold(MeterMinimumEv, settings.MeterMinimumEv, amount);
        MeterMaximumEv = Fold(MeterMaximumEv, settings.MeterMaximumEv, amount);
        LocalHighlightContrast = Fold(LocalHighlightContrast, settings.LocalHighlightContrast, amount);
        LocalShadowContrast = Fold(LocalShadowContrast, settings.LocalShadowContrast, amount);
        BloomIntensity = Fold(BloomIntensity, settings.BloomIntensity, amount);
        BloomThreshold = Fold(BloomThreshold, settings.BloomThreshold, amount);
        BloomKnee = Fold(BloomKnee, settings.BloomKnee, amount);
        BloomTint = Fold(BloomTint, settings.BloomTint, amount);
        Contrast = Fold(Contrast, settings.Contrast, amount);
        Saturation = Fold(Saturation, settings.Saturation, amount);
        ColourFilter = Fold(ColourFilter, settings.ColourFilter, amount);
        Temperature = Fold(Temperature, settings.Temperature, amount);
        Tint = Fold(Tint, settings.Tint, amount);
        HueShift = Fold(HueShift, settings.HueShift, amount);
        Grading = Fold(Grading, settings.Grading, amount);
        FogDensity = Fold(FogDensity, settings.FogDensity, amount);
        FogColour = Fold(FogColour, settings.FogColour, amount);
        FogHeightFalloff = Fold(FogHeightFalloff, settings.FogHeightFalloff, amount);
        FogSunScattering = Fold(FogSunScattering, settings.FogSunScattering, amount);
        VignetteIntensity = Fold(VignetteIntensity, settings.VignetteIntensity, amount);
        VignetteSmoothness = Fold(VignetteSmoothness, settings.VignetteSmoothness, amount);
        GrainIntensity = Fold(GrainIntensity, settings.GrainIntensity, amount);
        AberrationStrength = Fold(AberrationStrength, settings.AberrationStrength, amount);
        FlareIntensity = Fold(FlareIntensity, settings.FlareIntensity, amount);
        MaximumDefocus = Fold(MaximumDefocus, settings.MaximumDefocus, amount);
    }

    /// <summary>One field's three cases. See the type's remarks.</summary>
    static Blended? Fold(Blended? under, float? over, float weight) {
        if (over is not { } value) {
            return under;
        }

        return under is { } start
            ? new Blended(MathUtil.Lerp(start.Value, value, weight), MathUtil.Lerp(start.Weight, 1f, weight))
            : new Blended(value, weight);
    }

    static BlendedColour? Fold(BlendedColour? under, Vector3? over, float weight) {
        if (over is not { } value) {
            return under;
        }

        return under is { } start
            ? new BlendedColour(Vector3.Lerp(start.Value, value, weight), MathUtil.Lerp(start.Weight, 1f, weight))
            : new BlendedColour(value, weight);
    }

    static BlendedGrading? Fold(BlendedGrading? under, ColorGrading? over, float weight) {
        if (over is not { } value) {
            return under;
        }

        return under is { } start
            ? new BlendedGrading(ColorGrading.Lerp(start.Value, value, weight), MathUtil.Lerp(start.Weight, 1f, weight))
            : new BlendedGrading(value, weight);
    }

    /// <summary>The toggle's three cases — the third takes the heavier side rather than lerping.</summary>
    static BlendedToggle? Fold(BlendedToggle? under, bool? over, float weight) {
        if (over is not { } value) {
            return under;
        }

        return under is { } start
            ? new BlendedToggle(weight >= 0.5f ? value : start.Value, MathUtil.Lerp(start.Weight, 1f, weight))
            : new BlendedToggle(value, weight);
    }
}
