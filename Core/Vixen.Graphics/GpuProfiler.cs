// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Graphics;

/// <summary>One timed region of a frame on the GPU.</summary>
/// <param name="Name">What it is called — the render-graph pass's name, in practice.</param>
/// <param name="Level">How deeply nested it is, for drawing.</param>
/// <param name="BeginTicks">The GPU clock at its start.</param>
/// <param name="EndTicks">The GPU clock at its end.</param>
public readonly record struct GpuScope(string Name, int Level, ulong BeginTicks, ulong EndTicks) {
    /// <summary>How many ticks it took.</summary>
    public ulong DurationTicks => EndTicks >= BeginTicks ? EndTicks - BeginTicks : 0;
}

/// <summary>One frame's GPU work, resolved and converted to a readable unit.</summary>
/// <param name="FrameIndex">Which frame it was recorded in.</param>
/// <param name="Scopes">The regions, in the order they were opened.</param>
/// <param name="Period">The device's nanoseconds per tick.</param>
public sealed record GpuFrame(int FrameIndex, IReadOnlyList<GpuScope> Scopes, float Period) {
    /// <summary>The frame with nothing in it.</summary>
    public static GpuFrame Empty { get; } = new(0, [], 0f);

    /// <summary>The earliest reading in the frame, which everything else is drawn relative to.</summary>
    /// <remarks>
    ///     ⚠ <b>Relative, because a GPU timestamp's zero point means nothing.</b> It is comparable
    ///     with another reading from the same device and with nothing on the CPU — lining the two up
    ///     needs a calibrated pair, which is an extension a good many drivers do not have. See
    ///     <see cref="GpuTimestamps" />.
    /// </remarks>
    public ulong BeginTicks {
        get {
            var earliest = ulong.MaxValue;

            foreach (var scope in Scopes) {
                earliest = Math.Min(earliest, scope.BeginTicks);
            }

            return earliest == ulong.MaxValue ? 0 : earliest;
        }
    }

    /// <summary>How long the whole frame took on the GPU, in milliseconds.</summary>
    public double Milliseconds {
        get {
            var last = 0ul;
            var first = BeginTicks;

            foreach (var scope in Scopes) {
                last = Math.Max(last, scope.EndTicks);
            }

            return last <= first ? 0d : GpuTimestamps.ToMilliseconds(last - first, Period);
        }
    }

    /// <summary>How long one scope took, in milliseconds.</summary>
    /// <param name="scope">The scope.</param>
    /// <returns>Its duration.</returns>
    public double MillisecondsOf(GpuScope scope) => GpuTimestamps.ToMilliseconds(scope.DurationTicks, Period);

    /// <summary>Where a reading falls in the frame's window.</summary>
    /// <param name="ticks">The reading.</param>
    /// <returns>Zero at the frame's start, one at its end.</returns>
    public float Fraction(ulong ticks) {
        var first = BeginTicks;
        var last = 0ul;

        foreach (var scope in Scopes) {
            last = Math.Max(last, scope.EndTicks);
        }

        return last <= first || ticks <= first ? 0f : (float)Math.Clamp((ticks - first) / (double)(last - first), 0d, 1d);
    }
}

/// <summary>Something that turns a named region of a command list into a timed scope.</summary>
/// <remarks>
///     <para>
///         <b>The seam that lets the render graph time its passes without knowing what a profiler
///         is.</b> A graph holds one of these or holds nothing; when it holds nothing the emission is
///         a null check per pass and no GPU work at all, which is the only way a per-pass timestamp
///         pair can be the default-off feature it has to be.
///     </para>
///     <para>
///         ⚠ <b>Off by default is not caution, it is correctness on a tiler.</b> MoltenVK is the
///         development target here, and on tile-based hardware a query write can force the tile to be
///         resolved — so an always-on timestamp pair around every pass changes the very timings it
///         reports. A frame profiled and a frame shipped have to be the same frame, and that is only
///         true when the instrument can be taken out.
///     </para>
///     <para>
///         An interface rather than the concrete <see cref="GpuProfiler" /> because a timestamp pool
///         is one way to answer this and not the only one: a Tracy or PIX sink emits markers a host
///         tool timestamps for it, and would implement this without a
///         <see cref="QueryPoolHandle" /> anywhere in sight.
///     </para>
/// </remarks>
public interface IGpuScopeSink {
    /// <summary>Opens a named region.</summary>
    /// <param name="commands">The list the region's work is recorded into.</param>
    /// <param name="name">What to call it.</param>
    /// <returns>A token for <see cref="Close" />, or <see langword="null" /> when nothing was opened.</returns>
    /// <remarks>
    ///     ⚠ <b>Returning <see langword="null" /> rather than throwing is part of the contract.</b> A
    ///     frame with one pass more than the sink can hold is a timeline missing a bar; a frame that
    ///     threw is a renderer that stops drawing because a diagnostic is attached.
    /// </remarks>
    int? Begin(ICommandList commands, string name);

