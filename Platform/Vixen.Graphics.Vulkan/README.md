# Vixen.Graphics.Vulkan

The RHI's reference implementation.

## Read this first

**Everything that touches a driver in this assembly is unverified.** It was written against the
specification and the Silk.NET bindings on a machine with no Vulkan loader — no MoltenVK, no ICD —
at the maintainer's direction and with that stated up front. Treat `VulkanInstance` as a first draft
until it has met a driver.

What *is* tested, on a machine with no Vulkan at all:

| | |
|---|---|
| `VulkanFormats` | Every format, both directions, no collisions, sRGB preserved across the boundary. 7 tests. |
| `AdapterSelection` | The whole selection policy as a pure function. 9 tests. |

That split is deliberate rather than convenient. Format mapping and device selection are where a
mistake is *silent* — a format mapped to the wrong Vulkan enum renders the wrong colours rather than
failing, and a selector that quietly prefers the wrong GPU looks like a performance problem. Both are
expressible as pure functions over plain records, so both are tested now.

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

**The failure message names every device and why each was rejected.** "No suitable GPU found" is the
least useful error a graphics engine can produce, and every fact needed to do better is available at
the moment it would be thrown away.

**The debug messenger is chained onto the instance create info**, so the layers can report what goes
wrong *during* `vkCreateInstance` — otherwise the one window they cannot see into, and exactly where
a bad extension list fails.

**The messenger always returns `VK_FALSE`.** Returning non-zero aborts the call that triggered the
message; the specification reserves that for layer development, and it turns a warning into a crash.

**We request exactly Vulkan 1.1**, the floor [doc 05](../../docs/plan/05-graphics-rhi.md) states,
rather than the highest available — a driver should not enable behaviour we have not tested against.
A device that offers more is queried for it explicitly.

## Known gap

The debug callback writes to standard error rather than through the `ILogger` the instance was given.
Vulkan hands the callback a `void*` and a captured delegate would have to be pinned for the life of
the instance; doing it properly is the first thing to fix once there is a driver to test against.

## Still to come

Device and queue creation, the allocator, the swapchain, command lists, pipelines, descriptor sets,
barriers, and the dynamic-rendering path with its render-pass fallback. Their tests get written
against `Vixen.Graphics.Null` first, and against lavapipe in CI second.

Licensed under Apache-2.0.
