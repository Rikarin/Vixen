// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation;
using Vixen.Animation.Motions;
using Vixen.Animation.StateMachine;
using Vixen.Core;

namespace Vixen.Editor.AnimationGraph;

/// <summary>Something wrong with an animation graph, and where.</summary>
/// <param name="Id">A stable code, so a message can be searched for.</param>
/// <param name="Message">What is wrong, in the terms the author wrote it in.</param>
/// <param name="Layer">Which layer, or empty for the graph as a whole.</param>
/// <param name="State">Which state, or empty.</param>
public readonly record struct AnimationGraphDiagnostic(
    string Id,
    string Message,
    string Layer = "",
    string State = ""
) {
    /// <inheritdoc />
    public override string ToString() =>
        State.Length > 0 ? $"{Id}: {Layer}/{State}: {Message}"
            : Layer.Length > 0 ? $"{Id}: {Layer}: {Message}"
            : $"{Id}: {Message}";
}

/// <summary>What compiling an animation graph produced.</summary>
/// <param name="Parameters">The values the transitions read, in the document's order.</param>
/// <param name="Layers">The layers, base first, or empty when the graph could not be built.</param>
/// <param name="Diagnostics">Everything wrong with it.</param>
public sealed record AnimationGraphArtefact(
    AnimationParameters Parameters,
    IReadOnlyList<AnimationLayer> Layers,
    IReadOnlyList<AnimationGraphDiagnostic> Diagnostics
) {
    /// <summary>Whether anything at all was wrong.</summary>
    public bool Succeeded => Layers.Count > 0 && Diagnostics.Count == 0;
}

/// <summary>Turns an authored animation graph into the state machine the runtime plays.</summary>
/// <remarks>
///     <para>
///         <b>Every reference is resolved here and nowhere else.</b> The document names states,
///         parameters and joints by name and clips by GUID; the runtime wants indices, object
///         references and a resolved <see cref="AnimationClip" />. Doing that translation in one
///         place is what lets the editor report "this transition goes to a state that is not here"
///         as a line in a panel rather than as an exception from a constructor three layers down.
///     </para>
///     <para>
///         ⚠ <b>A clip that will not resolve is a diagnostic and an empty state, not a refusal.</b>
///         Authoring a graph before the clips are imported is the ordinary way round — the animator
///         lays out idle, walk and run and the clips arrive later — and a compiler that refused would
///         make the graph unopenable until every file existed. The state is built with a motion that
///         plays nothing, so the topology is still checkable and the missing clip is named.
///     </para>
///     <para>
///         ⚠ <b>The resolver is an argument.</b> What an <see cref="AssetId" /> means is the
///         application's — an asset database, a test's dictionary, a build's artefact store — and a
///         compiler that reached for one would be a compiler that could only run inside the editor.
///     </para>
/// </remarks>
public sealed class AnimationGraphCompiler {
    readonly List<AnimationGraphDiagnostic> diagnostics = [];

    /// <summary>Where a clip comes from, or <see langword="null" /> for a graph with none resolved.</summary>
    public Func<AssetId, AnimationClip?>? Clips { get; init; }

    /// <summary>The skeleton a mask is resolved against, or <see langword="null" /> for none.</summary>
    /// <remarks>
    ///     ⚠ <b>Without one a mask is reported rather than applied</b>, because a
    ///     <see cref="BoneMask" /> is weights per joint of a specific skeleton and there is no way to
    ///     build one from names alone. The editor says which layers are affected; it does not
    ///     silently drop the mask.
    /// </remarks>
    public Skeleton? Skeleton { get; init; }

    /// <summary>Where blends get their temporary poses, or <see langword="null" /> for a fresh one.</summary>
    public PoseScratch? Scratch { get; init; }

