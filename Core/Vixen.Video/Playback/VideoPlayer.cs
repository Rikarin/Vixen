// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Video.Playback;

/// <summary>What a player is doing.</summary>
public enum VideoPlaybackState : byte {
    /// <summary>Not playing, and positioned at the start.</summary>
    Stopped = 0,

    /// <summary>Running.</summary>
    Playing = 1,

    /// <summary>Holding a frame, and will resume where it stopped.</summary>
    Paused = 2,

    /// <summary>The video finished and is not looping.</summary>
    Ended = 3
}

/// <summary>How to set a player up.</summary>
/// <remarks>Every field is a default that is right for a cutscene, which is the ordinary case.</remarks>
public readonly record struct VideoPlayerOptions() {
    /// <summary>How many decoded frames to keep ahead of the clock.</summary>
    /// <remarks>
    ///     <para>
    ///         Four is about seventy milliseconds at 60 fps: enough that a decode which takes twice as
    ///         long as usual — a key frame, a page fault, a scheduler hiccup — is absorbed rather than
    ///         seen, and small enough that the queue costs twelve megabytes at 1080p rather than
    ///         fifty.
    ///     </para>
    ///     <para>
    ///         Raising it does not make playback smoother in the steady state. It only widens the
    ///         hiccup that can be hidden, at a megabyte or three a frame.
    ///     </para>
    /// </remarks>
    public int QueueCapacity { get; init; } = 4;

    /// <summary>Whether the player runs its own decode thread.</summary>
    /// <remarks>
    ///     False and the caller drives <see cref="VideoPlayer.Service" /> itself, which is what a
    ///     browser without threads does and what a test does when it wants the whole thing
    ///     deterministic.
    /// </remarks>
    public bool UseDecodeThread { get; init; } = true;

    /// <summary>Whether the video starts again when it ends.</summary>
    public bool Loop { get; init; }
}

/// <summary>Plays a video: decodes ahead of the clock, and says which frame is current.</summary>
/// <remarks>
///     <para>
///         <b>Three moving parts and one rule.</b> A decoder produces frames, a queue holds the next
///         few, and a clock says what time it is; the rule is that the current frame is the newest
///         one whose timestamp has passed. Everything else here — the thread, the pool, the loop
///         offset, the counters — exists to make that rule survive a decoder that is occasionally
///         slow.
///     </para>
///     <para>
///         <b>Late frames are dropped, never shown late.</b> If the clock has passed three frames
///         since the last update — a stall, a breakpoint, a game running at 20 fps against 60 fps
///         content — the player shows the third and counts two in <see cref="FramesDropped" />.
///         Showing them in sequence would put the picture behind the sound and keep it there, which
///         is the failure mode that never recovers on its own.
///     </para>
///     <para>
///         <b>The decode thread is optional and the pump is public.</b> Exactly as
///         <c>AudioStreamPump</c> is: <see cref="Service" /> does the whole job synchronously, so a
///         single-threaded platform calls it from its own loop and a test drives it frame by frame
///         with no timing at all.
///     </para>
///     <para>
///         <b><see cref="CurrentFrame" /> belongs to the player.</b> It is valid until the next
///         <see cref="Update" />, which is when the frame goes back to the pool. Anything that needs
///         to keep it copies it — see <c>VideoFrame.CopyFrom</c> — and anything that uploads it to a
///         texture does so in the same frame, which is what <c>VideoTexture</c> is for.
///     </para>
/// </remarks>
public sealed class VideoPlayer : IDisposable {
    readonly Lock gate = new();
    readonly VideoFramePool pool;
    readonly Queue<VideoFrame> ready = new();

    VideoFrame? current;
    bool decodeEnded;
    volatile bool disposed;
    TimeSpan lastDecoded;
    TimeSpan loopOffset;
    TimeSpan? pendingSeek;
    volatile bool running;
    Thread? thread;

    /// <summary>Creates a player over a decoder, set up the way a cutscene wants.</summary>
    /// <param name="decoder">What produces the frames. Owned: disposing this disposes it.</param>
    /// <exception cref="ArgumentNullException"><paramref name="decoder" /> is null.</exception>
    /// <remarks>
    ///     An overload rather than a defaulted parameter, because a defaulted struct parameter is
    ///     <c>default(VideoPlayerOptions)</c> — which skips the field initialisers and would hand
    ///     every caller who omitted the argument a queue capacity of zero.
    /// </remarks>
    public VideoPlayer(IVideoStreamDecoder decoder)
        : this(decoder, new VideoPlayerOptions()) { }

