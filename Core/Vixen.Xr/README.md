# Vixen.Xr

VR and AR, with no runtime in it: the session state machine, per-eye poses and asymmetric
projections, runtime-owned swapchains, the action-based input model, and an ECS bridge — plus a
simulated headset, so all of it runs on a machine that has none.

Spec: [docs/plan/06](../../docs/plan/06-rendering-pipeline.md) § Other renderables, which lists
"VR/XR stereo (multiview, OpenXR)"; the OpenXR binding itself is
[Platform/Vixen.Xr.OpenXR](../../Platform/Vixen.Xr.OpenXR/README.md).

```csharp
using var backend = new OpenXrBackend(new OpenXrOptions { Logger = logger });

if (!backend.IsAvailable) {
    // Ordinary. A laptop, a CI runner, a headset that is asleep.
    return;
}

// The runtime dictates how the graphics device is created, so it is asked first.
var requirements = backend.GetVulkanRequirements();
var device = VulkanDevice.Create(new VulkanDeviceOptions {
    RequiredInstanceExtensions = requirements.InstanceExtensions,
    RequiredDeviceExtensions = requirements.DeviceExtensions
});

using var session = backend.CreateSession(
    ToBinding(device.NativeHandles),
    new XrSessionOptions(),
    new VulkanXrImageImporter(device)
);
```

## The frame loop belongs to the runtime

This is the thing to get right, and everything else here is shaped by it.

```csharp
while (session.PollEvents()) {
    if (!session.BeginFrame(out var frame)) {
        continue;                        // not running yet
    }

    if (frame.ShouldRender) {
        var views = session.LocateViews(in frame);
        // …render each eye into its swapchain image…
    }

    session.EndFrame(in frame, layers);  // even when nothing was drawn
}
```

`BeginFrame` blocks in `xrWaitFrame` until the compositor wants the next frame. That is how a runtime
paces an application to the display and how it decides where the latency goes — a game that renders
on its own schedule and submits when it happens to finish gets judder that no frame rate fixes.

**Every begun frame is ended**, including the ones that draw nothing and the ones where the game
threw. A runtime waiting for a frame that never arrives stalls the compositor for the whole system.

**`Synchronised` is not `Visible`.** A synchronised session must submit frames and need not draw
them: the user has taken the headset off, or a system menu is up. `frame.ShouldRender` is the answer;
skipping the loop entirely is the mistake.

## Four angles, not a field of view

A headset's frustum is not symmetric — the lenses are canted, so each eye sees several degrees
further towards its own side than towards the nose. `XrProjection.FromFieldOfView` builds the
projection from the four half-angles the runtime reports, in the engine's own convention:
right-handed, row-vector, reverse-Z. `XrProjectionTests` asserts that a *symmetric* frustum through
it gives exactly what `Matrix4x4.PerspectiveFieldOfView` gives, which is what makes "the same
convention" a fact rather than a claim.

Feeding a headset a matrix from another engine's convention is the second most common way to get a
black eye buffer. The first is forgetting that the runtime owns the swapchain.

## The runtime owns the images

`IXrSwapchain` looks like `ISwapChain` on purpose and differs in one way that matters: the images are
allocated by the compositor, handed over as native handles, and have to be *adopted* by the graphics
backend rather than created by it. That is what `IXrImageImporter` is — the one operation an XR
swapchain needs that the RHI has no portable way to express, since the handle is a `VkImage` and
means nothing to any other backend.

There is no `Present`. Releasing an image says the rendering is done; `EndFrame` is what shows it.

## Actions, not buttons

A game asks for `teleport` and the runtime decides that on this headset it is the right thumbstick
and on that one the trackpad — and the user can rebind it. One action carries a state per hand, so
"either hand can pick things up" is one piece of code:

```csharp
var gameplay = new XrActionSet("gameplay", "Gameplay");
var grab = gameplay.CreateAction("grab", XrActionType.Float, "Grab");

gameplay.SuggestBindingForBothHands(XrInteractionProfiles.OculusTouch, grab, XrPaths.SqueezeValue);
gameplay.SuggestBindingForBothHands(XrInteractionProfiles.Simple, grab, XrPaths.SelectClick);

session.AttachActionSets([gameplay]);
```

**Everything is declared before the session attaches the sets and nothing after.** That is OpenXR's
rule rather than this module's: attaching is when the runtime resolves bindings and applies the
user's own remapping, and an action created afterwards can never be bound to anything. `XrActionSet`
freezes itself at that point and says so.

Always bind to `XrInteractionProfiles.Simple` as well as to the specific profiles. It is what makes a
game work on a headset that did not exist when it shipped.

## The rig is an entity

Tracked poses arrive in the runtime's reference space, whose origin is the middle of somebody's room.
`XrOrigin` marks the entity that space is nailed to, so teleporting the player is moving one
transform and the headset and hands follow. `XrTrackedPose` on an entity makes it follow a device;
removing it stops that.

`XrOrigin.UnitsPerMetre` is where a game that does not work in metres converts — once, at the
boundary. Every VR project that has quietly rescaled poses in six places has ended up with hands that
do not line up with the controllers.

## The simulated headset

`NullXrBackend` is what `NullAudioBackend` and `NullDevice` are: not a stub, a simulation. It walks
the same state sequence a runtime does, offsets the two eyes by an interpupillary distance, follows a
`HeadPose` the caller sets, and paces frames by a counter — so four hundred frames run in a
millisecond and produce exactly the timings a headset would have. Every test in `Vixen.Xr.Tests` runs
against it, including the ones about focus, about frames that must be closed, and about the two eyes
actually being different.

## What is not here

**A render feature.** What a renderer does with two views and a swapchain image is the renderer's
business; this module produces the poses, the projections and the images.

**Single-pass multiview.** `XrSwapchainDescription.ArrayLayers` is the hook — two layers is the
multiview target — and the RHI half of it (a `VK_KHR_multiview` render pass and the shader's
`gl_ViewIndex`) is `Vixen.Graphics`'s to add. Two passes work today and cost what two passes cost.

**Hand tracking, eye tracking, passthrough surfaces, anchors.** All extensions, all additive, none of
them needed to render a stereo frame.
