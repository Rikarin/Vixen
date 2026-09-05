// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

// ⚠ The whole assembly runs one test at a time, and what it buys is the only witness there is for a
// barrier — https://github.com/Rikarin/Vixen/issues/712.
//
// `VulkanDiagnostics` is process-wide: `Reset()`, `ErrorCount` and `Messages` are static, because the
// validation layers report on whichever thread hit the problem. Until this line every device class
// here opened its own `VulkanDevice` and xunit ran several at once — measured, not assumed: two test
// classes meeting at a `Barrier(2)` met in 34 ms — so a message could only be attributed to whichever
// test happened to be running. `TextureValidationDeviceTests` needs that attribution and nothing else
// in this suite asserts any barrier at all.
//
// The price is real and small, and it was measured rather than guessed: 875 tests, 5 s of run time in
// parallel and 7 s serialised, on the machine this was written on. `Platform/Vixen.Graphics.Vulkan.Tests` pays it with a
// `[Collection("Vulkan")]` over its device classes; here it is the assembly, because half of this
// suite opens a device and a per-class attribute would be one more list to keep in step with a
// folder.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
