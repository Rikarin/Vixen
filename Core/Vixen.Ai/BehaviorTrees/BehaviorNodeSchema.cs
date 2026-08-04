// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core;

namespace Vixen.Ai;

/// <summary>What kind of thing a schema entry describes, and therefore where it may go.</summary>
public enum BehaviorSlot : byte {
    /// <summary>A composite: it takes children.</summary>
    Composite,

    /// <summary>A task: a leaf that runs an action.</summary>
    Task,

    /// <summary>A decorator: attaches to any node and gates it.</summary>
    Decorator,

    /// <summary>A service: attaches to a composite and runs on an interval.</summary>
    Service
}

/// <summary>What a field holds, which is what the inspector draws and the file parses.</summary>
public enum BehaviorFieldKind : byte {
    /// <summary>Free text.</summary>
    Text,

    /// <summary>A number.</summary>
    Number,

    /// <summary>A whole number.</summary>
    Integer,

    /// <summary>A tick.</summary>
    Toggle,

    /// <summary>A blackboard key, picked from the tree's own list.</summary>
    Key,

    /// <summary>An interned word.</summary>
    Word,

    /// <summary>One of a named set — an enum, drawn as a dropdown.</summary>
    Choice
}

/// <summary>One settable thing on a node, decorator or service.</summary>
/// <param name="Name">What the file calls it.</param>
/// <param name="Label">What a person reads.</param>
/// <param name="Kind">What it holds.</param>
/// <param name="Description">What it means, for the inspector's tooltip.</param>
/// <param name="Default">What it is when nobody has said.</param>
/// <param name="Choices">The options, for <see cref="BehaviorFieldKind.Choice" />.</param>
/// <remarks>
///     The row an inspector draws without any per-node editor code, the way doc 34's
///     <c>GoalKindSchema</c> generates a goal's. That is not a saving of typing: an inspector written
///     by hand per node is fifty places for a label and a tooltip to drift from what the node does,
///     and the node library is a list this document expects to grow.
/// </remarks>
public readonly record struct BehaviorField(
    string Name,
    string Label,
    BehaviorFieldKind Kind,
    string Description,
    string Default = "",
    string[]? Choices = null
);

/// <summary>One entry in the node library: what it is called, where it goes, and what it takes.</summary>
/// <param name="Type">The name a file and the search popup use.</param>
/// <param name="Label">What a person reads.</param>
/// <param name="Category">Which group the search popup files it under.</param>
/// <param name="Slot">Where it may go.</param>
/// <param name="Description">What it does, for the popup and the inspector header.</param>
/// <param name="Fields">What it takes.</param>
public sealed record BehaviorNodeType(
    string Type,
    string Label,
    string Category,
    BehaviorSlot Slot,
    string Description,
    BehaviorField[] Fields
) {
    /// <summary>A field of this type, by name.</summary>
    /// <param name="name">Its name.</param>
    /// <returns>The field, or null.</returns>
    public BehaviorField? Field(string name) {
        foreach (var field in Fields) {
            if (string.Equals(field.Name, name, StringComparison.Ordinal)) {
                return field;
            }
        }

        return null;
    }
}

/// <summary>
///     The node library, declared once: what the editor offers, what a file may name, and what each
///     one takes.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>A table rather than reflection over the assembly.</b> ADR-002 rules out discovering
///         types at load, and a declared table is also what lets a node carry a category, a
///         description and a per-field tooltip — none of which a constructor signature has.
///     </para>
///     <para>
///         ⚠ <b>It lives in <c>Vixen.Ai</c> and not in the editor.</b> A game loading a
///         <c>.vxbt</c> at run time needs the same table to turn a type name into a decorator, so
///         putting it in the editor would mean two of them and a way for them to disagree about what
///         <c>Cooldown</c>'s field is called.
///     </para>
///     <para>
///         The tasks in this table are the ones <c>Vixen.Ai</c> ships. A project's own task is
///         registered with <see cref="Add" /> and appears in the popup beside them, which is how the
///         library grows without this file growing.
///     </para>
/// </remarks>
public sealed class BehaviorNodeSchema {
    readonly Dictionary<string, BehaviorNodeType> byType = new(StringComparer.Ordinal);
    readonly List<BehaviorNodeType> ordered = [];

