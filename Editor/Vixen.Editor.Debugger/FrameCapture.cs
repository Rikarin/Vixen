// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Graphics;

namespace Vixen.Editor.Debugger;

/// <summary>Which kind of RHI call a captured command was.</summary>
/// <remarks>
///     ⚠ <b>A vocabulary of its own rather than the Null backend's
///     <c>RecordedCommandKind</c>, and the difference is one file's worth of mapping.</b> What it
///     buys is that the panel does not know which backend produced the capture — doc 13 wants a
///     capture from a <i>running</i> frame eventually, which is a Vulkan command-stream hook, and a
///     panel written against the test backend's enum would have to be rewritten to accept one.
/// </remarks>
public enum CaptureCommandKind : byte {
    /// <summary>A render pass began.</summary>
    BeginPass,

    /// <summary>A render pass ended.</summary>
    EndPass,

    /// <summary>A debug group opened.</summary>
    PushGroup,

    /// <summary>A debug group closed.</summary>
    PopGroup,

    /// <summary>A debug marker.</summary>
    Marker,

    /// <summary>State was set — viewport, scissor, blend constant, stencil reference.</summary>
    SetState,

    /// <summary>A pipeline was bound.</summary>
    BindPipeline,

    /// <summary>A descriptor set was bound.</summary>
    BindDescriptorSet,

    /// <summary>A vertex buffer was bound.</summary>
    BindVertexBuffer,

    /// <summary>The index buffer was bound.</summary>
    BindIndexBuffer,

    /// <summary>Push constants were written.</summary>
    PushConstants,

    /// <summary>A draw.</summary>
    Draw,

    /// <summary>A compute dispatch.</summary>
    Dispatch,

    /// <summary>A copy.</summary>
    Copy,

    /// <summary>A barrier group.</summary>
    Barrier
}

/// <summary>Which piece of state a <see cref="CaptureCommandKind.SetState" /> command set.</summary>
public enum CaptureState : byte {
    /// <summary>The viewport.</summary>
    Viewport,

    /// <summary>The scissor rectangle.</summary>
    Scissor,

    /// <summary>The blend constant.</summary>
    BlendConstant,

    /// <summary>The stencil reference.</summary>
    StencilReference
}

/// <summary>One captured RHI call.</summary>
/// <param name="Sequence">Its position in the frame's stream, from zero.</param>
/// <param name="Kind">Which call it was.</param>
/// <param name="Label">A name, for the passes, groups and markers that carry one.</param>
/// <param name="A">First argument slot.</param>
/// <param name="B">Second.</param>
/// <param name="C">Third.</param>
/// <param name="D">Fourth.</param>
/// <param name="E">Fifth.</param>
/// <remarks>
///     Flat and shared-slot, the same trade <c>RecordedCommand</c> makes: one type keeps the stream
///     in one array and in order, and what each slot means is a property of the kind rather than of
///     the type.
/// </remarks>
public readonly record struct CapturedCommand(
    int Sequence,
    CaptureCommandKind Kind,
    string? Label = null,
    long A = 0,
    long B = 0,
    long C = 0,
    long D = 0,
    long E = 0
) {
    /// <summary>Whether this is a call somebody would want to step to.</summary>
    /// <remarks>
    ///     ⚠ <b>Draws and dispatches, not everything that touches the GPU.</b> "Step to the next
    ///     call" over a stream where a descriptor bind is a step is a control that takes forty
    ///     presses to reach the next thing that put a pixel anywhere.
    /// </remarks>
    public bool IsWork => Kind is CaptureCommandKind.Draw or CaptureCommandKind.Dispatch;

    /// <summary>A sentence describing the call and its arguments.</summary>
    public string Describe() =>
        Kind switch {
            CaptureCommandKind.BeginPass => $"Begin pass '{Label}' — {A} colour attachment(s)"
                + (B != 0 ? ", depth" : ""),
            CaptureCommandKind.EndPass => "End pass",
            CaptureCommandKind.PushGroup => $"Group '{Label}'",
            CaptureCommandKind.PopGroup => "End group",
            CaptureCommandKind.Marker => $"Marker '{Label}'",
            CaptureCommandKind.SetState => Describe((CaptureState)A),
            CaptureCommandKind.BindPipeline => string.Create(CultureInfo.InvariantCulture, $"Bind pipeline #{A}"),
            CaptureCommandKind.BindDescriptorSet => string.Create(
                CultureInfo.InvariantCulture,
                $"Bind descriptor set #{B} to {(DescriptorSetSlot)A}, {C} dynamic offset(s)"
            ),
            CaptureCommandKind.BindVertexBuffer => string.Create(
                CultureInfo.InvariantCulture,
                $"Bind vertex buffer #{B} to slot {A} at +{C}"
            ),
            CaptureCommandKind.BindIndexBuffer => string.Create(
                CultureInfo.InvariantCulture,
                $"Bind index buffer #{A} as {(IndexFormat)B} at +{C}"
            ),
            CaptureCommandKind.PushConstants => string.Create(
                CultureInfo.InvariantCulture,
                $"Push {C} byte(s) of constants to {(ShaderStage)A} at +{B}"
            ),
            CaptureCommandKind.Draw => string.Create(
                CultureInfo.InvariantCulture,
                $"Draw {A:N0} {(D != 0 ? "index" : "vert")}(ices) × {B:N0} instance(s)"
            ),
            CaptureCommandKind.Dispatch => string.Create(CultureInfo.InvariantCulture, $"Dispatch {A}×{B}×{C}"),
            CaptureCommandKind.Copy => Label ?? "Copy",
            _ => string.Create(CultureInfo.InvariantCulture, $"Barrier — {A} buffer(s), {B} texture(s)")
        };

    static string Describe(CaptureState state) =>
        state switch {
            CaptureState.Viewport => "Set viewport",
            CaptureState.Scissor => "Set scissor",
            CaptureState.BlendConstant => "Set blend constant",
            _ => "Set stencil reference"
        };
}

