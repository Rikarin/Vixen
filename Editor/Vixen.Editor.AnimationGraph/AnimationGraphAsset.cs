// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation;
using Vixen.Animation.Motions;
using Vixen.Animation.StateMachine;
using Vixen.Core;

namespace Vixen.Editor.AnimationGraph;

/// <summary>What a state plays.</summary>
/// <remarks>
///     A discriminator rather than a class hierarchy, because a state's motion is bound from YAML by
///     the same reader that binds a <c>.meta</c> — and a polymorphic tree in a text asset is a tag
///     people have to get right by hand. Three kinds is the whole set <c>Vixen.Animation</c> has.
/// </remarks>
public enum AnimationMotionKind {
    /// <summary>One clip.</summary>
    Clip,

    /// <summary>Clips blended along one parameter.</summary>
    Blend1D,

    /// <summary>Clips blended across two.</summary>
    Blend2D
}

/// <summary>One child of a blend tree: a clip and where it sits in the blend space.</summary>
/// <remarks>
///     ⚠ <b>Every member here is settable, and that is not laziness.</b> The YAML binder takes part
///     only in members it can write on both sides — <c>MaterialAsset.Parameters</c> records the same
///     thing — so a get-only collection is written out and then silently skipped on load, which is a
///     file that loses its contents by round-tripping.
/// </remarks>
[DataContract("AnimationBlendChild")]
public sealed class BlendChildData {
    /// <summary>The clip it plays.</summary>
    public AssetId Clip { get; set; }

    /// <summary>Where it sits on a 1D tree's axis.</summary>
    public float Threshold { get; set; }

    /// <summary>Where it sits on a 2D tree, horizontally.</summary>
    public float X { get; set; }

    /// <summary>And vertically.</summary>
    public float Y { get; set; }

    /// <summary>How fast it plays.</summary>
    public float Speed { get; set; } = 1f;
}

/// <summary>What a state plays, as the document holds it.</summary>
[DataContract("AnimationMotion")]
public sealed class AnimationMotionData {
    /// <summary>Which of the three kinds it is.</summary>
    public AnimationMotionKind Kind { get; set; } = AnimationMotionKind.Clip;

    /// <summary>The clip, for <see cref="AnimationMotionKind.Clip" />.</summary>
    public AssetId Clip { get; set; }

    /// <summary>How fast it plays, for a clip.</summary>
    public float Speed { get; set; } = 1f;

    /// <summary>Whether the clip is applied on top of what is already there.</summary>
    public bool Additive { get; set; }

    /// <summary>The parameter a blend tree reads, by name.</summary>
    public string ParameterX { get; set; } = string.Empty;

    /// <summary>And the second one, for a 2D tree.</summary>
    public string ParameterY { get; set; } = string.Empty;

    /// <summary>How a 2D tree interpolates between its children.</summary>
    public Blend2DMode Mode { get; set; } = Blend2DMode.FreeformDirectional;

    /// <summary>A blend tree's children.</summary>
    public List<BlendChildData> Children { get; set; } = [];
}

/// <summary>One test a transition has to pass, as the document holds it.</summary>
/// <remarks>
///     ⚠ <b>The parameter is named rather than indexed, which is what
///     <see cref="AnimationCondition" /> holds.</b> An index is a position in an
///     <see cref="AnimationParameters" /> set that does not exist until the graph is compiled, so a
///     file storing one would break the moment somebody reordered the parameter list — which is a
///     thing a list with an up arrow beside it invites.
/// </remarks>
[DataContract("AnimationCondition")]
public sealed class AnimationConditionData {
    /// <summary>Which parameter, by name.</summary>
    public string Parameter { get; set; } = string.Empty;

    /// <summary>How it is compared.</summary>
    public AnimationConditionMode Mode { get; set; } = AnimationConditionMode.If;

    /// <summary>What it is compared against.</summary>
    public float Threshold { get; set; }
}

/// <summary>One transition, as the document holds it.</summary>
[DataContract("AnimationTransition")]
public sealed class AnimationTransitionData {
    /// <summary>The state it goes to, by name.</summary>
    public string To { get; set; } = string.Empty;

    /// <summary>How long the blend takes, in seconds.</summary>
    public float Duration { get; set; } = 0.2f;

    /// <summary>Whether it waits for a point in the source state's own playback.</summary>
    public bool HasExitTime { get; set; }

    /// <summary>Which point, in normalised time.</summary>
    public float ExitTime { get; set; } = 1f;

    /// <summary>Where the destination starts, in normalised time.</summary>
    public float Offset { get; set; }

    /// <summary>What may interrupt it once it has started.</summary>
    public TransitionInterruption Interruption { get; set; } = TransitionInterruption.None;

    /// <summary>Whether a transition whose destination is its source restarts the state.</summary>
    public bool CanTransitionToSelf { get; set; }

