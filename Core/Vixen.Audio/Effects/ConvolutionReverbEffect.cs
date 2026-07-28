// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Dsp;
using Vixen.Audio.Sources;

namespace Vixen.Audio.Effects;

/// <summary>The reverb of an actual room, taken from a recording of it.</summary>
/// <remarks>
///     <para>
///         <b>What it is.</b> An impulse response is what a place does to a single click — every
///         reflection, in order, with the exact timing and colour of that room. Convolving a signal
///         with one puts the signal in that room, and it is indistinguishable from having recorded it
///         there, because it is the same arithmetic the air was doing. <see cref="ReverbEffect" />
///         approximates a room with eight comb filters and four allpasses; this does not approximate
///         anything.
///     </para>
///     <para>
///         <b>Why it needs a transform.</b> Convolution done directly is one multiply-accumulate per
///         impulse-response sample per output sample — a one-second response at 48 kHz is 48 000 of
///         them per sample, about two thousand times more work than exists. Multiplying two spectra
///         is convolving two signals, so the transform turns it into one complex multiply per bin.
///     </para>
///     <para>
///         <b>Uniformly partitioned overlap-add.</b> Transforming the whole response at once would
///         mean waiting a whole second before producing anything. Instead it is cut into partitions
///         the length of one block, each transformed once at load; every block, the input's spectrum
///         is pushed into a frequency-domain delay line and multiplied against every partition, and
///         the sum comes back out. Latency is one partition rather than the whole response.
///     </para>
///     <para>
///         <b>It is the most expensive effect here by a wide margin</b> — a second of stereo response
///         is roughly a hundred complex multiply-accumulates of transform size per block. Put it on
///         one aux bus and send to it; putting it on six buses is six of it.
///     </para>
/// </remarks>
public sealed class ConvolutionReverbEffect : IAudioEffect {
    readonly float[][] impulse;

    Fft? fft;
    float[][] partitionsReal = [];
    float[][] partitionsImaginary = [];
    float[][] historyReal = [];
    float[][] historyImaginary = [];
    float[] scratchReal = [];
    float[] scratchImaginary = [];
    float[] accumulatorReal = [];
    float[] accumulatorImaginary = [];
    float[] input = [];
    float[] overlap = [];
    float[] output = [];

    int partition;
    int transform;
    int partitionCount;
    int channelCount;
    int filled;
    int drained;

    /// <summary>An effect that convolves against an impulse response.</summary>
    /// <param name="response">
    ///     The room. Mono applies the same response to every channel; a response whose channel count
    ///     matches the output gives each channel its own, which is what a true stereo response is.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="response" /> is null.</exception>
    /// <exception cref="ArgumentException">The response is empty or has no channels.</exception>
    public ConvolutionReverbEffect(AudioClip response) {
        ArgumentNullException.ThrowIfNull(response);

        if (response.Channels <= 0 || response.FrameCount <= 0) {
            throw new ArgumentException("An impulse response with no frames convolves to silence.", nameof(response));
        }

        // Deinterleaved once, here, because the partitioning wants each channel contiguous and the
        // response never changes after this.
        var provider = new ClipSampleProvider(response);
        var frames = (int)provider.FrameCount;
        var interleaved = new float[frames * response.Channels];
        provider.Read(interleaved, frames);

        impulse = new float[response.Channels][];

        for (var channel = 0; channel < response.Channels; channel++) {
            var samples = new float[frames];

            for (var frame = 0; frame < frames; frame++) {
                samples[frame] = interleaved[(frame * response.Channels) + channel];
            }

            impulse[channel] = samples;
        }

        ResponseFrames = frames;
        ResponseChannels = response.Channels;
        ResponseSampleRate = response.SampleRate;
    }

    /// <summary>How long the response is, in frames.</summary>
    public int ResponseFrames { get; }

    /// <summary>How many channels the response has.</summary>
    public int ResponseChannels { get; }

    /// <summary>What rate the response was recorded at.</summary>
    /// <remarks>
    ///     Not resampled if it disagrees with the device. A response at the wrong rate is a room of
    ///     the wrong size and colour, which is audible; <see cref="IsRateMatched" /> is how a tool
    ///     finds out, and the fix belongs in the content build where it is paid for once.
    /// </remarks>
    public int ResponseSampleRate { get; }

    /// <summary>Whether the response's rate matches the device's.</summary>
    public bool IsRateMatched { get; private set; } = true;

    /// <summary>How many partitions the response was cut into.</summary>
    public int PartitionCount => partitionCount;

    /// <summary>How much latency the partitioning adds, in frames.</summary>
    public int LatencyFrames => partition;

    /// <summary>How much of the convolved signal to add.</summary>
    public float Wet { get; set; } = 0.3f;

    /// <summary>How much of the untouched signal to keep.</summary>
    public float Dry { get; set; } = 1f;

    /// <inheritdoc />
    public bool Enabled { get; set; } = true;

