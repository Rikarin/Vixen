// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Graphics;

namespace Vixen.Editor.Debugger;

/// <summary>One line of the state pane.</summary>
/// <param name="Group">Which heading it sits under.</param>
/// <param name="Label">What it is.</param>
/// <param name="Value">What it is set to.</param>
public readonly record struct StateRow(string Group, string Label, string Value);

/// <summary>Everything bound at a point in the stream.</summary>
/// <remarks>
///     <para>
///         <b>What doc 13 means by "inspect bound state and render targets".</b> A draw call on its
///         own says how many vertices; the question somebody opens a frame debugger to ask is
///         <i>which pipeline, with which descriptor sets, into which attachments</i> — and none of
///         that is in the draw call.
///     </para>
///     <para>
///         ⚠ <b>Vertex buffers are a sparse map rather than an array.</b> A renderer binds slot 0
///         and slot 3 and leaves 1 and 2 alone; an array sized to the largest slot would show two
///         rows saying "buffer #0", which reads as two bindings that are not there.
///     </para>
///     <para>
///         ⚠ <b>Everything a pass scopes is cleared when the pass ends.</b> Pipeline, bindings,
///         viewport and scissor are all pass-scoped in the RHI — <c>ICommandList.BeginRenderPass</c>
///         says so — so a state pane that carried a pipeline across a pass boundary would be showing
///         a binding the next pass does not have.
///     </para>
/// </remarks>
public sealed class DrawState {
    readonly Dictionary<int, long> vertexBuffers = [];
    readonly Dictionary<DescriptorSetSlot, long> descriptorSets = [];

    /// <summary>The pass in effect, or <see langword="null" /> outside one.</summary>
    public string? Pass { get; private set; }

    /// <summary>How many colour attachments the pass has.</summary>
    public long ColourAttachments { get; private set; }

    /// <summary>Whether it has a depth-stencil attachment.</summary>
    public bool HasDepth { get; private set; }

    /// <summary>The debug groups open at this point, outermost first.</summary>
    public IReadOnlyList<string> Groups => groups;

    readonly List<string> groups = [];

    /// <summary>The bound pipeline, or <see langword="null" />.</summary>
    public long? Pipeline { get; private set; }

    /// <summary>Which descriptor set is bound to each slot.</summary>
    public IReadOnlyDictionary<DescriptorSetSlot, long> DescriptorSets => descriptorSets;

    /// <summary>Which vertex buffer is bound to each slot.</summary>
    public IReadOnlyDictionary<int, long> VertexBuffers => vertexBuffers;

    /// <summary>The bound index buffer, or <see langword="null" />.</summary>
    public long? IndexBuffer { get; private set; }

    /// <summary>How wide an index is.</summary>
    public IndexFormat IndexFormat { get; private set; }

    /// <summary>The viewport, as width, height, x and y, or <see langword="null" />.</summary>
    public (long Width, long Height, long X, long Y)? Viewport { get; private set; }

    /// <summary>The scissor rectangle, or <see langword="null" />.</summary>
    public (long Width, long Height, long X, long Y)? Scissor { get; private set; }

    /// <summary>The stencil reference value.</summary>
    public long StencilReference { get; private set; }

    /// <summary>How many bytes of push constants were last written.</summary>
    public long PushConstantBytes { get; private set; }

