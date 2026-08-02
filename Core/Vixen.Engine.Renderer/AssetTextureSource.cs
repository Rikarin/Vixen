// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Assets;
using Vixen.Core;
using Vixen.Core.Imaging;
using Vixen.Graphics;

namespace Vixen.Engine.Renderer;

/// <summary>
///     The textures a material's features sample, loaded, uploaded and viewable.
/// </summary>
/// <remarks>
///     <para>
///         <b>The half of a bindless material that had nothing on the far side.</b> A feature carries
///         the name of a map it wants sampled and the material carries which texture that is; what
///         neither carries is a view, because a material is serialised on machines that have no device.
///         This is what turns the reference into one — read the <c>Texture</c> artefact a build wrote,
///         decode the KTX2, put the pixels on the device, and hand back a
///         <see cref="TextureViewHandle" /> for <c>MaterialRenderFeature</c> to register in the frame's
///         table.
///     </para>
///     <para>
///         <b>Three stages, and the split between them is where the costs are.</b> Reading a bundle and
///         decoding a container are file work and happen on a task; creating the texture is a device
///         call and happens on whichever thread asked; recording the copy needs a command list and
///         happens in <see cref="Update" />, once a frame. A texture is therefore ready the frame after
///         its bytes were recorded, which is what the fallback slot exists to cover.
///     </para>
///     <para>
///         ⚠ <b>Two dimensions and one layer.</b> A cube map and an array both decode fine and neither
///         is uploaded — <see cref="TextureData.FaceCount" /> and <see cref="TextureData.LayerCount" />
///         past one are refused rather than half-copied, because a sky sampled as its first face is a
///         picture that looks deliberate. The environment path builds its own cube today; a material
///         that wants one is the reason this will grow.
///     </para>
/// </remarks>
public sealed class AssetTextureSource : IDisposable {
    readonly IGraphicsDevice device;
    readonly AssetManager assets;
    readonly Dictionary<AssetReference, Entry> entries = [];
    readonly List<Entry> uploading = [];
    readonly List<TextureHandle> textures = [];
    readonly List<BufferHandle> staging = [];

    bool disposed;

    /// <summary>Builds a source over a device and a content manager.</summary>
    /// <param name="device">Where the textures live.</param>
    /// <param name="assets">Where their bytes come from.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public AssetTextureSource(IGraphicsDevice device, AssetManager assets) {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(assets);

