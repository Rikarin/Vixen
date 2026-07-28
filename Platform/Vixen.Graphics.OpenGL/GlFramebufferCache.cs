// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Graphics.OpenGL;

/// <summary>Framebuffer objects, keyed on the attachment set that produced them.</summary>
/// <remarks>
///     <para>
///         The RHI has no framebuffer object. A render pass names its attachments and the backend
///         works out the rest — which on Vulkan 1.3 is <c>vkCmdBeginRendering</c> and nothing else,
///         and on GL is an FBO that has to exist, be filled in, be validated, and be kept.
///     </para>
///     <para>
///         Kept, not rebuilt. Creating and attaching an FBO per pass is the single most expensive
///         mistake a GL backend can make: attaching a texture makes the driver re-validate the whole
///         attachment set, and several drivers recompile internal state when it changes. A renderer
///         with twelve passes has twelve attachment sets and they are the same twelve every frame.
///     </para>
///     <para>
///         Keyed on views rather than textures, because a pass that renders into mip 2 of a chain and
///         a pass that renders into mip 3 are different framebuffers over the same texture — and a
///         cache that could not tell them apart would render every mip into whichever came first.
///     </para>
/// </remarks>
sealed class GlFramebufferCache(IGlApi gl) : IDisposable {
    readonly Dictionary<Key, uint> framebuffers = [];

    /// <summary>How many framebuffer objects the cache is holding.</summary>
    /// <remarks>What a leak test asserts: a renderer with a fixed set of passes reaches a fixed
    /// number here and stays there.</remarks>
    public int Count => framebuffers.Count;

    /// <summary>The framebuffer for an attachment set, creating it the first time.</summary>
    /// <param name="attachments">The views, colour first, depth last.</param>
    /// <param name="colourCount">How many of them are colour attachments.</param>
    /// <param name="resolve">Turns a view into the texture, target, level and layer to attach.</param>
    public uint Get(
        ReadOnlySpan<GlAttachment> attachments,
        int colourCount,
        Func<TextureViewHandle, (uint Name, uint Target, int Level, int Layer, bool Layered, PixelFormat Format)> resolve
    ) {
        var key = Key.Of(attachments);

        if (framebuffers.TryGetValue(key, out var existing)) {
            return existing;
        }

        var framebuffer = gl.GenFramebuffer();
        gl.BindFramebuffer(GlConstants.DrawFramebuffer, framebuffer);

        Span<uint> draws = stackalloc uint[Math.Max(1, colourCount)];
        draws.Clear();

        for (var index = 0; index < attachments.Length; index++) {
            var attachment = attachments[index];
            var (name, target, level, layer, layered, format) = resolve(attachment.View);
            var point = GlFormats.Attachment(attachment.IsDepth ? format : PixelFormat.Rgba8UNorm, index);

            if (!attachment.IsDepth) {
                draws[index] = point;
            }

            if (layered) {
                gl.FramebufferTextureLayer(GlConstants.DrawFramebuffer, point, name, level, layer);
            } else {
                gl.FramebufferTexture2D(GlConstants.DrawFramebuffer, point, target, name, level);
            }
        }

        // Said explicitly and always. GL's default draw buffer for a user framebuffer is
        // COLOR_ATTACHMENT0 only, so a pass with two colour targets writes to one of them and
        // discards the other — with no error, and looking exactly like a shader that forgot to
        // write its second output.
        gl.DrawBuffers(colourCount > 0 ? draws[..colourCount] : draws[..0]);

        var status = gl.CheckFramebufferStatus(GlConstants.DrawFramebuffer);

        if (status != GlConstants.FramebufferComplete) {
            gl.DeleteFramebuffer(framebuffer);

            throw new InvalidOperationException(
                $"The attachment set for this render pass is not a complete framebuffer (0x{status:X4}). "
                + "The usual causes are attachments of different sizes, a format the driver will not "
                + "render to, or a depth format bound at a colour attachment point."
            );
        }

        framebuffers[key] = framebuffer;
        return framebuffer;
    }

    /// <summary>Drops every framebuffer that names a view, because the view is going away.</summary>
    /// <remarks>
    ///     GL deletes a framebuffer's attachment out from under it silently — the attachment point
    ///     becomes zero and the framebuffer becomes incomplete at the next bind, which surfaces as a
    ///     pass that draws nothing several frames after the destroy that caused it.
    /// </remarks>
    public void Forget(TextureViewHandle view) {
        var stale = framebuffers.Where(entry => entry.Key.Contains(view)).ToList();

        foreach (var (key, framebuffer) in stale) {
            gl.DeleteFramebuffer(framebuffer);
            framebuffers.Remove(key);
        }
    }

    /// <inheritdoc />
    public void Dispose() {
        foreach (var framebuffer in framebuffers.Values) {
            gl.DeleteFramebuffer(framebuffer);
        }

        framebuffers.Clear();
    }

    readonly struct Key : IEquatable<Key> {
        readonly TextureViewHandle[] views;
        readonly int hash;

        Key(TextureViewHandle[] views) {
            this.views = views;
            var code = new HashCode();

            foreach (var view in views) {
                code.Add(view);
            }

            hash = code.ToHashCode();
        }

        public static Key Of(ReadOnlySpan<GlAttachment> attachments) {
            var views = new TextureViewHandle[attachments.Length];

            for (var index = 0; index < attachments.Length; index++) {
                views[index] = attachments[index].View;
            }

            return new(views);
        }

        public bool Contains(TextureViewHandle view) => Array.IndexOf(views, view) >= 0;

        public bool Equals(Key other) => views.AsSpan().SequenceEqual(other.views);

        public override bool Equals(object? obj) => obj is Key other && Equals(other);

        public override int GetHashCode() => hash;
    }
}
