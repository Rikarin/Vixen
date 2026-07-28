// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;
using Vixen.Core;
using Vixen.Core.Collections;
using VkSemaphore = Silk.NET.Vulkan.Semaphore;

namespace Vixen.Graphics.Vulkan;

/// <summary>What to create a device as.</summary>
public readonly record struct VulkanDeviceOptions() {
    /// <summary>A surface a swapchain will be created for, so that presentation is planned for.</summary>
    /// <remarks>
    ///     Which queue family can present is a property of a <em>surface</em>, not of a device, so a
    ///     device meant for a window has to be told about the window before it is created. Passing
    ///     <see cref="SurfaceHandle.None" /> creates an offscreen device, which is what the dedicated
    ///     server and the golden-image suite use.
    /// </remarks>
    public SurfaceHandle Surface { get; init; } = SurfaceHandle.None;

    /// <summary>How many frames may be recorded before the first has to have finished.</summary>
    /// <remarks>
    ///     Two by default: one being recorded while one is on the GPU. Three trades a frame of
    ///     latency for a little more tolerance of a spiky frame time, which is a decision for the
    ///     renderer rather than for the RHI.
    /// </remarks>
    public int FramesInFlight { get; init; } = 2;

    /// <summary>Where the backend logs.</summary>
    public ILogger? Logger { get; init; }

    /// <summary>
    ///     Use <c>VkRenderPass</c> objects even where the device offers dynamic rendering.
    /// </summary>
    /// <remarks>
    ///     Not a performance knob — dynamic rendering is the better path wherever it exists, and this
    ///     is off by default. It is here because the fallback is <em>mandatory</em>
    ///     ([10](../../docs/plan/10-platforms.md) § Android: a large slice of Android is still on
    ///     Vulkan 1.1) and would otherwise have no coverage on any machine a developer owns. A path
    ///     that only runs on hardware nobody has is a path that is already broken.
    /// </remarks>
    public bool PreferRenderPassObjects { get; init; }

    /// <summary>Instance extensions that must be enabled, on top of whatever the backend needs.</summary>
    /// <remarks>
    ///     For interop with something that shares this device and dictates how it is created. An
    ///     OpenXR runtime is the case that made this exist: it names the extensions the instance and
    ///     the device must carry for the compositor to be able to read the images the game renders,
    ///     and they cannot be enabled after the fact. See <c>Vixen.Xr.OpenXR</c>.
    /// </remarks>
    public IReadOnlyList<string> RequiredInstanceExtensions { get; init; } = [];

    /// <summary>Device extensions that must be enabled, on top of whatever the backend needs.</summary>
    public IReadOnlyList<string> RequiredDeviceExtensions { get; init; } = [];

    /// <summary>
    ///     The physical device to use, as a raw <c>VkPhysicalDevice</c>, or zero to choose one.
    /// </summary>
    /// <remarks>
    ///     Also for interop, and not a preference. On a laptop with two GPUs the headset is wired to
    ///     one of them, and an XR runtime names which — rendering on the other means every frame
    ///     crosses the bus, when it works at all.
    /// </remarks>
    public nint PreferredPhysicalDevice { get; init; }
}

/// <summary>The raw Vulkan objects something sharing a device is handed.</summary>
/// <param name="Instance">The <c>VkInstance</c>.</param>
/// <param name="PhysicalDevice">The <c>VkPhysicalDevice</c>.</param>
/// <param name="Device">The <c>VkDevice</c>.</param>
/// <param name="GraphicsQueueFamily">Which family the graphics queue came from.</param>
/// <param name="GraphicsQueueIndex">Which queue within that family.</param>
/// <remarks>
///     Raw <see langword="nint" />s rather than Silk.NET's wrappers, so that a consumer can take
///     these without taking a dependency on Silk.NET.Vulkan — which is the whole point, since the
///     consumer this exists for is an OpenXR binding that is not a Vulkan module.
/// </remarks>
public readonly record struct VulkanNativeHandles(
    nint Instance,
    nint PhysicalDevice,
    nint Device,
    uint GraphicsQueueFamily,
    uint GraphicsQueueIndex
);

