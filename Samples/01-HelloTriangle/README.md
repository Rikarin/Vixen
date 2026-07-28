# 01 — Hello Triangle

The first triangle, and the first time every layer runs at once: the app host opens a window, the
desktop platform hands over its native surface, the Vulkan backend builds a device and a swapchain
from it, and the render graph places the barriers.

```bash
dotnet run --project Samples/01-HelloTriangle -c Release
```

Deliberately the whole stack and nothing else. There is no engine, no ECS and no asset pipeline —
those arrive in Phase 2, and this staying small is what makes it a platform smoke test rather than a
demo.

## Why it matters more than it looks

It is the only thing that exercises the swapchain's acquire and present path. That path cannot be
tested automatically: presenting needs a window, and AppKit aborts when one is created off the
process's main thread, which is why the desktop tests force SDL's dummy video driver on macOS
(see [`docs/plan/10-platforms.md`](../../docs/plan/10-platforms.md) § macOS).

The first time it presented to a real window it found two synchronisation bugs that the whole
headless Vulkan suite had passed straight through. Both are described where they were fixed —
`VulkanDevice.BeginFrame` and `VulkanSwapChain.Present`.

## Running it in CI

```bash
dotnet run --project Samples/01-HelloTriangle -c Release -- --vixen-frames 120
```

`--vixen-frames N` runs N frames and stops. It cannot assert what was drawn, but it proves the stack
starts, presents and shuts down without a validation error or a hang — and with the validation layers
installed, a validation error is a non-zero exit.

⚠ **This is a recipe, not a description of the build.** `ci.yml` builds and tests on all three
platforms and runs no sample, so nothing invokes the flag today. Wiring it in needs a headless
display on the Linux runner, which is why it has not been.

## Shaders

`Shaders/` holds the GLSL and the SPIR-V compiled from it. The RHI never parses shader source, and
Raven is not wired into the build yet ([`docs/plan/07`](../../docs/plan/07-raven-shader-pipeline.md)),
so the modules are committed:

```bash
glslc Shaders/triangle.vert -o Shaders/triangle.vert.spv
glslc Shaders/triangle.frag -o Shaders/triangle.frag.spv
```
