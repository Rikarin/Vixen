// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Silk.NET.Vulkan;
using VkFormat = Silk.NET.Vulkan.Format;

namespace Vixen.Graphics.Vulkan;

/// <summary>One attachment, reduced to what makes two render passes compatible.</summary>
/// <param name="Format">Its format.</param>
/// <param name="Samples">How many samples.</param>
/// <param name="Load">What happens to it at the start.</param>
/// <param name="Store">What happens to it at the end.</param>
/// <param name="StencilLoad">What happens to stencil at the start.</param>
/// <param name="StencilStore">What happens to stencil at the end.</param>
readonly record struct AttachmentKey(
    VkFormat Format,
    SampleCountFlags Samples,
    AttachmentLoadOp Load,
    AttachmentStoreOp Store,
    AttachmentLoadOp StencilLoad = AttachmentLoadOp.DontCare,
    AttachmentStoreOp StencilStore = AttachmentStoreOp.DontCare
);

/// <summary>Everything that decides which <c>VkRenderPass</c> a pass needs.</summary>
sealed record RenderPassKey(AttachmentKey[] Colour, AttachmentKey? Depth) {
    /// <inheritdoc />
    public bool Equals(RenderPassKey? other) =>
        other is not null && Depth == other.Depth && Colour.AsSpan().SequenceEqual(other.Colour);

    /// <inheritdoc />
    public override int GetHashCode() {
        var hash = new HashCode();
        hash.Add(Depth);

        foreach (var attachment in Colour) {
            hash.Add(attachment);
        }

        return hash.ToHashCode();
    }
}

/// <summary>Everything that decides which <c>VkFramebuffer</c> a pass needs.</summary>
sealed record FramebufferKey(ulong Pass, ulong[] Views, uint Width, uint Height) {
    /// <inheritdoc />
    public bool Equals(FramebufferKey? other) =>
        other is not null
        && Pass == other.Pass
        && Width == other.Width
        && Height == other.Height
        && Views.AsSpan().SequenceEqual(other.Views);

    /// <inheritdoc />
    public override int GetHashCode() {
        var hash = new HashCode();
        hash.Add(Pass);
        hash.Add(Width);
        hash.Add(Height);

        foreach (var view in Views) {
            hash.Add(view);
        }

        return hash.ToHashCode();
    }
}

/// <summary>Render-pass and framebuffer objects, made once and kept.</summary>
/// <remarks>
///     <para>
///         The fallback for devices without <c>VK_KHR_dynamic_rendering</c>, which
///         [10](../../docs/plan/10-platforms.md) § Android makes mandatory rather than optional: a
///         large slice of Android is still on Vulkan 1.1, and the RHI's render-pass API has to work
///         there.
///     </para>
///     <para>
///         Both objects are cached because both are expensive to create and are recreated with
///         identical parameters every frame otherwise — a renderer draws the same passes at the same
///         size sixty times a second, and creating a framebuffer per frame per pass is a driver
///         allocation per frame per pass forever.
///     </para>
///     <para>
///         <b>Layouts are not transitioned by the pass.</b> Initial and final layout are both the
///         attachment-optimal layout, so the pass neither transitions nor expects one — the RHI states
///         transitions explicitly through <c>Barrier</c>, and a render pass that also transitioned
///         would do it twice, silently, in a way that only shows up as a validation warning about the
///         layout not being what the pass expected.
///     </para>
/// </remarks>
sealed unsafe class RenderPassCache : IDisposable {
    readonly Vk api;
    readonly Device device;
    readonly Dictionary<RenderPassKey, RenderPass> passes = [];
    readonly Dictionary<FramebufferKey, Framebuffer> framebuffers = [];
    readonly Lock gate = new();

    bool disposed;

    public RenderPassCache(Vk api, Device device) {
        this.api = api;
        this.device = device;
    }

    /// <summary>How many distinct passes have been created, which is a number that should stop growing.</summary>
    public int PassCount {
        get {
            lock (gate) {
                return passes.Count;
            }
        }
    }

    /// <summary>How many distinct framebuffers have been created.</summary>
    public int FramebufferCount {
        get {
            lock (gate) {
                return framebuffers.Count;
            }
        }
    }

    /// <summary>The pass for a set of attachments, created if it is new.</summary>
    public RenderPass Get(RenderPassKey key) {
        lock (gate) {
            if (passes.TryGetValue(key, out var existing)) {
                return existing;
            }

            var created = Create(key);
            passes[key] = created;
            return created;
        }
    }

