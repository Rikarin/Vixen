// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;

namespace Vixen.Graphics.Null;

/// <summary>Which RHI call a recorded command was.</summary>
public enum RecordedCommandKind : byte {
    /// <summary>A render pass began.</summary>
    BeginRenderPass,

    /// <summary>A render pass ended.</summary>
    EndRenderPass,

    /// <summary>The viewport was set.</summary>
    SetViewport,

    /// <summary>The scissor rectangle was set.</summary>
    SetScissor,

    /// <summary>The blend constant was set.</summary>
    SetBlendConstant,

    /// <summary>The stencil reference was set.</summary>
    SetStencilReference,

    /// <summary>A pipeline was bound.</summary>
    BindPipeline,

    /// <summary>A descriptor set was bound.</summary>
    BindDescriptorSet,

    /// <summary>Push constants were written.</summary>
    PushConstants,

    /// <summary>A vertex buffer was bound.</summary>
    BindVertexBuffer,

    /// <summary>The index buffer was bound.</summary>
    BindIndexBuffer,

    /// <summary>A draw without indices.</summary>
    Draw,

    /// <summary>A draw with indices.</summary>
    DrawIndexed,

    /// <summary>An indirect indexed draw.</summary>
    DrawIndexedIndirect,

    /// <summary>An indexed indirect draw whose count came from a buffer.</summary>
    DrawIndexedIndirectCount,

    /// <summary>A compute dispatch.</summary>
    Dispatch,

    /// <summary>An indirect compute dispatch.</summary>
    DispatchIndirect,

    /// <summary>A barrier group.</summary>
    Barrier,

    /// <summary>A buffer-to-buffer copy.</summary>
    CopyBuffer,

    /// <summary>A buffer-to-texture copy.</summary>
    CopyBufferToTexture,

    /// <summary>A texture-to-buffer copy.</summary>
    CopyTextureToBuffer,

    /// <summary>A texture-to-texture copy.</summary>
    CopyTexture,

    /// <summary>A debug group opened.</summary>
    PushDebugGroup,

    /// <summary>A debug group closed.</summary>
    PopDebugGroup,

    /// <summary>A debug marker.</summary>
    InsertDebugMarker
}

/// <summary>One RHI call, as it was made.</summary>
/// <remarks>
///     <para>
///         Flat and comparable on purpose. An assertion is <c>Assert.Equal(expected, recorded)</c>
///         over a sequence, and the failure message has to be readable by somebody who did not write
///         the test — so the arguments are named in <see cref="ToString" /> rather than being a
///         tuple of numbers.
///     </para>
///     <para>
///         The argument slots are shared between kinds, the same trade
///         <c>Vixen.Platform.PlatformEvent</c> makes: one flat type keeps the stream in order and in
///         one array, and the factory methods on <see cref="CommandRecorder" /> are the only way to
///         build one, so a slot cannot be filled wrongly more than once.
///     </para>
/// </remarks>
/// <param name="Kind">Which call it was.</param>
/// <param name="Sequence">Its position in the stream, from 0.</param>
/// <param name="A">First argument slot.</param>
/// <param name="B">Second argument slot.</param>
/// <param name="C">Third argument slot.</param>
/// <param name="D">Fourth argument slot.</param>
/// <param name="E">Fifth argument slot.</param>
/// <param name="Text">A name, for the debug-group and marker kinds.</param>
public readonly record struct RecordedCommand(
    RecordedCommandKind Kind,
    int Sequence,
    long A = 0,
    long B = 0,
    long C = 0,
    long D = 0,
    long E = 0,
    string? Text = null
) {
    /// <summary>Whether this is the same call with the same arguments, ignoring where in the stream
    /// it happened.</summary>
    /// <param name="other">The command to compare with.</param>
    /// <remarks>
    ///     What an assertion about a subsequence wants. Comparing whole commands would make every
    ///     expectation depend on how many calls happened before it, so inserting a debug marker
    ///     would break twenty tests.
    /// </remarks>
    public bool Matches(RecordedCommand other) =>
        Kind == other.Kind
        && A == other.A
        && B == other.B
        && C == other.C
        && D == other.D
        && E == other.E
        && string.Equals(Text, other.Text, StringComparison.Ordinal);

    /// <inheritdoc />
    public override string ToString() {
        var builder = new StringBuilder(Kind.ToString());

        var arguments = Kind switch {
            RecordedCommandKind.Draw => $"vertices={A} instances={B} firstVertex={C} firstInstance={D}",
            RecordedCommandKind.DrawIndexed =>
                $"indices={A} instances={B} firstIndex={C} vertexOffset={D} firstInstance={E}",
            RecordedCommandKind.Dispatch => $"groups={A}×{B}×{C}",
            RecordedCommandKind.BindPipeline => $"pipeline={A}",
            RecordedCommandKind.BindVertexBuffer => $"slot={A} buffer={B} offset={C}",
            RecordedCommandKind.BindIndexBuffer => $"buffer={A} format={(IndexFormat)B} offset={C}",
            RecordedCommandKind.BindDescriptorSet => $"slot={(DescriptorSetSlot)A} set={B} offsets={C}",
            RecordedCommandKind.BeginRenderPass => $"colour={A} depth={(B != 0 ? "yes" : "no")} '{Text}'",
            RecordedCommandKind.Barrier => $"buffers={A} textures={B}",
            RecordedCommandKind.CopyBuffer => $"from={A}+{B} to={C}+{D} size={E}",
            RecordedCommandKind.SetViewport => $"{A}×{B} at {C},{D}",
            RecordedCommandKind.SetScissor => $"{A}×{B} at {C},{D}",
            RecordedCommandKind.PushConstants => $"stages={(ShaderStage)A} offset={B} size={C} head={D}",
            RecordedCommandKind.PushDebugGroup or RecordedCommandKind.InsertDebugMarker => $"'{Text}'",
            _ => string.Empty
        };

        if (arguments.Length > 0) {
            builder.Append(' ').Append(arguments);
        }

        return string.Create(CultureInfo.InvariantCulture, $"#{Sequence} {builder}");
    }
}
