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

        // Refused rather than ignored. A built-in is overwritten by the engine every frame, so a
        // caller setting one would see it revert and have nothing to go on; saying no here is the
        // difference between a bug found in a minute and one found in an afternoon.
        if (sheet[parameter].Builtin is not AudioBuiltinParameter.None) {
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

            if (sheet.HasBuiltins) {
                Observe(sheet, voice, target);
            }

            for (var i = 0; i < sheet.Count; i++) {
                sheet.Seek(i, ref current[i], target[i], deltaSeconds);
            }

            Apply(voice, sheet.Evaluate(current));
        }
    }

    /// <summary>Writes what the spatialiser already worked out into the built-in parameters' targets.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Targets and not values, so seeking still applies.</b> A distance parameter with a
    ///         seek time is a filter that lags the geometry, which is occasionally what somebody
    ///         wants; one with no seek time — the default — arrives immediately and behaves exactly as
    ///         if the engine had written the value.
    ///     </para>
    ///     <para>
    ///         Read from <c>Voice.LastSpatial</c>, which the audio thread wrote, on the game thread.
    ///         The same documented race as <c>Voice.Audibility</c>: every term is a float, and a curve
    ///         evaluated against last block's geometry is ten milliseconds stale.
    ///     </para>
    ///     <para>
    ///         <b>A voice that is not spatial has no geometry</b>, so its built-ins sit at zero rather
    ///         than at whatever the last spatial sound in that slot left behind. Distance zero is "on
    ///         top of the listener", which is what a sound in the room is.
    ///     </para>
    /// </remarks>
    static void Observe(AudioParameterSheet sheet, Voice voice, Span<float> targets) {
        var spatial = voice.IsSpatial ? voice.LastSpatial : default;

        for (var i = 0; i < sheet.Count; i++) {
            var builtin = sheet[i].Builtin;

            if (builtin is AudioBuiltinParameter.None) {
                continue;
            }

            var value = builtin switch {
                AudioBuiltinParameter.Distance => spatial.Distance,
                AudioBuiltinParameter.Direction => spatial.Azimuth,
                AudioBuiltinParameter.Elevation => spatial.Elevation,

                // Off the voice rather than out of the spatial result: this one is a raycast the game
                // thread made, not something the audio thread worked out while mixing.
                AudioBuiltinParameter.Occlusion => voice.Occlusion,
                _ => spatial.SourceSpeed
            };

            targets[i] = Math.Clamp(value, sheet[i].Minimum, sheet[i].Maximum);
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
