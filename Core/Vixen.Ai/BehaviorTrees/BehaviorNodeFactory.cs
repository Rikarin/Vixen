// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Ai;

/// <summary>Everything a node factory needs to turn one row of a file into an object.</summary>
/// <param name="Type">Its declaration in the schema, which is where the defaults and the field list live.</param>
/// <param name="Layout">The blackboard the tree's keys were compiled into.</param>
/// <param name="Fields">What the file said.</param>
/// <param name="Diagnostics">Where to say what could not be resolved.</param>
/// <remarks>
///     ⚠ <b>A factory reads its fields through here rather than off the dictionary.</b> The dictionary
///     holds what a file happened to say; this applies the declared default when it said nothing,
///     resolves a key <i>name</i> to the index the runtime uses, and reports a name that is not on the
///     blackboard instead of quietly binding key zero.
/// </remarks>
public readonly record struct BehaviorBuildContext(
    BehaviorNodeType Type,
    BlackboardLayout Layout,
    IReadOnlyDictionary<string, string> Fields,
    ICollection<BehaviorTreeDiagnostic> Diagnostics
) {
    /// <summary>A field's text.</summary>
    /// <param name="field">Which field.</param>
    /// <returns>What the file said, or the declared default.</returns>
    public string Text(string field) => BehaviorNodeSchema.Read(Type, Fields, field);

    /// <summary>A field as a number.</summary>
    /// <param name="field">Which field.</param>
    /// <returns>The value.</returns>
    public float Number(string field) => BehaviorNodeSchema.Number(Type, Fields, field);

    /// <summary>A field as a whole number.</summary>
    /// <param name="field">Which field.</param>
    /// <returns>The value.</returns>
    public int Integer(string field) => BehaviorNodeSchema.Integer(Type, Fields, field);

    /// <summary>A field as a switch.</summary>
    /// <param name="field">Which field.</param>
    /// <returns>The value.</returns>
    public bool Toggle(string field) => BehaviorNodeSchema.Toggle(Type, Fields, field);

    /// <summary>A field as one of an enumeration's names.</summary>
    /// <typeparam name="T">The enumeration.</typeparam>
    /// <param name="field">Which field.</param>
    /// <returns>The value, or the first name if the file said something else.</returns>
    public T Choice<T>(string field) where T : struct, Enum => BehaviorNodeSchema.Choice<T>(Type, Fields, field);

    /// <summary>A field as an interned word.</summary>
    /// <param name="field">Which field.</param>
    /// <returns>The symbol.</returns>
    public Symbol Word(string field) => Symbol.Intern(Text(field));

    /// <summary>A field naming a blackboard key, resolved to its index.</summary>
    /// <param name="field">Which field.</param>
    /// <returns>The key, or <see cref="BlackboardKey.Invalid" /> with a diagnostic recorded.</returns>
    public BlackboardKey Key(string field) {
        var name = Text(field);

        if (name.Length == 0) {
            Diagnostics.Add(new(Symbol.Intern(Type.Type), $"'{Type.Label}' needs a key for {field}."));

            return BlackboardKey.Invalid;
        }

        if (Layout.TryGetKey(Symbol.Intern(name), out var key)) {
            return key;
        }

        Diagnostics.Add(new(Symbol.Intern(Type.Type), $"'{name}' is not a key on this tree's blackboard."));

        return BlackboardKey.Invalid;
    }

    /// <summary>Records a problem with this row.</summary>
    /// <param name="message">What is wrong.</param>
    public void Report(string message) => Diagnostics.Add(new(Symbol.Intern(Type.Type), message));
}

/// <summary>A task, and how many bytes of per-agent state it needs.</summary>
/// <param name="Action">The task.</param>
/// <param name="StateSize">Its <c>StateSize</c>, which the registry needs and the interface cannot carry.</param>
/// <remarks>
///     ⚠ The size is here because <see cref="IAgentAction" /> deliberately has no instance member for
///     it — the size is a property of the <i>type</i>, is wanted before an instance exists, and a
///     virtual call per registration to fetch a constant would be the wrong shape. Every shipped task
///     declares a <c>static int StateSize</c>; a factory reads its own and passes it here.
/// </remarks>
public readonly record struct BehaviorTaskBuild(IAgentAction Action, int StateSize = 0);

/// <summary>Builds a decorator a project's own assembly defines.</summary>
/// <param name="context">The row and the lookups.</param>
/// <param name="aborts">What the file said it may interrupt.</param>
/// <returns>The decorator, or null to let the compiler report it as unbuildable.</returns>
public delegate BehaviorDecorator? BehaviorDecoratorFactory(in BehaviorBuildContext context, ObserverAborts aborts);

/// <summary>Builds a service a project's own assembly defines.</summary>
/// <param name="context">The row and the lookups.</param>
/// <returns>The service, or null.</returns>
public delegate BehaviorService? BehaviorServiceFactory(in BehaviorBuildContext context);

/// <summary>Builds a task a project's own assembly defines.</summary>
/// <param name="context">The row and the lookups.</param>
/// <returns>The task and its state size, or null.</returns>
public delegate BehaviorTaskBuild? BehaviorTaskFactory(in BehaviorBuildContext context);
