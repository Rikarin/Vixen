// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Audio.Effects;

/// <summary>A ceiling nothing gets past, without the distortion of a clamp.</summary>
/// <remarks>
///     <para>
///         <b>What this replaces.</b> The master used to end in <c>Math.Clamp(x, -1, 1)</c>. That is
///         a brickwall in the sense that nothing above one comes out, and it is also hard clipping:
///         every sample above the rail becomes a flat top, which is audible as buzz the moment a
///         scene gets busy. The clamp is still there behind this, demoted to what it should always
///         have been — a guard against a NaN or an overshoot nothing caught, not a level control.
///     </para>
///     <para>
///         <b>Look-ahead is what makes it transparent.</b> The signal is delayed by a couple of
///         milliseconds and the gain is decided from what is <em>about to</em> come out. So the gain
///         has already come down by the time a transient arrives, and the step that brings it down
///         happens during the quiet part before it, where nobody can hear it. Without look-ahead a
///         limiter has to choose between reacting late — letting the peak through — and reacting
///         instantly, which is a step in the middle of the waveform and is itself a click.
///     </para>
///     <para>
///         <b>The detector is a sliding-window maximum, and that is what makes the ceiling a
///         guarantee.</b> A one-pole envelope, which is what a compressor uses, only approaches the
///         peak — so a fast enough transient escapes it. Taking the true maximum over exactly the
///         look-ahead window means the gain applied to a sample was computed from a window that
///         contains that sample. The window maximum is kept in a monotonic deque, so it costs
///         amortised constant time per sample rather than a scan.
///     </para>
///     <para>
///         The cost is latency: <see cref="LookAheadSeconds" /> of it, two milliseconds by default.
///         That is a fifth of one 480-frame block and is well below what anybody notices, but it is
///         real and it is why this is a master-bus effect rather than something to put on six buses.
///     </para>
/// </remarks>
public sealed class LimiterEffect : IAudioEffect {
    float[] delay = [];
    float[] windowValues = [];
    long[] windowIndices = [];

    long head;
    long tail;
    long cursor;
    int mask;
    int look;
    int channelCount;
    int sampleRate;
    float gain = 1f;

    /// <summary>The loudest sample allowed out, in decibels. 0 dB is full scale.</summary>
    /// <remarks>
    ///     −0.3 dB rather than 0. A sample at exactly full scale is fine as a sample and is not fine
    ///     after a lossy codec or a resampler, both of which reconstruct a waveform that overshoots
    ///     between the samples it was given. Leaving a little room is what every mastering engineer
    ///     does and costs nothing audible.
    /// </remarks>
    public float CeilingDb { get; set; } = -0.3f;

    /// <summary>How far ahead it looks, and therefore how much latency it adds.</summary>
    /// <remarks>Fixed when <see cref="Prepare" /> runs; changing it afterwards does nothing until the next one.</remarks>
    public float LookAheadSeconds { get; init; } = 0.002f;

    /// <summary>How fast it lets go once the loud part has passed.</summary>
    /// <remarks>
    ///     A hundred milliseconds. Shorter makes the limiter audible as pumping on sustained
    ///     material; longer leaves the whole mix quiet after one explosion.
    /// </remarks>
    public float ReleaseSeconds { get; set; } = 0.1f;

    /// <inheritdoc />
    public bool Enabled { get; set; } = true;

    /// <summary>How much it is currently taking off, in decibels. Never positive.</summary>
    public float GainReductionDb { get; private set; }

    /// <summary>How much latency it is adding, in frames.</summary>
    public int LatencyFrames => look;

    /// <inheritdoc />
    public void Prepare(in AudioFormat format, int maxFrames) {
        sampleRate = format.SampleRate;
        channelCount = format.Channels;
        look = Math.Max(1, (int)(MathF.Max(LookAheadSeconds, 0f) * format.SampleRate));

        // The deque holds one entry per sample in the window — which is `look + 1` of them, because
        // it spans both ends — plus the one being pushed. Rounding the ring up to a power of two
        // turns the wrap into a mask.
        var capacity = 1;

        while (capacity <= look + 2) {
            capacity <<= 1;
        }

        mask = capacity - 1;
        windowValues = new float[capacity];
        windowIndices = new long[capacity];
        delay = new float[look * channelCount];
        Reset();
    }

    /// <inheritdoc />
    public void Process(Span<float> buffer, int frameCount, int channels) {
        if (!Enabled || channels != channelCount || delay.Length == 0) {
            return;
        }

        var ceiling = Decibels.ToLinear(CeilingDb);
        var release = ReleaseSeconds <= 0f
            ? 0f
            : MathF.Exp(-1f / (MathF.Max(ReleaseSeconds, 1e-6f) * sampleRate));

        var lowest = 1f;

        for (var frame = 0; frame < frameCount; frame++) {
            var offset = frame * channels;
            var slot = (int)(cursor % look) * channels;
            var loudest = 0f;

            // The incoming frame goes into the delay line, and what comes out is the frame from
            // `look` samples ago — the one whose gain we are now in a position to have decided,
            // because the window that decides it spans exactly [cursor − look, cursor].
            for (var channel = 0; channel < channels; channel++) {
                var incoming = buffer[offset + channel];
                loudest = MathF.Max(loudest, MathF.Abs(incoming));
                var outgoing = delay[slot + channel];
                delay[slot + channel] = incoming;
                buffer[offset + channel] = outgoing;
            }

            Push(cursor, loudest);
            var peak = windowValues[(int)(head & mask)];
            var target = peak > ceiling ? ceiling / peak : 1f;

            // Instant down, smoothed up. The step down is safe precisely because of the look-ahead:
            // it lands `window` samples before the peak that caused it reaches the output.
            gain = target < gain ? target : target + ((gain - target) * release);
            lowest = MathF.Min(lowest, gain);

            for (var channel = 0; channel < channels; channel++) {
                buffer[offset + channel] *= gain;
            }

            cursor++;
        }

        GainReductionDb = Decibels.FromLinear(lowest);
    }

    /// <inheritdoc />
    public void Reset() {
        Array.Clear(delay);
        Array.Clear(windowValues);
        Array.Clear(windowIndices);
        head = 0;
        tail = 0;
        cursor = 0;
        gain = 1f;
        GainReductionDb = 0f;
    }

    /// <summary>Adds a sample to the sliding maximum and drops what it has made irrelevant.</summary>
    /// <remarks>
    ///     The monotonic deque: anything already in it that is no larger than the arriving value can
    ///     never be the maximum again, because the new value is both bigger and lives longer. So the
    ///     tail is popped until the deque is decreasing, and the front is the maximum. Each sample is
    ///     pushed once and popped at most once.
    /// </remarks>
    void Push(long index, float value) {
        while (tail > head && windowValues[(int)((tail - 1) & mask)] <= value) {
            tail--;
        }

        windowValues[(int)(tail & mask)] = value;
        windowIndices[(int)(tail & mask)] = index;
        tail++;

        // The window is inclusive at both ends: the sample about to be output is at `index - look`
        // and has to still be in it, or the gain would have been decided without seeing it.
        while (windowIndices[(int)(head & mask)] < index - look) {
            head++;
        }
    }
}
