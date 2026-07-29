// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.SceneView;

/// <summary>What one pane cost this frame, for the readout in its corner.</summary>
/// <remarks>
///     <para>
///         <b>Written by whatever builds the frame and read by whatever draws the overlay.</b> The
///         counts are the collectors' own — how many entities were drawn, how many triangles and
///         segments went into the buffers — and the frame time is the host's, because a pane has no
///         clock. Neither half is computed here; this is the place the two meet, which is what stops
///         the overlay reaching into a renderer.
///     </para>
///     <para>
///         ⚠ <b>The frame time is smoothed and the counts are not.</b> A number that changes sixty
///         times a second is a number nobody can read, and an average over a fifth of a second is
///         what every profiler's headline figure is. The counts are exact because they are answers to
///         "what is in this scene" rather than measurements — a triangle count that drifted towards
///         its neighbours would be a bug report nobody could reproduce.
///     </para>
/// </remarks>
public sealed class ViewportStats {
    /// <summary>How much of a new frame time is taken, per frame.</summary>
    /// <remarks>
    ///     ⚠ <b>Per frame rather than per second, which makes it frame-rate dependent on purpose.</b>
    ///     The reading settles after roughly thirty frames however fast they arrive, so it is as
    ///     steady at fifteen frames a second as at three hundred — a time constant in seconds would
    ///     make the slow case the jumpy one, which is exactly the case somebody is reading it in.
    /// </remarks>
    public const float Smoothing = 0.03f;

    /// <summary>How many entities were drawn as surfaces.</summary>
    public int Entities { get; set; }

    /// <summary>How many triangles went into the frame's mesh buffer.</summary>
    public int Triangles { get; set; }

    /// <summary>How many line segments went into its line buffers.</summary>
    public int Segments { get; set; }

    /// <summary>How many draw calls the pane issued.</summary>
    public int Draws { get; set; }

    /// <summary>How long a frame takes, smoothed, in milliseconds.</summary>
    public float FrameMilliseconds { get; private set; }

    /// <summary>How many frames a second that is.</summary>
    public float FramesPerSecond => FrameMilliseconds > 0f ? 1000f / FrameMilliseconds : 0f;

    /// <summary>Folds one frame's duration into the average.</summary>
    /// <param name="delta">How long the frame took.</param>
    /// <remarks>
    ///     ⚠ <b>The first sample is taken whole rather than blended towards from zero.</b> Starting at
    ///     zero and easing in shows a frame rate climbing from infinity for the first second of every
    ///     session, which reads as the editor warming up and is an artefact of the filter.
    /// </remarks>
    public void Sample(TimeSpan delta) {
        var milliseconds = (float) delta.TotalMilliseconds;

        if (milliseconds <= 0f) {
            return;
        }

        FrameMilliseconds = FrameMilliseconds <= 0f
            ? milliseconds
            : FrameMilliseconds + ((milliseconds - FrameMilliseconds) * Smoothing);
    }

    /// <summary>Forgets the counts, for a pane that drew nothing.</summary>
    /// <remarks>
    ///     The frame time survives, because the pane still took a frame — a collapsed panel that
    ///     reported zero milliseconds would be the one place the readout lies about the editor being
    ///     fast.
    /// </remarks>
    public void Clear() {
        Entities = 0;
        Triangles = 0;
        Segments = 0;
        Draws = 0;
    }
}
