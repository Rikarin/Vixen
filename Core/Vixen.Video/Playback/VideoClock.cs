// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Video.Playback;

/// <summary>What decides which frame is the current one.</summary>
/// <remarks>
///     <para>
///         <b>Audio is the master clock when there is audio.</b> This is the one design decision in
///         video playback that everybody eventually arrives at, usually after trying the other two.
///         Driving the video from the frame timer and resampling the audio to match makes every
///         speed correction audible — the ear resolves a few cents of pitch and forty milliseconds of
///         timing, and the eye resolves neither. Driving both from the wall clock means correcting
///         both, which is the worst of it. So the sound plays untouched at its own rate, and the
///         picture is chosen to match where the sound has got to.
///     </para>
///     <para>
///         <b>With no audio, the clock is the frame delta.</b> <see cref="Advance" /> is called once
///         a frame with the engine's own <c>GameTime</c>, which means a video in a paused game pauses
///         and a video in a game running at half speed runs at half speed — both of which are what a
///         cutscene should do and neither of which a wall clock would give.
///     </para>
///     <para>
///         <b><see cref="Rate" /> is not a resampler.</b> It scales how fast this clock advances, so
///         a video at half rate shows each frame twice as long. The audio track, if there is one, is
///         unaffected and will drift — which is why anything other than 1 is for a video with no
///         sound, or for a scrub.
///     </para>
/// </remarks>
public sealed class VideoClock {
    Func<TimeSpan>? master;

    /// <summary>Where playback has got to.</summary>
    public TimeSpan Time { get; private set; }

    /// <summary>Whether <see cref="Advance" /> moves it.</summary>
    public bool IsRunning { get; private set; }

    /// <summary>How fast it runs relative to real time. One is normal.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Set to a negative rate.</exception>
    public double Rate {
        get;

        set {
            ArgumentOutOfRangeException.ThrowIfNegative(value);

            field = value;
        }
    } = 1d;

    /// <summary>
    ///     Where the time comes from instead, when something else is keeping it — the audio track.
    /// </summary>
    /// <remarks>
    ///     Set to the audio stream's position and the clock stops integrating deltas and starts
    ///     reporting what the sound card has actually played, which is the only number in the process
    ///     that cannot drift from what the listener is hearing. Set back to <see langword="null" />
    ///     and it resumes from wherever the master left it, rather than from where it had got to on
    ///     its own — so muting a video mid-play does not jump the picture.
    /// </remarks>
    public Func<TimeSpan>? Master {
        get => master;

        set {
            if (value is null && master is not null) {
                Time = master();
            }

            master = value;
        }
    }

    /// <summary>Whether an external clock is driving it.</summary>
    public bool IsSlaved => master is not null;

    /// <summary>Starts it.</summary>
    public void Start() => IsRunning = true;

    /// <summary>Stops it where it is.</summary>
    public void Stop() => IsRunning = false;

    /// <summary>Moves it, and stops it.</summary>
    /// <param name="time">Where to.</param>
    /// <remarks>
    ///     Also correct while slaved: the master will not have moved yet — an audio stream that has
    ///     just been told to seek has not played anything from the new position — so without this the
    ///     first frame after a seek would be chosen against the old time.
    /// </remarks>
    public void Reset(TimeSpan time) {
        Time = time < TimeSpan.Zero ? TimeSpan.Zero : time;
        IsRunning = false;
    }

    /// <summary>Moves the clock on by a frame's worth of time.</summary>
    /// <param name="delta">How long the frame was.</param>
    /// <remarks>
    ///     Reads the master rather than integrating when there is one, so calling this every frame is
    ///     correct whether or not the video has sound and the caller does not have to know which.
    /// </remarks>
    public void Advance(TimeSpan delta) {
        if (master is { } source) {
            Time = source();

            return;
        }

        if (!IsRunning || delta <= TimeSpan.Zero) {
            return;
        }

        Time += Rate == 1d ? delta : delta * Rate;
    }
}