    /// <summary>What has to hold for it to be taken.</summary>
    public List<AnimationConditionData> Conditions { get; set; } = [];
}

/// <summary>One state, as the document holds it.</summary>
/// <remarks>
///     ⚠ <b>The position is in the asset and that is deliberate.</b> Doc 11's argument for the node
///     graphs applies unchanged: an arrangement somebody spent an afternoon on is authored data, and
///     an editor that re-laid the graph out on every open would throw it away every time.
/// </remarks>
[DataContract("AnimationState")]
public sealed class AnimationStateData {
    /// <summary>Its name, unique within its layer, and what a transition names.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>What it plays.</summary>
    public AnimationMotionData Motion { get; set; } = new();

    /// <summary>How fast the state runs, on top of the motion's own speed.</summary>
    public float Speed { get; set; } = 1f;

    /// <summary>What happens when its motion runs past the end.</summary>
    public WrapMode Wrap { get; set; } = WrapMode.Loop;

    /// <summary>Where its box sits in the editor.</summary>
    public float X { get; set; }

    /// <summary>And how far down.</summary>
    public float Y { get; set; }

    /// <summary>What leaves it.</summary>
    public List<AnimationTransitionData> Transitions { get; set; } = [];
}

/// <summary>One layer, as the document holds it.</summary>
[DataContract("AnimationLayer")]
public sealed class AnimationLayerData {
    /// <summary>What it is called.</summary>
    public string Name { get; set; } = "Base";

    /// <summary>How much of it is applied.</summary>
    public float Weight { get; set; } = 1f;

    /// <summary>Whether it replaces the pose under it or adds to it.</summary>
    public LayerBlend Blend { get; set; } = LayerBlend.Override;

    /// <summary>The joints it may write, by name. Empty means all of them.</summary>
    /// <remarks>
    ///     ⚠ <b>Names rather than indices, and the reason is the same one the clip format has.</b> A
    ///     joint index is a position in a skeleton the editor has no guarantee of holding — the rig a
    ///     graph is authored against and the rig it is played on are allowed to differ — so a mask
    ///     that stored indices would silently mask the wrong arm.
    /// </remarks>
    public List<string> Mask { get; set; } = [];

    /// <summary>Whether its root motion reaches the character.</summary>
    public bool ContributesRootMotion { get; set; } = true;

    /// <summary>Which state it starts in, by name.</summary>
    public string Default { get; set; } = string.Empty;

    /// <summary>Its states.</summary>
    public List<AnimationStateData> States { get; set; } = [];

    /// <summary>Transitions that may be taken from anywhere in the layer.</summary>
    public List<AnimationTransitionData> AnyState { get; set; } = [];
}

/// <summary>One parameter, as the document holds it.</summary>
[DataContract("AnimationParameter")]
public sealed class AnimationParameterData {
    /// <summary>What it is called, and what a condition names.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>What it holds.</summary>
    public AnimationParameterType Type { get; set; } = AnimationParameterType.Float;

    /// <summary>Its value before anything sets it.</summary>
    public float Default { get; set; }
}

/// <summary>A whole <c>.vxanimgraph</c> document.</summary>
/// <remarks>
///     <para>
///         <b>The third graph doc 11 names, and it is not a dataflow graph.</b> A shader graph's edge
///         carries a value and a VFX graph's carries order; a state machine's carries <i>"may
///         become"</i> — several leave one state and several arrive at another, none of them has a
///         type, and there is no topological order because a graph without a cycle is a character
///         that can never return to idle. Every rule <c>Vixen.Editor.NodeGraph</c> is built around
///         would have to be switched off to hold one, so this is its own model. What it shares with
///         the other two graphs is the shape of the editor around it, which is where the sharing
///         belongs.
///     </para>
///     <para>
///         ⚠ <b>Everything cross-references by name and clips by GUID.</b> A transition names its
///         destination and a condition names its parameter, because both are things a person types
///         and both survive a reorder; a clip is a GUID for doc 08's reason, so moving the file needs
///         nothing done to this one and <c>ReferenceIndex</c> counts it.
///     </para>
/// </remarks>
[DataContract("AnimationGraph")]
public sealed class AnimationGraphAsset {
    /// <summary>The version this reader and writer speak.</summary>
    public const int Current = 1;

    /// <summary>What an animation graph is written as.</summary>
    public const string Extension = ".vxanimgraph";

    /// <summary>Which version of the format this file is.</summary>
    public int Version { get; set; } = Current;

    /// <summary>What the graph is called.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The parameters its transitions test.</summary>
    public List<AnimationParameterData> Parameters { get; set; } = [];

    /// <summary>The layers, base first.</summary>
    public List<AnimationLayerData> Layers { get; set; } = [];
}
