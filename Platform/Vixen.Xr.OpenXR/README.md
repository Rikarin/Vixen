# Vixen.Xr.OpenXR

The XR backend for the three desktops and Android: OpenXR behind the seams
[Vixen.Xr](../../Core/Vixen.Xr/README.md) defines.

## The order of operations is the runtime's

This is the part that cannot be rearranged, and the API's shape exists to make that impossible to get
wrong.

1. Construct `OpenXrBackend`. It creates the OpenXR instance and asks for a system.
2. Ask `GetVulkanRequirements()` — the extensions the `VkInstance` and `VkDevice` **must** be created
   with, and the Vulkan versions the runtime will work with.
3. Ask `GetVulkanPhysicalDevice(instance)` — which GPU. Not a preference: on a laptop with two, the
   headset is wired to one of them.
4. Create `VulkanDevice` with all of that.
5. `CreateSession(binding, options, new VulkanXrImageImporter(device))`.

A device created without the runtime's extensions produces a session that appears to work and then
shows a black headset. There is no error to catch, which is why this is an ordering rather than a
note in a document.

## `XR_KHR_vulkan_enable`, not `_enable2`

The second revision has the runtime create the Vulkan instance and device itself. That would mean
handing this module the job of building the engine's device — and `VulkanDevice` is where that lives,
with its allocator, its queue plan, its feature detection and its portability handling. The first
revision's contract is exactly the one this engine wants: the runtime says what it needs, the engine
creates the device, the runtime is handed it.

## No native payload

Unlike `Vixen.Audio.Backend.OpenAL`, which ships OpenAL Soft because a stock machine has none, this
package ships bindings only. The OpenXR loader is installed by whatever runtime owns the headset —
SteamVR, the Oculus software, Monado, the Quest's own — and a copy shipped inside a game would shadow
the one that knows where the device is.

The consequence is that a machine with no headset usually has no loader either, so `OpenXrBackend`'s
constructor is written to survive `DllNotFoundException` and report itself unavailable. Backend
selection constructs every candidate; this one must not take the process with it.

## What it says when it does not work

`UnavailableReason` is a sentence, because "VR did not start" has several completely different causes
and the person reading the log needs to know which:

| | |
|---|---|
| the loader is not on this machine | no runtime installed — the ordinary case on a CI runner |
| the runtime does not offer `XR_KHR_vulkan_enable` | a runtime for a different graphics API |
| a runtime is installed and no device is connected | the headset is unplugged or asleep |
| the runtime does not support this kind of device | asked for handheld on a headset runtime, or the reverse |

## Poses are located at a time, and the time is the display time

`LocateViews` is asked where the eyes will be when the frame is *displayed*, not where they are now.
Locating with the wrong time — this instant, or last frame's — is a constant lag that looks exactly
like a slow tracker and is diagnosed as one.

The same is true of a controller: a pose action's state says only whether it is active, and *where*
it is has to be located in an action space at a display time. That is why pose actions get an action
space when the sets are attached, and why the session keeps the last frame's display time.

## Bindings are suggestions

`xrSuggestInteractionProfileBindings` is named that way deliberately. The runtime may ignore a
suggestion — because the user rebound the action, or because the profile has no such input — and a
game that assumed otherwise is a game that cannot be rebound.

A profile the runtime has never heard of is refused rather than fatal here: a game suggesting
bindings for five controllers on a runtime that knows three keeps the three, which is what the
specification asks for.

## Testing

`Vixen.Xr.OpenXR.Tests` skips every test that needs a runtime, and asserts what does not need one —
the format table, the version packing, and that an unavailable backend refuses work with its reason
rather than crashing. Everything about sessions, projections, frames and actions is tested in
`Vixen.Xr.Tests` against `NullXrBackend`, which is why that simulation is worth having.
