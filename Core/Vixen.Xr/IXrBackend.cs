// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Xr;

/// <summary>What a runtime demands of the Vulkan device it will share.</summary>
/// <param name="InstanceExtensions">Extensions the <c>VkInstance</c> must be created with.</param>
/// <param name="DeviceExtensions">Extensions the <c>VkDevice</c> must be created with.</param>
/// <param name="MinimumApiVersion">The oldest Vulkan the runtime will work with.</param>
/// <param name="MaximumApiVersion">The newest it has been tested against.</param>
/// <remarks>
///     <para>
///         <b>The runtime dictates the device, which is why this is asked before one exists.</b> An XR
///         compositor and the application render on the same GPU and share images across a process
///         boundary, and the extensions that make that possible have to be present when the instance
///         and the device are created — not enabled afterwards. So the order is: create the backend,
///         ask what it needs, create the graphics device with it, and only then create a session.
///     </para>
///     <para>
///         Getting this wrong does not fail politely. A device created without the runtime's
///         extensions will create a session that appears to work and then produces a black headset,
///         which is why this is a hard requirement of the API's shape rather than a note in a
///         document.
///     </para>
/// </remarks>
public readonly record struct XrVulkanRequirements(
    IReadOnlyList<string> InstanceExtensions,
    IReadOnlyList<string> DeviceExtensions,
    Version MinimumApiVersion,
    Version MaximumApiVersion
) {
    /// <summary>Nothing required, which is what a backend with no runtime behind it asks for.</summary>
    public static XrVulkanRequirements None => new([], [], new Version(1, 0), new Version(1, 4));
}

/// <summary>The Vulkan objects a session is created against.</summary>
/// <param name="Instance">The <c>VkInstance</c>.</param>
/// <param name="PhysicalDevice">The <c>VkPhysicalDevice</c> the runtime named.</param>
/// <param name="Device">The <c>VkDevice</c>.</param>
/// <param name="QueueFamilyIndex">Which queue family the graphics queue came from.</param>
/// <param name="QueueIndex">Which queue within that family.</param>
/// <remarks>
///     Raw handles, deliberately. This module cannot reference <c>Vixen.Graphics.Vulkan</c> — that is
///     a platform assembly and this is a core one — and it has no business knowing what a Silk.NET
///     <c>Instance</c> is either. The Vulkan backend hands these over and the XR backend passes them
///     to the runtime; neither type system in between has an opinion.
/// </remarks>
public readonly record struct XrVulkanBinding(
    nint Instance,
    nint PhysicalDevice,
    nint Device,
    uint QueueFamilyIndex,
    uint QueueIndex
);

/// <summary>A way of reaching the machine's headset, if it has one.</summary>
/// <remarks>
///     <para>
///         Shaped like <c>IAudioBackend</c>, and for the same reasons. <see cref="IsAvailable" />
///         answers "could this process actually open a session" without throwing, because no headset
///         is the ordinary case — a CI runner, a developer's laptop, a machine whose runtime is
///         installed but has no device plugged in. A game that offers VR asks and offers it or does
///         not.
///     </para>
///     <para>
///         <b>Constructing a backend must be safe with no runtime installed.</b> Selection constructs
///         every candidate, so a missing <c>libopenxr_loader</c> makes the backend report itself
///         unavailable rather than taking the process down with a <c>DllNotFoundException</c>.
///     </para>
/// </remarks>
public interface IXrBackend : IDisposable {
    /// <summary>What to call it in a log — <c>OpenXR</c>, <c>Null</c>.</summary>
    string Name { get; }

    /// <summary>Whether this process could open a session through it.</summary>
    bool IsAvailable { get; }

    /// <summary>Why not, when it is not. Empty when it is.</summary>
    /// <remarks>
    ///     A sentence for a log line, not a code. "No headset" and "the runtime is installed but no
    ///     device is connected" and "the loader is not on this machine" are three completely different
    ///     situations for whoever is trying to work out why VR did not start.
    /// </remarks>
    string UnavailableReason { get; }

    /// <summary>What the runtime says about the attached headset.</summary>
    /// <param name="system">What it said.</param>
    /// <returns>Whether there is one.</returns>
    bool TryGetSystem(out XrSystemInfo system);

    /// <summary>What the runtime needs of the Vulkan device before one is created.</summary>
    /// <returns>The requirements.</returns>
    /// <exception cref="InvalidOperationException">There is no runtime.</exception>
    XrVulkanRequirements GetVulkanRequirements();

    /// <summary>Which physical device the runtime insists on.</summary>
    /// <param name="vulkanInstance">The <c>VkInstance</c> to look in.</param>
    /// <returns>The <c>VkPhysicalDevice</c>, or zero if the runtime does not care.</returns>
    /// <remarks>
    ///     Not a suggestion. On a laptop with two GPUs the headset is wired to one of them, and
    ///     rendering on the other means every frame crosses the bus — when it works at all.
    /// </remarks>
    nint GetVulkanPhysicalDevice(nint vulkanInstance);

    /// <summary>Opens a session.</summary>
    /// <param name="binding">The Vulkan objects to share.</param>
    /// <param name="options">How to set it up.</param>
    /// <param name="importer">
    ///     What turns the runtime's own images into textures the RHI can render into, or
    ///     <see langword="null" /> for a session that will not render.
    /// </param>
    /// <returns>The session, which starts <see cref="XrSessionState.Idle" />.</returns>
    /// <exception cref="InvalidOperationException">There is no runtime or no headset.</exception>
    IXrSession CreateSession(
        in XrVulkanBinding binding,
        in XrSessionOptions options,
        IXrImageImporter? importer = null
    );
}

/// <summary>Turns an image somebody else allocated into a texture the RHI can use.</summary>
/// <remarks>
///     <para>
///         <b>The one thing an XR swapchain needs that the RHI cannot do.</b> A window's swapchain
///         images are created by the graphics backend; a headset's are created by the compositor,
///         handed over as native handles, and have to be adopted rather than allocated. There is no
///         portable way to express that in <c>IGraphicsDevice</c> — the handle is a
///         <c>VkImage</c> and means nothing to any other backend — so it is an interface the graphics
///         backend that does understand it implements.
///     </para>
///     <para>
///         It sits here rather than in the OpenXR module so that <c>Vixen.Xr</c>'s swapchain seam is
///         complete on its own, and so that a second XR backend — or a test — plugs into the same
///         place.
///     </para>
/// </remarks>
public interface IXrImageImporter {
    /// <summary>Adopts an image.</summary>
    /// <param name="nativeImage">The handle, whose meaning is the graphics backend's business.</param>
    /// <param name="description">
    ///     What it is. Must match what it was actually created with; nothing can check, because the
    ///     image came from another API.
    /// </param>
    /// <returns>A texture handle for it. Destroying the handle must not destroy the image.</returns>
    Graphics.TextureHandle Import(nint nativeImage, in Graphics.TextureDescription description);

    /// <summary>Gives an adopted handle back.</summary>
    /// <param name="texture">The handle.</param>
    void Release(Graphics.TextureHandle texture);

    /// <summary>Creates a view of an adopted image.</summary>
    /// <param name="texture">The texture.</param>
    /// <returns>A view covering every layer and level.</returns>
    Graphics.TextureViewHandle CreateView(Graphics.TextureHandle texture);

    /// <summary>Destroys a view.</summary>
    /// <param name="view">The view.</param>
    void Release(Graphics.TextureViewHandle view);
}
