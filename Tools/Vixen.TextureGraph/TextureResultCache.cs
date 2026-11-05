// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.IO.Hashing;
using Vixen.Graphics;

namespace Vixen.TextureGraph;

/// <summary>What one image's texels are a function of, as a number.</summary>
/// <param name="Value">The 128-bit hash.</param>
/// <remarks>
///     <b>Content rather than identity.</b> Two ops anywhere — in two plans, or in the plan before and
///     the plan after an edit — with one key produce one picture, so nothing about a key says which
///     node it came from, which graph, or how the plan that held it was built. That is what lets the
///     evaluator ignore the difference between a value edit and a recompile.
/// </remarks>
public readonly record struct TextureResultKey(UInt128 Value) {
    /// <summary>Whether this names anything. The default key does not.</summary>
    public bool IsValid => Value != UInt128.Zero;

    /// <summary>The key of a run of bytes, which is what a caller's picture is identified by.</summary>
    /// <param name="bytes">The texels.</param>
    /// <returns>Their digest.</returns>
    public static TextureResultKey Of(ReadOnlySpan<byte> bytes) => new(XxHash128.HashToUInt128(bytes));
}

/// <summary>The ingredients of a <see cref="TextureResultKey" />, one flag each.</summary>
/// <remarks>
///     ⚠ <b>A switch for the tests and for nothing else.</b> A key missing an ingredient serves a stale
///     picture and says nothing, so the only proof that an ingredient is in the hash for a reason is a
///     suite that goes red without it — and a sabotage kept as a flag runs on every build, where one
///     done by hand is true on the day it was done. Production never reads anything but
///     <see cref="All" />.
/// </remarks>
[Flags]
enum TextureKeyParts {
    None = 0,

    /// <summary>The kernel's source and the library it compiles with, or a CPU op's identity.</summary>
    Kernel = 1,

    /// <summary>The uniform block as it reaches the GPU, the seed member aside.</summary>
    Parameters = 2,

    /// <summary>The keys of the images it reads, in binding order.</summary>
    Inputs = 4,

    /// <summary>The size of the image it writes.</summary>
    Resolution = 8,

    /// <summary>The format of the image it writes.</summary>
    Format = 16,

    /// <summary>The op's hashed seed, for a kernel that declares one.</summary>
    Seed = 32,

    /// <summary>The digest of a picture the caller supplied.</summary>
    Content = 64,

    All = Kernel | Parameters | Inputs | Resolution | Format | Seed | Content
}

/// <summary>The results of past evaluations, held as textures on the device, by content.</summary>
/// <remarks>
///     <para>
///         <b>Least recently used, against a byte budget.</b> Recency is "was part of the last plan",
///         and every result the current plan could use is touched whether or not this evaluation
///         read it: otherwise a long slider drag ages out the images upstream of the edit, which are
///         exactly the ones the next edit needs, in favour of the pictures of slider positions the
///         author has already left.
///     </para>
///     <para>
///         ⚠ <b>The budget binds between evaluations and is soft during one.</b> A live
///         <see cref="TextureBake" /> pins what it can read, and a texture a bake is holding cannot be
///         destroyed whatever the arithmetic says, so an evaluation whose outputs exceed the budget
///         runs and is trimmed when the bake is disposed. Every other image of that evaluation is
///         pooled exactly as the whole-plan path pools it, rather than kept, so a budget of zero is
///         slower and not wrong. With no bake alive <see cref="Bytes" /> is at most
///         <see cref="Budget" />.
///     </para>
///     <para>
///         ⚠ <b>Nothing is destroyed outside an idle device.</b> Eviction happens at the top of an
///         evaluation and when a bake is disposed, and both follow a <c>WaitIdle</c> — the argument
///         <c>TexturePlanEvaluator.EvictAuthored</c> makes for its pipelines. A texture evicted during
///         a run would be one the open command list may already have bound.
///     </para>
///     <para>
///         <b>A disk tier is deferred, and not for lack of a key.</b> The key is content and carries
///         no adapter, so what a disk tier owes is first a statement of what it may be shared across
///         — this suite proves byte identity on one device and nothing wider — and second a
///         read-back per result, which on the interactive path is the cost this type exists to
///         remove.
///     </para>
/// </remarks>
public sealed class TextureResultCache {
    /// <summary>What <see cref="Budget" /> starts at: two gibibytes.</summary>
    /// <remarks>
    ///     A 2K image is 32 MB at <see cref="TextureFormat.Rgba16Float" />, so this holds a forty-node
    ///     graph of those with room for the images a drag leaves behind.
    /// </remarks>
    public const long DefaultBudget = 2L << 30;

    readonly IGraphicsDevice device;
    readonly Dictionary<TextureResultKey, Entry> entries = [];
    readonly LinkedList<Entry> order = [];
    readonly Dictionary<TexturePoolSlot, Stack<Rented>> spare = [];