    /// <summary>Applies one command.</summary>
    /// <param name="command">The command.</param>
    public void Apply(in CapturedCommand command) {
        switch (command.Kind) {
            case CaptureCommandKind.BeginPass:
                Pass = command.Label;
                ColourAttachments = command.A;
                HasDepth = command.B != 0;
                break;

            case CaptureCommandKind.EndPass:
                Pass = null;
                ColourAttachments = 0;
                HasDepth = false;
                ClearPassScoped();

                break;

            case CaptureCommandKind.PushGroup:
                groups.Add(command.Label ?? "(unnamed)");
                break;

            case CaptureCommandKind.PopGroup:
                if (groups.Count > 0) {
                    groups.RemoveAt(groups.Count - 1);
                }

                break;

            case CaptureCommandKind.BindPipeline:
                Pipeline = command.A;
                break;

            case CaptureCommandKind.BindDescriptorSet:
                descriptorSets[(DescriptorSetSlot)command.A] = command.B;
                break;

            case CaptureCommandKind.BindVertexBuffer:
                vertexBuffers[(int)command.A] = command.B;
                break;

            case CaptureCommandKind.BindIndexBuffer:
                IndexBuffer = command.A;
                IndexFormat = (IndexFormat)command.B;

                break;

            case CaptureCommandKind.PushConstants:
                PushConstantBytes = command.C;
                break;

            case CaptureCommandKind.SetState:
                switch ((CaptureState)command.A) {
                    case CaptureState.Viewport:
                        Viewport = (command.B, command.C, command.D, command.E);
                        break;

                    case CaptureState.Scissor:
                        Scissor = (command.B, command.C, command.D, command.E);
                        break;

                    case CaptureState.StencilReference:
                        StencilReference = command.B;
                        break;

                    default:
                        break;
                }

                break;

            default:
                break;
        }
    }

    /// <summary>The state as rows, in the order a pane should show them.</summary>
    /// <returns>The rows.</returns>
    public IReadOnlyList<StateRow> Rows() {
        List<StateRow> rows = [];

        rows.Add(new("Target", "Render pass", Pass ?? "(outside a pass)"));

        if (Pass is not null) {
            rows.Add(new("Target", "Colour attachments", ColourAttachments.ToString(CultureInfo.InvariantCulture)));
            rows.Add(new("Target", "Depth-stencil", HasDepth ? "yes" : "no"));
        }

        if (groups.Count > 0) {
            rows.Add(new("Target", "Debug groups", string.Join(" › ", groups)));
        }

        rows.Add(new("Pipeline", "Pipeline", Pipeline is { } pipeline ? Handle(pipeline) : "(none)"));

        if (PushConstantBytes > 0) {
            rows.Add(
                new("Pipeline", "Push constants", PushConstantBytes.ToString(CultureInfo.InvariantCulture) + " bytes")
            );
        }

        // Sorted, because a dictionary's order is its hashing and a state pane whose rows moved
        // between two draws would look like the bindings had changed when they had not.
        foreach (var slot in descriptorSets.Keys.Order()) {
            rows.Add(new("Descriptors", slot.ToString(), Handle(descriptorSets[slot])));
        }

        foreach (var slot in vertexBuffers.Keys.Order()) {
            rows.Add(
                new("Geometry", "Vertex buffer " + slot.ToString(CultureInfo.InvariantCulture), Handle(vertexBuffers[slot]))
            );
        }

        if (IndexBuffer is { } indices) {
            rows.Add(new("Geometry", "Index buffer", Handle(indices) + " (" + IndexFormat + ")"));
        }

        if (Viewport is { } viewport) {
            rows.Add(new("Raster", "Viewport", Rectangle(viewport)));
        }

        if (Scissor is { } scissor) {
            rows.Add(new("Raster", "Scissor", Rectangle(scissor)));
        }

        if (StencilReference != 0) {
            rows.Add(new("Raster", "Stencil reference", StencilReference.ToString(CultureInfo.InvariantCulture)));
        }

        return rows;
    }

    /// <summary>Clears everything a render pass scopes.</summary>
    void ClearPassScoped() {
        Pipeline = null;
        IndexBuffer = null;
        Viewport = null;
        Scissor = null;
        StencilReference = 0;
        PushConstantBytes = 0;

        descriptorSets.Clear();
        vertexBuffers.Clear();
    }

    /// <summary>
    ///     A packed handle as a number a person can compare two rows on.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Shown as the packed value rather than split into index and generation.</b> A capture
    ///     is read by comparing — "is this the same buffer as the one two draws ago" — and the packed
    ///     value is the only form where two handles are equal exactly when they name the same
    ///     resource. A named form would need the device, which a capture outlives.
    /// </remarks>
    static string Handle(long packed) => packed == 0 ? "(none)" : "#" + packed.ToString(CultureInfo.InvariantCulture);

    static string Rectangle((long Width, long Height, long X, long Y) value) =>
        string.Create(CultureInfo.InvariantCulture, $"{value.Width}×{value.Height} at {value.X},{value.Y}");
}
