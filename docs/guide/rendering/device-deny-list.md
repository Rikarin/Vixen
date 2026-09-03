---
title: Refusing a GPU that lies
slug: rendering/device-deny-list
kind: guide
area: Rendering
summary: The curated list of GPUs and driver versions a backend must not be selected on — why a capability query cannot replace it, how a rule is written and matched, where it is consulted during device creation, and what it deliberately refuses to do.
api: [T:Vixen.Graphics.GpuDenyList, T:Vixen.Graphics.GpuDenyRule]
tags: [graphics, backends, android, drivers, vulkan]
since: 0.1
status: preview
related: [engine/booting-an-application, rendering/lit-path]
---

## What it is

`GpuDenyList` is a list of GPUs and driver versions a graphics backend must not be selected on. A
head sets one on `GraphicsOptions.DenyList`; `GraphicsHost` hands it to the backend; the backend
consults it while choosing a physical device, before it has created anything on one.

A `GpuDenyRule` is one entry: an adapter, a driver version, and a reason.

## What it is for

Every other refusal in adapter selection is the device answering a question honestly. It reports
Vulkan 1.0, or has no queue family that can draw, or cannot present to this surface. The engine asks,
the driver answers, the answer is believed.

⚠ **This exists for the driver that answers every question correctly and is wrong anyway.**
Doc 10 (`docs/plan/10-platforms.md`) § Android states the problem: Android driver fragmentation is
real, and some devices report Vulkan support and then fail on specific extensions. There is no
capability query for "this driver's `VK_KHR_dynamic_rendering` is a stub". There is only a list
somebody wrote down after a crash report.

That is why it is curated content rather than a heuristic. A heuristic guessing which drivers lie
would be wrong on hardware nobody tested, in the direction that turns the picture off.

## Using it

A rule is matched against `IGraphicsAdapter.Name` and `IGraphicsAdapter.DriverVersion`.

| Field | Matching | Wildcard |
|---|---|---|
| Adapter | Case-insensitive substring of the adapter's name | `*` — every adapter |
| Driver version | Case-insensitive substring of the driver version | `*` — every version |
| Reason | Not matched; printed | None — a rule with no reason is refused |

⚠ **Both fields are substrings, deliberately.** The same GPU reports `Mali-G78` on one device and
`Mali-G78 MC14` on another, and vendors add and drop prefixes between driver branches. A rule keyed
on equality stops matching after an OTA update and *looks* like coverage while doing nothing, which
is the worse of the two failures.

⚠ **The driver version is a substring rather than a range**, because `DriverVersion` is a string each
backend formats for humans and there is no ordering to compare against. Pretending otherwise would
mean parsing a different version scheme per vendor. A range is several rules — verbose, and honest.

⚠ **A rule matching every adapter *and* every driver is refused outright**, by the constructor and by
`GpuDenyList.Parse`. `* | * | …` is one keystroke from a rule for one device, and it would deny every
GPU on every machine — for a shipped game, a content update that turns the picture off. Name one of
the two fields.

### The text form

One rule per line, three `|`-separated fields; `#` starts a comment and blank lines are ignored.

⚠ **`Parse` throws on a line it cannot read rather than skipping it.** A deny-list is read to protect
a device from a driver, and the one outcome nobody can see is a rule that was silently dropped: the
run is green, the log is quiet, and the device the rule was written for is exactly as broken as
before. An empty adapter field and an empty reason field are failures for the same reason.

### Where it is consulted

`VulkanDeviceOptions.DenyList` is read inside adapter selection, between enumerating physical devices
and creating a logical one.

⚠ **That is the last moment at which "do not use this GPU" is still an answer rather than a regret.**
Asking after the device exists would mean having created a device on the driver the list exists to
stay away from — which, for the crash-on-creation cases, is the whole failure.

⚠ **The deny-list is asked before the capability floor.** A device both denied and below Vulkan 1.1
would otherwise be reported as "reports Vulkan 1.0", sending the reader after a driver update that
will not help. Order is what makes the message name the real cause.

⚠ **`VulkanDeviceOptions.PreferredPhysicalDevice` overrules the list.** Naming a raw
`VkPhysicalDevice` is an XR runtime saying which GPU the headset is wired to; there is no second
choice to fall through to, and refusing it would leave a session with no device rather than with a
discouraged one.

### What it does not do

- **It refuses an adapter, not a feature.** A denied adapter is one the backend is not selected on at
  all, which is what makes the head's preference list fall through — Vulkan denied, so OpenGL, so
  Null. A capability a device reports and does not have is a different problem with a different
  answer: `GraphicsDeviceFeatures` is where a renderer asks, and a deny-list that cleared a bit there
  would be lying in the other direction.
- **It is not loaded from content yet.** Doc 10 asks for the database to be shipped as content and
  updatable, so a game can be fixed on a device its build predates. Today a head sets it in
  `OnConfigure`, so a device discovered broken after a build shipped needs a new build. That gap is
  real and is stated rather than papered over.
- **It ships empty.** Vixen carries no curated rules. Every entry is a decision somebody made about
  hardware they have watched fail, and a list this repository guessed at would be one nobody could
  defend.

## Examples

A head that wants Vulkan, then GL, then nothing — and knows two devices where the first is a lie:

```csharp no-compile="a fragment; OnConfigure is the game's own override"
config.Graphics.Backends.Add(GraphicsBackend.Vulkan);
config.Graphics.Backends.Add(GraphicsBackend.OpenGl);
config.Graphics.Backends.Add(GraphicsBackend.Null);

config.Graphics.DenyList = GpuDenyList.Parse(
    """
    # adapter | driver version | reason
    Mali-G78  | *              | VK_KHR_dynamic_rendering is advertised and unimplemented
    Adreno    | 512.502        | crashes in vkCreateSwapchainKHR on rotation
    """
);
```

On a phone whose GPU matches the first rule, Vulkan refuses with that reason among the rejections and
the preference list falls through to OpenGL. On the same phone with a later driver, and on every
other machine, the list does nothing at all.

The rules can be built in code instead, which is what a test does and what a head with one entry
would reasonably do:

```csharp no-compile="a fragment; the list is whatever the head has decided on"
var denied = new GpuDenyList([
    new("Mali-G78", GpuDenyList.Any, "VK_KHR_dynamic_rendering is advertised and unimplemented")
]);
```

## See also

- [Booting an application](../engine/booting-an-application.md) — where `GraphicsOptions.Backends`
  is read, and what a refusal from one backend does to the chain.
- [The lit path](lit-path.md) — what a frame asks a device for, and therefore what a device lying
  about a capability costs.
- `docs/plan/10-platforms.md` § Android — the fragmentation this answers, and the GLES fallback it
  is meant to select.
