// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;

namespace Vixen.Animation;

/// <summary>What a parameter holds.</summary>
public enum AnimationParameterType {
    /// <summary>A number. What blend trees read and what most conditions compare.</summary>
    Float,

    /// <summary>A whole number, for a discrete choice a float would blur.</summary>
    Int,

    /// <summary>A flag that stays as it is set.</summary>
    Bool,

    /// <summary>
    ///     A flag that is cleared by the transition that consumed it, or by the end of the frame.
    /// </summary>
    Trigger
}

/// <summary>
///     The values a graph is driven by: what game code writes and what conditions and blend trees
///     read.
/// </summary>
/// <remarks>
///     <para>
///         <b>Named to set up, indexed to use.</b> Game code says <c>parameters.SetFloat("Speed",
///         v)</c> once per frame and the string lookup does not matter; a state machine evaluating
///         forty conditions per layer per frame cannot afford one, so a transition holds the index
///         it resolved at build time. Both surfaces are here, and the indexed one is what the graph
///         uses.
///     </para>
///     <para>
///         <b>Triggers are consumed, not cleared on a timer.</b> A trigger set by an input handler
///         has to survive until the state machine next looks at it, and has to stop existing the
///         moment a transition takes it — otherwise one button press fires the same transition again
///         the next time the graph passes through that state. <see cref="ConsumeTrigger" /> is what
///         a taken transition calls, and <see cref="ClearTriggers" /> at the end of an update is
///         what stops an unconsumed one leaking into the next frame.
///     </para>
///     <para>
///         Setting a parameter that does not exist is ignored rather than throwing. A graph is
///         content: it gets re-authored, and the game code that drove a parameter it no longer has
///         is a thing to notice in an editor, not a crash in a build.
///     </para>
/// </remarks>
public sealed class AnimationParameters {
    readonly Dictionary<string, int> byName = new(StringComparer.Ordinal);
    readonly List<string> names = [];
    readonly List<AnimationParameterType> types = [];
    readonly List<Value> values = [];

    /// <summary>How many parameters are declared.</summary>
    public int Count => values.Count;

    /// <summary>Declares a parameter, or returns the index of one already declared.</summary>
    /// <param name="name">Its name.</param>
    /// <param name="type">What it holds.</param>
    /// <returns>Its index.</returns>
    /// <remarks>
    ///     Re-declaring with a different type keeps the first declaration. A graph and the code
    ///     driving it disagreeing about whether <c>Speed</c> is a float or an int is a bug, and the
    ///     one that can be found is the one where the value stops changing — not the one where the
    ///     type flips depending on which ran first.
    /// </remarks>
    public int Declare(string name, AnimationParameterType type) {
        if (byName.TryGetValue(name, out var existing)) {
            return existing;
        }

        var index = values.Count;
        byName[name] = index;
        names.Add(name);
        types.Add(type);
        values.Add(default);

        return index;
    }

    /// <summary>The index of a parameter, or −1 if there is none by that name.</summary>
    /// <param name="name">Its name.</param>
    /// <returns>Its index, or −1.</returns>
    public int IndexOf(string name) => byName.TryGetValue(name, out var index) ? index : -1;

    /// <summary>What a parameter is called.</summary>
    /// <param name="index">Its index.</param>
    /// <returns>Its name.</returns>
    public string NameOf(int index) => names[index];

    /// <summary>What a parameter holds.</summary>
    /// <param name="index">Its index.</param>
    /// <returns>Its type.</returns>
    public AnimationParameterType TypeOf(int index) => types[index];

    /// <summary>Reads a parameter as a number.</summary>
    /// <param name="index">Its index, or −1 for a parameter that does not exist.</param>
    /// <returns>Its value, or zero.</returns>
    /// <remarks>
    ///     An <see cref="AnimationParameterType.Int" /> reads as its numeric value and a
    ///     <see cref="AnimationParameterType.Bool" /> as 0 or 1, so a condition does not have to
    ///     branch on the type to compare a threshold.
    /// </remarks>
    public float GetFloat(int index) {
        if ((uint)index >= (uint)values.Count) {
            return 0f;
        }

        var value = values[index];
        return types[index] is AnimationParameterType.Float ? value.Float : value.Int;
    }

    /// <summary>Reads a parameter as a whole number.</summary>
    /// <param name="index">Its index, or −1 for a parameter that does not exist.</param>
    /// <returns>Its value, or zero. A float truncates.</returns>
    public int GetInt(int index) {
        if ((uint)index >= (uint)values.Count) {
            return 0;
        }

        var value = values[index];
        return types[index] is AnimationParameterType.Float ? (int)value.Float : value.Int;
    }

