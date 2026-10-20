// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Editor.SceneView;
using Vixen.Engine.Diagnostics;
using Vixen.Ui;

namespace Vixen.Editor.App;

/// <summary>A play session's overlay panels, laid out against the focused pane (#1264).</summary>
/// <remarks>
///     <para>
///         <b>The decision, made on Jiu's behalf: option (3).</b> The same
///         <c>IDiagnosticOverlay.Draw</c> data a game's HUD is drawn from — the physics overlay's
///         "bodies awake/all", the frame statistics — run through <c>DiagnosticOverlays.Draw</c> once a
///         frame against the focused pane's size, and stroked by <see cref="PlayOverlayLayer" /> through
///         the interface's vector painter. One implementation per overlay, as in a game; no new render
///         pass and no presenter work.
///     </para>
///     <para>
///         ⚠ <b>Into an accumulator of its own and never into the session's.</b>
///         <c>EditorPlaySession.Debug</c> is the world-line accumulator every pane draws, so a panel
///         written there would land in every pane at one pane's coordinates — and its screen half is
///         what no pane can place anyway. The panel's text is screen segments too:
///         <c>OverlaySurface.Text</c> expands glyphs into strokes through <c>DebugFont</c>, so the
///         whole panel — background, frame and rows — is segments, and segments are what this paints.
///     </para>
///     <para>
///         ⚠ <b>The focused pane only.</b> Four panes repeating one panel is noise, and the focused
///         pane is the one the person is reading. Laid out in the pane's <i>render</i> pixels, which
///         is what <c>DiagnosticOverlays.Draw</c> asks for and what a game passes, and scaled back
///         into layout units where it is painted — <see cref="MarqueeOverlay" />'s conversion.
///     </para>
/// </remarks>
sealed class PaneOverlays {
    /// <summary>Where the overlays draw; the session's own accumulator is never handed to them here.</summary>
    readonly DebugDraw draw = new();

    readonly List<DebugScreenLine> segments = [];

    /// <summary>The layer over each pane of the current arrangement that paints them.</summary>
    readonly List<PlayOverlayLayer> layers = [];

    /// <summary>The pane the segments were laid out against, or <see langword="null" /> for none.</summary>
    public SceneViewport? Pane { get; private set; }

    /// <summary>This frame's panel segments, in <see cref="Pane" />'s render pixels.</summary>
    public IReadOnlyList<DebugScreenLine> Segments => segments;

    /// <summary>Puts a layer over a pane, which paints the panel while that pane is the focused one.</summary>
    /// <param name="pane">A pane of the current arrangement.</param>
    /// <remarks>Every pane gets one; which of them paints is decided per frame by <see cref="Pane" />.</remarks>
    public void Attach(SceneViewport pane) {
        ArgumentNullException.ThrowIfNull(pane);

        var layer = pane.Control.Overlay.Add<PlayOverlayLayer>();

        layer.Owner = pane;
        layer.Source = this;
        layers.Add(layer);
    }

    /// <summary>Drops the layers of the previous arrangement, whose panes a rearrangement replaced.</summary>
    public void Forget() => layers.Clear();

    /// <summary>How many overlay segments the layer over a pane stroked on the last frame.</summary>
    /// <param name="pane">The pane.</param>
    /// <returns>The count, zero for a pane with no layer.</returns>
    public int Painted(SceneViewport pane) => Layer(pane)?.Drawn ?? 0;

    /// <summary>Every endpoint the layer over a pane stroked on the last frame, in document space.</summary>
    /// <param name="pane">The pane.</param>
    /// <returns>The points, empty for a pane with no layer.</returns>
    public IReadOnlyList<Vector2> PaintedPoints(SceneViewport pane) => Layer(pane)?.Points ?? [];

    PlayOverlayLayer? Layer(SceneViewport pane) => layers.FirstOrDefault(layer => ReferenceEquals(layer.Owner, pane));

