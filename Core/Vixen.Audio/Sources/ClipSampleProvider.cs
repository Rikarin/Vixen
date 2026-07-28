// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Audio.Sources;

/// <summary>Plays an <see cref="AudioClip" /> that is already in memory.</summary>
/// <remarks>
///     <para>
///         The conversion from the clip's stored format to float happens here, one block at a time,
///         rather than once at load. Converting at load would triple the memory a 16-bit clip costs
///         for the whole time it is resident, to save a multiply per sample on the frames that are
///         actually playing — and the frames actually playing are a few hundred out of the millions
///         resident.
///     </para>
///     <para>
///         <b>The scale is <c>1 / 32768</c> and not <c>1 / 32767</c>.</b> That maps −32 768 to
///         exactly −1 and 32 767 to just under +1, which is the asymmetry two's complement actually
///         has. Dividing by 32 767 makes the most negative sample clip.
///     </para>
/// </remarks>
public sealed class ClipSampleProvider : IAudioSampleProvider {
    const float Int16Scale = 1f / 32_768f;

    readonly AudioClip clip;
    readonly int channels;
    long position;

    /// <summary>A provider over a clip.</summary>
    /// <param name="clip">The clip. Not copied — it must outlive the voice playing it.</param>
    /// <param name="loop">Whether to wrap round at the end instead of stopping.</param>
    /// <exception cref="ArgumentNullException"><paramref name="clip" /> is null.</exception>
    /// <exception cref="ArgumentException">The clip has no rate or no channels.</exception>
    public ClipSampleProvider(AudioClip clip, bool loop = false) {
        ArgumentNullException.ThrowIfNull(clip);

        if (clip.SampleRate <= 0 || clip.Channels is <= 0 or > AudioFormat.MaxChannels) {
            throw new ArgumentException(
                $"A clip at {clip.SampleRate} Hz over {clip.Channels} channels cannot be played. A "
                + "clip that came out of the content build always has both; one built by hand may "
                + "not.",
                nameof(clip)
            );
        }

        this.clip = clip;
        channels = clip.Channels;
        IsLooping = loop;
        FrameCount = clip.FrameCount;
        Format = new AudioFormat(clip.SampleRate, clip.Channels);
    }

    /// <summary>The clip being played.</summary>
    public AudioClip Clip => clip;

    /// <inheritdoc />
    public AudioFormat Format { get; }

    /// <inheritdoc />
    public long FrameCount { get; }

    /// <inheritdoc />
    public long Position => position;

    /// <inheritdoc />
    public bool IsLooping { get; }

    /// <inheritdoc />
    public int Read(Span<float> destination, int frameCount) {
        if (frameCount <= 0 || FrameCount <= 0) {
            return 0;
        }

        var written = 0;

        while (written < frameCount) {
            if (position >= FrameCount) {
                if (!IsLooping) {
                    break;
                }

                position = 0;
            }

            var run = (int)Math.Min(frameCount - written, FrameCount - position);
            Convert(destination.Slice(written * channels, run * channels), position, run);
            position += run;
            written += run;
        }

        return written;
    }

    /// <inheritdoc />
    public void Seek(long frame) => position = Math.Clamp(frame, 0, FrameCount);

    void Convert(Span<float> destination, long fromFrame, int frames) {
        var start = (int)(fromFrame * channels);
        var count = frames * channels;

        if (clip.Format is AudioSampleFormat.Float32) {
            clip.AsFloat32().Slice(start, count).CopyTo(destination);
            return;
        }

        var samples = clip.AsInt16().Slice(start, count);

        for (var i = 0; i < count; i++) {
            destination[i] = samples[i] * Int16Scale;
        }
    }
}
