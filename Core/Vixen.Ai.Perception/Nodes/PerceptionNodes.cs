// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vixen.Ai.Perception.Ecs;
using Vixen.Core.Mathematics;

namespace Vixen.Ai.Perception;

/// <summary>Whether this sense currently has anything, or has anything recent enough.</summary>
/// <param name="system">Where the perceived lists are.</param>
/// <param name="senses">Which senses count.</param>
/// <param name="key">The key the binding writes, which is what makes this observable.</param>
/// <param name="maximumAge">
///     How old the newest report may be, in seconds. Zero means it has to be perceived right now.
/// </param>
/// <param name="aborts">What it may interrupt.</param>
/// <remarks>
///     <para>
///         doc 37 § Part 3's <c>PerceivedTarget</c>. It reads the perceived list rather than the
///         blackboard — the list has the sense and the age on it and a key does not — but it
///         <b>observes</b> a key, and that pairing is the whole trick: the binding writes the key when
///         a pass changes what is perceived, the key's observers are what interrupt a branch, and this
///         decorator is what then answers the finer question the key could not carry.
///     </para>
///     <para>
///         ⚠ <b>An <c>Aborts</c> other than <c>None</c> without a bound key does nothing</b>, and
///         <c>BehaviorTreeCompiler</c> says so rather than letting it ship. A perceived list changing
///         is not an event the tree can see; only a blackboard write is.
///     </para>
/// </remarks>
public sealed class PerceivedTargetDecorator(
    PerceptionSystem system,
    SenseMask senses,
    BlackboardKey key,
    float maximumAge = 0f,
    ObserverAborts aborts = ObserverAborts.None
) : BehaviorDecorator {
    readonly PerceptionSystem system = system ?? throw new ArgumentNullException(nameof(system));
    readonly BlackboardKey[] observed = key.IsValid ? [key] : [];

    /// <inheritdoc />
    public override ObserverAborts Aborts => aborts;

    /// <inheritdoc />
    public override ReadOnlySpan<BlackboardKey> ObservedKeys => observed;

    /// <summary>
    ///     Re-tested every step, because a target walking out of a cone changes nothing on the
    ///     blackboard until the next pass and the branch should not outlive the condition.
    /// </summary>
    public override bool Continuous => true;

    /// <inheritdoc />
    public override bool Evaluate(in BehaviorContext context, ReadOnlySpan<byte> state) {
        if (system.PerceivedBy(context.Agent.World, context.Agent.Entity) is not { } perceived) {
            return false;
        }

        if (maximumAge <= 0f) {
            return perceived.IsPerceiving(senses);
        }

        return perceived.TryFreshest(senses, out var target) && target.AgeAt(system.Clock) <= maximumAge;
    }
}

/// <summary>Writes the nearest currently-perceived target into a key, on an interval.</summary>
/// <param name="system">Where the perceived lists are.</param>
/// <param name="senses">Which senses count.</param>
/// <param name="target">The key holding the entity.</param>
/// <param name="location">The key holding where it is, or null for none.</param>
/// <remarks>
///     doc 37 § Part 3's <c>NearestPerceived</c>. <c>UpdateBlackboardService</c> would not do: an
///     <see cref="IWorldSensor" /> writes one key and nearest-perceived is naturally two, and the
///     "nearest" it needs is a distance from the listener rather than a value read out of the world.
///
///     ⚠ <b>Nearest, not freshest — and that is a different node from what the binding does.</b> A
///     binding follows the most recent news, which is what "react to what just happened" means. A
///     service like this one follows the closest thing, which is what "shoot at whichever one is
///     about to reach me" means. Both are wanted, so both exist and neither is a mode of the other.
/// </remarks>
public sealed class NearestPerceivedService(
    PerceptionSystem system,
    SenseMask senses,
    BlackboardKey target,
    BlackboardKey? location = null
) : BehaviorService {
    readonly PerceptionSystem system = system ?? throw new ArgumentNullException(nameof(system));

    /// <inheritdoc />
    public override void Tick(in BehaviorContext context, Span<byte> state, float delta) {
        var world = context.Agent.World;
        var entity = context.Agent.Entity;

        if (system.PerceivedBy(world, entity) is not { } perceived) {
            return;
        }

        var here = PerceptionSystem.PositionOf(world, entity);

        if (!perceived.TryNearest(senses, here, out var nearest)) {
            context.Blackboard.Clear(target);

            if (location is { } where) {
                context.Blackboard.Clear(where);
            }

            return;
        }

        context.Blackboard.SetEntity(target, nearest.Source);

        if (location is { } known) {
            context.Blackboard.SetVector3(known, nearest.LastKnownLocation);
        }
    }
}

/// <summary>Makes a noise where the agent is, and finishes.</summary>
/// <param name="system">Where to report it.</param>
/// <param name="loudness">How loud, as a multiple of a listener's hearing range.</param>
/// <remarks>
///     doc 37 § Part 3's <c>MakeNoise</c>. A one-frame task rather than a decorator or a service,
///     because a noise is an event: it happens once, at a point in a branch, and a tree that wanted a
///     continuous one would put this under a <c>Loop</c> and say so.
/// </remarks>
public sealed class MakeNoiseTask(PerceptionSystem system, float loudness = 1f) : IAgentAction {
    readonly PerceptionSystem system = system ?? throw new ArgumentNullException(nameof(system));

