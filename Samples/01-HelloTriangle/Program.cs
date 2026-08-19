// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.App;

namespace Vixen.Samples.HelloTriangle;

/// <summary>The first triangle, and the first time every layer runs at once — the desktop head.</summary>
/// <remarks>
///     <para>
///         Deliberately the whole stack and nothing else: the app host opens a window, the desktop
///         platform hands over its native surface, the Vulkan backend builds a device and a
///         swapchain from it, and the render graph places the barriers. There is no engine, no ECS,
///         no asset pipeline — that staying small is what makes it a platform smoke test rather than
///         a demo.
///     </para>
///     <para>
///         It is also the only thing that exercises acquire and present. Those cannot be tested
///         automatically: presenting needs a window, and AppKit aborts when one is created off the
///         process's main thread, which is why the desktop tests force SDL's dummy video driver on
///         macOS ([10](../../docs/plan/10-platforms.md)). So this is where that path is verified, by
///         hand. <c>--vixen-frames N</c> — which this sample needed and which therefore belongs to the
///         host rather than to it — lets CI at least prove the whole stack starts, presents and stops
///         without a validation error or a hang.
///     </para>
///     <para>
///         <b>The game itself is <see cref="TriangleGame" />; this file is only the desktop way in.</b>
///         The iOS and Android heads are sibling projects that link the same source and differ in
///         exactly one thing: who owns the frame loop. Here it is
///         <see cref="VixenApplication.Run" />; on a phone the operating system owns the main thread
///         and calls <c>RunFrame</c> instead.
///     </para>
/// </remarks>
static class Program {
    // ⚠ The platform is the host's to choose, and this used to take the choice away. It built a
    // `DesktopPlatform` and handed it to `WithPlatform`, which `AppBuilder.Build` honours ahead of
    // the factory — so `--vixen-headless` was parsed into `AppConfig.Headless` and then never asked,
    // and a run that said it wanted no display server opened an SDL window regardless. The reason
    // given was that SDL fixes a window's graphics API at creation and the Vulkan flag has to be
    // requested up front; that is true and already handled, because
    // `DesktopPlatformOptions.RequestGpuSurface` defaults to true and `PlatformHost.Create` leaves it
    // at the default. The hand-built platform bought nothing and cost the flag.
    //
    // No console provider either: the host adds one for every variant except Release, which is where
    // the thirty lines this sample used to carry now live.
    static int Main(string[] arguments) => VixenApp.Run<TriangleGame>(arguments);
}
