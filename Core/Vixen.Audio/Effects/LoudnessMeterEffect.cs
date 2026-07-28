// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Spatial;

namespace Vixen.Audio.Effects;

/// <summary>How loud the mix actually is, by the standard every platform measures against.</summary>
/// <remarks>
///     <para>
///         <b>Loudness is not level.</b> A peak meter says how close the signal is to clipping, which
///         is a question about arithmetic; a loudness meter says how loud it sounds, which is a
///         question about ears. Two mixes that peak at the same place can be six decibels apart to a
///         listener, and it is the second number that Xbox, PlayStation and every streaming platform
///         hold you to. Shipping without one means finding out at certification.
///     </para>
///     <para>
///         <b>ITU-R BS.1770 / EBU R128, which is one algorithm with two names.</b> K-weight each
///         channel — a high-pass and a high shelf, standing in for the head and torso — take the mean
///         square over a window, weight the surrounds up by 1.5 dB, sum, and put it in decibels with
///         an offset that makes a 1 kHz sine at −23 dBFS read −23 LUFS. Every part of it is in the
///         standard and none of it is a choice.
///     </para>
///     <para>
///         <b>The gate is the part everybody gets wrong.</b> Integrated loudness is not the mean of
///         the whole programme: blocks below −70 LUFS are dropped outright, and then blocks more than
///         10 LU below the mean of what is left are dropped too, and the mean is taken again. Without
///         it, a minute of silence at the end of a level halves the reported loudness of the level.
///     </para>
///     <para>
///         <b>It changes nothing.</b> The signal passes through untouched — this is a meter, and a
///         meter that altered what it measured would be a compressor.
///     </para>
///     <para>
///         <b>Sample peak, not true peak.</b> A true-peak meter oversamples by four to catch what the
///         reconstruction filter will do between samples, which is another filter bank per channel for
///         a number that is typically a decibel higher. It is owed for certification proper; the
///         sample peak is what says whether the mix is clipping now.
///     </para>
/// </remarks>
public sealed class LoudnessMeterEffect : IAudioEffect {
    /// <summary>Below this, a block is silence and is not part of the programme at all.</summary>
    const float AbsoluteGateLufs = -70f;

    /// <summary>How far below the ungated mean a block has to be before it is dropped as well.</summary>
    const float RelativeGateLu = 10f;

    /// <summary>What makes a −23 dBFS sine read −23 LUFS.</summary>
    const float Offset = -0.691f;

    // 400 ms blocks overlapping by three quarters, which is what the standard specifies: the
    // momentary window is one block, and the 100 ms hop is what makes the meter move at a readable
    // rate rather than in 400 ms steps.
    const float BlockSeconds = 0.4f;
    const float HopSeconds = 0.1f;

    // Three seconds of them, for the short-term reading.
    const int ShortTermBlocks = 30;

    float[] shelfZ1 = [];
    float[] shelfZ2 = [];
    float[] highZ1 = [];
    float[] highZ2 = [];
    float[] weights = [];
    double[] hopSums = [];
    double[] shortTerm = [];

    BiquadCoefficients shelf = BiquadCoefficients.Identity;
    BiquadCoefficients high = BiquadCoefficients.Identity;

    int channelCount;
    int sampleRate;
    int hopFrames;
    int hopsPerBlock;
    int hopCursor;
    int hopFilled;
    int hopWritten;
    int shortTermWritten;

    double integratedSum;
    long integratedBlocks;
    double gatedSum;
    long gatedBlocks;

    /// <inheritdoc />
    public bool Enabled { get; set; } = true;

    /// <summary>The last 400 milliseconds, in LUFS. Negative infinity when there is nothing yet.</summary>
    public float Momentary { get; private set; } = float.NegativeInfinity;

    /// <summary>The last three seconds, in LUFS.</summary>
    public float ShortTerm { get; private set; } = float.NegativeInfinity;

