// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Effects;
using Vixen.Audio.Mixing;

namespace Vixen.Audio.Parameters;

/// <summary>Every voice's parameter values, as one flat table the engine owns.</summary>
/// <remarks>
///     <para>
///         <b>A table and not a field on <c>Voice</c>.</b> Parameters are a game-thread idea: they are
///         set from gameplay, stepped once a frame, and what the audio thread sees is the four floats
///         they resolve to. Keeping them here means the render path holds nothing it does not read,
///         and it means the whole of a parameter's machinery — names, curves, seeking — can be
///         touched without a thought about which thread is in it.
///     </para>
///     <para>
///         <b>Sized once, for the whole pool.</b> Voice capacity times
///         <see cref="AudioParameterSheet.MaxParameters" /> floats, twice over, which for sixty-four
///         voices is four kilobytes. Attaching a sheet to a voice allocates nothing, which matters
///         because it happens on every play of every event that has parameters.
///     </para>
///     <para>
///         <b>Generations, because slots are reused.</b> A sheet is attached to a use of a slot, not
///         to the slot; without the generation, a footstep starting in the slot a submerged voice just
///         vacated would inherit its low-pass.
///     </para>
/// </remarks>
sealed class VoiceParameters {
    readonly AudioParameterSheet?[] sheets;
    readonly int[] generations;
    readonly float[] values;
    readonly float[] targets;

    public VoiceParameters(int voiceCapacity) {
        sheets = new AudioParameterSheet?[voiceCapacity];
        generations = new int[voiceCapacity];
        values = new float[voiceCapacity * AudioParameterSheet.MaxParameters];
        targets = new float[voiceCapacity * AudioParameterSheet.MaxParameters];
    }

    /// <summary>The sheet a use of a slot is running, if it is still that use.</summary>
    public AudioParameterSheet? SheetOf(int index, int generation) =>
        generations[index] == generation ? sheets[index] : null;

    /// <summary>Gives a voice a sheet, with every parameter at its default.</summary>
    public void Attach(int index, int generation, AudioParameterSheet sheet) {
        sheets[index] = sheet;
        generations[index] = generation;
        var span = Slice(index);
        sheet.CopyDefaultsTo(span);
        span.CopyTo(Slice(targets, index));
    }

    /// <summary>Takes a sheet away and leaves the voice unautomated.</summary>
    public void Detach(int index) {
        sheets[index] = null;
        Slice(index).Clear();
        Slice(targets, index).Clear();
    }

    /// <summary>Points a parameter at a new value. It gets there at the rate the parameter asked for.</summary>
    public bool SetTarget(int index, int generation, int parameter, float value) {
        var sheet = SheetOf(index, generation);

        if (sheet is null || (uint)parameter >= (uint)sheet.Count) {
            return false;
        }

        var clamped = Math.Clamp(value, sheet[parameter].Minimum, sheet[parameter].Maximum);
        targets[(index * AudioParameterSheet.MaxParameters) + parameter] = clamped;
        return true;
    }

    /// <summary>Where a parameter currently is, which is not always where it was pointed.</summary>
    public float ValueOf(int index, int generation, int parameter) {
        var sheet = SheetOf(index, generation);

        return sheet is not null && (uint)parameter < (uint)sheet.Count
            ? values[(index * AudioParameterSheet.MaxParameters) + parameter]
            : 0f;
    }

    /// <summary>Steps every automated voice and writes what its curves worked out onto it.</summary>
    /// <param name="voices">The pool.</param>
    /// <param name="deltaSeconds">How much game time has passed.</param>
    /// <remarks>
    ///     Once a frame, on the game thread. A voice whose generation has moved on has been reused, so
    ///     its sheet is dropped here rather than at the moment it ended — nothing runs at that moment,
    ///     and the audio thread must not be the one to let go of a managed reference.
    /// </remarks>
    public void Step(Voice[] voices, float deltaSeconds) {
        for (var index = 0; index < sheets.Length; index++) {
            var sheet = sheets[index];

            if (sheet is null) {
                continue;
            }

            var voice = voices[index];

            if (voice.Generation != generations[index]) {
                Detach(index);
                continue;
            }

            var current = Slice(index);
            var target = Slice(targets, index);

            for (var i = 0; i < sheet.Count; i++) {
                sheet.Seek(i, ref current[i], target[i], deltaSeconds);
            }

            Apply(voice, sheet.Evaluate(current));
        }
    }

    /// <summary>Turns what the curves asked for into the four numbers a voice reads.</summary>
    internal static void Apply(Voice voice, in AudioParameterResult result) {
        voice.ParameterGain = result.GainDb == 0f ? 1f : Decibels.ToLinear(result.GainDb);

        voice.ParameterPitch = result.PitchSemitones == 0f
            ? 1f
            : MathF.Pow(2f, result.PitchSemitones / 12f);

        voice.ParameterLowPassHz = result.LowPassHz;
        voice.ParameterHighPassHz = result.HighPassHz;
    }

    Span<float> Slice(int index) => Slice(values, index);

    static Span<float> Slice(float[] table, int index) =>
        table.AsSpan(index * AudioParameterSheet.MaxParameters, AudioParameterSheet.MaxParameters);
}
