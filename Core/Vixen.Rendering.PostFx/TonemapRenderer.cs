// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Rendering.Compositor;
using Vixen.Shaders;
using Vixen.Shaders.Generated;

namespace Vixen.Rendering.PostFx;

/// <summary>
///     The pass every frame ends with: linear HDR in, displayable colour out.
/// </summary>
/// <remarks>
///     <para>
///         Everything upstream works in linear radiance with no upper bound, because that is what
///         light is and what makes exposure, bloom and additive lighting compose correctly. A display
///         takes neither of those things, so something has to map an unbounded range onto zero to one
///         and encode it — and doing it anywhere but the end means every effect after it works on
///         numbers that have already been squashed.
///     </para>
///     <para>
///         <strong>It ran before this existed, and that is the point of the change.</strong>
///         <c>Tonemap.rvn</c> has been the worked example for the full-screen node since the node was
///         written, configured by hand wherever a frame needed one — a shader name, five parameter
///         keys and three bindings, spelled out per host. As an effect it is one line in a document
///         like every other, and the grading LUT the shader has always taken finally has something
///         that binds one.
///     </para>
/// </remarks>
public sealed class TonemapRenderer() : PostEffectRenderer(
    TonemapKeys.ShaderName,
    TonemapKeys.UsedPermutationKeys,
    TonemapKeys.ConstantBufferBinding
), IPostProcessTarget {
    PostProcessOverlay applied;

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ Recorded rather than applied. The authored properties stay exactly as the document set
    ///     them and <c>Configure</c> lays the overlay over them every frame — a node that wrote into
    ///     its own properties here would lose the authored value on the first frame a volume reached
    ///     it, and walking back out would restore the volume's numbers rather than the document's.
    /// </remarks>
    public void Apply(in PostProcessOverlay overlay) => applied = overlay;

    /// <summary>The linear HDR colour it maps.</summary>
    public required string Source { get; init; }

    /// <summary>
    ///     A 3D colour lookup table, or empty for none.
    /// </summary>
    /// <remarks>
    ///     What a colourist authors: the whole grade as one texture, applied after the tonemap so the
    ///     values it indexes are display-referred and bounded. Empty leaves
    ///     <see cref="TonemapKeys.UseLut" /> false, which folds the sample and the table out of the
    ///     variant entirely.
    /// </remarks>
    public string Lut { get; init; } = "";

    /// <summary>Which curve maps the range: 0 Reinhard, 1 ACES, 2 AgX, 3 filmic, 4 none.</summary>
    /// <remarks>
    ///     ⚠ <b>3 was documented as Uncharted and implemented as a clamp</b>, in three places, for as
    ///     long as the operator existed — so a frame asking for a filmic curve got a hard clip, which
    ///     is a plausible picture and the wrong one. It is Hable's curve now, shaped by the six
    ///     <c>Filmic*</c> properties below, and the clamp moved to 4.
    /// </remarks>
    public int Operator { get; set; } = 1;

    /// <summary>Whether the result is encoded to sRGB here rather than by the swapchain.</summary>
    /// <remarks>
    ///     False by default, because a target declared with an sRGB format encodes on write and doing
    ///     it twice is a washed-out frame. True is for a target that does not — an offscreen buffer a
    ///     screenshot is taken from, or a backend without sRGB swapchains.
    /// </remarks>
    public bool EncodeSrgb { get; set; }

    /// <summary>What the scene's radiance is multiplied by before the curve.</summary>
    /// <remarks>Ignored when <see cref="ExposureBuffer" /> names one — see there for which wins.</remarks>
    public float Exposure { get; set; } = 1f;

    /// <summary>
    ///     The buffer <c>AutoExposure</c> left this frame's measured exposure in, or empty to use
    ///     <see cref="Exposure" />.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The buffer wins where it is named, and the two are a permutation apart rather than
    ///         a branch.</b> Naming one selects a variant that declares the binding and reads it;
    ///         leaving it empty selects the variant that existed before auto-exposure did, with no
    ///         buffer declared and none bound. A frame cannot accidentally get both.
    ///     </para>
    ///     <para>
    ///         <b>What it buys is that the number never crosses the bus.</b> The reduction produces it
    ///         on the device and the tonemap consumes it there; a host that read it back to set
    ///         <see cref="Exposure" /> would pay a stall and a frame of latency for a value it does
    ///         not use — which is the arrangement <c>AutoExposure.rvn</c> was written to avoid, and
    ///         the reason that pass is compute at all.
    ///     </para>
    /// </remarks>
    public string ExposureBuffer { get; init; } = "";

    /// <summary>
    ///     The bloom pyramid to add before the curve, or empty for a frame with no glow.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This is where a <c>!Bloom</c> node's output goes, and there is nowhere else for it
    ///         to go.</b> That node publishes the pyramid — the part of the image above its threshold,
    ///         blurred — and not the scene with a glow on it. A frame that pointed its tonemap at the
    ///         pyramid instead of at the scene threw the scene away, and with a level lit below the
    ///         threshold it threw away everything: a black window, with every counter saying the frame
    ///         drew. This sample's compositor carries the comment describing exactly that.
    ///     </para>
    ///     <para>
    ///         Added before the exposure multiply, because the pyramid is in the source's units. See
    ///         <c>Tonemap.rvn</c>'s <c>UseBloom</c> for why it is not a pass of its own.
    ///     </para>
    /// </remarks>
    public string Bloom { get; init; } = "";

    /// <summary>How much of the pyramid reaches the image.</summary>
    /// <remarks>
    ///     A fraction, because bloom is the light a lens spilled and not a second exposure. Above
    ///     about a third the image reads as though it were being viewed through grease.
    /// </remarks>
    public float BloomIntensity { get; set; } = 0.2f;

    /// <summary>What colour the spill is — the coating on the lens rather than the light.</summary>
    public Vector3 BloomTint { get; set; } = Vector3.One;

    /// <summary>Dust and smears on the front element, or empty for a clean lens.</summary>
    /// <remarks>
    ///     ⚠ It multiplies the <em>bloom</em>, not the image. Dirt is only visible because bright
    ///     light scatters off it, which is why a dirty lens shows nothing in an evenly lit room and
    ///     streaks across the frame pointed at the sun. Added to the image it would be a decal.
    /// </remarks>
    public string BloomDirt { get; init; } = "";

    /// <summary>How much the dirt brightens the bloom.</summary>
    public float BloomDirtIntensity { get; set; }

    /// <summary>The four-range colour decision list, or null for no grade.</summary>
    /// <remarks>
    ///     <para>
    ///         Unreal's decomposition: saturation, contrast, gamma, gain and offset, applied globally
    ///         and then per tonal range. It is the ASC CDL, which is what a grading suite exports — so
    ///         a grade authored in Resolve carries across as numbers rather than as a baked table.
    ///     </para>
    ///     <para>
    ///         ⚠ Null and <see cref="ColorGrading.Neutral" /> are not the same thing to the variant
    ///         cache: null leaves <c>UseColorGrading</c> off and the whole function out of the
    ///         compiled shader. They are the same picture.
    ///     </para>
    /// </remarks>
    public ColorGrading? Grading { get; set; }

    /// <summary>
    ///     The view whose lens sets the exposure, or null for an authored one.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The point of a camera being one component.</b> An aperture, a shutter and an ISO are
    ///         an exposure value — <c>Photometry.Ev100FromCamera</c> — and the aperture is also what
    ///         decides the defocus. A frame that read its exposure off the same lens it is focused
    ///         through cannot have the two disagree.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Only when <see cref="LensExposure" /> is on</b>, and see there for why the lens
    ///         does <em>not</em> simply win. The order is measured, then authored, then the lens.
    ///     </para>
    /// </remarks>
    public RenderView? View { get; set; }

    /// <summary>Whether the view's lens supplies the exposure, because nothing else did.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The lens loses to an authored exposure, and that reverses what this did when a
    ///         lens was a component somebody had to add.</b> Every camera is a physical camera now, so
    ///         "the view has a lens" is true of every frame and can no longer mean "the author asked
    ///         for a physical exposure". A document that writes <c>ev100: 13</c> has made a decision —
    ///         this level's dusk is a stop and a half under what a meter says — and a lens quietly
    ///         overriding it would move every authored frame by however many stops its aperture
    ///         happens to be.
    ///     </para>
    ///     <para>
    ///         So the factory turns this on exactly when the document named neither <c>exposure</c>
    ///         nor <c>ev100</c>, and the resulting order is: a measured exposure beats an authored
    ///         one beats the lens.
    ///     </para>
    /// </remarks>
    public bool LensExposure { get; set; }

    /// <summary>The radiance that maps to white.</summary>
    public float WhitePoint { get; set; } = 4f;

    /// <summary>Contrast, applied around middle grey.</summary>
    public float Contrast { get; set; } = 1f;

    /// <summary>Saturation, 0 for greyscale.</summary>
    public float Saturation { get; set; } = 1f;

    /// <summary>
    ///     The colour temperature the scene is lit at, in kelvin, or zero to leave it alone.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>What a camera's white balance dial says, and it reads the same way round.</b>
    ///         Setting this to 3200 tells the grade the scene is lit by a 3200 K source, so the image
    ///         gets <em>cooler</em> — a warm lamp comes out white. That is the inverse of
    ///         <c>Light.Temperature</c>, which says what colour an emitter is.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Kelvin, and it used to be a −1..1 warm/cool lerp between two hard-coded triples.</b>
    ///         That version and the black-body curve the lights use disagreed about what any given
    ///         temperature looked like, which in a scene lit in kelvin is one engine giving two
    ///         answers. Both sides are <see cref="Photometry.FromTemperature" /> now — see
    ///         <see cref="Photometry.WhiteBalance" />, which is literally its inverse — so a lamp at
    ///         3200 K under a grade balanced to 3200 K cancels exactly.
    ///     </para>
    /// </remarks>
    public float Temperature { get; set; }

    /// <summary>Green to magenta, −1 to 1. The axis a colour temperature does not have.</summary>
    /// <remarks>
    ///     Every black body sits on one line through chromaticity space, and real illuminants —
    ///     fluorescent tubes most of all — sit off it. This is the distance off, and it is what a
    ///     camera's tint slider is for.
    /// </remarks>
    public float Tint { get; set; }

    /// <summary>Multiplied into the scene before anything else grades it.</summary>
    /// <remarks>A filter on a lens: it belongs before the curve, and it is scene-referred.</remarks>
    public Vector3 ColorFilter { get; set; } = Vector3.One;

    /// <summary>Hue rotation in radians.</summary>
    /// <remarks>
    ///     A YIQ matrix rather than an HSV round trip, because this runs on unbounded scene light and
    ///     HSV is defined on 0..1. It preserves luminance exactly, so a hue shift cannot change the
    ///     exposure.
    /// </remarks>
    public float HueShift { get; set; }

    /// <summary>Where split toning crosses over, −1 towards shadows and 1 towards highlights.</summary>
    public float SplitBalance { get; set; }

    /// <summary>Hable's shoulder strength. Read when <see cref="Operator" /> is 3.</summary>
    public float FilmicShoulderStrength { get; set; } = 0.15f;

    /// <summary>Hable's linear strength.</summary>
    public float FilmicLinearStrength { get; set; } = 0.5f;

    /// <summary>Hable's linear angle.</summary>
    public float FilmicLinearAngle { get; set; } = 0.1f;

    /// <summary>Hable's toe strength.</summary>
    public float FilmicToeStrength { get; set; } = 0.2f;

    /// <summary>Hable's toe numerator.</summary>
    public float FilmicToeNumerator { get; set; } = 0.02f;

    /// <summary>Hable's toe denominator.</summary>
    public float FilmicToeDenominator { get; set; } = 0.3f;

    /// <summary>What the shadows are tinted toward.</summary>
    public Vector3 ShadowTint { get; set; } = Vector3.One;

    /// <summary>What the highlights are tinted toward.</summary>
    public Vector3 HighlightTint { get; set; } = Vector3.One;

    /// <summary>How many entries the LUT has along one axis.</summary>
    /// <remarks>
    ///     Needed because a 3D LUT is sampled with a half-texel inset at both ends — without it the
    ///     first and last entries are half weighted, which shows as black and white points that are
    ///     slightly wrong and nothing else.
    /// </remarks>
    public float LutSize { get; set; } = 32f;

    /// <inheritdoc />
    protected override void Configure(
        CompositorFrame frame,
        ParameterCollection parameters,
        IList<ResourceBinding> bindings
    ) {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(parameters);

        var graded = !string.IsNullOrEmpty(Lut);

        var measured = !string.IsNullOrEmpty(ExposureBuffer);
        var glowing = !string.IsNullOrEmpty(Bloom);

        parameters.Set(TonemapKeys.Operator, Operator);
        parameters.Set(TonemapKeys.UseLut, graded);
        parameters.Set(TonemapKeys.EncodeSrgb, EncodeSrgb);
        parameters.Set(TonemapKeys.UseExposureBuffer, measured);
        parameters.Set(TonemapKeys.UseBloom, glowing);

        parameters.Set(TonemapKeys.BloomIntensity, applied.BloomIntensity?.Over(BloomIntensity) ?? BloomIntensity);
        parameters.Set(TonemapKeys.BloomTint, applied.BloomTint?.Over(BloomTint) ?? BloomTint);
        parameters.Set(TonemapKeys.BloomDirtIntensity, string.IsNullOrEmpty(BloomDirt) ? 0f : BloomDirtIntensity);

        // ⚠ An overlay grade reaches a node whose own grade is off: the permutation turns on and the
        // blend starts from Neutral — never from null-as-default, whose zeroed gain is a black frame.
        // That is what lets a look profile grade a Standard Frame whose emitted tonemap is neutral.
        var grading = applied.Grading is { } grade
            ? grade.Over(Grading ?? ColorGrading.Neutral)
            : Grading;

        parameters.Set(TonemapKeys.UseColorGrading, grading is not null);

        if (grading is { } cdl) {
            Apply(parameters, cdl);
        }
        parameters.Set(TonemapKeys.Exposure, ExposureFor());

        // ⚠ A compensation rather than a scale on `Exposure`, and it has to be: a metered frame
        // ignores that uniform entirely — the shader reads the buffer — so a volume that darkened a
        // cellar by scaling it would do nothing in exactly the frames that have an auto-exposure.
        parameters.Set(TonemapKeys.ExposureCompensation, applied.ExposureCompensation?.Over(0f) ?? 0f);
        parameters.Set(TonemapKeys.WhitePoint, WhitePoint);
        parameters.Set(TonemapKeys.Contrast, applied.Contrast?.Over(Contrast) ?? Contrast);
        parameters.Set(TonemapKeys.Saturation, applied.Saturation?.Over(Saturation) ?? Saturation);
        // Resolved on the host, so the shader multiplies a triple it does not have to derive — and
        // so the grade and the scene's lights are the same curve read in two directions.
        parameters.Set(TonemapKeys.WhiteBalance, WhiteBalanceFor());
        parameters.Set(TonemapKeys.ColorFilter, applied.ColourFilter?.Over(ColorFilter) ?? ColorFilter);
        parameters.Set(TonemapKeys.HueShift, applied.HueShift?.Over(HueShift) ?? HueShift);
        parameters.Set(TonemapKeys.SplitBalance, SplitBalance);
        parameters.Set(TonemapKeys.FilmicShoulderStrength, FilmicShoulderStrength);
        parameters.Set(TonemapKeys.FilmicLinearStrength, FilmicLinearStrength);
        parameters.Set(TonemapKeys.FilmicLinearAngle, FilmicLinearAngle);
        parameters.Set(TonemapKeys.FilmicToeStrength, FilmicToeStrength);
        parameters.Set(TonemapKeys.FilmicToeNumerator, FilmicToeNumerator);
        parameters.Set(TonemapKeys.FilmicToeDenominator, FilmicToeDenominator);
        parameters.Set(TonemapKeys.ShadowTint, ShadowTint);
        parameters.Set(TonemapKeys.HighlightTint, HighlightTint);
        parameters.Set(TonemapKeys.LutSize, LutSize);

        Read(bindings, TonemapKeys.SourceBinding, Source);

        // The table's binding exists in the default variant whether or not this frame has a table, and
        // a descriptor set with a hole in it is a validation error. The source stands in — a texture
        // that certainly exists — and `UseLut` is what stops it being read.
        Read(bindings, TonemapKeys.LutBinding, graded ? Lut : Source);

        // The same stand-in rule as the table above, and for the same reason: `bloom` is a 2D texture
        // declared in every variant, so a frame with no pyramid still owes the set something. The
        // scene itself is the resource that certainly exists, and `UseBloom` is what stops it being
        // read — a frame that read it would be adding a blurless copy of itself.
        Read(bindings, TonemapKeys.BloomBinding, glowing ? Bloom : Source);

        // The same stand-in rule again, and the intensity above is what stops it being read: a clean
        // lens still owes the set a texture at this binding.
        Read(bindings, TonemapKeys.BloomDirtBinding, BloomDirt is { Length: > 0 } dirt ? dirt : Source);

        Sample(bindings, TonemapKeys.SourceSamplerBinding, Samplers!.LinearClamp);
        Sample(bindings, TonemapKeys.LutSamplerBinding, Samplers!.LinearClamp);
        Sample(bindings, TonemapKeys.BloomSamplerBinding, Samplers!.LinearClamp);
        Sample(bindings, TonemapKeys.BloomDirtSamplerBinding, Samplers!.LinearClamp);

        // ⚠ Bound only when the permutation reads it. Unlike the LUT above — whose binding exists in
        // every variant, so a stand-in has to fill it — the exposure buffer folds out of the variant
        // entirely when `UseExposureBuffer` is false, and there is no texture that would stand in for
        // a buffer anyway.
        if (measured) {
            ReadBuffer(bindings, TonemapKeys.ExposureBufferBinding, ExposureBuffer);
        }
    }

    /// <summary>Writes one colour decision list into the pass's parameters.</summary>
    /// <remarks>
    ///     Twenty-two values written by hand rather than through a loop over reflection, because a
    ///     parameter key is a distinct static field and there is nothing to iterate. The grouping into
    ///     <see cref="ColorGradingRange" /> is what keeps that from spreading to the callers.
    /// </remarks>
    static void Apply(ParameterCollection parameters, in ColorGrading grading) {
        Range(parameters, grading.Global, TonemapKeys.GradeGlobalSaturation, TonemapKeys.GradeGlobalContrast,
            TonemapKeys.GradeGlobalGamma, TonemapKeys.GradeGlobalGain, TonemapKeys.GradeGlobalOffset);

        Range(parameters, grading.Shadows, TonemapKeys.GradeShadowSaturation, TonemapKeys.GradeShadowContrast,
            TonemapKeys.GradeShadowGamma, TonemapKeys.GradeShadowGain, TonemapKeys.GradeShadowOffset);

        Range(parameters, grading.Midtones, TonemapKeys.GradeMidtoneSaturation, TonemapKeys.GradeMidtoneContrast,
            TonemapKeys.GradeMidtoneGamma, TonemapKeys.GradeMidtoneGain, TonemapKeys.GradeMidtoneOffset);

        Range(parameters, grading.Highlights, TonemapKeys.GradeHighlightSaturation,
            TonemapKeys.GradeHighlightContrast, TonemapKeys.GradeHighlightGamma, TonemapKeys.GradeHighlightGain,
            TonemapKeys.GradeHighlightOffset);

        parameters.Set(TonemapKeys.GradeShadowsMax, grading.ShadowsMax);
        parameters.Set(TonemapKeys.GradeHighlightsMin, grading.HighlightsMin);
    }

    static void Range(
        ParameterCollection parameters,
        in ColorGradingRange range,
        ParameterKey<float> saturation,
        ParameterKey<float> contrast,
        ParameterKey<Vector3> gamma,
        ParameterKey<Vector3> gain,
        ParameterKey<Vector3> offset
    ) {
        parameters.Set(saturation, range.Saturation);
        parameters.Set(contrast, range.Contrast);
        parameters.Set(gamma, range.Gamma);
        parameters.Set(gain, range.Gain);
        parameters.Set(offset, range.Offset);
    }

    /// <summary>This frame's white balance, blended as a multiplier rather than as a temperature.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A volume's temperature cannot be interpolated in kelvin, and this is the one field
    ///         in <c>PostProcessSettings</c> where that is true.</b> Every other one is a value whose
    ///         zero is a value — a bloom intensity of nothing, a neutral hue shift — so lerping from
    ///         the authored number toward the volume's passes through numbers that mean what they say.
    ///         <see cref="Temperature" />'s zero is a <em>sentinel</em>: it means "do not white
    ///         balance", and there is no temperature that means that.
    ///     </para>
    ///     <para>
    ///         So a frame with no authored temperature, entering a volume that asks for 7800 K, used
    ///         to lerp the kelvin — and one thousandth of the way in it was asking for 7.8 K, which
    ///         <c>Photometry.FromTemperature</c> clamps to 1667 K: a deep orange illuminant whose
    ///         reciprocal is a <b>massive blue correction</b>. The multiplier went from (1, 1, 1) to
    ///         (0.000, 0.003, 13.8) across the first thousandth of the blend and then recovered
    ///         towards (1.04, 1.01, 0.82) as the volume faded in. On screen that is a hard flip to
    ///         blue at the edge of the volume, getting less blue as you walk further in — which is
    ///         exactly what it looked like.
    ///     </para>
    ///     <para>
    ///         <b>Converting both ends first and interpolating the multiplier is well defined at every
    ///         weight</b>, because a white balance is a plain per-channel gain and neutral is its
    ///         identity. The same walk now runs monotonically from (1, 1, 1) to the volume's value with
    ///         no excursion outside them.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>One weight for both, and it is the larger.</b> The temperature and the tint are two
    ///         halves of one multiplier, so they cannot be lerped independently without the second
    ///         undoing part of the first. In every real frame both come from the same volume and carry
    ///         the same weight; the compromise only shows if two volumes at different distances each
    ///         set one half, which is a level nobody has authored.
    ///     </para>
    /// </remarks>
    Vector3 WhiteBalanceFor() {
        var authored = Photometry.WhiteBalance(Temperature, Tint);

        if (applied.Temperature is null && applied.Tint is null) {
            return Triple(authored);
        }

        // ⚠ `.Value` and not `.Over(...)`: the volume's own number, unblended. The interpolation
        // happens below, in the multiplier, which is the whole point.
        var target = Photometry.WhiteBalance(
            applied.Temperature?.Value ?? Temperature,
            applied.Tint?.Value ?? Tint
        );

        var weight = MathF.Max(applied.Temperature?.Weight ?? 0f, applied.Tint?.Weight ?? 0f);

        return Vector3.Lerp(Triple(authored), Triple(target), Math.Clamp(weight, 0f, 1f));
    }

    static Vector3 Triple(Color3 value) => new(value.R, value.G, value.B);

    /// <summary>This frame's linear exposure, from whichever source is the most specific.</summary>
    /// <remarks>
    ///     <para>
    ///         The order is: an overlay's pinned EV, then an authored exposure, then the lens — and a
    ///         measured exposure still beats them all, because a metered frame's shader reads the
    ///         buffer and ignores this uniform entirely. That last fact is why the look profile pins
    ///         the <em>fixed</em> exposure only: metering it is <c>meterMinimumEv</c> and
    ///         <c>meterMaximumEv</c>'s job, on the meter.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The pin blends in stops, not in linear exposure.</b> Half way between EV 10 and
    ///         EV 14 is EV 12; the linear midpoint is nearly a stop brighter. So the authored value
    ///         goes to EV, the lerp happens there, and the result comes back — the temperature's
    ///         convert-then-blend rule, in the one other unit whose lerp is wrong raw.
    ///     </para>
    /// </remarks>
    float ExposureFor() {
        var authored = LensExposure && View?.Camera?.Lens is { HasLens: true } lens
            ? Photometry.ExposureFromEv100(lens.Ev100)
            : Exposure;

        return applied.Ev100 is { } pinned
            ? Photometry.ExposureFromEv100(pinned.Over(Photometry.Ev100FromExposure(authored)))
            : authored;
    }
}