    /// <summary>Reads a parameter as a flag.</summary>
    /// <param name="index">Its index, or −1 for a parameter that does not exist.</param>
    /// <returns>Whether it is set.</returns>
    public bool GetBool(int index) => GetInt(index) != 0;

    /// <summary>Reads a parameter as a number, by name.</summary>
    /// <param name="name">Its name.</param>
    /// <returns>Its value, or zero if there is no such parameter.</returns>
    public float GetFloat(string name) => GetFloat(IndexOf(name));

    /// <summary>Reads a parameter as a whole number, by name.</summary>
    /// <param name="name">Its name.</param>
    /// <returns>Its value, or zero if there is no such parameter.</returns>
    public int GetInt(string name) => GetInt(IndexOf(name));

    /// <summary>Reads a parameter as a flag, by name.</summary>
    /// <param name="name">Its name.</param>
    /// <returns>Whether it is set.</returns>
    public bool GetBool(string name) => GetBool(IndexOf(name));

    /// <summary>Writes a number.</summary>
    /// <param name="index">The parameter's index. Out-of-range indices are ignored.</param>
    /// <param name="value">The value.</param>
    public void SetFloat(int index, float value) {
        if ((uint)index >= (uint)values.Count) {
            return;
        }

        values[index] = types[index] is AnimationParameterType.Float
            ? new Value { Float = value }
            : new Value { Int = (int)value };
    }

    /// <summary>Writes a whole number.</summary>
    /// <param name="index">The parameter's index. Out-of-range indices are ignored.</param>
    /// <param name="value">The value.</param>
    public void SetInt(int index, int value) {
        if ((uint)index >= (uint)values.Count) {
            return;
        }

        values[index] = types[index] is AnimationParameterType.Float
            ? new Value { Float = value }
            : new Value { Int = value };
    }

    /// <summary>Writes a flag.</summary>
    /// <param name="index">The parameter's index. Out-of-range indices are ignored.</param>
    /// <param name="value">The value.</param>
    public void SetBool(int index, bool value) => SetInt(index, value ? 1 : 0);

    /// <summary>Writes a number, by name. Declares the parameter if it does not exist.</summary>
    /// <param name="name">The parameter's name.</param>
    /// <param name="value">The value.</param>
    public void SetFloat(string name, float value) =>
        SetFloat(Declare(name, AnimationParameterType.Float), value);

    /// <summary>Writes a whole number, by name. Declares the parameter if it does not exist.</summary>
    /// <param name="name">The parameter's name.</param>
    /// <param name="value">The value.</param>
    public void SetInt(string name, int value) => SetInt(Declare(name, AnimationParameterType.Int), value);

    /// <summary>Writes a flag, by name. Declares the parameter if it does not exist.</summary>
    /// <param name="name">The parameter's name.</param>
    /// <param name="value">The value.</param>
    public void SetBool(string name, bool value) => SetBool(Declare(name, AnimationParameterType.Bool), value);

    /// <summary>Raises a trigger, to be consumed by the next transition that wants it.</summary>
    /// <param name="name">The trigger's name. Declared if it does not exist.</param>
    public void SetTrigger(string name) => SetInt(Declare(name, AnimationParameterType.Trigger), 1);

    /// <summary>Clears a trigger by hand, for a jump that has been cancelled.</summary>
    /// <param name="name">The trigger's name.</param>
    public void ResetTrigger(string name) {
        var index = IndexOf(name);

        if (index >= 0) {
            SetInt(index, 0);
        }
    }

    /// <summary>Takes a trigger, if it is raised.</summary>
    /// <param name="index">The trigger's index.</param>
    /// <returns><see langword="true" /> if it was raised, in which case it is now cleared.</returns>
    public bool ConsumeTrigger(int index) {
        if ((uint)index >= (uint)values.Count || types[index] is not AnimationParameterType.Trigger) {
            return false;
        }

        if (values[index].Int == 0) {
            return false;
        }

        values[index] = default;
        return true;
    }

    /// <summary>Clears every trigger nothing consumed.</summary>
    /// <remarks>
    ///     Called at the end of an update. A trigger that survived the frame it was raised in would
    ///     fire the moment the graph reaches a state that wants it, which for a jump pressed during
    ///     a landing is a second jump nobody asked for.
    /// </remarks>
    public void ClearTriggers() {
        for (var index = 0; index < values.Count; index++) {
            if (types[index] is AnimationParameterType.Trigger) {
                values[index] = default;
            }
        }
    }

    [StructLayout(LayoutKind.Explicit)]
    struct Value {
        [FieldOffset(0)]
        public float Float;

        [FieldOffset(0)]
        public int Int;
    }
}
