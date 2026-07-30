// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Graphics.Null;

namespace Vixen.Editor.Debugger;

/// <summary>Turns the Null backend's recorded stream into a capture the panel can step through.</summary>
/// <remarks>
///     <para>
///         <b>Doc 20's E4 names this: "`Vixen.Graphics.Null`'s recording harness is the shape" a
///         frame capture takes.</b> It is the only recording path the engine has today, and it is a
///         real one — the editor's own frame, recorded against a Null device, is a stream of the same
///         calls a Vulkan device would receive.
///     </para>
///     <para>
///         ⚠ <b>The one file in this assembly that knows a backend exists.</b> Everything else takes
///         <see cref="CapturedCommand" />, so a Vulkan command-stream hook — doc 13's eventual
///         answer, and the one that can capture a frame that is actually on screen — arrives as a
///         second adapter beside this one rather than as a change to the frame debugger.
///     </para>
///     <para>
///         ⚠ <b>What this cannot give is the intermediate render target.</b> Doc 13 wants stepping to
///         draw N to <i>present</i> what the frame had drawn by then, which needs a device that
///         actually executed the calls. A Null capture has the state and not the pixels, and the
///         panel says so rather than showing an empty image somebody would read as a black target.
///     </para>
/// </remarks>
public static class NullFrameCapture {
    /// <summary>Converts a recorder's stream.</summary>
    /// <param name="recorder">The recorder.</param>
    /// <param name="name">What to call the capture.</param>
    /// <returns>The capture.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="recorder" /> is null.</exception>
    public static FrameCapture From(CommandRecorder recorder, string name = "Captured frame") {
        ArgumentNullException.ThrowIfNull(recorder);

        return From(recorder.Commands, name);
    }

    /// <summary>Converts a stream somebody else took out of a recorder.</summary>
    /// <param name="stream">The commands, in submission order.</param>
    /// <param name="name">What to call the capture.</param>
    /// <returns>The capture.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream" /> is null.</exception>
    public static FrameCapture From(IReadOnlyList<RecordedCommand> stream, string name = "Captured frame") {
        ArgumentNullException.ThrowIfNull(stream);

        List<CapturedCommand> converted = new(stream.Count);

        foreach (var command in stream) {
            if (Translate(command) is { } translated) {
                converted.Add(translated);
            }
        }

        return new(name, converted);
    }

    /// <summary>
    ///     One recorded call as a captured one, or <see langword="null" /> for a call the frame
    ///     debugger has nothing to say about.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The indirect draws collapse into <see cref="CaptureCommandKind.Draw" /> with a zero
    ///     count.</b> That is honest rather than lossy: the whole point of an indirect draw is that
    ///     the count is in a buffer the CPU has not read, so a capture claiming a number would be
    ///     claiming one it made up. The row says indirect and names the argument buffer.
    /// </remarks>
    static CapturedCommand? Translate(RecordedCommand command) =>
        command.Kind switch {
            RecordedCommandKind.BeginRenderPass => new(
                command.Sequence,
                CaptureCommandKind.BeginPass,
                command.Text,
                command.A,
                command.B
            ),
            RecordedCommandKind.EndRenderPass => new(command.Sequence, CaptureCommandKind.EndPass),
            RecordedCommandKind.PushDebugGroup => new(command.Sequence, CaptureCommandKind.PushGroup, command.Text),
            RecordedCommandKind.PopDebugGroup => new(command.Sequence, CaptureCommandKind.PopGroup),
            RecordedCommandKind.InsertDebugMarker => new(command.Sequence, CaptureCommandKind.Marker, command.Text),

            RecordedCommandKind.SetViewport => new(
                command.Sequence,
                CaptureCommandKind.SetState,
                A: (long)CaptureState.Viewport,
                B: command.A,
                C: command.B,
                D: command.C,
                E: command.D
            ),
            RecordedCommandKind.SetScissor => new(
                command.Sequence,
                CaptureCommandKind.SetState,
                A: (long)CaptureState.Scissor,
                B: command.A,
                C: command.B,
                D: command.C,
                E: command.D
            ),
            RecordedCommandKind.SetBlendConstant => new(
                command.Sequence,
                CaptureCommandKind.SetState,
                A: (long)CaptureState.BlendConstant
            ),
            RecordedCommandKind.SetStencilReference => new(
                command.Sequence,
                CaptureCommandKind.SetState,
                A: (long)CaptureState.StencilReference,
                B: command.A
            ),

            RecordedCommandKind.BindPipeline => new(command.Sequence, CaptureCommandKind.BindPipeline, A: command.A),
            RecordedCommandKind.BindDescriptorSet => new(
                command.Sequence,
                CaptureCommandKind.BindDescriptorSet,
                A: command.A,
                B: command.B,
                C: command.C
            ),
            RecordedCommandKind.BindVertexBuffer => new(
                command.Sequence,
                CaptureCommandKind.BindVertexBuffer,
                A: command.A,
                B: command.B,
                C: command.C
            ),
            RecordedCommandKind.BindIndexBuffer => new(
                command.Sequence,
                CaptureCommandKind.BindIndexBuffer,
                A: command.A,
                B: command.B,
                C: command.C
            ),
            RecordedCommandKind.PushConstants => new(
                command.Sequence,
                CaptureCommandKind.PushConstants,
                A: command.A,
                B: command.B,
                C: command.C
            ),

            RecordedCommandKind.Draw => new(
                command.Sequence,
                CaptureCommandKind.Draw,
                A: command.A,
                B: command.B,
                C: command.C
            ),
            RecordedCommandKind.DrawIndexed => new(
                command.Sequence,
                CaptureCommandKind.Draw,
                A: command.A,
                B: command.B,
                C: command.C,
                D: 1
            ),
            RecordedCommandKind.DrawIndexedIndirect or RecordedCommandKind.DrawIndexedIndirectCount => new(
                command.Sequence,
                CaptureCommandKind.Draw,
                "indirect from buffer #" + command.A.ToString(System.Globalization.CultureInfo.InvariantCulture),
                D: 1
            ),

            RecordedCommandKind.Dispatch => new(
                command.Sequence,
                CaptureCommandKind.Dispatch,
                A: command.A,
                B: command.B,
                C: command.C
            ),
            RecordedCommandKind.DispatchIndirect => new(command.Sequence, CaptureCommandKind.Dispatch),

            RecordedCommandKind.Barrier => new(
                command.Sequence,
                CaptureCommandKind.Barrier,
                A: command.A,
                B: command.B
            ),

            RecordedCommandKind.CopyBuffer => new(command.Sequence, CaptureCommandKind.Copy, "Copy buffer"),
            RecordedCommandKind.CopyBufferToTexture => new(
                command.Sequence,
                CaptureCommandKind.Copy,
                "Copy buffer to texture"
            ),
            RecordedCommandKind.CopyTextureToBuffer => new(
                command.Sequence,
                CaptureCommandKind.Copy,
                "Copy texture to buffer"
            ),
            RecordedCommandKind.CopyTexture => new(command.Sequence, CaptureCommandKind.Copy, "Copy texture"),

            // ⚠ Dropped rather than shown. A timestamp write is the *profiler* recording into this
            // frame, and a capture that listed the diagnostic's own commands would be reporting on
            // itself — which is how a frame debugger ends up showing two extra calls per pass that
            // are not in the frame anybody shipped.
            _ => null
        };
}
