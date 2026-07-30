// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;

namespace Vixen.Rendering.Lighting;

/// <summary>An <see cref="IIrradianceCaptureSource" /> that renders each probe's cube on a device.</summary>
/// <remarks>
///     <para>
///         The join between <see cref="IrradianceCubeCapture" />, which records six passes, and
///         <see cref="CapturedIrradianceFiller" />, which wants a finished cube in hand. Everything
///         between the two is submit-and-wait, and that is the whole of this type.
///     </para>
///     <para>
///         ⚠ <b>One submit and one full stall per probe.</b> A field of a thousand probes is a
///         thousand round trips to the GPU, which is minutes rather than a frame — and that is the
///         right shape for what this is: doc 19 § L2's filler B is a build step, and
///         <see cref="CapturedIrradianceFiller.Fill(IrradianceFields.IrradianceField, int)" /> is
///         budgeted so it can report progress a brick at a time. Recording every probe of a budget
///         into one list and reading them all back afterwards is the obvious improvement and is not
///         done; it wants a ring of targets rather than the one this reuses.
///     </para>
///     <para>
///         <b>It cannot be used inside a frame.</b> The stall is the point: a capture is only
///         meaningful once the copies have run, and nothing else can wait for them on the caller's
///         behalf.
///     </para>
/// </remarks>
public sealed class RenderedIrradianceCaptures : IIrradianceCaptureSource, IDisposable {
    readonly IGraphicsDevice device;
    readonly IrradianceCubeCapture cube;
    readonly IrradianceCubeCapture.DrawFace draw;

    bool disposed;

    /// <summary>Builds a source over a capture and something that draws a scene.</summary>
    /// <param name="device">The device to submit on.</param>
    /// <param name="capture">The capture that records the six faces. This takes ownership of it.</param>
    /// <param name="draw">What to draw into each face.</param>
    /// <exception cref="ArgumentNullException">An argument is missing.</exception>
    public RenderedIrradianceCaptures(
        IGraphicsDevice device,
        IrradianceCubeCapture capture,
        IrradianceCubeCapture.DrawFace draw
    ) {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(draw);

        this.device = device;
        this.cube = capture;
        this.draw = draw;
    }

    /// <summary>Where probes are worth capturing, or null for everywhere.</summary>
    /// <remarks>
    ///     <para>
    ///         A field is a box and a scene is not, so a field large enough to cover a level has
    ///         probes in places the level does not go. Refusing them is what
    ///         <see cref="CapturedIrradianceFiller.Skipped" /> counts, and it is cheaper than a
    ///         capture that renders six empty faces to discover the same thing.
    ///     </para>
    ///     <para>
    ///         ⚠ Refusing leaves the probe holding whatever it held, which for a fresh field is
    ///         nothing at all. That is the right answer — <see cref="IrradianceFields.IrradianceField.Dilate" />
    ///         is what fills a probe nobody could capture — and it is only right because a refusal and
    ///         a black capture are kept distinct.
    ///     </para>
    /// </remarks>
    public BoundingBox? Bounds { get; set; }

    /// <summary>What to run once per probe, before its six passes are recorded.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Because six views cannot be culled from inside a render pass.</b> A capture that
    ///         draws the real scene draws it through <c>RenderSystem</c>, which wants the frame's
    ///         views set, culled, prepared and sorted before anything records — and the six views a
    ///         probe needs are not known until the probe's position is. This is where that happens.
    ///     </para>
    ///     <para>
    ///         Also where a descriptor allocator's frame begins, for the same reason: a capture is a
    ///         frame's worth of descriptors and there is no frame around it to begin one.
    ///     </para>
    ///     <para>
    ///         Null for a caller drawing something simpler than a scene — a fixture with one pipeline
    ///         and one vertex buffer needs no preparation at all, and most of the tests here are that.
    ///     </para>
    /// </remarks>
    public Action<Vector3>? Prepare { get; set; }

    /// <summary>Run once a probe's list is recorded and before it is submitted.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The last point at which a validation layer's verdict is still actionable.</b>
    ///         Everything a layer has to say about <i>recording</i> — a set the pipeline needs and
    ///         nothing bound, attachment formats that disagree with the pass — it has already said by
    ///         the time the list is finished. Submitting anyway submits work the driver has been told
    ///         is invalid, and that is a GPU fault and a dead process rather than a failure anybody
    ///         can read. <c>Fixture.Render</c> in the golden tests learnt this at the cost of
    ///         fifty-six tests that never ran.
    ///     </para>
    ///     <para>
    ///         Throwing from here is the intended use. It is a hook rather than a check of this
    ///         type's own because what counts as a diagnostic belongs to a backend, and nothing in
    ///         <c>Vixen.Rendering</c> knows which one is underneath.
    ///     </para>
    /// </remarks>
    public Action? Recorded { get; set; }

    /// <summary>How many probes were captured.</summary>
    public int Captured { get; private set; }

    /// <inheritdoc />
    public bool TryCapture(Vector3 position, out IrradianceCapture capture) {
        ObjectDisposedException.ThrowIf(disposed, this);

        capture = default;

        if (Bounds is { } box && !box.Contains(position)) {
            return false;
        }

        // ⚠ Before the frame begins, not inside it. Culling and sorting touch nothing on the device,
        // and a caller that begins a descriptor frame here needs to do it before the list that
        // allocates from it exists.
        Prepare?.Invoke(position);

        device.BeginFrame();

        using (var commands = device.BeginCommandList(QueueKind.Graphics, "irradiance capture")) {
            cube.Record(commands, position, draw);
            commands.Finish();
            Recorded?.Invoke();
            device.GraphicsQueue.Submit([commands]);
        }

        device.EndFrame();

        // ⚠ Before the read, and it is the whole reason this type submits rather than recording into
        // a list the caller owns: a readback buffer read before its copy has run holds zeros, and a
        // probe that saw nothing is a plausible answer rather than an obvious failure.
        device.WaitIdle();

        if (!cube.TryRead(out capture)) {
            return false;
        }

        Captured++;

        return true;
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;
        cube.Dispose();
    }
}
