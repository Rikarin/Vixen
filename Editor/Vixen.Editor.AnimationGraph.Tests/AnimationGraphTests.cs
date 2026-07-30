// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation;
using Vixen.Animation.StateMachine;
using Vixen.Core.Yaml;
using Vixen.Editor.AnimationGraph;
using Xunit;

namespace Tests;

/// <summary>The third graph: what it stores, and what compiling it produces.</summary>
/// <remarks>
///     No UI. What is under test is that an authored document turns into the runtime's own state
///     machine, that every name it carries is resolved rather than passed through, and that a graph
///     with something wrong with it still yields the parts that are right — which is the property the
///     whole compiler is arranged around.
/// </remarks>
public class AnimationGraphTests {
    /// <summary>A locomotion graph, wired as an animator would wire one.</summary>
    static AnimationGraphAsset Locomotion() {
        var graph = new AnimationGraphAsset { Name = "Locomotion" };

        graph.Parameters.Add(new() { Name = "Speed", Type = AnimationParameterType.Float });
        graph.Parameters.Add(new() { Name = "Jump", Type = AnimationParameterType.Trigger });

        var layer = new AnimationLayerData { Name = "Base", Default = "Idle" };

        var idle = new AnimationStateData { Name = "Idle", X = 40f, Y = 40f };
        var move = new AnimationStateData { Name = "Move", X = 260f, Y = 40f };
        var jump = new AnimationStateData { Name = "Jump", X = 480f, Y = 40f, Wrap = WrapMode.Clamp };

        move.Motion = new() { Kind = AnimationMotionKind.Blend1D, ParameterX = "Speed" };

        idle.Transitions.Add(new() {
            To = "Move",
            Conditions = [new() { Parameter = "Speed", Mode = AnimationConditionMode.Greater, Threshold = 0.1f }]
        });

        move.Transitions.Add(new() {
            To = "Idle",
            Conditions = [new() { Parameter = "Speed", Mode = AnimationConditionMode.Less, Threshold = 0.1f }]
        });

        layer.States.Add(idle);
        layer.States.Add(move);
        layer.States.Add(jump);

        layer.AnyState.Add(new() {
            To = "Jump",
            Conditions = [new() { Parameter = "Jump", Mode = AnimationConditionMode.If }]
        });

        graph.Layers.Add(layer);
        return graph;
    }

    [Fact]
    public void CompilesEveryStateAndTransition() {
        var artefact = AnimationGraphCompiler.Build(Locomotion());

        Assert.Single(artefact.Layers);

        var machine = artefact.Layers[0].States.Machine;

        Assert.Equal(3, machine.States.Length);
        Assert.Equal(machine.IndexOf("Idle"), machine.DefaultState);
        Assert.Single(machine.AnyStateTransitions);

        // The transition's destination is the object, not the name — which is the whole of what
        // compiling a graph does.
        Assert.Same(machine[machine.IndexOf("Jump")], machine.AnyStateTransitions[0].Destination);
    }

    [Fact]
    public void ResolvesConditionParametersByName() {
        var artefact = AnimationGraphCompiler.Build(Locomotion());
        var machine = artefact.Layers[0].States.Machine;

        var leaving = machine[machine.IndexOf("Idle")].Transitions[0];

        Assert.Single(leaving.Conditions);
        Assert.Equal(artefact.Parameters.IndexOf("Speed"), leaving.Conditions[0].Parameter);
    }

    [Fact]
    public void ReportsATransitionThatGoesNowhereAndKeepsTheRest() {
        var graph = Locomotion();

        graph.Layers[0].States[0].Transitions.Add(new() { To = "Sprint" });

        var artefact = AnimationGraphCompiler.Build(graph);

        Assert.Contains(artefact.Diagnostics, diagnostic => diagnostic.Id == "AG0011");
        Assert.Single(artefact.Layers);

        // The good transition survives the bad one, which is what "reports rather than refuses"
        // has to mean to be worth anything.
        Assert.Single(artefact.Layers[0].States.Machine[0].Transitions);
    }

    [Fact]
    public void ReportsATransitionThatWouldFireImmediately() {
        var graph = Locomotion();

        graph.Layers[0].States[2].Transitions.Add(new() { To = "Idle" });

        Assert.Contains(AnimationGraphCompiler.Build(graph).Diagnostics, diagnostic => diagnostic.Id == "AG0013");
    }

    [Fact]
    public void ReportsAnUnresolvedClipAndStillBuildsTheState() {
        var graph = Locomotion();

        graph.Layers[0].States[0].Motion = new() { Kind = AnimationMotionKind.Clip };

        var artefact = AnimationGraphCompiler.Build(graph);

        Assert.Contains(artefact.Diagnostics, diagnostic => diagnostic.Id == "AG0016");
        Assert.Equal(3, artefact.Layers[0].States.Machine.States.Length);
    }

    [Fact]
    public void ReportsAMaskWithNoSkeletonRatherThanDroppingIt() {
        var graph = Locomotion();

        graph.Layers[0].Mask.Add("Spine");

        var artefact = AnimationGraphCompiler.Build(graph);

        Assert.Contains(artefact.Diagnostics, diagnostic => diagnostic.Id == "AG0009");
        Assert.Null(artefact.Layers[0].Mask);
    }

    [Fact]
    public void ADefaultStateThatIsNotInTheLayerIsReportedAndFallsBack() {
        var graph = Locomotion();

        graph.Layers[0].Default = "Sprint";

        var artefact = AnimationGraphCompiler.Build(graph);

        Assert.Contains(artefact.Diagnostics, diagnostic => diagnostic.Id == "AG0008");
        Assert.Equal(0, artefact.Layers[0].States.Machine.DefaultState);
    }

    [Fact]
    public void RoundTripsThroughYaml() {
        var before = Locomotion();
        var after = YamlSerializer.Parse<AnimationGraphAsset>(YamlSerializer.ToYaml(before));

        Assert.Equal(before.Name, after.Name);
        Assert.Equal(before.Parameters.Count, after.Parameters.Count);
        Assert.Single(after.Layers);

        var layer = after.Layers[0];

        Assert.Equal(3, layer.States.Count);
        Assert.Equal("Idle", layer.Default);
        Assert.Single(layer.AnyState);

        // The editor position survives, which is what makes an arrangement authored data rather
        // than something the editor re-invents on every open.
        Assert.Equal(260f, layer.States[1].X);

        // And the blend tree's parameter, which is the one cross-reference a state carries.
        Assert.Equal("Speed", layer.States[1].Motion.ParameterX);
    }

    [Fact]
    public void AGraphWithNoLayersIsReportedRatherThanThrowing() {
        var artefact = AnimationGraphCompiler.Build(new AnimationGraphAsset());

        Assert.Empty(artefact.Layers);
        Assert.Contains(artefact.Diagnostics, diagnostic => diagnostic.Id == "AG0003");
    }
}
