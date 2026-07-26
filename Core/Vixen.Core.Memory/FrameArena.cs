// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Core.Memory;

/// <summary>
///     The two process-wide arenas: one reset every frame, one for scoped scratch. Both are
///     per-thread, so a worker allocating from them never touches memory another core is writing.
/// </summary>
/// <remarks>
///     <para>
///         <b><see cref="Frame" /></b> holds anything whose lifetime is the frame — render command
///         payloads, culling results, layout scratch — and is reset once per frame by the engine
///         loop, not by whoever allocated from it.
///     </para>
///     <para>
///         <b><see cref="Temp" /></b> is for scratch inside a single call, taken and given back with
///         <c>using var scope = FrameArena.PushTemp();</c>. It exists separately so that a function
///         wanting a scratch buffer does not have to know whether it is inside a frame at all, which
///         matters for the asset pipeline and the editor.
///     </para>
///     <para>
///         Thread-local means each thread pays for its own blocks. That is the intended cost: an
///         arena shared across threads would need a lock on the one operation that has to be an
///         atomic-free pointer bump.
///     </para>
/// </remarks>
public static class FrameArena {
    [ThreadStatic]
    static ArenaAllocator? frame;

    [ThreadStatic]
    static ArenaAllocator? temp;

    /// <summary>This thread's frame arena. Reset once per frame by the engine loop.</summary>
    public static ArenaAllocator Frame => frame ??= new(ArenaAllocator.DefaultBlockSize, "FrameArena.Frame");

    /// <summary>This thread's scratch arena. Used through <see cref="PushTemp" />.</summary>
    public static ArenaAllocator Temp => temp ??= new(ArenaAllocator.DefaultBlockSize, "FrameArena.Temp");

    /// <summary>Opens a scratch scope on this thread's <see cref="Temp" /> arena.</summary>
    /// <returns>A scope to <c>using</c>, which rewinds the arena when it closes.</returns>
    public static ArenaAllocator.Scope PushTemp() => Temp.Push();

    /// <summary>
    ///     Resets this thread's frame arena. Called by the engine loop at a defined frame boundary,
    ///     after everything holding frame memory has finished with it.
    /// </summary>
    /// <remarks>
    ///     Every pointer handed out by this thread's frame arena becomes dangling. That is what the
    ///     defined boundary is for.
    /// </remarks>
    public static void ResetFrame() => frame?.Reset();

    /// <summary>
    ///     Releases this thread's arenas entirely. For a worker thread shutting down, and for tests,
    ///     which would otherwise see each other's high-water marks.
    /// </summary>
    public static void Release() {
        frame?.Dispose();
        temp?.Dispose();
        frame = null;
        temp = null;
    }
}
