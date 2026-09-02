# Vixen.Graphics.Vulkan

The RHI's reference implementation.

## It has met a driver

Instance creation, portability handling and physical-device enumeration are **verified** against
Vulkan Loader 1.4.350 and MoltenVK 1.4.2 on Apple silicon. The riskiest guess — that MoltenVK needs
both `VK_KHR_portability_enumeration` *and* the create flag or the Loader returns no devices — was
right, and the test that asserts it now passes rather than skipping.

The first thing that actually went wrong was not Vulkan at all: see **Finding the loader** below.

Tested without a driver, and worth keeping that way:

| | |
|---|---|
| `VulkanFormats` | Every format, both directions, no collisions, sRGB preserved across the boundary. 7 tests. |
| `AdapterSelection` | The whole selection policy as a pure function. 9 tests. |

That split is deliberate rather than convenient. Format mapping and device selection are where a
mistake is *silent* — a format mapped to the wrong Vulkan enum renders the wrong colours rather than
failing, and a selector that quietly prefers the wrong GPU looks like a performance problem. Both are
expressible as pure functions over plain records, so both are tested now.

## Finding the loader

`Vk.GetApi()` asks the OS to resolve `libvulkan` by name, and on macOS the dynamic linker's default
search path is `/usr/local/lib` and `/usr/lib` — **not** `/opt/homebrew/lib`, which is where Homebrew
puts it on Apple silicon. So a machine with a perfectly working Vulkan (`vulkaninfo` lists MoltenVK,
the ICD is registered) fails to load with a `DllNotFoundException` whose message says nothing about
paths. That was the very first failure when this code met a real SDK, before a single Vulkan call ran.

`VulkanLoader` tries the OS first, then probes `VULKAN_SDK`, Homebrew and the versioned soname.
`DYLD_LIBRARY_PATH` would also work and is the wrong fix: SIP strips it in some launch paths, it has
to be set before the process starts, and it makes running the engine depend on the shell that
started it.

## Validation layers are a separate install

`brew install vulkan-loader molten-vk` gives you a working Vulkan and **no validation layers** —
`vulkan-validationlayers` is its own formula. Without it `VulkanInstance` reports
`ValidationEnabled == false` and logs event 2001, because silently running unvalidated would defeat
the non-negotiable in [doc 00](../../docs/plan/00-vision-and-principles.md).

```bash
brew install vulkan-validationlayers
```

**And on Homebrew that is not enough.** The layer *enumerates* — `vulkaninfo` lists it and so does
`vkEnumerateInstanceLayerProperties` — and then `vkCreateInstance` fails with
`VK_ERROR_LAYER_NOT_PRESENT`. The cause is the same one as above, one level down: the manifest names
its library by bare filename (`libVkLayer_khronos_validation.dylib`) and the dynamic linker resolves
that against `/usr/local/lib` and `/usr/lib`, not `/opt/homebrew/lib`. Pre-loading the dylib by
absolute path does not help, because the loader's own `dlopen` still uses the bare name — measured,
not assumed.

So installing the layers *breaks* instance creation unless the process starts with:

```bash
export DYLD_LIBRARY_PATH=/opt/homebrew/lib
```

`VulkanInstance` survives it either way: a `VK_ERROR_LAYER_NOT_PRESENT` is retried without the layer
and logged as event 2002 with that hint. Running unvalidated is bad; failing to open a window because
a development aid is mispackaged is worse.

## The decisions

**Software devices are ranked last and never skipped.** lavapipe is a conformant Vulkan 1.3 driver
with no GPU, and it is what makes this backend, the validation layers and the golden-image suite
testable on a standard CI runner ([doc 10](../../docs/plan/10-platforms.md) § Linux). A selector that
filtered it out would make the most valuable CI leg in the plan impossible.

**Portability is handled at instance creation, both halves of it.** On macOS the Loader will not
return MoltenVK's `VkPhysicalDevice` unless the instance carries `VK_KHR_portability_enumeration`
*and* the matching create flag. With only one of them the symptom is "no Vulkan devices found" on a
machine that works fine — so `AdapterSelection` says exactly that when it enumerates nothing, and a
test asserts the message.

**The viewport is submitted with a negative height, and the front-face winding maps straight
through anyway.** Vulkan's clip space has +Y down and the engine's has it up
([Conventions.md](../../Core/Vixen.Core.Mathematics/Conventions.md)), so `SetViewport` flips Y the
standard way rather than putting a flip in every vertex shader. The tempting conclusion — that the
flip mirrors framebuffer coordinates, so `VulkanEnums.ToVulkan(FrontFace)` must invert to pay for
it — counts one mirror where there are two: Vulkan's own +Y-down convention is the other, and they
cancel. A mesh wound counter-clockwise seen from outside — what `MeshPrimitives` builds, and what
`FrontFace.CounterClockwise` names — arrives counter-clockwise where facing is decided. The mapping
*was* inverted once on that argument: `CullMode.Back` then kept the inside of every closed mesh, and
the golden fixtures drew nothing with no validation error anywhere. The `cull-back` and `cull-front`
references and a unit test on the mapping now both pin the identity.