    /// <summary>Compiles a graph.</summary>
    /// <param name="asset">The document.</param>
    /// <returns>The layers, the parameters, and everything wrong.</returns>
    public AnimationGraphArtefact Compile(AnimationGraphAsset asset) {
        ArgumentNullException.ThrowIfNull(asset);

        diagnostics.Clear();

        // ⚠ Declared into the field before a single layer is walked, because a blend tree resolves
        // its parameter while its state is being built — and a compiler that filled this in at the
        // end would report every tree in the graph as reading a parameter nobody declared.
        var parameters = this.parameters = new AnimationParameters();
        var declared = new HashSet<string>(StringComparer.Ordinal);

        foreach (var parameter in asset.Parameters) {
            if (parameter.Name.Length == 0) {
                Report("AG0001", "A parameter with no name cannot be referred to by a condition.");
                continue;
            }

            if (!declared.Add(parameter.Name)) {
                Report("AG0002", $"'{parameter.Name}' is declared twice. A condition could not say which.");
                continue;
            }

            var index = parameters.Declare(parameter.Name, parameter.Type);

            switch (parameter.Type) {
                case AnimationParameterType.Float:
                    parameters.SetFloat(index, parameter.Default);
                    break;

                case AnimationParameterType.Bool:
                case AnimationParameterType.Trigger:
                    parameters.SetBool(index, parameter.Default != 0f);
                    break;

                default:
                    parameters.SetInt(index, (int) parameter.Default);
                    break;
            }
        }

        if (asset.Layers.Count == 0) {
            Report("AG0003", "This graph has no layers, so it would play nothing at all.");

            return new(parameters, [], [.. diagnostics]);
        }

        // ⚠ Sized from the skeleton when there is one, and from nothing when there is not — a
        // scratch buffer is per joint, and a graph compiled without a rig is one being checked for
        // its topology rather than one about to be evaluated. Blending into a zero-joint pose is a
        // no-op rather than an overrun.
        var scratch = Scratch ?? new PoseScratch(Skeleton?.JointCount ?? 0);
        List<AnimationLayer> layers = [];

        foreach (var layer in asset.Layers) {
            if (Layer(layer, parameters, scratch) is { } built) {
                layers.Add(built);
            }
        }

        return new(parameters, layers, [.. diagnostics]);
    }

    AnimationLayer? Layer(AnimationLayerData data, AnimationParameters parameters, PoseScratch scratch) {
        if (data.States.Count == 0) {
            Report("AG0004", "A layer with no states cannot be run.", data.Name);

            return null;
        }

        Dictionary<string, AnimationState> byName = new(StringComparer.Ordinal);
        List<AnimationState> states = [];

        foreach (var state in data.States) {
            if (state.Name.Length == 0) {
                Report("AG0005", "A state with no name cannot be transitioned to.", data.Name);
                continue;
            }

            var built = new AnimationState(state.Name, Motion(state.Motion, data.Name, state.Name)) {
                Speed = state.Speed,
                Wrap = state.Wrap
            };

            if (!byName.TryAdd(state.Name, built)) {
                Report("AG0006", $"'{state.Name}' appears twice in this layer.", data.Name, state.Name);
                continue;
            }

            states.Add(built);
        }

        if (states.Count == 0) {
            Report("AG0007", "Every state in this layer was refused, so there is nothing to start in.", data.Name);

            return null;
        }

        // ⚠ Wired after every state exists, not as each is built. A transition may name a state that
        // comes later in the file — a walk that goes back to an idle declared below it is the
        // ordinary case — and a single pass would report half a graph's transitions as dangling.
        foreach (var state in data.States) {
            if (!byName.TryGetValue(state.Name, out var source)) {
                continue;
            }

            foreach (var transition in state.Transitions) {
                if (Transition(transition, byName, parameters, data.Name, state.Name) is { } built) {
                    source.AddTransition(built);
                }
            }
        }

        AnimationState? start = null;

        if (data.Default.Length > 0 && !byName.TryGetValue(data.Default, out start)) {
            Report(
                "AG0008",
                $"The layer starts in '{data.Default}', which is not one of its states. It will start in "
                + $"'{states[0].Name}' instead.",
                data.Name
            );
        }

        var machine = new AnimationStateMachine(states, start);

        foreach (var transition in data.AnyState) {
            if (Transition(transition, byName, parameters, data.Name, "Any State") is not { } built) {
                continue;
            }

            // The machine's own builder takes a destination and a duration and gives back the
            // transition it made, so the conditions are copied onto that one rather than the one
            // above being handed over — there is no way to add a constructed transition to the
            // any-state list, and there should not be: it is the machine that owns the order.
            var any = machine.TransitionFromAnyState(built.Destination, built.Duration);

            foreach (var condition in built.Conditions) {
                any.When(condition);
            }
        }

        var result = new AnimationLayer(data.Name, machine, parameters, scratch) {
            Weight = data.Weight,
            Blend = data.Blend,
            ContributesRootMotion = data.ContributesRootMotion
        };

        if (data.Mask.Count > 0) {
            result.Mask = Mask(data);
        }

        return result;
    }