    /// <summary>Creates a schema holding everything <c>Vixen.Ai</c> ships.</summary>
    public BehaviorNodeSchema() {
        foreach (var type in Builtin()) {
            Add(type);
        }
    }

    /// <summary>The shared one, for a caller that has no reason to build its own.</summary>
    public static BehaviorNodeSchema Default { get; } = new();

    /// <summary>Everything in it, in declaration order.</summary>
    public IReadOnlyList<BehaviorNodeType> Types => ordered;

    /// <summary>Adds a type.</summary>
    /// <param name="type">The declaration.</param>
    /// <returns>This schema.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="type" /> is null.</exception>
    /// <exception cref="InvalidOperationException">Something is already called that.</exception>
    public BehaviorNodeSchema Add(BehaviorNodeType type) {
        ArgumentNullException.ThrowIfNull(type);

        if (!byType.TryAdd(type.Type, type)) {
            throw new InvalidOperationException($"'{type.Type}' is already in this schema.");
        }

        ordered.Add(type);

        return this;
    }

    /// <summary>Looks a type up.</summary>
    /// <param name="type">Its name.</param>
    /// <param name="found">Where to put it.</param>
    /// <returns>Whether the schema has it.</returns>
    public bool TryGet(string type, out BehaviorNodeType? found) => byType.TryGetValue(type, out found);

    /// <summary>Everything that may go in one slot.</summary>
    /// <param name="slot">Which slot.</param>
    /// <returns>The types, in declaration order.</returns>
    /// <remarks>
    ///     What the search-to-create popup is filtered by: dropping on a composite's child row offers
    ///     composites and tasks, and dropping on a node's decorator strip offers decorators.
    /// </remarks>
    public IEnumerable<BehaviorNodeType> For(BehaviorSlot slot) => ordered.Where(type => type.Slot == slot);

    /// <summary>Reads a field off a bag, falling back to the declared default.</summary>
    /// <param name="type">The declaration.</param>
    /// <param name="fields">What the file holds.</param>
    /// <param name="name">Which field.</param>
    /// <returns>The text.</returns>
    public static string Read(BehaviorNodeType type, IReadOnlyDictionary<string, string> fields, string name) {
        if (fields is not null && fields.TryGetValue(name, out var value)) {
            return value;
        }

        return type?.Field(name)?.Default ?? string.Empty;
    }

