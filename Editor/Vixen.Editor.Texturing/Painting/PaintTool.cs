// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Terrain;

namespace Vixen.Editor.Texturing.Painting;

/// <summary>What the pointer does over a texture set.</summary>
enum PaintToolMode {
    /// <summary>Nothing. Rows are selected, the preview is panned, and a drag paints nothing.</summary>
    Select = 0,

    /// <summary>A drag lays a stroke into the selected paint layer.</summary>
    Paint = 1
}

/// <summary>
///     The brush an artist has dialled in, and whether the pointer is holding it.
/// </summary>
/// <remarks>
///     <para>
///         <b>The settings half of doc 48 § M9's surface, and it is deliberately not a viewport.</b>
///         <see cref="PaintSession" />'s remarks name the three things a viewport must do — pointer
///         to texels, screen radius to texels through the hit triangle's texel density, and the
///         mirrors — and none of them is here. What is here is the state those three would be
///         driving: a mode, a brush, and a colour, held by the module so that it survives the panel
///         being closed and reopened.
///     </para>
///     <para>
///         ⚠ <b>Every setter clamps, and the reason is that the range is the setting's meaning
///         rather than a validation of it.</b> A flow of 1.4 is not an error to report; it is the
///         same brush as a flow of 1, because <c>PaintStroke</c> clamps it anyway. Clamping at the
///         edge of the model means a slider, a text field and a preset all arrive at the same brush
///         — and the one place a bad number could still do damage, a radius of zero, is refused by
///         the floor rather than by an exception out of a control's changed event.
///     </para>
///     <para>
///         ⚠ <b>Radius is in texels of the atlas and not in pixels of the screen.</b>
///         <see cref="PaintBrush.Radius" />'s own remarks say why: a brush measured in UV changes
///         size when the set's resolution changes, which is the setting most likely to change after
///         the art is made. A 3D surface converts a screen radius into texels through
///         <c>UvDensity</c>; that conversion is the surface's and this number is what it produces.
///     </para>
/// </remarks>
sealed class PaintTool {
    /// <summary>The smallest brush, in texels. A radius under this cannot cover a texel centre.</summary>
    public const float MinimumRadius = 0.5f;

    /// <summary>The largest, in texels. A stamp this size is a fill, and it is still a stamp.</summary>
    public const float MaximumRadius = 512f;

    /// <summary>What the pointer does.</summary>
    public PaintToolMode Mode { get; set; } = PaintToolMode.Select;

    /// <summary>The brush.</summary>
    public PaintBrush Brush { get; private set; } = PaintBrush.Default;

    /// <summary>What is being painted, packed <c>0xAABBGGRR</c>.</summary>
    public uint Colour { get; set; } = 0xFFFFFFFFu;

    /// <summary>How much the stroke's path lags the pointer, 0…1.</summary>
    /// <remarks>
    ///     On the tool rather than on the brush because it is a filter on the <em>input points</em>
    ///     — doc 48 § D13 — so it belongs to how the artist is dragging and not to what the brush
    ///     deposits. <see cref="PaintSession.Begin" /> takes it separately for that reason.
    /// </remarks>
    public float Smoothing { get; private set; }

    /// <summary>Whether a drag would paint.</summary>
    public bool IsPainting => Mode == PaintToolMode.Paint;

    /// <summary>Swaps between painting and not.</summary>
    /// <returns>The mode it is now in.</returns>
    public PaintToolMode Toggle() {
        Mode = Mode == PaintToolMode.Paint ? PaintToolMode.Select : PaintToolMode.Paint;

        return Mode;
    }

    /// <summary>How far the brush reaches, in texels.</summary>
    /// <param name="texels">The radius. Clamped to <see cref="MinimumRadius" />…<see cref="MaximumRadius" />.</param>
    public void SetRadius(float texels) =>
        Brush = Brush with { Radius = Math.Clamp(Safe(texels, MinimumRadius), MinimumRadius, MaximumRadius) };