    BoneMask? Mask(AnimationLayerData data) {
        if (Skeleton is not { } skeleton) {
            Report(
                "AG0009",
                $"This layer masks {data.Mask.Count} joint(s) and no skeleton was supplied, so the mask is "
                + "not applied. Open the graph against the rig it is authored for.",
                data.Name
            );

            return null;
        }

        var builder = BoneMask.Excluding(skeleton);
        var applied = 0;

        foreach (var joint in data.Mask) {
            if (skeleton.IndexOf(joint) < 0) {
                Report("AG0010", $"The mask names '{joint}', which this rig does not have.", data.Name);
                continue;
            }

            builder = builder.Set(joint, 1f);
            applied++;
        }

        return applied > 0 ? builder.Build() : null;
    }

    AnimationTransition? Transition(
        AnimationTransitionData data,
        Dictionary<string, AnimationState> byName,
        AnimationParameters parameters,
        string layer,
        string state
    ) {
        if (!byName.TryGetValue(data.To, out var destination)) {
            Report(
                "AG0011",
                data.To.Length == 0
                    ? "A transition with no destination goes nowhere."
                    : $"A transition goes to '{data.To}', which is not a state in this layer.",
                layer,
                state
            );

            return null;
        }

        var transition = new AnimationTransition(destination, data.Duration) {
            HasExitTime = data.HasExitTime,
            ExitTime = data.ExitTime,
            Offset = data.Offset,
            Interruption = data.Interruption,
            CanTransitionToSelf = data.CanTransitionToSelf
        };

        foreach (var condition in data.Conditions) {
            var index = parameters.IndexOf(condition.Parameter);

            if (index < 0) {
                Report(
                    "AG0012",
                    $"A condition reads '{condition.Parameter}', which the graph does not declare.",
                    layer,
                    state
                );

                continue;
            }

            transition.When(new(index, condition.Mode, condition.Threshold));
        }

        // ⚠ A transition with neither a condition nor an exit time fires on the first frame the
        // state is entered, every time, which reads as the state being skipped. Said out loud
        // because it is the single commonest thing to get wrong in a graph like this, and because
        // it is legal — an entry state that immediately moves on is a real thing to author.
        if (transition.Conditions.Count == 0 && !transition.HasExitTime) {
            Report(
                "AG0013",
                $"The transition to '{data.To}' has no conditions and no exit time, so it is taken as soon as "
                + "the state is entered.",
                layer,
                state
            );
        }

        return transition;
    }

    Motion Motion(AnimationMotionData data, string layer, string state) =>
        data.Kind switch {
            AnimationMotionKind.Blend1D => Blend1D(data, layer, state),
            AnimationMotionKind.Blend2D => Blend2D(data, layer, state),
            _ => Clip(data.Clip, data.Speed, data.Additive, layer, state) ?? Empty(state)
        };

    Motion Blend1D(AnimationMotionData data, string layer, string state) {
        var parameter = data.ParameterX;
        List<BlendTree1DChild> children = [];

        foreach (var child in data.Children.OrderBy(entry => entry.Threshold)) {
            if (Clip(child.Clip, child.Speed, additive: false, layer, state) is { } motion) {
                children.Add(new(motion, child.Threshold));
            }
        }

        if (children.Count == 0) {
            Report("AG0014", "A blend tree with no resolvable clips plays nothing.", layer, state);

            return Empty(state);
        }

        // ⚠ Declared on demand rather than reported as missing, because a blend tree's parameter is
        // the one place where the author's *intent* is unambiguous: a tree that reads `Speed` needs
        // a float called `Speed`, and refusing would leave the state playing nothing over a typo
        // that is one keystroke from being right. It is still reported.
        var index = Parameter(parameter, layer, state);

        return new BlendTree1D(index, children) { Name = state };
    }