    /// <summary>Lays the enabled overlays out against the focused pane, or clears them.</summary>
    /// <param name="play">The play session, whose overlay registry is read.</param>
    /// <param name="focused">The focused pane, if there is one.</param>
    /// <remarks>
    ///     After the session's step, so the numbers are this frame's — the order a game's
    ///     <c>PreRender</c> overlay system gives. Every frame rather than only while playing, so the
    ///     frame after Stop empties the panel instead of leaving the last session's on screen.
    /// </remarks>
    public void Update(EditorPlaySession play, SceneViewport? focused) {
        ArgumentNullException.ThrowIfNull(play);

        segments.Clear();
        Pane = null;

        if (!play.IsPlaying || focused is null || play.Controller.Loop is not { } loop) {
            return;
        }

        // ⚠ The pane's layout box at its render scale rather than `RenderWidth`, which is the size of
        // the target last rendered and is zero until a renderer has drawn the pane once — a headless
        // session never does. The layer divides by the same scale, so the two cannot disagree.
        var scale = focused.Control.RenderScale <= 0f ? 1f : focused.Control.RenderScale;
        var bounds = focused.Control.Bounds;
        var size = new Vector2(bounds.Width * scale, bounds.Height * scale);

        draw.Clear();
        play.Overlays.Draw(draw, size, loop.Time);

        foreach (var segment in draw.ScreenLines) {
            segments.Add(segment);
        }

        Pane = focused;
    }
}

/// <summary>Strokes <see cref="PaneOverlays" />' segments over its pane, when its pane is the one they are for.</summary>
/// <remarks>
///     <para>
///         <b><see cref="MarqueeOverlay" />'s shape</b>: an element in the pane's
///         <c>Viewport.Overlay</c>, drawing in the pane's coordinates from its own
///         <see cref="UiElement.OnDraw" />, taking no pointer events because it has no box to take
///         them in.
///     </para>
///     <para>
///         ⚠ <b>Retained drawing (#671) needs nothing from it.</b> An element whose type overrides
///         <see cref="UiElement.OnDraw" /> is never replayed, so a panel whose numbers change every
///         frame is emitted fresh every frame, and the Debug build's replay verifier has nothing of
///         this element's to compare.
///     </para>
///     <para>
///         One stroke per colour rather than one per segment: a counts panel is a few hundred
///         segments in three or four colours, and a draw command each would be a few hundred commands
///         where four do.
///     </para>
/// </remarks>
sealed class PlayOverlayLayer : UiElement {
    readonly Dictionary<Color4, PathBuilder> paths = [];
    readonly List<Vector2> points = [];

    /// <inheritdoc />
    protected override string TagName => "play-overlay";

    /// <summary>The pane this layer is over.</summary>
    public SceneViewport? Owner { get; set; }

    /// <summary>The laid-out panel, shared by every pane's layer.</summary>
    public PaneOverlays? Source { get; set; }

    /// <summary>How many segments the last draw stroked.</summary>
    public int Drawn { get; private set; }



    /// <summary>Every endpoint the last draw stroked, in document space.</summary>
    public IReadOnlyList<Vector2> Points => points;

    /// <inheritdoc />
    protected override void OnDraw(DrawContext context) {
        base.OnDraw(context);

        Drawn = 0;
        points.Clear();

        if (Owner is not { } owner || Source is not { } source || !ReferenceEquals(source.Pane, owner) || source.Segments.Count == 0) {
            return;
        }

        // Render pixels back into layout units, as the band does: the panel was laid out against the
        // render target, and everything in a draw list is device-independent.
        var scale = owner.Control.RenderScale <= 0f ? 1f : owner.Control.RenderScale;
        var bounds = owner.Control.Bounds;
        var origin = new Vector2(bounds.X, bounds.Y);

        foreach (var path in paths.Values) {
            path.Clear();
        }

        foreach (var segment in source.Segments) {
            if (!paths.TryGetValue(segment.Colour, out var path)) {
                paths[segment.Colour] = path = new PathBuilder();
            }

            var from = origin + (segment.From / scale);
            var to = origin + (segment.To / scale);

            path.MoveTo(from).LineTo(to);
            points.Add(from);
            points.Add(to);
        }

        foreach (var (colour, path) in paths) {
            context.Stroke(path, colour, 1f, cap: LineCap.Square);
        }

        Drawn = source.Segments.Count;
    }
}