**The failure message names every device and why each was rejected.** "No suitable GPU found" is the
least useful error a graphics engine can produce, and every fact needed to do better is available at
the moment it would be thrown away.

**The debug messenger is chained onto the instance create info**, so the layers can report what goes
wrong *during* `vkCreateInstance` — otherwise the one window they cannot see into, and exactly where
a bad extension list fails.

**The messenger always returns `VK_FALSE`.** Returning non-zero aborts the call that triggered the
message; the specification reserves that for layer development, and it turns a warning into a crash.

**A swapchain for a second window makes and owns its own `VkSurfaceKHR`.** A device has exactly one
surface of its own, created before the physical device is chosen because the queue families are
selected against it — and a `VkSurfaceKHR` may have exactly one swapchain at a time, so a second
swapchain built on the device's surface fails with `VK_ERROR_NATIVE_WINDOW_IN_USE_KHR`. So
`VulkanSwapChain` compares the `SurfaceHandle` it was given against the one the device was created
from and creates its own when they differ, destroying it with itself and leaving the device's alone.
Present support is then re-asked *for that surface*: every desktop driver in practice says yes, which
is exactly why finding out by way of undefined behaviour on the one that does not would be finding it
out the hard way. This is what an editor tearing a dock panel out onto the desktop needs.

**Presentation is tested on a surface with no window, and that is not a stand-in for one.**
`SurfaceKind.Headless` maps to `VK_EXT_headless_surface`: an ordinary `VkSurfaceKHR` that reports
capabilities, formats and present modes, carries a real swapchain, and hands images back round
through an actual presentation engine — there is simply nothing on the other end of the present. So
`VulkanPresentationTests` runs the production `VulkanSwapChain`, not a double and not the offscreen
chain, and the validation layers watch it: sharing one present semaphore between images, or acquiring
the same image twice, both go red there.

⚠ **This corrects a claim the tests used to make** — that acquire and present cannot be covered
because they need a window and AppKit aborts a process that makes one off the main thread. What
genuinely needs a window is turning a real `CAMetalLayer` into a `VkSurfaceKHR`; everything built on
the surface does not care which kind it was. What a headless surface still cannot produce is
`VK_ERROR_OUT_OF_DATE_KHR` or `VK_SUBOPTIMAL_KHR`, which come from a window server resizing a surface
underneath the swapchain — their translation is asserted directly instead, because a rebuild loop that
never converges is what collapsing those two costs.

**A stated sample type is checked here, and an unstated one is not.** `DescriptorBinding.SampleType`
is WebGPU's requirement — Vulkan takes any view a layout's "sampled image" binding is given, and the
shader's own type decides how it is read. But a layout that *does* say `Depth`, and is then handed a
colour view or a sampler that does not compare, is wrong on the web and would be found there rather
than here. So a declaration is checked and silence is not: nothing that worked before is refused, and
a renderer that describes its shadow map properly finds out on the machine it is being written on.

**We request exactly Vulkan 1.1**, the floor [doc 05](../../docs/plan/05-graphics-rhi.md) states,
rather than the highest available — a driver should not enable behaviour we have not tested against.
A device that offers more is queried for it explicitly.

**Descriptor indexing is asked about as four features, not as an extension string.** MoltenVK offers
`VK_EXT_descriptor_indexing` on every device it runs on and gates the features behind the Metal
argument-buffer tier, so a capability answered from the string reports bindless on every Mac and then
fails at `vkCreateDescriptorSetLayout` — long after the renderer chose its path. `VulkanFeatures`
therefore asks `vkGetPhysicalDeviceFeatures2` for runtime-descriptor-array, partially-bound,
non-uniform indexing and update-after-bind, and `vkGetPhysicalDeviceProperties2` for the two
update-after-bind ceilings that decide how large a table may be. Device creation enables exactly
those four and nothing else.

**The `pNext` chain at device creation is a chain.** It used to be one structure or none, and adding
a second the same way would have silently dropped whichever was not last — which for descriptor
indexing means a device that reports bindless, is created without it, and fails at the first
unbounded layout with a message about the layout.

**A bindless set gets its own descriptor pool.** `MaxBindlessDescriptors` is a six-digit number on a
desktop driver where `SetsPerPool` sizes the shared pools for sets of a handful of bindings, so
allocating one from a shared pool exhausts it and then exhausts the empty one made to replace it. The
dedicated pool is also the only descriptor memory here large enough that waiting for the device to go
away is the wrong answer, so it is destroyed with its set.

## Known gap

The debug callback writes to standard error rather than through the `ILogger` the instance was given.
Vulkan hands the callback a `void*` and a captured delegate would have to be pinned for the life of
the instance; doing it properly is the first thing to fix once there is a driver to test against.

## Still to come

Device and queue creation, the allocator, the swapchain, command lists, pipelines, descriptor sets,
barriers, and the dynamic-rendering path with its render-pass fallback. Their tests get written
against `Vixen.Graphics.Null` first, and against lavapipe in CI second.

Licensed under Apache-2.0.