    long held;
    long idle;
    ulong nonces;
    bool disposed;

    internal TextureResultCache(IGraphicsDevice device) => this.device = device;

    /// <summary>How many bytes of textures this may hold while no bake is alive.</summary>
    /// <remarks>Lowering it takes effect at the next evaluation.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">The budget is negative.</exception>
    public long Budget {
        get;
        set {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            field = value;
        }
    } = DefaultBudget;

    /// <summary>How many bytes of textures this holds right now.</summary>
    public long Bytes => held + idle;

    /// <summary>How many results this holds right now.</summary>
    public int Count => entries.Count;

    /// <summary>How many ops have been answered from here, across every evaluation.</summary>
    public long Hits { get; private set; }

    /// <summary>How many ops had to run, across every evaluation.</summary>
    public long Misses { get; private set; }

    /// <summary>How many results have been dropped to stay under the budget.</summary>
    public long Evictions { get; private set; }

    /// <summary>What goes into a key. <see cref="TextureKeyParts.All" /> outside a sabotage test.</summary>
    internal TextureKeyParts Parts { get; set; } = TextureKeyParts.All;

    /// <summary>Drops every result nothing is reading.</summary>
    public void Clear() {
        ObjectDisposedException.ThrowIf(disposed, this);

        device.WaitIdle();
        DropUnpinned(0);
    }

    /// <summary>The bytes one texture of a shape takes.</summary>
    internal static long BytesOf(TexturePoolSlot shape) =>
        (long)shape.Width * shape.Height * TextureFormats.BytesPerTexel(shape.Format);

    /// <summary>A key that no other evaluation can produce, for a result nothing can name.</summary>
    /// <remarks>
    ///     ⚠ <b>What an unknown input costs, and it is a miss rather than a guess.</b> A picture
    ///     supplied with no digest may be a canvas that has changed since, behind a handle that has
    ///     not, so everything computed from it runs every time.
    /// </remarks>
    internal TextureResultKey Nonce() => new(new UInt128(0xDEADBEEFUL, ++nonces));

    /// <summary>The result under a key, if there is one.</summary>
    /// <param name="key">The key.</param>
    /// <param name="shape">The shape the plan says that image has.</param>
    /// <returns>The entry, or <see langword="null" />.</returns>
    /// <exception cref="InvalidOperationException">The entry is not the shape the plan says.</exception>
    /// <remarks>
    ///     ⚠ <b>Refused rather than treated as a miss.</b> The size and the format are in the key, so
    ///     a mismatch is either a 128-bit collision or a key that has lost an ingredient, and the
    ///     second is a defect that treating this as a miss would hide everywhere except the images
    ///     that happen to share a shape.
    /// </remarks>
    internal Entry? Find(TextureResultKey key, TexturePoolSlot shape) {
        if (!entries.TryGetValue(key, out var entry)) {
            return null;
        }

        if (entry.Shape != shape) {
            throw new InvalidOperationException(
                $"The result cached under {key.Value:X32} is {entry.Shape} and the plan says that image is {shape}. "
                + "Size and format are both in the key, so either a key has lost an ingredient or this is a "
                + "128-bit collision, and it is not the second."
            );
        }

        return entry;
    }

    /// <summary>Marks a result as part of the current plan.</summary>
    internal void Touch(Entry entry) {
        order.Remove(entry.Node);
        order.AddLast(entry.Node);
    }

    /// <summary>Stops a result being evicted until <see cref="Unpin" />.</summary>
    internal static void Pin(Entry entry) => entry.Pins++;

    /// <summary>Lets a result go.</summary>
    internal void Unpin(Entry entry) {
        entry.Pins--;

        // ⚠ The evaluator went first, and a bake that outlived it must still be readable: a texture
        // a bake holds is destroyed by whoever lets go of it last.
        if (disposed && entry.Pins == 0) {
            Destroy(entry.Texture, entry.View);
        }
    }

    /// <summary>Counts one evaluation.</summary>
    internal void Tally(long hits, long misses) {
        Hits += hits;
        Misses += misses;
    }

    /// <summary>A texture of a shape, recycled when one is idle.</summary>
    /// <param name="shape">What is wanted.</param>
    /// <param name="name">What to call a new one.</param>
    /// <returns>The texture and its view, which the caller gives back or files.</returns>
    internal Rented Rent(TexturePoolSlot shape, string name) {
        if (spare.TryGetValue(shape, out var stack) && stack.TryPop(out var recycled)) {
            idle -= BytesOf(shape);

            return recycled;
        }

        var texture = device.CreateTexture(
            new(
                TextureFormats.Pixel(shape.Format),
                shape.Width,
                shape.Height,
                TexturePlanEvaluator.PooledUsage,
                Name: name
            )
        );

        return new(shape, texture, device.CreateTextureView(texture));
    }