    /// <summary>Closes a region.</summary>
    /// <param name="commands">The list the region's work was recorded into.</param>
    /// <param name="token">What <see cref="Begin" /> returned. <see langword="null" /> does nothing.</param>
    void Close(ICommandList commands, int? token);
}

/// <summary>
///     Records a frame's passes into a device's query pools and reads them back once the GPU has
///     caught up.
/// </summary>
/// <remarks>
///     <para>
///         <b>Doc 20 says the GPU profiler is the one item in E4 that "cannot start with the
///         panel", and this is what it was waiting for.</b> The RHI now has a query pool, a
///         <c>WriteTimestamp</c> and a resolve path; what this class owns is the part above them —
///         which pool a frame writes into, when it is safe to read one back, and how a pair of
///         readings becomes a named region.
///     </para>
///     <para>
///         ⚠ <b>It lives beside the RHI and not in the editor, because the thing being measured is a
///         game's frame.</b> A profiler a game cannot reference is a profiler that can only ever
///         report on the editor, and the editor's frame is not the frame anybody ships. The panels
///         that draw a <see cref="GpuFrame" /> stay in <c>Vixen.Editor.Profiler</c>; what moved down
///         here is only the instrument.
///     </para>
///     <para>
///         ⚠ <b>One pool per frame in flight, and it is not an optimisation.</b> A single pool
///         written every frame is one the GPU is still writing while the CPU reads it, so the
///         readings are a mixture of two frames — which draws as passes that overlap impossibly.
///         With <c>FramesInFlight</c> pools, the frame being recorded and the frame being read are
///         never the same object.
///     </para>
///     <para>
///         ⚠ <b>Reading never waits.</b> <see cref="Resolve" /> asks for the oldest pool and takes
///         <see langword="false" /> for an answer, because the alternative is a stall on the frame
///         thread once per frame — a profiler that halves the frame rate it is reporting. The first
///         few frames after attaching therefore produce nothing, which is correct and is why
///         <see cref="Latest" /> starts empty.
///     </para>
/// </remarks>
public sealed class GpuProfiler : IGpuScopeSink, IDisposable {
    /// <summary>How many regions one frame may record when no ceiling is given.</summary>
    /// <remarks>
    ///     <para>
    ///         Two queries each, so this is a pool of 512 — a couple of kilobytes on the device.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Raised from 64 when the render graph started emitting a scope per pass.</b>
    ///         Sixty-four was "more than any frame this engine records" while the only caller was a
    ///         host timing one region by hand; sample 13's standard frame declares far more passes
    ///         than that, and a ceiling reached is a timeline that silently stops halfway through the
    ///         frame — the bars that are there look right, and the expensive pass you opened the
    ///         panel for is simply absent. Sizing it to the frame is not an option: the pools cannot
    ///         be recreated mid-flight without racing the frames still reading them.
    ///     </para>
    /// </remarks>
    public const int DefaultScopeCapacity = 256;

    readonly IGraphicsDevice device;
    readonly QueryPoolHandle[] pools;
    readonly List<PendingScope>[] pending;
    readonly int[] frames;
    readonly ulong[] readings;

    int slot;
    int open;
    bool disposed;

    /// <summary>Attaches to a device.</summary>
    /// <param name="device">The device whose frames are being timed.</param>
    /// <param name="scopeCapacity">How many regions one frame may record.</param>
    /// <exception cref="ArgumentNullException"><paramref name="device" /> is null.</exception>
    /// <exception cref="NotSupportedException">The device reports no timestamp queries.</exception>
    public GpuProfiler(IGraphicsDevice device, int scopeCapacity = DefaultScopeCapacity) {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(scopeCapacity);

        if (!device.Features.HasTimestampQueries) {
            throw new NotSupportedException(
                "This device reports no timestamp queries, so its frames cannot be timed. Ask "
                + "Features.HasTimestampQueries and show the reason rather than an empty timeline."
            );
        }

        this.device = device;

        ScopeCapacity = scopeCapacity;
        Period = device.Features.TimestampPeriod;

        // ⚠ One more than the device's frames in flight. `TryResolveQueries` answers for a
        // submission the GPU has finished, and the frame that has *just* been submitted is not one
        // of those — so with exactly FramesInFlight pools the oldest is still the one being written.
        var depth = Math.Max(2, device.FramesInFlight + 1);

        pools = new QueryPoolHandle[depth];
        pending = new List<PendingScope>[depth];
        frames = new int[depth];
        readings = new ulong[scopeCapacity * 2];

        for (var index = 0; index < depth; index++) {
            pools[index] = device.CreateQueryPool(
                new(QueryKind.Timestamp, scopeCapacity * 2, $"gpu profiler {index}")
            );

            pending[index] = [];
            frames[index] = -1;
        }

        Latest = GpuFrame.Empty;
    }

    /// <summary>How many regions one frame may record.</summary>
    public int ScopeCapacity { get; }

    /// <summary>The device's nanoseconds per tick.</summary>
    public float Period { get; }