    /// <summary>Creates a player over a decoder.</summary>
    /// <param name="decoder">What produces the frames. Owned: disposing this disposes it.</param>
    /// <param name="options">How to set it up.</param>
    /// <exception cref="ArgumentNullException"><paramref name="decoder" /> is null.</exception>
    /// <exception cref="ArgumentException">The queue capacity is not positive.</exception>
    public VideoPlayer(IVideoStreamDecoder decoder, VideoPlayerOptions options) {
        ArgumentNullException.ThrowIfNull(decoder);

        if (options.QueueCapacity <= 0) {
            throw new ArgumentException(
                $"A queue capacity of {options.QueueCapacity} decodes nothing. A zero here is almost "
                + "always `default(VideoPlayerOptions)`, which skips the field initialisers — use "
                + "`new VideoPlayerOptions()` instead.",
                nameof(options)
            );
        }

        Decoder = decoder;
        Options = options;
        Loop = options.Loop;

        // Two more than the queue: the frame being decoded into, and the one on screen.
        pool = new VideoFramePool(options.QueueCapacity + 2);

        if (options.UseDecodeThread) {
            running = true;
            thread = new Thread(DecodeLoop) {
                IsBackground = true,
                Name = "Vixen video decode"
            };

            thread.Start();
        }
    }

    /// <summary>What it was set up with.</summary>
    public VideoPlayerOptions Options { get; }

    /// <summary>The decoder behind it.</summary>
    public IVideoStreamDecoder Decoder { get; }

    /// <summary>The clock deciding which frame is current.</summary>
    /// <remarks>
    ///     Exposed rather than wrapped, because the thing a caller most often wants to do with it is
    ///     hand it a master — <c>player.Clock.Master = () =&gt; audio.Position</c> — and a property
    ///     per clock feature would be four properties that forward.
    /// </remarks>
    public VideoClock Clock { get; } = new();

    /// <summary>What the player is doing.</summary>
    public VideoPlaybackState State { get; private set; } = VideoPlaybackState.Stopped;

    /// <summary>Whether it starts again when it ends.</summary>
    public bool Loop { get; set; }

    /// <summary>The frame that should be on screen, or <see langword="null" /> before the first one.</summary>
    /// <remarks>Valid until the next <see cref="Update" />.</remarks>
    public VideoFrame? CurrentFrame => current;

    /// <summary>Bumped every time <see cref="CurrentFrame" /> becomes a different picture.</summary>
    /// <remarks>
    ///     What an uploader watches, so that a 24 fps video in a 144 fps game is copied to the GPU
    ///     twenty-four times a second rather than a hundred and forty-four. Comparing the frames
    ///     themselves would not do: they are pooled, so the same instance comes round again.
    /// </remarks>
    public uint FrameVersion { get; private set; }

    /// <summary>How many frames have been shown.</summary>
    public long FramesShown { get; private set; }

    /// <summary>How many were decoded, became due, and were skipped because a newer one was also due.</summary>
    public long FramesDropped { get; private set; }

    /// <summary>How many updates found the queue empty with the video still playing.</summary>
    /// <remarks>
    ///     The one number that says whether decoding is keeping up. Zero in the steady state; a
    ///     number that climbs means the decoder is slower than the content, and no amount of queue
    ///     will fix it.
    /// </remarks>
    public long DecodeStalls { get; private set; }

    /// <summary>How many decoded frames are waiting.</summary>
    public int QueuedFrames {
        get {
            lock (gate) {
                return ready.Count;
            }
        }
    }

    /// <summary>Where playback has got to.</summary>
    public TimeSpan Position => Clock.Time;

    /// <summary>How long the video is, or zero if the container did not say.</summary>
    public TimeSpan Duration => Decoder.Duration;

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;
        running = false;
        thread?.Join(TimeSpan.FromSeconds(2));
        thread = null;

        lock (gate) {
            while (ready.TryDequeue(out var frame)) {
                pool.Return(frame);
            }

            if (current is { } shown) {
                pool.Return(shown);
                current = null;
            }

            pool.Clear();
        }