/// <summary>The Vulkan logical device: everything the RHI can do, done.</summary>
/// <remarks>
///     <para>
///         Handles index tables this object owns. Nothing here is finalisable and nothing is
///         reference-counted by the collector: a GPU object's lifetime belongs to the renderer, and
///         destruction is deferred by <see cref="FramesInFlight" /> frames so that a resource
///         released while the GPU is still reading it stays alive until it is not.
///     </para>
///     <para>
///         Recording is thread-safe; creation and destruction are serialised by a lock. That split
///         is deliberate — command recording is the hot path a renderer parallelises, and resource
///         creation is not.
///     </para>
/// </remarks>
public sealed unsafe partial class VulkanDevice : IGraphicsDevice {
    readonly VulkanInstance instance;
    readonly VulkanAdapter adapter;
    readonly Device device;
    readonly VulkanAllocator allocator;
    readonly ILogger? logger;
    readonly bool ownsInstance;
    readonly bool preferRenderPassObjects;

    readonly KhrSwapchain? khrSwapchain;
    readonly KhrSurface? khrSurface;
    readonly KhrDynamicRendering? khrDynamicRendering;
    readonly ExtDebugUtils? debugUtils;
    readonly SurfaceKHR surface;

    readonly VulkanQueue[] queues;
    readonly VulkanQueue graphics;
    readonly VulkanQueue compute;
    readonly VulkanQueue transfer;

    readonly HandlePool<GpuBuffer> buffers = new(64);
    readonly HandlePool<GpuTexture> textures = new(64);
    readonly HandlePool<GpuTextureView> views = new(64);
    readonly HandlePool<GpuSampler> samplers = new(16);
    readonly HandlePool<GpuShader> shaders = new(32);
    readonly HandlePool<GpuDescriptorSetLayout> setLayouts = new(16);
    readonly HandlePool<GpuPipelineLayout> pipelineLayouts = new(16);
    readonly HandlePool<GpuDescriptorSet> descriptorSets = new(64);
    readonly HandlePool<GpuPipeline> pipelines = new(32);

    readonly Lock gate = new();
    readonly List<Action>[] retiring;
    readonly ConcurrentDictionary<(int Thread, int Frame), CommandPool> commandPools = [];
    readonly List<DescriptorPool> descriptorPools = [];
    readonly RenderPassCache renderPasses;

    readonly VkSemaphore[] semaphores;
    readonly List<(VkSemaphore Semaphore, PipelineStageFlags Stage)> pendingWaits = [];

    int frame;
    int semaphoreCursor;
    bool presentExpected;
    VkSemaphore lastSignalled;
    bool disposed;

