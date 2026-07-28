// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Graphics.WebGPU;

/// <summary>The queue, and the replay of everything recorded into it.</summary>
/// <remarks>
///     <para>
///         <b>WebGPU has one queue.</b> The RHI has three, so all three of them are this one, and
///         <see cref="GraphicsDeviceFeatures.HasAsyncCompute" /> and its transfer counterpart are
///         both false — which is the honest report and the one a renderer that overlaps work needs
///         to read.
///     </para>
///     <para>
///         Replay is where the deferred stream turns back into API calls, and it is shared: the
///         native and browser surfaces both get it, and neither can drift from the other. It also
///         does the two things WebGPU's model needs that the RHI's does not mention — opening a
///         compute pass around dispatches, because WebGPU has no dispatch outside one, and turning
///         each <c>PushConstants</c> into a ring allocation and a bind.
///     </para>
/// </remarks>
sealed class WebGpuQueue(WebGpuDevice device, QueueKind kind) : ICommandSubmitter {
    /// <inheritdoc />
    public QueueKind Kind => kind;

    /// <inheritdoc />
    public void Submit(ReadOnlySpan<ICommandList> lists) {
        if (lists.IsEmpty) {
            return;
        }

        foreach (var list in lists) {
            if (!list.IsRecorded) {
                throw new InvalidOperationException(
                    "A command list was submitted before Finish() was called on it. WebGPU would have "
                    + "nothing to finish an encoder from."
                );
            }

            if (list is WebGpuCommandList { Submitted: true } already) {
                throw new InvalidOperationException(
                    $"Command list '{already.Name}' was submitted twice. A list is a one-shot recording: "
                    + "its stream is replayed into an encoder and that encoder is consumed."
                );
            }
        }

        device.Replay(lists);
    }

    /// <inheritdoc />
    public void WaitIdle() => device.WaitIdle();
}