    /// <summary>Reads a number.</summary>
    /// <param name="type">The declaration.</param>
    /// <param name="fields">What the file holds.</param>
    /// <param name="name">Which field.</param>
    /// <returns>The number, or zero.</returns>
    public static float Number(BehaviorNodeType type, IReadOnlyDictionary<string, string> fields, string name) =>
        float.TryParse(Read(type, fields, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0f;

    /// <summary>Reads a whole number.</summary>
    /// <param name="type">The declaration.</param>
    /// <param name="fields">What the file holds.</param>
    /// <param name="name">Which field.</param>
    /// <returns>The number, or zero.</returns>
    public static int Integer(BehaviorNodeType type, IReadOnlyDictionary<string, string> fields, string name) =>
        int.TryParse(Read(type, fields, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;

    /// <summary>Reads a tick.</summary>
    /// <param name="type">The declaration.</param>
    /// <param name="fields">What the file holds.</param>
    /// <param name="name">Which field.</param>
    /// <returns>Whether it is on.</returns>
    public static bool Toggle(BehaviorNodeType type, IReadOnlyDictionary<string, string> fields, string name) =>
        bool.TryParse(Read(type, fields, name), out var value) && value;

    /// <summary>Reads a choice as an enum.</summary>
    /// <typeparam name="T">The enum.</typeparam>
    /// <param name="type">The declaration.</param>
    /// <param name="fields">What the file holds.</param>
    /// <param name="name">Which field.</param>
    /// <returns>The value, or the enum's default.</returns>
    public static T Choice<T>(BehaviorNodeType type, IReadOnlyDictionary<string, string> fields, string name)
        where T : struct, Enum =>
        Enum.TryParse<T>(Read(type, fields, name), out var value) ? value : default;

    static IEnumerable<BehaviorNodeType> Builtin() {
        var aborts = new BehaviorField(
            "Aborts",
            "Aborts",
            BehaviorFieldKind.Choice,
            "What this may interrupt when the key it reads changes. Self tears down its own branch; "
            + "LowerPriority takes over from something after it.",
            nameof(ObserverAborts.None),
            Enum.GetNames<ObserverAborts>()
        );

        var key = new BehaviorField("Key", "Key", BehaviorFieldKind.Key, "Which blackboard key it reads.");

        yield return new(
            "Selector",
            "Selector",
            "Composites",
            BehaviorSlot.Composite,
            "Children left to right until one succeeds. Fails if all of them fail.",
            []
        );

        yield return new(
            "Sequence",
            "Sequence",
            "Composites",
            BehaviorSlot.Composite,
            "Children left to right until one fails. Succeeds if all of them succeed.",
            []
        );

        yield return new(
            "Priority",
            "Priority",
            "Composites",
            BehaviorSlot.Composite,
            "A selector that re-evaluates from child zero every step rather than resuming, so a "
            + "higher-priority child takes over with no observer anywhere.",
            []
        );

        yield return new(
            "RandomSelector",
            "Random selector",
            "Composites",
            BehaviorSlot.Composite,
            "A selector over a shuffled order, weighted per child, from the agent's own stream.",
            []
        );

        yield return new(
            "Parallel",
            "Parallel",
            "Composites",
            BehaviorSlot.Composite,
            "One main task with a background branch beside it. The first child must be a task.",
            [
                new(
                    "FinishMode",
                    "When the main task ends",
                    BehaviorFieldKind.Choice,
                    "Whether the background branch is aborted at once or allowed to finish.",
                    nameof(ParallelFinishMode.Immediate),
                    Enum.GetNames<ParallelFinishMode>()
                )
            ]
        );

        yield return new(
            "Blackboard",
            "Blackboard",
            "Conditions",
            BehaviorSlot.Decorator,
            "A key is set, is not set, or compares to a constant.",
            [
                key,
                new(
                    "Test",
                    "Test",
                    BehaviorFieldKind.Choice,
                    "How to compare it. An unset key fails every comparison rather than reading as zero.",
                    nameof(BlackboardTest.IsSet),
                    Enum.GetNames<BlackboardTest>()
                ),
                new("Value", "Value", BehaviorFieldKind.Number, "What to compare against.", "0"),
                new("Word", "Word", BehaviorFieldKind.Word, "The word to compare a symbol key against."),
                aborts
            ]
        );

        yield return new(
            "CompareEntries",
            "Compare entries",
            "Conditions",
            BehaviorSlot.Decorator,
            "Two keys of the same type, against each other.",
            [
                new("Left", "Left", BehaviorFieldKind.Key, "The key on the left of the comparison."),
                new("Right", "Right", BehaviorFieldKind.Key, "The one on the right."),
                new(
                    "Test",
                    "Test",
                    BehaviorFieldKind.Choice,
                    "How to compare them.",
                    nameof(BlackboardTest.Equal),
                    Enum.GetNames<BlackboardTest>()
                ),
                aborts
            ]
        );

        yield return new(
            "IsAtLocation",
            "Is at location",
            "Conditions",
            BehaviorSlot.Decorator,
            "Two position keys, within an acceptance radius of each other.",
            [
                new("Here", "Here", BehaviorFieldKind.Key, "Where the agent is."),
                new("There", "There", BehaviorFieldKind.Key, "Where it is trying to be."),
                new("Radius", "Acceptance radius", BehaviorFieldKind.Number, "How close counts as arrived.", "1"),
                new(
                    "IgnoreHeight",
                    "Ignore height",
                    BehaviorFieldKind.Toggle,
                    "Whether to compare on the ground plane only.",
                    "true"
                ),
                aborts
            ]
        );

        yield return new(
            "Cone",
            "Cone",
            "Conditions",
            BehaviorSlot.Decorator,
            "A position is inside a cone from another, along a direction.",
            [
                new("Origin", "Origin", BehaviorFieldKind.Key, "Where the cone starts."),
                new("Direction", "Direction", BehaviorFieldKind.Key, "Which way it points."),
                new("Target", "Target", BehaviorFieldKind.Key, "The position to test."),
                new("HalfAngle", "Half angle", BehaviorFieldKind.Number, "Half the opening angle, in degrees.", "45"),
                new(
                    "KeepTesting",
                    "Keep testing",
                    BehaviorFieldKind.Toggle,
                    "Whether it keeps testing while the branch runs, and fails it when the target leaves.",
                    "false"
                ),
                aborts
            ]
        );

        yield return new(
            "Inverter",
            "Inverter",
            "Flow",
            BehaviorSlot.Decorator,
            "Turns the node's result over. Gates nothing on the way in.",
            []
        );

        yield return new(
            "ForceSuccess",
            "Force success",
            "Flow",
            BehaviorSlot.Decorator,
            "Reports success whatever the node did.",
            []
        );

        yield return new(
            "ForceFailure",
            "Force failure",
            "Flow",
            BehaviorSlot.Decorator,
            "Reports failure whatever the node did.",
            []
        );

        yield return new(
            "RandomChance",
            "Random chance",
            "Flow",
            BehaviorSlot.Decorator,
            "Passes with a fixed probability, from the agent's own stream.",
            [new("Probability", "Probability", BehaviorFieldKind.Number, "How often it passes, from 0 to 1.", "0.5")]
        );

        yield return new(
            "Cooldown",
            "Cooldown",
            "Timing",
            BehaviorSlot.Decorator,
            "Refuses entry until a number of seconds has passed since this branch last ended.",
            [new("Seconds", "Seconds", BehaviorFieldKind.Number, "How long.", "1")]
        );

        yield return new(
            "TimeLimit",
            "Time limit",
            "Timing",
            BehaviorSlot.Decorator,
            "Fails the branch once it has been running for too long. Re-tested every step.",
            [new("Seconds", "Seconds", BehaviorFieldKind.Number, "How long it may run.", "5")]
        );

        yield return new(
            "TagCooldown",
            "Tag cooldown",
            "Timing",
            BehaviorSlot.Decorator,
            "Refuses entry while a cooldown shared by name across the tree is running.",
            [
                new("Tag", "Tag", BehaviorFieldKind.Word, "The name of the cooldown."),
                new("Seconds", "Seconds", BehaviorFieldKind.Number, "How long it lasts.", "1")
            ]
        );

        yield return new(
            "SetTagCooldown",
            "Set tag cooldown",
            "Timing",
            BehaviorSlot.Decorator,
            "Starts a named cooldown when this branch finishes.",
            [
                new("Tag", "Tag", BehaviorFieldKind.Word, "The name of the cooldown."),
                new("Seconds", "Seconds", BehaviorFieldKind.Number, "How long it lasts.", "1")
            ]
        );

        yield return new(
            "Loop",
            "Loop",
            "Flow",
            BehaviorSlot.Decorator,
            "Runs the node again: a fixed number of times, or until it fails, with a timeout.",
            [
                new("Times", "Times", BehaviorFieldKind.Integer, "How many, counting the first run. Zero means until failure.", "0"),
                new("Timeout", "Timeout", BehaviorFieldKind.Number, "How long it may keep going. Required when Times is zero.", "5")
            ]
        );

        yield return new(
            "UpdateBlackboard",
            "Update blackboard",
            "Services",
            BehaviorSlot.Service,
            "Runs a registered sensor on an interval and writes what it says into a key.",
            [
                new("Sensor", "Sensor", BehaviorFieldKind.Text, "Which registered sensor to run."),
                key
            ]
        );

        yield return new(
            "Wait",
            "Wait",
            "Tasks",
            BehaviorSlot.Task,
            "Waits a fixed number of seconds, then succeeds.",
            [new("Seconds", "Seconds", BehaviorFieldKind.Number, "How long.", "1")]
        );

        yield return new(
            "WaitBlackboardTime",
            "Wait (from a key)",
            "Tasks",
            BehaviorSlot.Task,
            "Waits for however long a key says.",
            [
                key,
                new("Deviation", "Deviation", BehaviorFieldKind.Number, "How much to jitter it, from the agent's stream.", "0")
            ]
        );

        yield return new(
            "FinishWith",
            "Finish with",
            "Tasks",
            BehaviorSlot.Task,
            "Succeeds or fails at once. The branch terminator.",
            [
                new(
                    "Result",
                    "Result",
                    BehaviorFieldKind.Choice,
                    "What to report.",
                    nameof(ActionStatus.Succeeded),
                    [nameof(ActionStatus.Succeeded), nameof(ActionStatus.Failed)]
                )
            ]
        );

        yield return new(
            "SetBlackboardValue",
            "Set blackboard value",
            "Tasks",
            BehaviorSlot.Task,
            "Writes a constant, or another key, into a key.",
            [
                key,
                new("Value", "Value", BehaviorFieldKind.Number, "The number to write.", "0"),
                new("Word", "Word", BehaviorFieldKind.Word, "The word to write, for a symbol key."),
                new("From", "Copy from", BehaviorFieldKind.Key, "Copy this key instead of writing a constant.")
            ]
        );

        yield return new(
            "ClearBlackboardValue",
            "Clear blackboard value",
            "Tasks",
            BehaviorSlot.Task,
            "Unsets a key.",
            [key]
        );

        yield return new(
            "Log",
            "Log",
            "Tasks",
            BehaviorSlot.Task,
            "Narrates into the debug record, so a headless run can be read afterwards.",
            [new("Message", "Message", BehaviorFieldKind.Word, "What to say.")]
        );

        yield return new(
            "RunSubtree",
            "Run subtree",
            "Tasks",
            BehaviorSlot.Task,
            "Runs another tree, spliced in at compile time so priority survives the boundary.",
            [new("Tree", "Tree", BehaviorFieldKind.Text, "Which tree, by name.")]
        );

        yield return new(
            "RunSubtreeDynamic",
            "Run subtree (from a key)",
            "Tasks",
            BehaviorSlot.Task,
            "Runs the tree a key names. Cannot be spliced, so it gets an instance of its own.",
            [key]
        );

        yield return new(
            "RunUtilitySet",
            "Run utility set",
            "Tasks",
            BehaviorSlot.Task,
            "Runs a utility set as a leaf, until something above it aborts. It never finishes on its own.",
            [new("Set", "Set", BehaviorFieldKind.Text, "Which utility set to run, by name.")]
        );

        // ── The two that take other decorators ────────────────────────────────────────────────
        // ⚠ Their operands are the attachment's Children rather than a field, because a field is a
        // string and a decorator is not. They were built in P1 and stayed unauthorable until the
        // table was read against this schema.
        yield return new(
            "Composite",
            "Composite condition",
            "Decorators",
            BehaviorSlot.Decorator,
            "Joins the decorators under it with AND, OR or NOT, so a condition is not a branch.",
            [
                new(
                    "Logic",
                    "Logic",
                    BehaviorFieldKind.Choice,
                    "How to join them.",
                    nameof(DecoratorLogic.And),
                    Enum.GetNames<DecoratorLogic>()
                ),
                aborts
            ]
        );

        yield return new(
            "ConditionalLoop",
            "Conditional loop",
            "Decorators",
            BehaviorSlot.Decorator,
            "Runs the node again for as long as the decorator under it passes.",
            []
        );
    }
}