    VulkanDevice(
        VulkanInstance instance,
        VulkanAdapter adapter,
        Device device,
        SurfaceKHR surface,
        VulkanDeviceOptions options,
        bool ownsInstance
    ) {
        this.instance = instance;
        this.adapter = adapter;
        this.device = device;
        this.surface = surface;
        this.ownsInstance = ownsInstance;
        preferRenderPassObjects = options.PreferRenderPassObjects;
        logger = options.Logger;
        FramesInFlight = Math.Max(1, options.FramesInFlight);

        var api = instance.Api;
        api.TryGetDeviceExtension(instance.Handle, device, out khrSwapchain);
        api.TryGetInstanceExtension(instance.Handle, out KhrSurface loadedSurface);
        khrSurface = loadedSurface;
        api.TryGetInstanceExtension(instance.Handle, out ExtDebugUtils loadedDebug);
        debugUtils = instance.ValidationEnabled ? loadedDebug : null;

        if (adapter.UsableApiVersion < VulkanFeatures.Version13
            && adapter.Extensions.Contains(KhrDynamicRendering.ExtensionName)) {
            api.TryGetDeviceExtension(instance.Handle, device, out khrDynamicRendering);
        }

        allocator = new(api, device, adapter.Memory);
        renderPasses = new(api, device);

        var plan = adapter.Queues;
        var byFamily = new Dictionary<uint, VulkanQueue>();

        foreach (var family in plan.DistinctFamilies()) {
            Queue handle;
            api.GetDeviceQueue(device, family, 0, &handle);
            byFamily[family] = new(this, api, device, handle, family, FramesInFlight);
        }

        graphics = byFamily[plan.Graphics];
        compute = byFamily[plan.Compute];
        transfer = byFamily[plan.Transfer];
        graphics.Kind = QueueKind.Graphics;
        compute.Kind = compute == graphics ? QueueKind.Graphics : QueueKind.Compute;
        transfer.Kind = transfer == graphics ? QueueKind.Graphics : QueueKind.Transfer;
        queues = [.. byFamily.Values];

        retiring = new List<Action>[FramesInFlight];

        for (var index = 0; index < FramesInFlight; index++) {
            retiring[index] = [];
        }

        // Enough for a handful of swapchains and a few submissions each. Sized rather than grown
        // because growing means creating a semaphore mid-frame, and a semaphore created while the
        // GPU is busy is a driver call at exactly the wrong moment.
        semaphores = new VkSemaphore[FramesInFlight * 16];

        for (var index = 0; index < semaphores.Length; index++) {
            var info = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };

            fixed (VkSemaphore* target = &semaphores[index]) {
                api.CreateSemaphore(device, &info, null, target);
            }
        }
    }

    /// <inheritdoc />
    public IGraphicsAdapter Adapter => adapter;

    /// <inheritdoc />
    public GraphicsDeviceFeatures Features => adapter.Features;

    /// <inheritdoc />
    public ICommandSubmitter GraphicsQueue => graphics;

    /// <inheritdoc />
    public ICommandSubmitter ComputeQueue => compute;

    /// <inheritdoc />
    public ICommandSubmitter TransferQueue => transfer;

    /// <inheritdoc />
    public int FramesInFlight { get; }

    /// <summary>Whether the validation layers are watching this device.</summary>
    public bool ValidationEnabled => instance.ValidationEnabled;

    /// <summary>Which frame slot is being recorded into.</summary>
    internal int FrameSlot => frame % FramesInFlight;

    internal Vk Api => instance.Api;

    internal Device Handle => device;

    internal VulkanAdapter Adapters => adapter;

    /// <summary>The raw objects something sharing this device needs.</summary>
    /// <remarks>
    ///     <para>
    ///         Exposed because interop is a real requirement and the alternative is worse. An OpenXR
    ///         runtime renders into the same device the game does and has to be handed the instance,
    ///         the physical device, the device and a queue — and the module that does the handing
    ///         (<c>Vixen.Xr.OpenXR</c>) cannot reference Silk.NET.Vulkan's types without becoming a
    ///         Vulkan module itself.
    ///     </para>
    ///     <para>
    ///         <b>Nothing about these is owned by the caller.</b> They are borrowed for the lifetime
    ///         of this device, and destroying any of them through them is undefined in the ordinary
    ///         way.
    ///     </para>
    /// </remarks>
    public VulkanNativeHandles NativeHandles => new(
        instance.Handle.Handle,
        adapter.Handle.Handle,
        device.Handle,
        graphics.Family,

        // The backend takes queue 0 of each family it uses; see QueueFamilySelection.
        0
    );

    /// <summary>Adopts an image this device did not create, as a texture handle.</summary>
    /// <param name="image">The raw <c>VkImage</c>.</param>
    /// <param name="description">What it is — the size, format and usage it was created with.</param>
    /// <returns>A handle to it.</returns>
    /// <remarks>
    ///     <para>
    ///         For images an OpenXR runtime allocated: an XR swapchain's images belong to the
    ///         compositor, possibly in memory it can reproject without a copy, and the RHI has to be
    ///         told to adopt them rather than to create them. This is the same path a window's
    ///         swapchain images take — <see cref="Destroy(TextureHandle)" /> releases the handle and
    ///         leaves the image alone.
    ///     </para>
    ///     <para>
    ///         <b>The description must match what the image was actually created with.</b> Nothing
    ///         checks it, because nothing can: the image came from another API. A mismatch shows up
    ///         as a view that cannot be created, or as a copy that reads the wrong extent.
    ///     </para>
    /// </remarks>
    public TextureHandle ImportImage(nint image, in TextureDescription description) =>
        AdoptSwapChainImage(new Image((ulong)image), in description);

    /// <summary>Whether rendering goes through <c>VK_KHR_dynamic_rendering</c> rather than a pass object.</summary>
    public bool UsesDynamicRendering =>
        !preferRenderPassObjects
        && Features.HasDynamicRendering
        && (khrDynamicRendering is not null || IsCoreDynamicRendering);

    bool IsCoreDynamicRendering => adapter.UsableApiVersion >= VulkanFeatures.Version13;

    internal KhrDynamicRendering? DynamicRendering => khrDynamicRendering;

    internal RenderPassCache RenderPasses => renderPasses;

    internal ExtDebugUtils? DebugUtils => debugUtils;

    /// <summary>Creates a device with the documented defaults, or explains why it could not.</summary>
    /// <exception cref="PlatformNotSupportedException">Vulkan or a suitable GPU is unavailable.</exception>
    public static VulkanDevice Create() => Create(new VulkanDeviceOptions());

    /// <summary>Creates a device, or explains why it could not.</summary>
    /// <param name="options">What to create.</param>
    /// <exception cref="PlatformNotSupportedException">Vulkan or a suitable GPU is unavailable.</exception>
    /// <remarks>
    ///     Two overloads rather than one with <c>= default</c>, for the reason
    ///     <see cref="VulkanInstance.Create()" /> gives: <c>default</c> skips a record struct's
    ///     property initialisers, so an omitted argument would have meant one frame in flight rather
    ///     than the documented two.
    /// </remarks>
    public static VulkanDevice Create(VulkanDeviceOptions options) {
        if (!TryCreate(options, out var device, out var reason)) {
            throw new PlatformNotSupportedException(reason);
        }

        return device;
    }

    /// <summary>Creates a device, reporting failure rather than throwing.</summary>
    /// <param name="options">What to create.</param>
    /// <param name="device">The device, when it was created.</param>
    /// <param name="reason">Why it was not, when it was not.</param>
    public static bool TryCreate(
        VulkanDeviceOptions options,
        [NotNullWhen(true)] out VulkanDevice? device,
        [NotNullWhen(false)] out string? reason
    ) {
        device = null;
        var presenting = options.Surface.CanPresent;

        var instanceOptions = new VulkanInstanceOptions {
            ApplicationName = "Vixen",
            Logger = options.Logger,
            RequiredExtensions = presenting
                ? [.. VulkanSurface.RequiredExtensions(options.Surface.Kind), .. options.RequiredInstanceExtensions]
                : [.. options.RequiredInstanceExtensions]
        };

        if (!VulkanInstance.TryCreate(instanceOptions, out var instance, out reason)) {
            return false;
        }

        var api = instance.Api;
        var surface = default(SurfaceKHR);
        KhrSurface? khrSurface = null;

        try {
            if (presenting) {
                api.TryGetInstanceExtension(instance.Handle, out KhrSurface loaded);
                khrSurface = loaded;

                if (!VulkanSurface.TryCreate(instance, options.Surface, out surface, out reason)) {
                    instance.Dispose();
                    return false;
                }
            }

            var adapters = VulkanAdapter.Enumerate(instance, surface, khrSurface);

            // A named physical device wins over scoring, because whatever named it knows something
            // scoring cannot: which GPU the display is actually wired to.
            var adapter = options.PreferredPhysicalDevice != 0
                ? adapters.Find(candidate => (nint)candidate.Handle.Handle == options.PreferredPhysicalDevice)
                : null;

            if (adapter is null && options.PreferredPhysicalDevice != 0) {
                DestroySurface(khrSurface, instance, surface);
                instance.Dispose();
                reason = $"The physical device 0x{options.PreferredPhysicalDevice:X} was asked for and this "
                    + $"instance enumerated {adapters.Count} others, none of them it.";

                return false;
            }

            if (adapter is null && !VulkanAdapter.TrySelect(adapters, presenting, out adapter, out reason)) {
                DestroySurface(khrSurface, instance, surface);
                instance.Dispose();
                return false;
            }

            if (!TryCreateLogicalDevice(instance, adapter, presenting, options.RequiredDeviceExtensions, out var handle, out reason)) {
                DestroySurface(khrSurface, instance, surface);
                instance.Dispose();
                return false;
            }

            device = new(instance, adapter, handle, surface, options, true);

            // Guarded rather than left to the generated call site: two of these arguments allocate,
            // and the boot log is the one place where a disabled Information level is normal.
            if (options.Logger is { } logger && logger.IsEnabled(LogLevel.Information)) {
                var kind = adapter.Kind.ToString();
                var version = AdapterSelection.Describe(adapter.UsableApiVersion);
                var path = device.UsesDynamicRendering ? "dynamic rendering" : "render-pass objects";
                VulkanLog.DeviceCreated(logger, adapter.Name, kind, version, path, instance.ValidationEnabled);
            }

            reason = null;
            return true;
        } catch (Exception exception) when (exception is InvalidOperationException or VulkanException) {
            DestroySurface(khrSurface, instance, surface);
            instance.Dispose();
            reason = exception.Message;
            return false;
        }
    }

    /// <inheritdoc />
    public void BeginFrame() {
        lock (gate) {
            ThrowIfDisposed();
            var slot = FrameSlot;

            foreach (var queue in queues) {
                queue.WaitAndReset(slot);
            }

            // Only now is it safe: every command buffer that could have referenced these has retired.
            foreach (var action in retiring[slot]) {
                action();
            }

            retiring[slot].Clear();
            semaphoreCursor = slot * (semaphores.Length / FramesInFlight);
            lastSignalled = default;

            // pendingWaits and presentExpected are deliberately *not* cleared. A pending wait is a
            // promise that a signalled semaphore will be waited on, and dropping one is never right:
            // the semaphore stays signalled, the next acquire that reuses it is invalid, and the
            // presentation engine is handed an image nothing waited for. Clearing them here made the
            // conventional order — acquire the image, then begin the frame — silently unsynchronised,
            // which is precisely the bug Samples/01 found the first time it presented to a real
            // window. Honouring a wait late is correct; discarding it is not.

            foreach (var pool in commandPools) {
                if (pool.Key.Frame == slot) {
                    Api.ResetCommandPool(device, pool.Value, 0);
                }
            }
        }
    }

    /// <inheritdoc />
    public void EndFrame() {
        lock (gate) {
            ThrowIfDisposed();

            // An empty submission whose only job is to signal. Every queue gets one, whether or not
            // anything was submitted to it this frame: BeginFrame waits on all of them, and a fence
            // that is never signalled is a hang rather than a slowdown.
            foreach (var queue in queues) {
                var info = new SubmitInfo { SType = StructureType.SubmitInfo };
                Api.QueueSubmit(queue.Handle, 1, &info, queue.FenceFor(FrameSlot));
            }

            frame++;
        }
    }

    /// <inheritdoc />
    public void WaitIdle() {
        lock (gate) {
            if (!disposed) {
                Api.DeviceWaitIdle(device);
            }
        }
    }

    /// <inheritdoc />
    public ICommandList BeginCommandList(QueueKind kind = QueueKind.Graphics, string name = "") {
        ThrowIfDisposed();
        var slot = FrameSlot;
        var queue = QueueFor(kind);

        var pool = commandPools.GetOrAdd(
            (Environment.CurrentManagedThreadId, slot),
            key => CreatePool(queue.Family, key.Thread)
        );

        var allocate = new CommandBufferAllocateInfo {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = pool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1
        };

        CommandBuffer buffer;
        Check(Api.AllocateCommandBuffers(device, &allocate, &buffer), "vkAllocateCommandBuffers");

        var begin = new CommandBufferBeginInfo {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit
        };

        Check(Api.BeginCommandBuffer(buffer, &begin), "vkBeginCommandBuffer");
        return new VulkanCommandList(this, buffer, kind, name);
    }

    /// <inheritdoc />
    public void Dispose() {
        lock (gate) {
            if (disposed) {
                return;
            }

            Api.DeviceWaitIdle(device);
            disposed = true;

            foreach (var pending in retiring) {
                foreach (var action in pending) {
                    action();
                }

                pending.Clear();
            }

            DestroyAll();
            renderPasses.Dispose();

            foreach (var semaphore in semaphores) {
                Api.DestroySemaphore(device, semaphore, null);
            }

            foreach (var pool in descriptorPools) {
                Api.DestroyDescriptorPool(device, pool, null);
            }

            foreach (var pool in commandPools.Values) {
                Api.DestroyCommandPool(device, pool, null);
            }

            foreach (var queue in queues) {
                queue.Destroy();
            }

            allocator.Dispose();
            Api.DestroyDevice(device, null);
            DestroySurface(khrSurface, instance, surface);
            khrSwapchain?.Dispose();
            khrSurface?.Dispose();
            khrDynamicRendering?.Dispose();

            if (ownsInstance) {
                instance.Dispose();
            }
        }
    }

    /// <summary>Runs an action once the frame being recorded now cannot still be on the GPU.</summary>
    /// <param name="action">What to do.</param>
    /// <remarks>
    ///     Every <c>Destroy</c> goes through here. Destroying a Vulkan object that a submitted command
    ///     buffer still references is undefined behaviour, and the window in which it is unsafe is
    ///     exactly <see cref="FramesInFlight" /> frames wide — which the caller has no way of knowing
    ///     and should not have to.
    /// </remarks>
    internal void Retire(Action action) {
        lock (gate) {
            if (disposed) {
                action();
                return;
            }

            retiring[FrameSlot].Add(action);
        }
    }

    internal void SubmitTo(VulkanQueue queue, CommandBuffer* buffers, int count) {
        lock (gate) {
            ThrowIfDisposed();

            // The previous submission's signal is waited on by this one. That chains a frame's
            // submissions in the order they were made — which is the order they already run in on one
            // queue — and, more importantly, means every semaphore this device signals is waited on
            // exactly once. A signalled semaphore nobody waits on stays signalled forever, and
            // signalling it again next frame is invalid usage the layers report as a complaint about
            // the *second* signal, a frame away from the submission that actually leaked it.
            if (lastSignalled.Handle != 0) {
                pendingWaits.Add((lastSignalled, PipelineStageFlags.AllCommandsBit));
                lastSignalled = default;
            }

            var waitCount = pendingWaits.Count;
            var waitSemaphores = stackalloc VkSemaphore[Math.Max(1, waitCount)];
            var waitStages = stackalloc PipelineStageFlags[Math.Max(1, waitCount)];

            for (var index = 0; index < waitCount; index++) {
                waitSemaphores[index] = pendingWaits[index].Semaphore;
                waitStages[index] = pendingWaits[index].Stage;
            }

            pendingWaits.Clear();

            // Signalled only when something will wait: a present. Offscreen work — the dedicated
            // server, the golden-image suite — has no waiter at all, and signalling for it was the
            // other half of the same bug.
            var signal = presentExpected ? NextSemaphore() : default;

            var info = new SubmitInfo {
                SType = StructureType.SubmitInfo,
                CommandBufferCount = (uint)count,
                PCommandBuffers = buffers,
                WaitSemaphoreCount = (uint)waitCount,
                PWaitSemaphores = waitCount > 0 ? waitSemaphores : null,
                PWaitDstStageMask = waitCount > 0 ? waitStages : null,
                SignalSemaphoreCount = signal.Handle != 0 ? 1u : 0u,
                PSignalSemaphores = signal.Handle != 0 ? &signal : null
            };

            Check(Api.QueueSubmit(queue.Handle, 1, &info, default), "vkQueueSubmit");
            lastSignalled = signal;
        }
    }

    /// <summary>Registers a semaphore the next submission has to wait on.</summary>
    /// <remarks>
    ///     Called by a swapchain acquire, which is also what tells the device a present is coming and
    ///     therefore that submissions have to signal something for it to wait on.
    /// </remarks>
    internal void WaitBeforeNextSubmit(VkSemaphore semaphore, PipelineStageFlags stage) {
        lock (gate) {
            pendingWaits.Add((semaphore, stage));
            presentExpected = true;
        }
    }

    /// <summary>What a present has to wait on before the image is safe to show.</summary>
    /// <remarks>
    ///     The last submission's signal, normally. If nothing was submitted since the acquire — a
    ///     present of an untouched image, which the swapchain permits — the acquire's own semaphore is
    ///     handed over instead, because it is still unwaited and presenting is a legal thing to wait
    ///     it with.
    /// </remarks>
    internal VkSemaphore TakePresentWait() {
        lock (gate) {
            if (lastSignalled.Handle != 0) {
                var signalled = lastSignalled;
                lastSignalled = default;
                return signalled;
            }

            if (pendingWaits.Count > 0) {
                var acquired = pendingWaits[0].Semaphore;
                pendingWaits.RemoveAt(0);
                return acquired;
            }

            return default;
        }
    }

    internal VulkanQueue QueueFor(QueueKind kind) => kind switch {
        QueueKind.Compute => compute,
        QueueKind.Transfer => transfer,
        _ => graphics
    };

    internal KhrSwapchain? Swapchains => khrSwapchain;

    internal KhrSurface? Surfaces => khrSurface;

    internal SurfaceKHR Surface => surface;

    internal VulkanAllocator Allocator => allocator;

    internal static void Check(Result result, string call) {
        if (result != Result.Success) {
            throw new VulkanException($"{call} failed with {result}.");
        }
    }

    VkSemaphore NextSemaphore() {
        var perFrame = semaphores.Length / FramesInFlight;
        var start = FrameSlot * perFrame;

        if (semaphoreCursor >= start + perFrame) {
            throw new InvalidOperationException(
                $"More than {perFrame} submissions in one frame. The semaphore ring is sized at device "
                + "creation because growing it mid-frame means a driver call at the worst possible "
                + "moment; raise it rather than reusing a semaphore that may still be pending."
            );
        }

        return semaphores[semaphoreCursor++];
    }

    CommandPool CreatePool(uint family, int thread) {
        var info = new CommandPoolCreateInfo {
            SType = StructureType.CommandPoolCreateInfo,
            QueueFamilyIndex = family,

            // Transient because every buffer allocated from it is one-time-submit and the whole pool
            // is reset each frame. No per-buffer reset flag: resetting the pool is one call rather
            // than one per list, and it is what lets the driver reuse the whole arena.
            Flags = CommandPoolCreateFlags.TransientBit
        };

        CommandPool pool;
        Check(Api.CreateCommandPool(device, &info, null, &pool), "vkCreateCommandPool");
        Name(ObjectType.CommandPool, pool.Handle, $"CommandPool(thread {thread})");
        return pool;
    }

    void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(disposed, typeof(VulkanDevice));

    static void DestroySurface(KhrSurface? khrSurface, VulkanInstance instance, SurfaceKHR surface) {
        if (surface.Handle != 0) {
            khrSurface?.DestroySurface(instance.Handle, surface, null);
        }
    }

    static bool TryCreateLogicalDevice(
        VulkanInstance instance,
        VulkanAdapter adapter,
        bool presenting,
        IReadOnlyList<string> required,
        out Device device,
        [NotNullWhen(false)] out string? reason
    ) {
        device = default;
        var api = instance.Api;
        var extensions = new List<string>();

        if (presenting) {
            extensions.Add(KhrSwapchain.ExtensionName);
        }

        // Whatever is sharing this device asked for these, and it asked before the device existed
        // because that is the only moment they can be enabled. Duplicates are filtered rather than
        // rejected: an interop layer naming VK_KHR_swapchain on a presenting device is reasonable.
        foreach (var name in required) {
            if (!extensions.Contains(name)) {
                extensions.Add(name);
            }
        }

        // Mandatory where offered, not optional: the specification says a device that advertises
        // VK_KHR_portability_subset *must* have it enabled, and MoltenVK advertises it always. Leaving
        // it out is invalid usage that most drivers tolerate and some do not.
        if (adapter.Extensions.Contains(VulkanAdapter.PortabilitySubset)) {
            extensions.Add(VulkanAdapter.PortabilitySubset);
        }

        // VK_KHR_dynamic_rendering does not stand alone below Vulkan 1.3: it requires
        // VK_KHR_depth_stencil_resolve, which requires VK_KHR_create_renderpass2. Both became core in
        // 1.2, so a 1.2 device needs neither and a 1.1 device needs both — and enabling the extension
        // without its dependencies is invalid usage that MoltenVK accepts and the validation layers
        // reject. Found exactly that way, on the first run against a real driver with layers loaded.
        var wantsDynamicRendering = adapter.UsableApiVersion < VulkanFeatures.Version13
            && adapter.Extensions.Contains(KhrDynamicRendering.ExtensionName)
            && (adapter.UsableApiVersion >= VulkanFeatures.Version12
                || (adapter.Extensions.Contains(VulkanFeatures.CreateRenderPass2)
                    && adapter.Extensions.Contains(VulkanFeatures.DepthStencilResolve)));

        if (wantsDynamicRendering) {
            if (adapter.UsableApiVersion < VulkanFeatures.Version12) {
                extensions.Add(VulkanFeatures.CreateRenderPass2);
                extensions.Add(VulkanFeatures.DepthStencilResolve);
            }

            extensions.Add(KhrDynamicRendering.ExtensionName);
        }

        var missing = extensions.Where(name => !adapter.Extensions.Contains(name)).ToArray();

        if (missing.Length > 0) {
            reason = $"'{adapter.Name}' does not offer required device extension(s): "
                + string.Join(", ", missing);

            return false;
        }

        var families = adapter.Queues.DistinctFamilies().ToArray();
        var priority = 1f;
        var queueInfos = stackalloc DeviceQueueCreateInfo[families.Length];

        for (var index = 0; index < families.Length; index++) {
            queueInfos[index] = new() {
                SType = StructureType.DeviceQueueCreateInfo,
                QueueFamilyIndex = families[index],
                QueueCount = 1,
                PQueuePriorities = &priority
            };
        }

        // Only what the device reported it has. Asking for a feature a device lacks fails device
        // creation outright, so the request is the intersection of what we want and what it offers.
        var enabled = new PhysicalDeviceFeatures {
            SamplerAnisotropy = adapter.Supported.SamplerAnisotropy,
            FillModeNonSolid = adapter.Supported.FillModeNonSolid,
            DepthClamp = adapter.Supported.DepthClamp,
            IndependentBlend = adapter.Supported.IndependentBlend,
            MultiDrawIndirect = adapter.Supported.MultiDrawIndirect,
            GeometryShader = adapter.Supported.GeometryShader,
            TessellationShader = adapter.Supported.TessellationShader,
            ShaderFloat64 = adapter.Supported.ShaderFloat64,
            PipelineStatisticsQuery = adapter.Supported.PipelineStatisticsQuery,
            FragmentStoresAndAtomics = adapter.Supported.FragmentStoresAndAtomics
        };

        var dynamicRendering = new PhysicalDeviceDynamicRenderingFeaturesKHR {
            SType = StructureType.PhysicalDeviceDynamicRenderingFeaturesKhr,
            DynamicRendering = true
        };

        var names = SilkMarshal.StringArrayToPtr(extensions);

        try {
            var create = new DeviceCreateInfo {
                SType = StructureType.DeviceCreateInfo,
                QueueCreateInfoCount = (uint)families.Length,
                PQueueCreateInfos = queueInfos,
                EnabledExtensionCount = (uint)extensions.Count,
                PpEnabledExtensionNames = (byte**)names,
                PEnabledFeatures = &enabled,
                PNext = wantsDynamicRendering || adapter.UsableApiVersion >= VulkanFeatures.Version13
                    ? &dynamicRendering
                    : null
            };

            Device handle;
            var result = api.CreateDevice(adapter.Handle, &create, null, &handle);

            if (result != Result.Success) {
                reason = $"vkCreateDevice failed with {result} on '{adapter.Name}'.";
                return false;
            }

            device = handle;
            reason = null;
            return true;
        } finally {
            SilkMarshal.Free(names);
        }
    }
}

/// <summary>A Vulkan call that failed in a way the caller could not have prevented.</summary>
/// <remarks>
///     Distinct from <see cref="ArgumentException" />, which is what a bad description gets: this one
///     means the driver refused something the RHI believed was legal, and the message carries the
///     entry point and the result code because those are the only two facts that narrow it down.
/// </remarks>
public sealed class VulkanException : Exception {
    /// <summary>Creates one.</summary>
    /// <param name="message">What failed.</param>
    public VulkanException(string message) : base(message) { }

    /// <summary>Creates one.</summary>
    public VulkanException() { }

    /// <summary>Creates one.</summary>
    /// <param name="message">What failed.</param>
    /// <param name="innerException">What caused it.</param>
    public VulkanException(string message, Exception innerException) : base(message, innerException) { }
}
