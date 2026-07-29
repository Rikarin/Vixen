// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Rendering.Sprites;

/// <summary>What an animation does when it runs off the end of its frames.</summary>
public enum SpriteWrap {
    /// <summary>Holds the last frame forever.</summary>
    /// <remarks>
    ///     ⚠ <b>Holds rather than stops</b>, because this type answers "which frame at time t" and has
    ///     nowhere to put "and it is over". Whoever is playing it knows the <see cref="SpriteAnimation.Duration" />
    ///     and can decide; a sampler that returned -1 past the end would make every caller handle a
    ///     case that only exists for one of the three wraps.
    /// </remarks>
    Once,

    /// <summary>Starts again from the first frame.</summary>
    Loop,

    /// <summary>Runs to the end and back, without repeating either end.</summary>
    PingPong
}

/// <summary>
///     A sequence of a sheet's frames and the rate they are played at.
/// </summary>
/// <remarks>
///     <para>
///         <b>Indices into a sheet, not sprites.</b> An animation is a playlist and the sheet is the
///         library — so two animations sharing frames share integers, a re-cut sheet does not
///         invalidate every clip that draws from it, and the whole thing serialises as a list of
///         numbers.
///     </para>
///     <para>
///         ⚠ <b>Sampled, never stepped.</b> <see cref="FrameAt" /> is a pure function of a time, so
///         nothing here holds a playhead: rewinding is passing a smaller number, and two things
///         playing the same clip out of step are two numbers rather than two copies. A stepped
///         animation drifts with the frame rate it was stepped at, and the drift is the kind that
///         only shows up on somebody else's machine.
///     </para>
/// </remarks>
[DataContract("SpriteAnimation")]
public sealed record SpriteAnimation {
    /// <summary>What the clip is called.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Which of the sheet's sprites it plays, in order.</summary>
    public int[] Frames { get; init; } = [];

    /// <summary>How many frames a second.</summary>
    public float FrameRate { get; init; } = 12f;

    /// <summary>What happens after the last frame.</summary>
    public SpriteWrap Wrap { get; init; } = SpriteWrap.Loop;

    /// <summary>How long one pass through the frames takes, in seconds.</summary>
    /// <remarks>
    ///     One pass, so a <see cref="SpriteWrap.PingPong" /> clip's round trip is very nearly twice
    ///     this — twice it less the two frames the turn-arounds do not repeat.
    /// </remarks>
    public float Duration => FrameRate > 0f ? Frames.Length / FrameRate : 0f;

    /// <summary>Which of <see cref="Frames" /> is showing at a time.</summary>
    /// <param name="time">Seconds since the clip started. Negative runs it backwards.</param>
    /// <returns>An index into <see cref="Frames" />, or -1 when there are none.</returns>
    /// <remarks>
    ///     ⚠ <b>Floor, not round.</b> A frame is on screen from its own start until the next one's, so
    ///     the frame at <c>t</c> is the last one that has begun. Rounding would show frame zero for
    ///     half a frame at the start and shorten the last one by the same amount, which reads as a
    ///     stutter at both ends of every clip.
    /// </remarks>
    public int FrameAt(float time) {
        var count = Frames.Length;

        if (count == 0) {
            return -1;
        }

        if (FrameRate <= 0f || count == 1) {
            return 0;
        }

        var step = (int)MathF.Floor(time * FrameRate);

        return Wrap switch {
            SpriteWrap.Once => Math.Clamp(step, 0, count - 1),
            SpriteWrap.PingPong => PingPong(step, count),

            // ⚠ Not `step % count`. C#'s remainder keeps the sign of its left operand, so a negative
            // time — a clip started in the future, a rewind past zero — would index backwards out of
            // the array. The double modulo is what makes the sequence periodic rather than merely
            // repeating for positive times.
            _ => ((step % count) + count) % count
        };
    }

    /// <summary>Which of the sheet's sprites is showing at a time.</summary>
    /// <param name="time">Seconds since the clip started.</param>
    /// <returns>An index into the sheet, or -1 when the clip has no frames.</returns>
    public int SpriteAt(float time) => FrameAt(time) is var frame && frame >= 0 ? Frames[frame] : -1;

    /// <summary>Which of the sheet's sprites is showing, resolved against the sheet it names.</summary>
    /// <param name="sheet">The sheet the frames index.</param>
    /// <param name="time">Seconds since the clip started.</param>
    /// <returns>The sprite, or null when the clip is empty or a frame is out of the sheet's range.</returns>
    /// <remarks>
    ///     ⚠ <b>A frame past the end of the sheet is null rather than a throw.</b> The two are
    ///     separate pieces of content and one of them can be re-cut without the other; a clip left
    ///     pointing at a frame that no longer exists should draw nothing for a moment, not take the
    ///     frame down.
    /// </remarks>
    public Sprite? SpriteAt(SpriteSheet sheet, float time) {
        ArgumentNullException.ThrowIfNull(sheet);

        var index = SpriteAt(time);

        return index >= 0 && index < sheet.Count ? sheet[index] : null;
    }

    /// <summary>The frame of a sequence that runs to the end and back.</summary>
    /// <remarks>
    ///     The period is <c>2n - 2</c> rather than <c>2n</c>, because the first and last frames are
    ///     the turn-arounds and showing either of them twice in a row is the visible stutter a
    ///     ping-pong exists to avoid.
    /// </remarks>
    static int PingPong(int step, int count) {
        var period = (2 * count) - 2;
        var position = ((step % period) + period) % period;

        return position < count ? position : period - position;
    }
}
