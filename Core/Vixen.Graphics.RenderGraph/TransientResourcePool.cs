// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Graphics.RenderGraph;

/// <summary>Physical resources the graph hands out and takes back.</summary>
/// <remarks>
///     <para>
///         This is where "transient aliasing" actually happens, and it is worth being exact about
///         what it is and is not. Two virtual resources whose lifetimes do not overlap are given the
///         <em>same physical resource</em> — a 4 K GBuffer target and a 4 K post-FX target that never
///         coexist cost one allocation between them, which is the saving
///         [05](../../docs/plan/05-graphics-rhi.md) describes.
///     </para>
///     <para>
///         It is <b>not</b> memory aliasing in the Vulkan sense: two resources of different
///         descriptions overlapping in one allocation. That needs placed resources, which
///         <see cref="IGraphicsDevice" /> does not expose and which two of the six planned backends
///         cannot express at all. Reuse gets most of the benefit with none of the API surface, and
///         the distinction is written down here rather than left for someone to discover from a
///         memory graph that did not shrink as much as they expected.
///     </para>
///     <para>
///         Resources persist across frames. Creating and destroying a GBuffer sixty times a second
///         is a driver allocation per frame per target; the pool holds them and hands the same ones
///         back, which is what makes the graph free to run every frame.
///     </para>
/// </remarks>
public sealed class TransientResourcePool : IDisposable {
    readonly IGraphicsDevice device;
    readonly List<Entry> entries = [];

    bool disposed;

    /// <summary>Creates a pool for a device.</summary>
    /// <param name="device">The device its resources come from.</param>
    public TransientResourcePool(IGraphicsDevice device) => this.device = device;

    /// <summary>How many physical resources are held.</summary>
    public int Count => entries.Count;

    /// <summary>How many are currently lent out.</summary>
    public int InUse {
        get {
            var count = 0;

            foreach (var entry in entries) {
                if (entry.Borrowed) {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>How many times a lend found an existing resource rather than creating one.</summary>
    /// <remarks>
    ///     The number that says whether aliasing is working. A graph whose second frame still creates
    ///     resources is one whose descriptions are not matching, which is a silent performance bug.
    /// </remarks>
    public int Reuses { get; private set; }

    /// <summary>Takes a texture matching a description.</summary>
    /// <param name="description">What is wanted.</param>
    /// <returns>The slot it came from, for <see cref="Release" />.</returns>
    public int AcquireTexture(in TextureDescription description) {
        ObjectDisposedException.ThrowIf(disposed, this);

        for (var index = 0; index < entries.Count; index++) {
            var entry = entries[index];

            if (entry.Borrowed || !entry.IsTexture || !Matches(entry.TextureDescription, description)) {
                continue;
            }

            entry.Borrowed = true;
            Reuses++;
            return index;
        }

        var texture = device.CreateTexture(description);

        entries.Add(new() {
            IsTexture = true,
            TextureDescription = description,
            Texture = texture,
            View = device.CreateTextureView(texture),
            Borrowed = true
        });

        return entries.Count - 1;
    }

    /// <summary>Takes a buffer matching a description.</summary>
    /// <param name="description">What is wanted.</param>
    /// <returns>The slot it came from, for <see cref="Release" />.</returns>
    public int AcquireBuffer(in BufferDescription description) {
        ObjectDisposedException.ThrowIf(disposed, this);

        for (var index = 0; index < entries.Count; index++) {
            var entry = entries[index];

            if (entry.Borrowed || entry.IsTexture || !Matches(entry.BufferDescription, description)) {
                continue;
            }

            entry.Borrowed = true;
            Reuses++;
            return index;
        }

        entries.Add(new() {
            IsTexture = false,
            BufferDescription = description,
            Buffer = device.CreateBuffer(description),
            Borrowed = true
        });

        return entries.Count - 1;
    }

    /// <summary>Gives one back.</summary>
    /// <param name="slot">What <c>Acquire</c> returned.</param>
    public void Release(int slot) {
        if (slot >= 0 && slot < entries.Count) {
            entries[slot].Borrowed = false;
        }
    }

    /// <summary>The texture in a slot.</summary>
    /// <param name="slot">The slot.</param>
    public TextureHandle TextureAt(int slot) => entries[slot].Texture;

    /// <summary>The default view of the texture in a slot.</summary>
    /// <param name="slot">The slot.</param>
    public TextureViewHandle ViewAt(int slot) => entries[slot].View;

    /// <summary>The buffer in a slot.</summary>
    /// <param name="slot">The slot.</param>
    public BufferHandle BufferAt(int slot) => entries[slot].Buffer;

    /// <summary>Marks everything free, without destroying anything.</summary>
    /// <remarks>
    ///     What a frame boundary does. The physical resources stay; only the claim on them is
    ///     dropped, so the next frame's identical graph reuses every one of them.
    /// </remarks>
    public void ReleaseAll() {
        foreach (var entry in entries) {
            entry.Borrowed = false;
        }
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        foreach (var entry in entries) {
            if (entry.IsTexture) {
                device.Destroy(entry.View);
                device.Destroy(entry.Texture);
            } else {
                device.Destroy(entry.Buffer);
            }
        }

        entries.Clear();
    }

    /// <summary>Whether an existing texture can serve a request.</summary>
    /// <remarks>
    ///     Everything but the name, which exists for a capture and never changes what the resource
    ///     is. Comparing names would defeat reuse entirely — every pass names its target differently,
    ///     which is the whole point of naming them.
    /// </remarks>
    static bool Matches(in TextureDescription held, in TextureDescription wanted) =>
        held.Format == wanted.Format
        && held.Width == wanted.Width
        && held.Height == wanted.Height
        && held.Depth == wanted.Depth
        && held.MipLevels == wanted.MipLevels
        && held.ArrayLayers == wanted.ArrayLayers
        && held.SampleCount == wanted.SampleCount
        && held.Dimension == wanted.Dimension

        // The usage must *cover* what is wanted, not equal it: a target created for colour and copy
        // serves a request for colour alone, and refusing it would allocate a second one.
        && (held.Usage & wanted.Usage) == wanted.Usage;

    static bool Matches(in BufferDescription held, in BufferDescription wanted) =>
        held.Size == wanted.Size
        && held.Access == wanted.Access
        && (held.Usage & wanted.Usage) == wanted.Usage;

    sealed class Entry {
        public bool IsTexture { get; init; }

        public TextureDescription TextureDescription { get; init; }

        public BufferDescription BufferDescription { get; init; }

        public TextureHandle Texture { get; init; }

        public TextureViewHandle View { get; init; }

        public BufferHandle Buffer { get; init; }

        public bool Borrowed { get; set; }
    }
}