        Decoder.Dispose();
    }

    /// <summary>Starts or resumes playback.</summary>
    public void Play() {
        if (State == VideoPlaybackState.Ended) {
            Seek(TimeSpan.Zero);
        }

        State = VideoPlaybackState.Playing;
        Clock.Start();
    }

    /// <summary>Holds the current frame.</summary>
    /// <remarks>
    ///     The decoder keeps running until the queue is full and then stops, so resuming does not
    ///     start with a stall — which is the whole reason pausing is not just "stop calling Update".
    /// </remarks>
    public void Pause() {
        if (State == VideoPlaybackState.Playing) {
            State = VideoPlaybackState.Paused;
        }

        Clock.Stop();
    }

    /// <summary>Stops and returns to the start.</summary>
    public void Stop() {
        Seek(TimeSpan.Zero);
        State = VideoPlaybackState.Stopped;
        Clock.Stop();
    }

    /// <summary>Moves to a position.</summary>
    /// <param name="position">Where to go. Clamped to the video.</param>
    /// <exception cref="NotSupportedException">The decoder cannot seek.</exception>
    /// <remarks>
    ///     <para>
    ///         The clock moves at once and the picture follows, because the seek itself happens on
    ///         the decode thread — a caller that blocked here would block the frame it was called
    ///         from, for a disk read.
    ///     </para>
    ///     <para>
    ///         Until the first frame at the new position arrives, <see cref="CurrentFrame" /> is
    ///         still the old one. Showing black instead would be a flash on every scrub.
    ///     </para>
    /// </remarks>
    public void Seek(TimeSpan position) {
        if (!Decoder.CanSeek) {
            throw new NotSupportedException("This video cannot seek.");
        }

        var target = position < TimeSpan.Zero ? TimeSpan.Zero : position;

        lock (gate) {
            pendingSeek = target;
        }

        Clock.Reset(target);

        if (State == VideoPlaybackState.Ended) {
            State = VideoPlaybackState.Paused;
        }

        if (State == VideoPlaybackState.Playing) {
            Clock.Start();
        }
    }

    /// <summary>Makes the sound the master clock, and the picture follow it.</summary>
    /// <param name="audio">
    ///     What is actually being played. <see cref="Vixen.Audio.Sources.IAudioSampleProvider.Position" />
    ///     on a streaming provider is frames <em>delivered to the mixer</em>, which is the number this
    ///     wants: it lags the decoder by the whole ring buffer and leads the speaker only by the
    ///     device's own block, so it is the closest thing in the process to where the listener is.
    /// </param>
    /// <param name="offset">
    ///     Added to the audio's position. Positive shows the picture earlier. The default of zero is
    ///     right for a device whose latency the mixer already accounts for; a platform that reports
    ///     otherwise is what this exists for.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="audio" /> is null.</exception>
    /// <remarks>
    ///     <para>
    ///         <b>Slaving to the decoder's position instead would put the picture half a second
    ///         ahead.</b> A decoder is filled ahead of playback by design — that is what the buffer is
    ///         for — so its position is where the sound <em>will</em> be, not where it is. This is the
    ///         single easiest way to get A/V sync visibly wrong while every part of it looks correct.
    ///     </para>
    ///     <para>
    ///         Pass <see langword="null" /> to <see cref="VideoClock.Master" /> to hand the clock
    ///         back to the frame delta — it resumes from wherever the sound had reached rather than
    ///         from where it had got to on its own, so muting mid-play does not jump the picture.
    ///     </para>
    /// </remarks>
    public void FollowAudio(Vixen.Audio.Sources.IAudioSampleProvider audio, TimeSpan offset = default) {
        ArgumentNullException.ThrowIfNull(audio);

        var rate = audio.Format.SampleRate;

        if (rate <= 0) {
            throw new ArgumentException("The audio source reports no sample rate, so it cannot be a clock.", nameof(audio));
        }

        Clock.Master = () => TimeSpan.FromSeconds((double)audio.Position / rate) + offset;
    }

    /// <summary>Advances the clock and chooses the frame to show.</summary>
    /// <param name="delta">How long the last frame was.</param>
    /// <remarks>
    ///     Called once a frame from the game loop — or from <c>VideoSystem</c>, which does it for
    ///     every player in a world.
    /// </remarks>
    public void Update(TimeSpan delta) {
        if (disposed) {
            return;
        }

        if (thread is null) {
            // No decode thread: fill the queue here, before the clock moves, so that a caller
            // driving everything from one thread sees exactly the frames a threaded player would.
            for (var attempt = 0; attempt <= Options.QueueCapacity; attempt++) {
                if (!Service()) {
                    break;
                }
            }
        }

        if (State == VideoPlaybackState.Playing) {
            Clock.Advance(delta);
        }

        var time = Clock.Time;
        var taken = 0;

        lock (gate) {
            while (ready.TryPeek(out var next) && next.Timestamp <= time) {
                var frame = ready.Dequeue();

                if (current is { } previous) {
                    pool.Return(previous);
                }

                current = frame;
                taken++;
            }

            if (taken == 0 && State == VideoPlaybackState.Playing && ready.Count == 0 && !decodeEnded) {
                DecodeStalls++;
            }

            if (State == VideoPlaybackState.Playing && decodeEnded && ready.Count == 0 && !Loop) {
                State = VideoPlaybackState.Ended;
                Clock.Stop();
            }
        }

        if (taken == 0) {
            return;
        }

        FramesShown++;
        FramesDropped += taken - 1;
        FrameVersion++;
    }

    /// <summary>Decodes at most one frame into the queue.</summary>
    /// <returns>
    ///     Whether there is more to do straight away. False means the queue is full, the video has
    ///     ended, or the decoder had nothing — all three of which mean "come back later".
    /// </returns>
    /// <remarks>
    ///     Public because the decode thread is optional. It is safe to call from any one thread at a
    ///     time; calling it from two at once is not, and there is no reason to.
    /// </remarks>
    public bool Service() {
        if (disposed) {
            return false;
        }

        TimeSpan? seek;

        lock (gate) {
            seek = pendingSeek;
            pendingSeek = null;

            if (seek is null && (ready.Count >= Options.QueueCapacity || decodeEnded)) {
                return false;
            }
        }

        if (seek is { } target) {
            Decoder.Seek(target);

            lock (gate) {
                while (ready.TryDequeue(out var stale)) {
                    pool.Return(stale);
                }

                decodeEnded = false;
                loopOffset = TimeSpan.Zero;
                lastDecoded = target;
            }
        }

        VideoFrame frame;

        lock (gate) {
            frame = pool.Rent(Decoder.Format);
        }

        VideoDecodeStatus status;

        try {
            status = Decoder.DecodeNext(frame);
        } catch (InvalidDataException) {
            lock (gate) {
                pool.Return(frame);
                decodeEnded = true;
            }

            throw;
        }

        switch (status) {
            case VideoDecodeStatus.Decoded:
            case VideoDecodeStatus.FormatChanged:
                frame.Timestamp += loopOffset;
                lastDecoded = frame.Timestamp;

                lock (gate) {
                    ready.Enqueue(frame);
                }

                return true;

            case VideoDecodeStatus.NeedMoreData:
                // Not an end and not an error: the decoder is accumulating. Give the frame back and
                // come round again, rather than spinning on a decoder that has said it needs time.
                lock (gate) {
                    pool.Return(frame);
                }

                return false;

            case VideoDecodeStatus.EndOfStream when Loop && Decoder.CanSeek:
                // The stream's timestamps start again at zero and the clock does not, so every frame
                // of the second pass is offset by where the first one ended. Without this, looping
                // would make every frame instantly late and the player would drop the whole video.
                loopOffset = lastDecoded + FrameStep();
                Decoder.Seek(TimeSpan.Zero);

                lock (gate) {
                    pool.Return(frame);
                }

                return true;

            default:
                lock (gate) {
                    pool.Return(frame);
                    decodeEnded = true;
                }

                return false;
        }
    }

    /// <summary>How long a frame lasts, for the loop offset. A guess is better than zero here.</summary>
    TimeSpan FrameStep() {
        var rate = Decoder.Format.FrameRate;

        return rate.IsKnown ? rate.FrameDuration : TimeSpan.FromMilliseconds(1000d / 30);
    }

    void DecodeLoop() {
        while (running) {
            bool more;

            try {
                more = Service();
            } catch (InvalidDataException) {
                // A damaged file stops this video and nothing else. The player reports Ended, which
                // is what the caller would have to handle anyway, and the process keeps its frame.
                return;
            } catch (ObjectDisposedException) {
                return;
            }

            if (!more) {
                Thread.Sleep(2);
            }
        }
    }
}