    /// <summary>Everything since the last <see cref="Reset" />, gated, in LUFS.</summary>
    /// <remarks>
    ///     The number a platform's requirement is written in — <c>−24 LKFS ±2</c> for broadcast in
    ///     North America, <c>−23 LUFS</c> in Europe, and somewhere between <c>−18</c> and <c>−24</c>
    ///     for most console guidance. It only means anything over a representative stretch of play.
    /// </remarks>
    public float Integrated { get; private set; } = float.NegativeInfinity;

    /// <summary>The loudest single sample since the last <see cref="Reset" />.</summary>
    public float SamplePeak { get; private set; }

    /// <summary>How many 400 ms blocks have gone into <see cref="Integrated" /> after gating.</summary>
    public long GatedBlocks => gatedBlocks;

    /// <inheritdoc />
    public void Prepare(in AudioFormat format, int maxFrames) {
        channelCount = format.Channels;
        sampleRate = format.SampleRate;

        shelfZ1 = new float[channelCount];
        shelfZ2 = new float[channelCount];
        highZ1 = new float[channelCount];
        highZ2 = new float[channelCount];
        weights = new float[channelCount];

        var lfe = SpeakerLayout.LowFrequencyChannel(channelCount);
        var angles = SpeakerLayout.Angles(channelCount);

        for (var channel = 0; channel < channelCount; channel++) {
            // The surrounds are weighted up by 1.5 dB and the LFE is not counted at all — both
            // straight out of the standard, and both because a listener does not hear those channels
            // the way they hear the fronts.
            weights[channel] = channel == lfe ? 0f
                : !angles.IsEmpty && MathF.Abs(angles[channel]) > 90f ? 1.41f
                : 1f;
        }

        hopFrames = Math.Max((int)(sampleRate * HopSeconds), 1);
        hopsPerBlock = Math.Max((int)MathF.Round(BlockSeconds / HopSeconds), 1);
        hopSums = new double[hopsPerBlock];
        shortTerm = new double[ShortTermBlocks];

        Design();
        Reset();
    }

    /// <inheritdoc />
    public void Process(Span<float> buffer, int frameCount, int channels) {
        if (!Enabled || channels != channelCount || sampleRate <= 0) {
            return;
        }

        for (var frame = 0; frame < frameCount; frame++) {
            var offset = frame * channels;
            var weighted = 0.0;

            for (var channel = 0; channel < channels; channel++) {
                var value = buffer[offset + channel];
                SamplePeak = MathF.Max(SamplePeak, MathF.Abs(value));

                if (weights[channel] <= 0f) {
                    continue;
                }

                // The head filter first and then the high-pass, which is the order the standard
                // specifies — they do not commute exactly in floating point and the reference
                // implementations all do it this way round.
                var shelved = (shelf.B0 * value) + shelfZ1[channel];
                shelfZ1[channel] = (shelf.B1 * value) - (shelf.A1 * shelved) + shelfZ2[channel];
                shelfZ2[channel] = (shelf.B2 * value) - (shelf.A2 * shelved);

                var passed = (high.B0 * shelved) + highZ1[channel];
                highZ1[channel] = (high.B1 * shelved) - (high.A1 * passed) + highZ2[channel];
                highZ2[channel] = (high.B2 * shelved) - (high.A2 * passed);

                weighted += weights[channel] * passed * passed;
            }

            hopSums[hopCursor] += weighted;

            if (++hopFilled < hopFrames) {
                continue;
            }

            hopFilled = 0;
            hopWritten++;

            // Before the cursor moves, so the block is summed while all four hops are full. Advancing
            // first and summing after includes the empty bucket the cursor has just landed on, which
            // is a quarter of the window missing and a reading about 1.2 LU low — wrong in a way that
            // looks plausible, which is the worst kind.
            if (hopWritten >= hopsPerBlock) {
                Complete();
            }

            hopCursor = (hopCursor + 1) % hopsPerBlock;
            hopSums[hopCursor] = 0.0;
        }
    }