/// <summary>One row of the frame debugger's tree: a pass, a group, or a call.</summary>
/// <remarks>
///     The nesting a capture has and a flat command list does not. Passes and debug groups both
///     open and close, so a frame's stream is a tree whether or not anybody built one — and finding
///     the shadow pass in a stream of two thousand calls without it is the ten-minute scroll
///     <c>ICommandList.PushDebugGroup</c>'s own remarks describe.
/// </remarks>
public sealed class CaptureNode {
    readonly List<CaptureNode> children = [];

    internal CaptureNode(CapturedCommand command, int level) {
        Command = command;
        Level = level;
    }

    /// <summary>The call that opened this node.</summary>
    public CapturedCommand Command { get; }

    /// <summary>How deeply nested it is.</summary>
    public int Level { get; }

    /// <summary>What is inside it, in stream order.</summary>
    public IReadOnlyList<CaptureNode> Children => children;

    /// <summary>Where in the stream this node's scope ends.</summary>
    /// <remarks>
    ///     The sequence of the matching end, or of the node itself for a leaf. What "select this pass
    ///     and show the state at the end of it" is computed from.
    /// </remarks>
    public int EndSequence { get; internal set; }

    /// <summary>How many draws and dispatches are inside it, at any depth.</summary>
    public int WorkCount {
        get {
            var total = Command.IsWork ? 1 : 0;

            foreach (var child in children) {
                total += child.WorkCount;
            }

            return total;
        }
    }

    /// <summary>What the row says.</summary>
    public string Label => Command.Describe();

    internal void Add(CaptureNode child) => children.Add(child);

    /// <summary>Calls <paramref name="visit" /> on this node and everything under it, parents first.</summary>
    /// <param name="visit">What to call.</param>
    /// <exception cref="ArgumentNullException"><paramref name="visit" /> is null.</exception>
    public void Walk(Action<CaptureNode> visit) {
        ArgumentNullException.ThrowIfNull(visit);

        visit(this);

        foreach (var child in children) {
            child.Walk(visit);
        }
    }
}

/// <summary>One frame's command stream, as a tree and as a list.</summary>
/// <remarks>
///     <para>
///         <b>Both, because the two answer different questions.</b> The tree is how somebody finds
///         the pass they care about; the list is what <see cref="StateAt" /> replays, and replaying a
///         tree would mean walking it in an order that is not the order the GPU sees.
///     </para>
///     <para>
///         ⚠ <b>An unbalanced stream is tolerated rather than refused.</b> A capture taken from a
///         frame that threw halfway through has a pass that never ended, and that capture is exactly
///         the one somebody needs to look at. Unclosed scopes end at the last command rather than
///         making the whole capture unopenable.
///     </para>
/// </remarks>
public sealed class FrameCapture {
    /// <summary>The capture with nothing in it.</summary>
    public static FrameCapture Empty { get; } = new("", []);

    readonly CapturedCommand[] commands;
    readonly CaptureNode[] roots;
    readonly int[] work;

    /// <summary>Builds a capture from a stream.</summary>
    /// <param name="name">What to call it — a frame number, a pass name, whatever the source knows.</param>
    /// <param name="stream">The calls, in submission order.</param>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    public FrameCapture(string name, IReadOnlyList<CapturedCommand> stream) {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(stream);