    /// <inheritdoc />
    public void Prepare(in AudioFormat format, int maxFrames) {
        channelCount = format.Channels;
        IsRateMatched = ResponseSampleRate == format.SampleRate;

        // The partition is a power of two at least as long as a block, and the transform is twice
        // that — the extra half is where the tail of each partition's convolution lands, which is
        // what overlap-add adds back.
        partition = Fft.NextSize(Math.Max(maxFrames, 64));
        transform = partition * 2;
        fft = new Fft(transform);
        partitionCount = ((ResponseFrames + partition - 1) / partition);

        partitionsReal = new float[ResponseChannels * partitionCount][];
        partitionsImaginary = new float[ResponseChannels * partitionCount][];

        var real = new float[transform];
        var imaginary = new float[transform];

        for (var channel = 0; channel < ResponseChannels; channel++) {
            for (var index = 0; index < partitionCount; index++) {
                Array.Clear(real);
                Array.Clear(imaginary);

                var start = index * partition;
                var length = Math.Min(partition, ResponseFrames - start);
                impulse[channel].AsSpan(start, length).CopyTo(real);

                fft.Forward(real, imaginary);
                partitionsReal[(channel * partitionCount) + index] = [.. real];
                partitionsImaginary[(channel * partitionCount) + index] = [.. imaginary];
            }
        }

        historyReal = new float[channelCount * partitionCount][];
        historyImaginary = new float[channelCount * partitionCount][];

        for (var i = 0; i < historyReal.Length; i++) {
            historyReal[i] = new float[transform];
            historyImaginary[i] = new float[transform];
        }

        scratchReal = new float[transform];
        scratchImaginary = new float[transform];
        accumulatorReal = new float[transform];
        accumulatorImaginary = new float[transform];
        input = new float[partition * channelCount];
        overlap = new float[partition * channelCount];
        output = new float[partition * channelCount];
        filled = 0;
        drained = 0;
    }

    /// <inheritdoc />
    public void Process(Span<float> buffer, int frameCount, int channels) {
        if (!Enabled || channels != channelCount || fft is null) {
            return;
        }

        var wet = Wet;
        var dry = Dry;

        for (var frame = 0; frame < frameCount; frame++) {
            var offset = frame * channels;

            // A block boundary of the effect's own, which need not line up with the caller's: the
            // partition is a power of two and the caller's block need not be.
            if (drained >= filled) {
                Convolve();
                filled = partition;
                drained = 0;
            }

            for (var channel = 0; channel < channels; channel++) {
                var value = buffer[offset + channel];
                input[(drained * channels) + channel] = value;
                buffer[offset + channel] = (value * dry) + (output[(drained * channels) + channel] * wet);
            }

            drained++;
        }
    }

    /// <inheritdoc />
    public void Reset() {
        foreach (var block in historyReal) {
            Array.Clear(block);
        }

        foreach (var block in historyImaginary) {
            Array.Clear(block);
        }

        Array.Clear(overlap);
        Array.Clear(output);
        Array.Clear(input);
        filled = 0;
        drained = 0;
    }

    /// <inheritdoc />
    public bool TrySetProperty(string name, float value) {
        switch (name) {
            case "Wet":
                Wet = value;
                return true;

            case "Dry":
                Dry = value;
                return true;

            default:
                return false;
        }
    }

    /// <summary>Convolves one partition's worth of input, and readies the next block of output.</summary>
    /// <remarks>
    ///     <para>
    ///         The frequency-domain delay line: this block's spectrum goes into slot zero and the
    ///         older ones shift down, so multiplying slot <c>k</c> by impulse partition <c>k</c> and
    ///         summing gives every partition's contribution to <em>this</em> output block. Rotating
    ///         the slot index rather than the arrays would save the shift; the shift is
    ///         <c>partitionCount</c> reference assignments a block, which is nothing beside the
    ///         transforms either side of it.
    ///     </para>
    ///     <para>
    ///         The output of a transform-length convolution is longer than a partition, and the tail
    ///         belongs to the next block. That is what <c>overlap</c> carries.
    ///     </para>
    /// </remarks>
    void Convolve() {
        for (var channel = 0; channel < channelCount; channel++) {
            var slots = channel * partitionCount;

            // Shift the delay line, reusing the oldest block's arrays for the newest so that a
            // steady state allocates nothing.
            var newestReal = historyReal[slots + partitionCount - 1];
            var newestImaginary = historyImaginary[slots + partitionCount - 1];

            for (var index = partitionCount - 1; index > 0; index--) {
                historyReal[slots + index] = historyReal[slots + index - 1];
                historyImaginary[slots + index] = historyImaginary[slots + index - 1];
            }

            historyReal[slots] = newestReal;
            historyImaginary[slots] = newestImaginary;

            Array.Clear(newestReal);
            Array.Clear(newestImaginary);

            for (var frame = 0; frame < filled; frame++) {
                newestReal[frame] = input[(frame * channelCount) + channel];
            }

            fft!.Forward(newestReal, newestImaginary);

            Array.Clear(accumulatorReal);
            Array.Clear(accumulatorImaginary);

            // A mono response is applied to every channel; one that matches the output gives each
            // channel its own.
            var responseChannel = ResponseChannels == channelCount ? channel : 0;
            var responseSlots = responseChannel * partitionCount;

            for (var index = 0; index < partitionCount; index++) {
                var hr = historyReal[slots + index];
                var hi = historyImaginary[slots + index];
                var pr = partitionsReal[responseSlots + index];
                var pi = partitionsImaginary[responseSlots + index];

                for (var bin = 0; bin < transform; bin++) {
                    accumulatorReal[bin] += (hr[bin] * pr[bin]) - (hi[bin] * pi[bin]);
                    accumulatorImaginary[bin] += (hr[bin] * pi[bin]) + (hi[bin] * pr[bin]);
                }
            }

            accumulatorReal.CopyTo(scratchReal, 0);
            accumulatorImaginary.CopyTo(scratchImaginary, 0);
            fft.Inverse(scratchReal, scratchImaginary);

            for (var frame = 0; frame < partition; frame++) {
                var index = (frame * channelCount) + channel;
                output[index] = scratchReal[frame] + overlap[index];
                overlap[index] = scratchReal[partition + frame];
            }
        }

        Array.Clear(input);
    }
}