    /// <summary>How many bytes it needs.</summary>
    public static int StateSize => Unsafe.SizeOf<int>();

    /// <inheritdoc />
    public void Start(in AgentContext context, Span<byte> state) { }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ Reports on the <i>first</i> tick and remembers it did. A task under a <c>Parallel</c>'s
    ///     background branch, or one whose parent keeps it running for a frame or two, would otherwise
    ///     make one noise per frame — a footstep that reads as a stampede.
    /// </remarks>
    public ActionStatus Tick(in AgentContext context, Span<byte> state, float delta) {
        ref var made = ref MemoryMarshal.AsRef<int>(state);

        if (made == 0) {
            made = 1;
            system.ReportNoise(context.Entity, PerceptionSystem.PositionOf(context.World, context.Entity), loudness);
        }

        return ActionStatus.Succeeded;
    }

    /// <inheritdoc />
    public void Abort(in AgentContext context, Span<byte> state) { }
}

/// <summary>The three nodes this assembly adds to the library, and how a file builds them.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Registered rather than declared in <c>BehaviorNodeSchema</c>'s own table, because these
///         types are not in <c>Vixen.Ai</c>.</b> The table lives there so that a game loading a
///         <c>.vxbt</c> at run time and the editor authoring one read the same declarations; a node
///         whose implementation is somewhere else has to arrive the same way a project's own node
///         does, which is the mechanism <see cref="BehaviorTreeResolver.AddDecorator" /> is.
///     </para>
///     <para>
///         Registering into <see cref="BehaviorNodeSchema.Default" /> is what puts them in the
///         editor's search popup and gives them an inspector, and it is what a game does once at
///         start-up. A caller that wants them in one tree and not another builds its own schema.
///     </para>
/// </remarks>
public static class PerceptionNodes {
    /// <summary>The declarations, for a caller adding them to a schema of its own.</summary>
    /// <returns>The three node types.</returns>
    public static IEnumerable<BehaviorNodeType> Describe() {
        var senses = new BehaviorField(
            "Senses",
            "Senses",
            BehaviorFieldKind.Choice,
            "Which sense to ask about.",
            nameof(AiSense.Sight),
            Enum.GetNames<AiSense>()
        );

        yield return new(
            "PerceivedTarget",
            "Perceived target",
            "Perception",
            BehaviorSlot.Decorator,
            "This sense currently perceives something, or perceived it recently enough.",
            [
                senses,
                new(
                    "Key",
                    "Key",
                    BehaviorFieldKind.Key,
                    "The key the perception binding writes. Observing it is what lets this interrupt."
                ),
                new(
                    "MaximumAge",
                    "Maximum age",
                    BehaviorFieldKind.Number,
                    "How stale the newest report may be, in seconds. Zero means it must be perceived now.",
                    "0"
                ),
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
            "NearestPerceived",
            "Nearest perceived",
            "Perception",
            BehaviorSlot.Service,
            "Writes the nearest currently-perceived target of a sense into a key.",
            [
                senses,
                new("Key", "Target key", BehaviorFieldKind.Key, "Where to put the entity."),
                new("Location", "Location key", BehaviorFieldKind.Key, "Where to put its position. Optional.")
            ]
        );

        yield return new(
            "MakeNoise",
            "Make noise",
            "Perception",
            BehaviorSlot.Task,
            "Emits a hearing stimulus where the agent is.",
            [
                new(
                    "Loudness",
                    "Loudness",
                    BehaviorFieldKind.Number,
                    "A multiple of a listener's hearing range. 3 carries three times as far.",
                    "1"
                )
            ]
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
    /// <param name="system">The system whose perceived lists the nodes read.</param>
    /// <returns>The resolver.</returns>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    public static BehaviorTreeResolver Register(BehaviorTreeResolver resolver, PerceptionSystem system) {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(system);

        Register(resolver.Schema);

        resolver.AddDecorator(
            "PerceivedTarget",
            (in BehaviorBuildContext context, ObserverAborts aborts) => new PerceivedTargetDecorator(
                system,
                Senses.Bit(context.Choice<AiSense>("Senses")),
                context.Key("Key"),
                context.Number("MaximumAge"),
                aborts
            )
        );

        resolver.AddService(
            "NearestPerceived",
            (in BehaviorBuildContext context) => new NearestPerceivedService(
                system,
                Senses.Bit(context.Choice<AiSense>("Senses")),
                context.Key("Key"),
                context.Text("Location").Length > 0 ? context.Key("Location") : null
            )
        );

        resolver.AddTask(
            "MakeNoise",
            (in BehaviorBuildContext context) =>
                new BehaviorTaskBuild(new MakeNoiseTask(system, context.Number("Loudness")), MakeNoiseTask.StateSize)
        );

        return resolver;
    }
}