        this.device = device;
        this.assets = assets;
    }

    /// <summary>How many distinct textures have been asked for.</summary>
    public int Requested => entries.Count;

    /// <summary>How many are on the device and viewable.</summary>
    public int Loaded {
        get {
            var count = 0;

            foreach (var entry in entries.Values) {
                if (entry.View.IsValid) {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>How many will not arrive.</summary>
    /// <remarks>
    ///     A reference nothing shipped, a chunk that is not a KTX2, a texture whose shape this does not
    ///     upload. All three are content problems and all three look like a material sampling the
    ///     table's fallback, which is a defined picture and the wrong one.
    /// </remarks>
    public int Failed {
        get {
            var count = 0;

            foreach (var entry in entries.Values) {
                if (entry.Failed) {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>The view a reference names, if its pixels are on the device yet.</summary>
    /// <param name="reference">Which texture.</param>
    /// <param name="view">The view, when this returns true.</param>
    /// <returns>Whether it is ready.</returns>
    /// <exception cref="ObjectDisposedException">This has been disposed.</exception>
    public bool TryGet(AssetReference reference, out TextureViewHandle view) {
        ObjectDisposedException.ThrowIf(disposed, this);

        view = default;

        if (reference.IsNull) {
            return false;
        }

        if (!entries.TryGetValue(reference, out var entry)) {
            entries[reference] = entry = Begin(reference);
        }

        if (entry.View.IsValid) {
            view = entry.View;
            return true;
        }

        // Created here rather than on the task, because a device call belongs on a thread the caller
        // chose. The copy still has to be recorded, so this is not yet ready.
        if (!entry.Failed && entry.Texture.IsValid == false && entry.Decoded is { IsCompletedSuccessfully: true }) {
            Create(entry);
        }

        if (entry.Decoded is { IsFaulted: true }) {
            entry.Failed = true;
        }

        return false;
    }

    /// <summary>Records the copies for every texture whose bytes are waiting.</summary>
    /// <param name="commands">The frame's list.</param>
    /// <exception cref="ArgumentNullException"><paramref name="commands" /> is null.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The state a barrier claims a texture was in has to be the state it was in.</b> A
    ///         fresh texture is <see cref="ResourceState.Undefined" />, so the transition into
    ///         <see cref="ResourceState.CopyDestination" /> is from undefined exactly once and from
    ///         nothing else ever — this only ever uploads a texture once, which is what makes that
    ///         simple here and is why <c>UiRenderer</c>'s atlas, which re-uploads, has to track it.
    ///     </para>
    ///     <para>
    ///         The staging buffers are kept rather than freed, and that is a cost worth naming: a
    ///         scene's textures hold a second copy of themselves in host memory until this is disposed.
    ///         Freeing them needs to know the copy has retired, which needs a fence this does not have;
    ///         the alternative — destroying a buffer the GPU may still be reading — is the one failure
    ///         the RHI's deferred destroy cannot save a caller from.
    ///     </para>
    /// </remarks>
    public void Update(ICommandList commands) {
        ArgumentNullException.ThrowIfNull(commands);
        ObjectDisposedException.ThrowIf(disposed, this);

        if (uploading.Count == 0) {
            return;
        }

        foreach (var entry in uploading) {
            var data = entry.Data!;

            commands.Barrier(
                new([], [new(entry.Texture, ResourceState.Undefined, ResourceState.CopyDestination)])
            );

            for (var level = 0; level < data.LevelCount; level++) {
                var mip = data.Levels[level];

                commands.CopyBufferToTexture(
                    entry.Staging,
                    mip.Offset,
                    new(entry.Texture, level),
                    new(mip.Width, mip.Height, mip.Depth)
                );
            }

            commands.Barrier(
                new([], [new(entry.Texture, ResourceState.CopyDestination, ResourceState.ShaderRead)])
            );

            // The view last, because it is what TryGet answers with: a view that existed before the
            // copy was recorded would be a material sampling undefined memory for a frame, which is
            // exactly the glitch the fallback slot is meant to make impossible.
            entry.View = device.CreateTextureView(entry.Texture);

            // The view last, because it is what TryGet answers with: a view that existed before the
            // copy was recorded would be a material sampling undefined memory for a frame, which is
            // exactly the glitch the fallback slot is meant to make impossible.


            // Nothing needs the pixels once they are recorded, and a scene's worth of them is the
            // largest thing this holds.
            entry.Data = null;
        }

        uploading.Clear();
    }

    /// <summary>Starts a read, and answers with the entry that will hold it.</summary>
    Entry Begin(AssetReference reference) {
        var entry = new Entry();

        string address;

        try {
            address = assets.AddressOf(reference);
        } catch (ReferenceNotFoundException) {
            // A reference the catalog does not know is one texture in a level, and a frame that threw
            // would take the level with it.
            entry.Failed = true;
            return entry;
        }

        // Off the asking thread on purpose: the ask happens inside extraction, and a bundle read plus
        // a block-compressed decode is the worst work there is to do inside a frame.
        entry.Decoded = Task.Run(
            () => {
                using var stream = assets.Open(address);
                using var memory = new MemoryStream();

                stream.CopyTo(memory);

                return Ktx2.Read(memory.ToArray());
            }
        );

        return entry;
    }

    /// <summary>Creates the texture and fills its staging buffer.</summary>
    void Create(Entry entry) {
        var data = entry.Decoded!.Result;

        if (data.FaceCount > 1 || data.LayerCount > 1) {
            entry.Failed = true;
            return;
        }

        // ⚠ The dimension follows the depth, and it did not before. A `.cube` grading table arrives
        // here as a volume, and a description that left `Dimension` at its 2D default created an
        // array-shaped texture the shader's `Texture3D` binding cannot be satisfied by — the
        // descriptor write is refused and the pass loses its whole set. Every other texture in the
        // engine has a depth of one and takes the same branch it always did.
        var texture = device.CreateTexture(
            new(
                data.Format,
                data.Width,
                data.Height,
                TextureUsage.Sampled | TextureUsage.CopyDestination,
                data.Depth,
                data.LevelCount,
                Dimension: data.Depth > 1 ? TextureDimension.Texture3D : TextureDimension.Texture2D,
                Name: "Material.Texture"
            )
        );

        var buffer = device.CreateBuffer(
            new(data.ByteLength, BufferUsage.CopySource, MemoryAccess.HostUpload, "Material.Texture.Staging")
        );

        device.Write(buffer, 0, data.Pixels);

        entry.Texture = texture;
        entry.Staging = buffer;
        entry.Data = data;

        textures.Add(texture);
        staging.Add(buffer);
        uploading.Add(entry);
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        foreach (var entry in entries.Values) {
            if (entry.View.IsValid) {
                device.Destroy(entry.View);
            }
        }

        foreach (var texture in textures) {
            device.Destroy(texture);
        }

        foreach (var buffer in staging) {
            device.Destroy(buffer);
        }

        entries.Clear();
        textures.Clear();
        staging.Clear();
        uploading.Clear();
    }

    /// <summary>One texture, somewhere between named and sampled.</summary>
    sealed class Entry {
        public Task<TextureData>? Decoded;
        public TextureData? Data;
        public TextureHandle Texture;
        public BufferHandle Staging;
        public TextureViewHandle View;
        public bool Failed;
    }
}
