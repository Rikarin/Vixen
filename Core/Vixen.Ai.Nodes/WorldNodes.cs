// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio;
using Vixen.Navigation;

namespace Vixen.Ai.Nodes;

/// <summary>The nodes this assembly adds to the library, and how a file builds them.</summary>
/// <remarks>
///     <para>
///         The same arrangement <c>Vixen.Ai.Perception</c> uses, and for the same reason:
///         <c>BehaviorNodeSchema</c> lives in <c>Vixen.Ai</c> so that a game loading a tree and an
///         editor authoring one read one table, and <c>Vixen.Ai</c> cannot construct a type it does
///         not reference.
///     </para>
///     <para>
///         ⚠ <b>Two of the seven need something a schema cannot name.</b> <c>DoesPathExist</c> needs a
///         <see cref="NavMeshQuery" /> and <c>PlaySound</c> needs an <see cref="AudioClip" />, and
///         neither is a string in a file — one is a live object over a baked mesh and the other is an
///         asset a content build produced. So the resolver overload takes the query, and sounds are
///         registered by name the way sensors already are. A tree authored before either exists still
///         compiles: the decorator is reported and the branch reads as the dead end it is.
///     </para>
/// </remarks>
public static class WorldNodes {
    /// <summary>The declarations, for a caller adding them to a schema of its own.</summary>
    /// <returns>The node types.</returns>
    public static IEnumerable<BehaviorNodeType> Describe() {
        var key = new BehaviorField(
            "Key",
            "Key",
            BehaviorFieldKind.Key,
            "The key naming where to go — a position, or an entity to follow."
        );

        var acceptance = new BehaviorField(
            "Acceptance",
            "Acceptance radius",
            BehaviorFieldKind.Number,
            "How close counts as arrived, in metres. Measured horizontally, so a slope does not defeat it.",
            "1.5"
        );

        yield return new(
            "MoveTo",
            "Move to",
            "Movement",
            BehaviorSlot.Task,
            "Walks to a key's position or entity over the navmesh.",
            [
                key,
                acceptance,
                new(
                    "Repath",
                    "Repath distance",
                    BehaviorFieldKind.Number,
                    "How far a followed entity may move before the path is planned again, in metres.",
                    "1"
                )
            ]
        );

        yield return new(
            "MoveDirectlyToward",
            "Move directly toward",
            "Movement",
            BehaviorSlot.Task,
            "Walks toward a key in a straight line, ignoring navigation.",
            [
                key,
                new("Speed", "Speed", BehaviorFieldKind.Number, "How fast, in metres a second.", "3"),
                acceptance with { Default = "0.5" }
            ]
        );

        yield return new(
            "Patrol",
            "Patrol",
            "Movement",
            BehaviorSlot.Task,
            "Walks the route on the entity's own PatrolRoute. Only the forward mode ever finishes.",
            [acceptance]
        );

        yield return new(
            "RotateToward",
            "Rotate toward",
            "Movement",
            BehaviorSlot.Task,
            "Turns to face a key, or the agent's focus when no key is given. Yaw only.",
            [
                key with { Description = "The key to face. Leave empty to use the agent's focus." },
                new("Rate", "Degrees a second", BehaviorFieldKind.Number, "How fast it turns.", "360"),
                new("Tolerance", "Tolerance", BehaviorFieldKind.Number, "How close counts, in degrees.", "5")
            ]
        );

        yield return new(
            "DoesPathExist",
            "Does path exist",
            "Movement",
            BehaviorSlot.Decorator,
            "Whether the agent could actually reach what a key names.",
            [
                key,
                new(
                    "Test",
                    "Test",
                    BehaviorFieldKind.Choice,
                    "How hard to look. A raycast says no to anything round a corner; a full search is exact and expensive.",
                    nameof(PathTest.Budgeted),
                    Enum.GetNames<PathTest>()
                ),
                new("Budget", "Node budget", BehaviorFieldKind.Integer, "How many nodes the budgeted test may open.", "256"),
                new(
                    "Aborts",
                    "Aborts",
                    BehaviorFieldKind.Choice,
                    "What this may interrupt when the key changes.",
                    nameof(ObserverAborts.None),
                    Enum.GetNames<ObserverAborts>()
                )
            ]
        );

        yield return new(
            "PlayAnimation",
            "Play animation",
            "Presentation",
            BehaviorSlot.Task,
            "Plays a state on an animation layer, and optionally waits for it to play through.",
            [
                new("Layer", "Layer", BehaviorFieldKind.Text, "Which layer. Empty means the first one."),
                new("State", "State", BehaviorFieldKind.Text, "Which state to play."),
                new("Crossfade", "Crossfade", BehaviorFieldKind.Number, "How long to blend into it, in seconds.", "0.15"),
                new("Wait", "Wait for it", BehaviorFieldKind.Toggle, "Whether the task runs until it has played through.", "true")
            ]
        );

        yield return new(
            "PlaySound",
            "Play sound",
            "Presentation",
            BehaviorSlot.Task,
            "Plays a registered clip on the agent's own audio source.",
            [
                new("Sound", "Sound", BehaviorFieldKind.Text, "Which registered sound to play."),
                new("Gain", "Gain", BehaviorFieldKind.Number, "Its linear gain.", "1"),
                new("Wait", "Wait for it", BehaviorFieldKind.Toggle, "Whether the task runs for the length of the clip.", "false")
            ]
        );

        yield return new(
            "DefaultFocus",
            "Default focus",
            "Presentation",
            BehaviorSlot.Service,
            "Keeps the agent's AiFocus pointed at what a key names, and clears it when the key is unset.",
            [key]
        );
    }