    /// <summary>Closes off a 400 ms block and folds it into every reading.</summary>
    void Complete() {
        var total = 0.0;

        foreach (var sum in hopSums) {
            total += sum;
        }

        var mean = total / (hopFrames * (double)hopsPerBlock);
        var loudness = Loudness(mean);
        Momentary = loudness;

        shortTerm[shortTermWritten % ShortTermBlocks] = mean;
        shortTermWritten++;
        var window = Math.Min(shortTermWritten, ShortTermBlocks);
        var recent = 0.0;

        for (var i = 0; i < window; i++) {
            recent += shortTerm[i];
        }

        ShortTerm = Loudness(recent / window);

        // The absolute gate, applied as the block arrives — below −70 LUFS is not quiet programme,
        // it is the absence of programme, and it never counts towards anything.
        if (loudness <= AbsoluteGateLufs) {
            return;
        }

        integratedSum += mean;
        integratedBlocks++;

        // The relative gate: the threshold is ten below the mean of everything that passed the
        // absolute one, and the answer is the mean of what passes that. Recomputed from the running
        // sums rather than from a kept list of blocks, which is why an hour of play costs no memory.
        var ungated = Loudness(integratedSum / integratedBlocks) - RelativeGateLu;

        if (loudness > ungated) {
            gatedSum += mean;
            gatedBlocks++;
        }

        Integrated = gatedBlocks > 0 ? Loudness(gatedSum / gatedBlocks) : float.NegativeInfinity;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Clears the readings as well as the filters, so this is where a measurement of a level
    ///     starts. An integrated figure that spans a main menu says nothing about the level.
    /// </remarks>
    public void Reset() {
        Array.Clear(shelfZ1);
        Array.Clear(shelfZ2);
        Array.Clear(highZ1);
        Array.Clear(highZ2);
        Array.Clear(hopSums);
        Array.Clear(shortTerm);

        hopCursor = 0;
        hopFilled = 0;
        hopWritten = 0;
        shortTermWritten = 0;
        integratedSum = 0.0;
        integratedBlocks = 0;
        gatedSum = 0.0;
        gatedBlocks = 0;

        Momentary = float.NegativeInfinity;
        ShortTerm = float.NegativeInfinity;
        Integrated = float.NegativeInfinity;
        SamplePeak = 0f;
    }

    static float Loudness(double meanSquare) =>
        meanSquare > 0.0 ? Offset + (float)(10.0 * Math.Log10(meanSquare)) : float.NegativeInfinity;

    /// <summary>Designs the two K-weighting filters for whatever rate the device runs at.</summary>
    /// <remarks>
    ///     <b>Derived, not the table.</b> BS.1770 prints coefficients for 48 kHz and nothing else, and
    ///     a meter that used them at 44.1 would be measuring a different filter — the shelf would sit
    ///     an eighth of an octave low. The analytic forms below are the standard's own filter
    ///     specifications solved for an arbitrary rate, and at 48 kHz they reproduce the printed
    ///     numbers.
    /// </remarks>
    void Design() {
        // The head shelf: +4 dB above about 1.7 kHz.
        const float ShelfHz = 1_681.9744f;
        const float ShelfGainDb = 3.99984f;
        const float ShelfQ = 0.7071752f;

        var k = MathF.Tan(MathF.PI * ShelfHz / sampleRate);
        var vh = MathF.Pow(10f, ShelfGainDb / 20f);
        var vb = MathF.Pow(vh, 0.4996668f);
        var denominator = 1f + (k / ShelfQ) + (k * k);

        shelf = new BiquadCoefficients(
            (vh + (vb * k / ShelfQ) + (k * k)) / denominator,
            2f * ((k * k) - vh) / denominator,
            (vh - (vb * k / ShelfQ) + (k * k)) / denominator,
            2f * ((k * k) - 1f) / denominator,
            (1f - (k / ShelfQ) + (k * k)) / denominator
        );

        // The high-pass: everything below about 38 Hz is not loudness, it is rumble.
        const float HighHz = 38.1354709f;
        const float HighQ = 0.5003270f;

        var hk = MathF.Tan(MathF.PI * HighHz / sampleRate);
        var hd = 1f + (hk / HighQ) + (hk * hk);

        high = new BiquadCoefficients(
            1f,
            -2f,
            1f,
            2f * ((hk * hk) - 1f) / hd,
            (1f - (hk / HighQ) + (hk * hk)) / hd
        );
    }
}
