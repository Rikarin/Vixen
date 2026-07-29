# Vixen.Graphics.Null

The graphics backend with no GPU.

```csharp
using var device = new NullDevice(new() { Record = true });

using var list = device.BeginCommandList();
list.BeginRenderPass(pass);
list.BindPipeline(pipeline);
list.Draw(3);
list.EndRenderPass();
list.Finish();
device.GraphicsQueue.Submit([list]);

Assert.Single(device.Recorder!.OfKind(RecordedCommandKind.Draw));
```

## Two jobs, and they are the same job

It is what a **dedicated server** renders on ([doc 17](../../docs/plan/17-app-heads-and-shipping.md)),
and it is what **every RHI test** runs against. Because the second happens on every build, the first
cannot quietly rot — the same argument that put `Vixen.Platform.Headless` in Phase 1, and the reason
[doc 05](../../docs/plan/05-graphics-rhi.md) calls this the most thoroughly exercised backend in the
engine.

Which is why **recording is off by default**. A server that accumulated a command log would run out
of memory some hours in. A test turns it on; a server never does.

## What it makes possible

**"Did my render feature emit the right calls" becomes a unit test.** That is a question about a
sequence of calls, and answering it by rendering an image and diffing it is slower, flakier, and
tells you less about what went wrong. `CommandRecorder` gives the sequence, `RecordedCommand`
compares and prints readably, and `Dump()` indents by debug group so a frame's stream is navigable.

`Contains` matches a **contiguous** run rather than "somewhere in the stream" — a looser match would
pass when a barrier was inserted in the middle of a copy, which is exactly the mistake worth
catching.

**The stream is in submission order, not recording order.** Lists record on several threads at once,
so writing into the shared recorder as calls happened would produce a log whose order depended on the
scheduler. Submission order is what the GPU would see.

## The validation is the other half

Each of these is undefined behaviour on a real backend, and each is caught here on a machine with no
GPU, with a message saying what was wrong:

- a draw outside a render pass, or a nested pass
- a dispatch, copy or barrier **inside** a pass — a tiled GPU would have to resolve the tile
- a list finished inside a pass, or with a debug group still open
- a list submitted before `Finish()`, or submitted twice
- a buffer copied onto itself
- a host write to a device-local buffer, or past the end of one
- a handle used after it was destroyed — caught by its generation
- a compute pipeline on a device that reports no compute, so the fallback path the capability exists
  for actually gets taken
- a descriptor written as a kind other than the one its set layout declared, or written to a binding
  the layout does not declare at all

The last one was left out for a while, on the grounds that `VulkanDevice` already checks it. It does —
but only on a machine that has a driver, and only with the validation layers switched on. Without them
the write lands, the shader reads whichever kind it was compiled for, and the frame comes out wrong
instead of the run failing. Checking it here makes it a red build on an agent with no GPU, and turning
it on found a real one immediately: the forward lighting feature declared its block
`DynamicUniformBuffer` and wrote it as a plain one, so every per-object offset it bound would have
been ignored and every object would have lit itself with the first one's lights. The rest were fixture
layouts that had drifted from what the code they exercise actually binds, which is the same bug one
step removed — a test that could not have caught the first.

## Resource creation genuinely does not allocate

A handle and its description, and nothing proportional to the size asked for — so a server that
creates and destroys a 4K render target every frame stays flat. Shader bytecode is measured and
dropped rather than kept: nothing here will ever compile it. `LiveResourceCount` is what a leak test
asserts on.

`NullSwapChain.NextStatus` is a field, so the out-of-date and device-lost paths — the ones nobody
exercises until a driver update breaks them on a user's machine — are reachable from a test. Doc 05
asks for that fault injection; here it costs a field.

## Still to come

`Vixen.Graphics.Vulkan`, and the first triangle. This backend is what its tests will be written
against first.

Licensed under Apache-2.0.
