// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Animation.StateMachine;

/// <summary>How a condition compares a parameter against its threshold.</summary>
public enum AnimationConditionMode {
    /// <summary>The flag is set, or the trigger has been raised.</summary>
    If,

    /// <summary>The flag is not set.</summary>
    IfNot,

    /// <summary>The number is greater than the threshold.</summary>
    Greater,

    /// <summary>The number is less than the threshold.</summary>
    Less,

    /// <summary>The whole number equals the threshold.</summary>
    Equals,

    /// <summary>The whole number does not equal the threshold.</summary>
    NotEqual
}

/// <summary>One test a transition has to pass.</summary>
/// <param name="Parameter">The index of the parameter being tested.</param>
/// <param name="Mode">How it is compared.</param>
/// <param name="Threshold">What it is compared against. Ignored by <c>If</c> and <c>IfNot</c>.</param>
/// <remarks>
///     <para>
///         <b>Greater and Less are strict, and there is no <c>GreaterOrEqual</c>.</b> Unity's set,
///         and for a reason worth keeping: a graph whose transitions fire on exact equality of a
///         float is a graph that works until the value arrives as 0.30000001. Equality is offered
///         for whole numbers only, where it means something.
///     </para>
///     <para>
///         <b>A trigger is not read here.</b> <see cref="IsSatisfied" /> peeks at it, because a
///         transition with three conditions must not consume a trigger when the other two fail. The
///         consumption happens in <see cref="Consume" />, which only the transition that was
///         actually taken calls.
///     </para>
/// </remarks>
public readonly record struct AnimationCondition(
    int Parameter,
    AnimationConditionMode Mode,
    float Threshold = 0f
) {
    /// <summary>Builds a condition against a named parameter, declaring it if it is not already.</summary>
    /// <param name="parameters">The parameter set.</param>
    /// <param name="parameter">The parameter's name.</param>
    /// <param name="mode">How it is compared.</param>
    /// <param name="threshold">What it is compared against.</param>
    /// <returns>The condition.</returns>
    public static AnimationCondition On(
        AnimationParameters parameters,
        string parameter,
        AnimationConditionMode mode,
        float threshold = 0f
    ) {
        ArgumentNullException.ThrowIfNull(parameters);

        var type = mode switch {
            AnimationConditionMode.Greater or AnimationConditionMode.Less => AnimationParameterType.Float,
            AnimationConditionMode.Equals or AnimationConditionMode.NotEqual => AnimationParameterType.Int,
            _ => AnimationParameterType.Bool
        };

        return new(parameters.Declare(parameter, type), mode, threshold);
    }

    /// <summary>Builds a condition on a trigger, declaring it if it is not already.</summary>
    /// <param name="parameters">The parameter set.</param>
    /// <param name="trigger">The trigger's name.</param>
    /// <returns>The condition.</returns>
    public static AnimationCondition OnTrigger(AnimationParameters parameters, string trigger) {
        ArgumentNullException.ThrowIfNull(parameters);
        return new(parameters.Declare(trigger, AnimationParameterType.Trigger), AnimationConditionMode.If);
    }

    /// <summary>Whether the condition holds, without taking anything.</summary>
    /// <param name="parameters">The parameter set.</param>
    /// <returns><see langword="true" /> if it holds.</returns>
    public bool IsSatisfied(AnimationParameters parameters) {
        ArgumentNullException.ThrowIfNull(parameters);

        return Mode switch {
            AnimationConditionMode.If => parameters.GetBool(Parameter),
            AnimationConditionMode.IfNot => !parameters.GetBool(Parameter),
            AnimationConditionMode.Greater => parameters.GetFloat(Parameter) > Threshold,
            AnimationConditionMode.Less => parameters.GetFloat(Parameter) < Threshold,
            AnimationConditionMode.Equals => parameters.GetInt(Parameter) == (int)Threshold,
            AnimationConditionMode.NotEqual => parameters.GetInt(Parameter) != (int)Threshold,
            _ => false
        };
    }

    /// <summary>Takes whatever this condition consumes. Called only by a transition that was taken.</summary>
    /// <param name="parameters">The parameter set.</param>
    public void Consume(AnimationParameters parameters) {
        ArgumentNullException.ThrowIfNull(parameters);

        if (Mode is AnimationConditionMode.If) {
            parameters.ConsumeTrigger(Parameter);
        }
    }
}
