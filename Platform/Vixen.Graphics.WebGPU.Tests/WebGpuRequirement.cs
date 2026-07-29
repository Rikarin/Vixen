// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Graphics.WebGPU.Native;
using Xunit;

namespace Vixen.Graphics.WebGPU.Tests;

/// <summary>Whether a missing implementation is a reason to skip or a reason to fail.</summary>
/// <remarks>
///     <para>
///         The same argument <c>VulkanRequirement</c> makes, with one difference that matters.
///         Vulkan is usually installed; <b>nothing installs WebGPU</b>. No desktop operating system
///         ships Dawn or wgpu-native, and <c>Silk.NET.WebGPU</c> is bindings only — so on a fresh
///         clone the ordinary answer is "not restored", and skipping is right.
///     </para>
///     <para>
///         It is exactly wrong on a CI leg whose purpose is to exercise the backend, where a runner
///         that failed to restore would report a green build having proved nothing. That leg sets
///         <c>VIXEN_REQUIRE_WEBGPU=1</c> and every skip here becomes a failure naming what was
///         missing — including the command that fixes it, because "no WebGPU" and "you have not run
///         the restore" look identical from a test log.
///     </para>
/// </remarks>
static class WebGpuRequirement {
    static readonly Lock Gate = new();

    static bool attempted;
    static string? failure;

    /// <summary>Whether the environment insists an implementation be present.</summary>
    public static bool Demanded =>
        Environment.GetEnvironmentVariable("VIXEN_REQUIRE_WEBGPU") is "1" or "true" or "TRUE";

    /// <summary>An offscreen device, or a skip.</summary>
    /// <remarks>
    ///     <para>
    ///         Offscreen: no surface, which is what the golden-image suite and a dedicated server use
    ///         and the only thing a headless CI runner can do. The swapchain path stays covered by
    ///         the fake, which can produce an out-of-date surface on demand and a real one cannot.
    ///     </para>
    ///     <para>
    ///         A device per test rather than one shared. It costs a few milliseconds, and the
    ///         alternative is that one test's leaked resource is another test's failure — which on a
    ///         backend whose whole subject is resource lifetime would be the worst possible place to
    ///         economise.
    ///     </para>
    /// </remarks>
    public static WebGpuDevice Device(int framesInFlight = 2) {
        var binding = Binding();

        return new(binding, new WebGpuDeviceOptions { FramesInFlight = framesInFlight });
    }

    /// <summary>A binding, or a skip that says why there is none.</summary>
    public static NativeWebGpuBinding Binding() {
        lock (Gate) {
            // The first attempt is remembered, so a suite of thirty tests does not walk the library
            // search thirty times to be told the same thing.
            if (attempted && failure is not null) {
                Unavailable(failure);
            }

            attempted = true;

            if (NativeWebGpuBinding.TryCreate(new() { Surface = SurfaceHandle.None }, out var binding, out var reason)) {
                return binding;
            }

            failure = reason;
            Unavailable(reason);

            throw new InvalidOperationException("unreachable: Unavailable does not return");
        }
    }

    static void Unavailable(string reason) {
        if (Demanded) {
            Assert.Fail(
                $"VIXEN_REQUIRE_WEBGPU is set, so this test may not skip: {reason} Run "
                + "'./build.sh RestoreNativeDeps', which fetches the wgpu-native pinned in "
                + "build/native-dependencies.json."
            );
        }

        Assert.Skip($"{reason} Run './build.sh RestoreNativeDeps' to fetch it.");
    }
}