        Name = name;

        commands = new CapturedCommand[stream.Count];
        List<int> drawn = [];

        for (var index = 0; index < stream.Count; index++) {
            // ⚠ Renumbered rather than trusted. A capture is assembled from several command lists,
            // each of which numbered its own calls from zero — so the sequence a caller reads off a
            // node has to be this capture's index, or `StateAt` replays the wrong prefix.
            commands[index] = stream[index] with { Sequence = index };

            if (commands[index].IsWork) {
                drawn.Add(index);
            }
        }

        work = [.. drawn];
        roots = BuildTree(commands);
    }

    /// <summary>What the capture is called.</summary>
    public string Name { get; }

    /// <summary>Every call, in submission order.</summary>
    public IReadOnlyList<CapturedCommand> Commands => commands;

    /// <summary>The top-level passes and groups.</summary>
    public IReadOnlyList<CaptureNode> Roots => roots;

    /// <summary>The stream positions of the draws and dispatches, in order.</summary>
    /// <remarks>What "next draw call" steps through, and what makes stepping O(1) rather than a
    ///     scan of the stream per press.</remarks>
    public IReadOnlyList<int> Work => work;

    /// <summary>How many draws and dispatches the frame issued.</summary>
    public int WorkCount => work.Length;

    /// <summary>Whether anything was captured.</summary>
    public bool IsEmpty => commands.Length == 0;

    /// <summary>Replays the stream up to a call and reports the state in effect there.</summary>
    /// <param name="sequence">Which call. Out-of-range values clamp.</param>
    /// <returns>The bound state, as the GPU would have it when that call ran.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Replayed from the start every time rather than kept as a per-call snapshot.</b>
    ///         A frame is a few thousand commands and this is a walk over an array of structs, which
    ///         is microseconds; a snapshot per call would be a copy of the whole state vector per
    ///         draw, which for a real frame is megabytes of the editor's heap held for as long as the
    ///         capture is open.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Inclusive of the named call.</b> "The state at draw N" means the state the draw
    ///         <i>used</i>, so a bind at the same index as the draw — which cannot happen, but a
    ///         pass-begin at it can — is applied before the answer is returned.
    ///     </para>
    /// </remarks>
    public DrawState StateAt(int sequence) {
        var state = new DrawState();

        if (commands.Length == 0) {
            return state;
        }

        var last = Math.Clamp(sequence, 0, commands.Length - 1);

        for (var index = 0; index <= last; index++) {
            state.Apply(commands[index]);
        }

        return state;
    }

    /// <summary>The next draw or dispatch at or after a position.</summary>
    /// <param name="sequence">Where to start looking.</param>
    /// <returns>Its stream position, or <see langword="null" /> when there is none.</returns>
    public int? NextWork(int sequence) {
        foreach (var index in work) {
            if (index >= sequence) {
                return index;
            }
        }

        return null;
    }

    /// <summary>The previous draw or dispatch at or before a position.</summary>
    /// <param name="sequence">Where to start looking.</param>
    /// <returns>Its stream position, or <see langword="null" /> when there is none.</returns>
    public int? PreviousWork(int sequence) {
        for (var index = work.Length - 1; index >= 0; index--) {
            if (work[index] <= sequence) {
                return work[index];
            }
        }

        return null;
    }

    static CaptureNode[] BuildTree(CapturedCommand[] stream) {
        List<CaptureNode> roots = [];
        List<CaptureNode> open = [];

        foreach (var command in stream) {
            switch (command.Kind) {
                case CaptureCommandKind.EndPass:
                case CaptureCommandKind.PopGroup:
                    if (open.Count > 0) {
                        open[^1].EndSequence = command.Sequence;
                        open.RemoveAt(open.Count - 1);
                    }

                    // The closing call is not a row of its own: it says nothing the opening row did
                    // not, and a tree with an "End pass" line under every pass is twice as tall for
                    // no information.
                    continue;

                default:
                    break;
            }

            var node = new CaptureNode(command, open.Count) { EndSequence = command.Sequence };

            if (open.Count == 0) {
                roots.Add(node);
            } else {
                open[^1].Add(node);
            }

            if (command.Kind is CaptureCommandKind.BeginPass or CaptureCommandKind.PushGroup) {
                open.Add(node);
            }
        }

        // Whatever is still open ended when the stream did — a capture from a frame that threw is
        // the capture somebody most needs to be able to open.
        foreach (var node in open) {
            node.EndSequence = stream.Length - 1;
        }

        return [.. roots];
    }
}