    /// <summary>Puts back a texture no result lives in.</summary>
    internal void Return(Rented rented) {
        if (disposed) {
            Destroy(rented.Texture, rented.View);

            return;
        }

        if (!spare.TryGetValue(rented.Shape, out var stack)) {
            spare[rented.Shape] = stack = [];
        }

        stack.Push(rented);
        idle += BytesOf(rented.Shape);
    }

    /// <summary>Files a result, unless the same one is already here.</summary>
    /// <param name="key">Its key.</param>
    /// <param name="rented">The texture it is in.</param>
    /// <returns>The pinned entry: the new one, or the one that got there first.</returns>
    /// <remarks>
    ///     ⚠ <b>Two ops in one plan can have one key</b> — two nodes with the same inputs and the same
    ///     numbers and no seed — and both run, because neither was in the cache when the evaluation
    ///     was decided. The second texture is identical and goes back to the pool.
    /// </remarks>
    internal Entry Add(TextureResultKey key, Rented rented) {
        if (entries.TryGetValue(key, out var existing)) {
            Return(rented);
            Pin(existing);

            return existing;
        }

        var entry = new Entry(key, rented.Shape, rented.Texture, rented.View);

        entry.Node = order.AddLast(entry);
        entries[key] = entry;
        held += BytesOf(rented.Shape);
        Pin(entry);

        return entry;
    }

    /// <summary>Takes a result out to be rewritten, keeping its texture and its pixels.</summary>
    /// <param name="entry">The entry, which nothing may have pinned.</param>
    /// <returns>Its texture, to be filed again under the key it is rewritten to.</returns>
    internal Rented TakeOut(Entry entry) {
        order.Remove(entry.Node);
        entries.Remove(entry.Key);
        held -= BytesOf(entry.Shape);

        return new(entry.Shape, entry.Texture, entry.View);
    }

    /// <summary>Turns the oldest results into idle textures, so that a run can reuse them.</summary>
    /// <param name="incoming">Bytes the run needs.</param>
    /// <remarks>
    ///     ⚠ <b>Before the run rents, and without destroying anything.</b> On a long drag every update
    ///     replaces results of the shapes it is about to want, and evicting after renting would
    ///     create a texture and destroy an identical one per op, for the rest of the drag.
    /// </remarks>
    internal void Reclaim(long incoming) {
        for (var node = order.First; node is not null && held + incoming > Budget;) {
            var next = node.Next;
            var entry = node.Value;

            if (entry.Pins == 0) {
                Evict(entry);
                Return(new(entry.Shape, entry.Texture, entry.View));
            }

            node = next;
        }
    }

    /// <summary>Evicts until this is under the budget, or nothing evictable is left.</summary>
    /// <param name="incoming">Bytes about to arrive, which the budget has to leave room for.</param>
    /// <remarks>Only with the device idle, and never with a list open.</remarks>
    internal void Trim(long incoming = 0) => DropUnpinned(Math.Max(0, Budget - incoming));

    /// <summary>Gives everything back, except what a live bake is holding.</summary>
    internal void Release() {
        if (disposed) {
            return;
        }

        DropUnpinned(0);
        disposed = true;
    }

    void DropUnpinned(long target) {
        // Idle textures first: they hold no picture anybody could ask for again.
        foreach (var (shape, stack) in spare) {
            while (Bytes > target && stack.TryPop(out var rented)) {
                idle -= BytesOf(shape);
                Destroy(rented.Texture, rented.View);
            }
        }

        for (var node = order.First; node is not null && Bytes > target;) {
            var next = node.Next;
            var entry = node.Value;

            if (entry.Pins == 0) {
                Evict(entry);
                Destroy(entry.Texture, entry.View);
            }

            node = next;
        }
    }

    void Evict(Entry entry) {
        order.Remove(entry.Node);
        entries.Remove(entry.Key);
        held -= BytesOf(entry.Shape);
        Evictions++;
    }

    void Destroy(TextureHandle texture, TextureViewHandle view) {
        device.Destroy(view);
        device.Destroy(texture);
    }

    /// <summary>A texture and the view over it, between owners.</summary>
    internal readonly record struct Rented(TexturePoolSlot Shape, TextureHandle Texture, TextureViewHandle View);

    /// <summary>One held result.</summary>
    internal sealed class Entry(
        TextureResultKey key,
        TexturePoolSlot shape,
        TextureHandle texture,
        TextureViewHandle view
    ) {
        public TextureResultKey Key { get; } = key;

        public TexturePoolSlot Shape { get; } = shape;

        public TextureHandle Texture { get; } = texture;

        public TextureViewHandle View { get; } = view;

        /// <summary>How many live bakes can read this.</summary>
        public int Pins { get; set; }

        /// <summary>Where it is in the recency list.</summary>
        public LinkedListNode<Entry> Node { get; set; } = null!;
    }
}