    Motion Blend2D(AnimationMotionData data, string layer, string state) {
        List<BlendTree2DChild> children = [];

        foreach (var child in data.Children) {
            if (Clip(child.Clip, child.Speed, additive: false, layer, state) is { } motion) {
                children.Add(new(motion, new(child.X, child.Y)));
            }
        }

        if (children.Count == 0) {
            Report("AG0014", "A blend tree with no resolvable clips plays nothing.", layer, state);

            return Empty(state);
        }

        return new BlendTree2D(
            Parameter(data.ParameterX, layer, state),
            Parameter(data.ParameterY, layer, state),
            children,
            data.Mode
        ) { Name = state };
    }

    /// <summary>The index of a parameter a blend tree reads, reporting one that is not declared.</summary>
    /// <remarks>
    ///     ⚠ <b>Zero is what an undeclared parameter resolves to, not −1.</b> A blend tree indexes
    ///     the parameter set directly, so a negative index is an exception from inside an evaluation
    ///     that runs sixty times a second; the first parameter is a value the author can see being
    ///     wrong. The diagnostic is what says so.
    /// </remarks>
    int Parameter(string name, string layer, string state) {
        if (parameters?.IndexOf(name) is { } index && index >= 0) {
            return index;
        }

        Report(
            "AG0015",
            name.Length == 0
                ? "A blend tree reads no parameter, so it will sit at zero."
                : $"A blend tree reads '{name}', which the graph does not declare, so it will sit at zero.",
            layer,
            state
        );

        return 0;
    }

    AnimationParameters? parameters;

    ClipMotion? Clip(AssetId asset, float speed, bool additive, string layer, string state) {
        if (asset.IsEmpty) {
            Report("AG0016", "This state has no clip, so it plays nothing.", layer, state);

            return null;
        }

        if (Clips?.Invoke(asset) is not { } clip) {
            Report(
                "AG0017",
                $"The clip {asset} could not be loaded. The state's topology is still checked; it will not "
                + "play until the clip is imported.",
                layer,
                state
            );

            return null;
        }

        return new(clip, speed, additive);
    }

    /// <summary>A motion for a state whose clip is missing: it holds the pose it was given.</summary>
    /// <remarks>
    ///     ⚠ <b>Not null and not a throw.</b> An <see cref="AnimationState" /> must have a motion, and
    ///     a state machine with a hole in it cannot be checked for the thing an author actually wants
    ///     checked — whether the transitions reach every state. This is what makes the rest of the
    ///     compile meaningful while a clip is missing.
    /// </remarks>
    static Motion Empty(string name) => new EmptyMotion { Name = name };

    void Report(string id, string message, string layer = "", string state = "") =>
        diagnostics.Add(new(id, message, layer, state));

    /// <summary>Compiles a graph, for a caller that wants one line rather than an object.</summary>
    /// <param name="asset">The document.</param>
    /// <param name="clips">Where a clip comes from.</param>
    /// <param name="skeleton">The rig masks are resolved against.</param>
    /// <returns>The artefact.</returns>
    public static AnimationGraphArtefact Build(
        AnimationGraphAsset asset,
        Func<AssetId, AnimationClip?>? clips = null,
        Skeleton? skeleton = null
    ) => new AnimationGraphCompiler { Clips = clips, Skeleton = skeleton }.Compile(asset);
}

/// <summary>A motion that writes nothing: what a state with no clip plays.</summary>
/// <remarks>
///     Its own type rather than a clip of zero length, because there is no such clip —
///     <see cref="AnimationClip" /> is baked against a skeleton and a zero-length one is one frame
///     long by construction. Leaving the destination untouched means the pose under it survives,
///     which for a layer that is masked to an arm is the arm staying where the layer below put it.
/// </remarks>
public sealed class EmptyMotion : Motion {
    /// <inheritdoc />
    public override float Length(AnimationParameters parameters) => 1f;

    /// <inheritdoc />
    public override RootMotionDelta Evaluate(in MotionContext context, Span<BoneTransform> destination) =>
        default;
}