    /// <summary>The framebuffer for a pass and a set of views, created if it is new.</summary>
    public Framebuffer GetFramebuffer(RenderPass pass, ReadOnlySpan<ImageView> views, uint width, uint height) {
        var handles = new ulong[views.Length];

        for (var index = 0; index < views.Length; index++) {
            handles[index] = views[index].Handle;
        }

        var key = new FramebufferKey(pass.Handle, handles, width, height);

        lock (gate) {
            if (framebuffers.TryGetValue(key, out var existing)) {
                return existing;
            }

            fixed (ImageView* attachments = views) {
                var info = new FramebufferCreateInfo {
                    SType = StructureType.FramebufferCreateInfo,
                    RenderPass = pass,
                    AttachmentCount = (uint)views.Length,
                    PAttachments = attachments,
                    Width = width,
                    Height = height,
                    Layers = 1
                };

                Framebuffer created;
                VulkanDevice.Check(api.CreateFramebuffer(device, &info, null, &created), "vkCreateFramebuffer");
                framebuffers[key] = created;
                return created;
            }
        }
    }

    /// <summary>Drops every framebuffer that names a view.</summary>
    /// <param name="view">The view about to be destroyed.</param>
    /// <remarks>
    ///     A framebuffer holds its attachments' image views, so destroying a view that a cached
    ///     framebuffer still names leaves a dangling reference the next frame walks into. Called from
    ///     the view's own destruction, which is deferred by frames-in-flight — so by the time this
    ///     runs, nothing on the GPU is using either.
    /// </remarks>
    public void Invalidate(ImageView view) {
        lock (gate) {
            if (disposed) {
                return;
            }

            var doomed = new List<FramebufferKey>();

            foreach (var (key, _) in framebuffers) {
                if (Array.IndexOf(key.Views, view.Handle) >= 0) {
                    doomed.Add(key);
                }
            }

            foreach (var key in doomed) {
                api.DestroyFramebuffer(device, framebuffers[key], null);
                framebuffers.Remove(key);
            }
        }
    }

    /// <inheritdoc />
    public void Dispose() {
        lock (gate) {
            if (disposed) {
                return;
            }

            disposed = true;

            foreach (var framebuffer in framebuffers.Values) {
                api.DestroyFramebuffer(device, framebuffer, null);
            }

            foreach (var pass in passes.Values) {
                api.DestroyRenderPass(device, pass, null);
            }

            framebuffers.Clear();
            passes.Clear();
        }
    }

    RenderPass Create(RenderPassKey key) {
        var total = key.Colour.Length + (key.Depth is null ? 0 : 1);
        var descriptions = stackalloc AttachmentDescription[Math.Max(1, total)];
        var references = stackalloc AttachmentReference[Math.Max(1, key.Colour.Length)];

        for (var index = 0; index < key.Colour.Length; index++) {
            var attachment = key.Colour[index];

            descriptions[index] = new() {
                Format = attachment.Format,
                Samples = attachment.Samples,
                LoadOp = attachment.Load,
                StoreOp = attachment.Store,
                StencilLoadOp = AttachmentLoadOp.DontCare,
                StencilStoreOp = AttachmentStoreOp.DontCare,
                InitialLayout = ImageLayout.ColorAttachmentOptimal,
                FinalLayout = ImageLayout.ColorAttachmentOptimal
            };

            references[index] = new() {
                Attachment = (uint)index,
                Layout = ImageLayout.ColorAttachmentOptimal
            };
        }

        var depthReference = new AttachmentReference {
            Attachment = (uint)key.Colour.Length,
            Layout = ImageLayout.DepthStencilAttachmentOptimal
        };

        if (key.Depth is { } depth) {
            descriptions[key.Colour.Length] = new() {
                Format = depth.Format,
                Samples = depth.Samples,
                LoadOp = depth.Load,
                StoreOp = depth.Store,
                StencilLoadOp = depth.StencilLoad,
                StencilStoreOp = depth.StencilStore,
                InitialLayout = ImageLayout.DepthStencilAttachmentOptimal,
                FinalLayout = ImageLayout.DepthStencilAttachmentOptimal
            };
        }

        var subpass = new SubpassDescription {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            ColorAttachmentCount = (uint)key.Colour.Length,
            PColorAttachments = key.Colour.Length > 0 ? references : null,
            PDepthStencilAttachment = key.Depth is null ? null : &depthReference
        };

        var info = new RenderPassCreateInfo {
            SType = StructureType.RenderPassCreateInfo,
            AttachmentCount = (uint)total,
            PAttachments = total > 0 ? descriptions : null,
            SubpassCount = 1,
            PSubpasses = &subpass
        };

        RenderPass pass;
        VulkanDevice.Check(api.CreateRenderPass(device, &info, null, &pass), "vkCreateRenderPass");
        return pass;
    }
}