    /// <summary>The most recent frame that has come back from the GPU.</summary>
    public GpuFrame Latest { get; private set; }

    /// <summary>How many frames were recorded but never read, because the panel closed first.</summary>
    public int Abandoned { get; private set; }

    /// <summary>How many regions the frame being recorded asked for beyond <see cref="ScopeCapacity" />.</summary>
    /// <remarks>
    ///     ⚠ <b>The number that says a timeline is lying by omission.</b> Overflow drops a region
    ///     rather than throwing, which is the right trade in a frame and the wrong one to keep quiet
    ///     about: a timeline whose last third is missing looks exactly like a frame whose last third
    ///     is free. Non-zero means raise <see cref="ScopeCapacity" />.
    /// </remarks>
    public int Dropped { get; private set; }

    /// <summary>Starts recording a frame's regions into the next pool.</summary>
    /// <param name="commands">The list the frame's passes are recorded into.</param>
    /// <param name="frameIndex">Which frame this is, for labelling.</param>
    /// <exception cref="ArgumentNullException"><paramref name="commands" /> is null.</exception>
    /// <remarks>
    ///     ⚠ <b>Must be called outside a render pass, because the reset it records is.</b> Vulkan
    ///     will not reset a query inside a pass, and a reset performed from the host between
    ///     submissions would be racing the frame still reading it.
    /// </remarks>
    public void BeginFrame(ICommandList commands, int frameIndex) {
        ArgumentNullException.ThrowIfNull(commands);
        ObjectDisposedException.ThrowIf(disposed, this);

        slot = (slot + 1) % pools.Length;

        if (pending[slot].Count > 0) {
            Abandoned++;
        }

        pending[slot].Clear();
        frames[slot] = frameIndex;
        open = 0;
        Dropped = 0;

        commands.ResetQueries(pools[slot], 0, ScopeCapacity * 2);
    }

    /// <summary>Opens a named region.</summary>
    /// <param name="commands">The list the region's work is recorded into.</param>
    /// <param name="name">What to call it.</param>
    /// <returns>
    ///     A token for <see cref="Close" />, or <see langword="null" /> when the frame has already
    ///     recorded <see cref="ScopeCapacity" /> regions.
    /// </returns>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    /// <remarks>
    ///     ⚠ <b>Running out of capacity drops the region rather than throwing.</b> A frame with one
    ///     pass too many is a timeline missing a bar; a frame that threw is a renderer that stops
    ///     drawing because a diagnostic panel is open, which is a much worse trade. What was dropped
    ///     is counted in <see cref="Dropped" /> rather than lost silently.
    /// </remarks>
    public int? Begin(ICommandList commands, string name) {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(name);
        ObjectDisposedException.ThrowIf(disposed, this);

        if (pending[slot].Count >= ScopeCapacity) {
            Dropped++;
            return null;
        }

        var index = pending[slot].Count;
        pending[slot].Add(new(name, open++));

        commands.WriteTimestamp(pools[slot], index * 2);
        return index;
    }

    /// <summary>Closes a region.</summary>
    /// <param name="commands">The list the region's work was recorded into.</param>
    /// <param name="token">What <see cref="Begin" /> returned. <see langword="null" /> does nothing.</param>
    /// <exception cref="ArgumentNullException"><paramref name="commands" /> is null.</exception>
    public void Close(ICommandList commands, int? token) {
        ArgumentNullException.ThrowIfNull(commands);
        ObjectDisposedException.ThrowIf(disposed, this);

        if (token is not { } index) {
            return;
        }

        open = Math.Max(0, open - 1);
        commands.WriteTimestamp(pools[slot], (index * 2) + 1);
    }

    /// <summary>Reads back whatever the GPU has finished, without waiting for it.</summary>
    /// <returns>Whether a frame came back.</returns>
    /// <remarks>
    ///     Called once a frame, after <see cref="BeginFrame" />: the pool it asks about is the one
    ///     furthest from the one being written, which is the oldest submission still on the device.
    /// </remarks>
    public bool Resolve() {
        ObjectDisposedException.ThrowIf(disposed, this);

        var oldest = (slot + 1) % pools.Length;
        var scopes = pending[oldest];

        if (scopes.Count == 0) {
            return false;
        }

        var wanted = scopes.Count * 2;

        if (!device.TryResolveQueries(pools[oldest], 0, readings.AsSpan(0, wanted))) {
            return false;
        }

        var resolved = new GpuScope[scopes.Count];

        for (var index = 0; index < scopes.Count; index++) {
            resolved[index] = new(
                scopes[index].Name,
                scopes[index].Level,
                readings[index * 2],
                readings[(index * 2) + 1]
            );
        }

        Latest = new(frames[oldest], resolved, Period);

        // Emptied, so the same frame is not reported twice while the next one is still in flight —
        // which would draw as the timeline freezing rather than as it waiting.
        scopes.Clear();

        return true;
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        foreach (var pool in pools) {
            device.Destroy(pool);
        }
    }

    readonly record struct PendingScope(string Name, int Level);
}