    /// <summary>Adds them to a schema.</summary>
    /// <param name="schema">The schema, or null for the shared one.</param>
    /// <returns>The schema.</returns>
    /// <remarks>Safe to call twice: a type already in the schema is left alone.</remarks>
    public static BehaviorNodeSchema Register(BehaviorNodeSchema? schema = null) {
        var target = schema ?? BehaviorNodeSchema.Default;

        foreach (var type in Describe()) {
            if (!target.TryGet(type.Type, out _)) {
                target.Add(type);
            }
        }

        return target;
    }

    /// <summary>Teaches a resolver to build them.</summary>
    /// <param name="resolver">The resolver a <c>.vxbt</c> is compiled against.</param>
    /// <param name="query">The navmesh <c>DoesPathExist</c> asks, or null to leave that node unbuildable.</param>
    /// <param name="sounds">The clips <c>PlaySound</c> may name, or null for none.</param>
    /// <returns>The resolver.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="resolver" /> is null.</exception>
    public static BehaviorTreeResolver Register(
        BehaviorTreeResolver resolver,
        NavMeshQuery? query = null,
        IReadOnlyDictionary<string, AudioClip>? sounds = null
    ) {
        ArgumentNullException.ThrowIfNull(resolver);

        Register(resolver.Schema);

        resolver.AddTask(
            "MoveTo",
            (in BehaviorBuildContext context) => new BehaviorTaskBuild(
                new MoveToTask(context.Key("Key"), context.Number("Acceptance"), context.Number("Repath")),
                MoveToTask.StateSize
            )
        );

        resolver.AddTask(
            "MoveDirectlyToward",
            (in BehaviorBuildContext context) => new BehaviorTaskBuild(
                new MoveDirectlyTowardTask(context.Key("Key"), context.Number("Speed"), context.Number("Acceptance"))
            )
        );

        resolver.AddTask(
            "Patrol",
            (in BehaviorBuildContext context) =>
                new BehaviorTaskBuild(new PatrolTask(context.Number("Acceptance")), PatrolTask.StateSize)
        );

        resolver.AddTask(
            "RotateToward",
            (in BehaviorBuildContext context) => new BehaviorTaskBuild(
                new RotateTowardTask(
                    // Optional, unlike everywhere else: an empty key means "use the focus", which is
                    // the whole point of having a focus, so asking for one would be a diagnostic on
                    // the ordinary case.
                    context.Text("Key").Length > 0 ? context.Key("Key") : BlackboardKey.Invalid,
                    context.Number("Rate"),
                    context.Number("Tolerance")
                )
            )
        );

        resolver.AddTask(
            "PlayAnimation",
            (in BehaviorBuildContext context) => new BehaviorTaskBuild(
                new PlayAnimationTask(
                    context.Text("Layer"),
                    context.Text("State"),
                    context.Number("Crossfade"),
                    context.Toggle("Wait")
                ),
                PlayAnimationTask.StateSize
            )
        );

        resolver.AddService("DefaultFocus", (in BehaviorBuildContext context) => new DefaultFocusService(context.Key("Key")));

        if (query is not null) {
            resolver.AddDecorator(
                "DoesPathExist",
                (in BehaviorBuildContext context, ObserverAborts aborts) => new DoesPathExistDecorator(
                    query,
                    context.Key("Key"),
                    context.Choice<PathTest>("Test"),
                    context.Integer("Budget"),
                    aborts
                )
            );
        }

        if (sounds is not null) {
            resolver.AddTask(
                "PlaySound",
                (in BehaviorBuildContext context) => {
                    var name = context.Text("Sound");

                    if (!sounds.TryGetValue(name, out var clip)) {
                        context.Report($"No sound called '{name}' is registered.");
                    }

                    return new BehaviorTaskBuild(
                        new PlaySoundTask(clip, context.Toggle("Wait"), context.Number("Gain")),
                        PlaySoundTask.StateSize
                    );
                }
            );
        }

        return resolver;
    }
}