    /// <summary>What fraction of the radius is falloff rather than plateau.</summary>
    public void SetFalloff(float fraction) => Brush = Brush with { Falloff = Unit(fraction) };

    /// <summary>How much one stamp deposits.</summary>
    public void SetFlow(float flow) => Brush = Brush with { Flow = Unit(flow) };

    /// <summary>The most the whole stroke may reach on any one texel.</summary>
    public void SetOpacity(float opacity) => Brush = Brush with { Opacity = Unit(opacity) };

    /// <summary>How far apart stamps are, as a fraction of the radius.</summary>
    /// <remarks>
    ///     ⚠ <b>Floored well above zero.</b> Spacing is a divisor in <c>BrushStroke</c>: a spacing of
    ///     zero is a stamp every zero texels, which is a drag that never returns. One hundredth of
    ///     the radius is already finer than any brush an artist can see the steps of.
    /// </remarks>
    public void SetSpacing(float fraction) =>
        Brush = Brush with { Spacing = Math.Clamp(Safe(fraction, 0.01f), 0.01f, 4f) };

    /// <summary>Which falloff curve.</summary>
    public void SetCurve(BrushFalloffKind curve) => Brush = Brush with { Curve = curve };

    /// <summary>How far a stamp may wander off the path, as a fraction of the radius.</summary>
    public void SetPositionJitter(float fraction) => Brush = Brush with { PositionJitter = Unit(fraction) };

    /// <summary>How far a stamp's angle may turn, in <b>degrees</b>, either way.</summary>
    /// <remarks>
    ///     ⚠ <b>Degrees here and radians on the brush, and the conversion belongs on this side.</b>
    ///     <see cref="PaintBrush.AngleJitter" /> is radians because everything downstream of it is;
    ///     a person setting one types 45. Putting the conversion in the control instead would mean
    ///     every future control that sets it has to remember.
    /// </remarks>
    public void SetAngleJitter(float degrees) =>
        Brush = Brush with { AngleJitter = Math.Clamp(Safe(degrees, 0f), 0f, 180f) * (MathF.PI / 180f) };

    /// <summary>How much a stamp's radius may shrink, as a fraction. Shrink only.</summary>
    public void SetSizeJitter(float fraction) => Brush = Brush with { SizeJitter = Unit(fraction) };

    /// <summary>How much the path lags the pointer.</summary>
    /// <remarks>
    ///     Capped below one, not at it: a smoothing of exactly one is a path that never reaches the
    ///     pointer, which is a brush that paints one stamp and then nothing however far the artist
    ///     drags. <c>PaintStroke</c> clamps to the same number for the same reason.
    /// </remarks>
    public void SetSmoothing(float amount) => Smoothing = Math.Clamp(Safe(amount, 0f), 0f, 0.999f);

    /// <summary>The brush's angle jitter as a person reads it.</summary>
    public float AngleJitterDegrees => Brush.AngleJitter * (180f / MathF.PI);

    /// <summary>How the brush reads in one line, for the inspector's heading.</summary>
    public string Describe() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{Brush.Radius:0.#} px · {Brush.Curve} {Brush.Falloff * 100f:0}% · flow {Brush.Flow * 100f:0}% · "
            + $"opacity {Brush.Opacity * 100f:0}%"
        );

    /// <summary>A 0…1 setting, with a not-a-number treated as zero rather than propagated.</summary>
    /// <remarks>
    ///     ⚠ <b>NaN is the case a clamp does not handle</b>: <c>Math.Clamp</c> of a NaN is a NaN, and
    ///     a NaN radius makes every weight a NaN and every texel it touches transparent — a stroke
    ///     that erases. A text field that has been half-typed into is where one comes from.
    /// </remarks>
    static float Unit(float value) => Math.Clamp(Safe(value, 0f), 0f, 1f);

    static float Safe(float value, float fallback) => float.IsNaN(value) ? fallback : value;
}
