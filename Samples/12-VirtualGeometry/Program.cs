// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.App;

namespace Vixen.Samples.VirtualGeometry;

/// <summary>The way in to the virtualized-geometry frame.</summary>
/// <remarks>
///     <para>
///         Desktop by default, like <c>Samples/03</c> and for the same reason: this sample exists to
///         show a thing on a screen — the traversal's cut, recolouring itself as the camera moves —
///         and the multi-platform proof lives in <c>Samples/01</c>.
///     </para>
///     <para>
///         ⚠ <b>The platform is <see cref="PlatformHost" />'s to choose, and this file used to take
///         that choice away.</b> It built a <c>DesktopPlatform</c> by hand and handed it to
///         <c>WithPlatform</c>, which <see cref="AppBuilder.Build" /> honours ahead of the factory —
///         so <c>--vixen-headless</c> was read, stored in <c>AppConfig.Headless</c>, and then never
///         consulted, and a run that asked for no display server opened an SDL window anyway. The
///         reason given for building it here — that SDL fixes a window's graphics API at creation and
///         the Vulkan flag has to be asked for up front — is real but already handled:
///         <c>DesktopPlatformOptions.RequestGpuSurface</c> defaults to <see langword="true" />, and
///         <see cref="PlatformHost.Create" /> leaves it at that default. Nothing was bought by the
///         hand-built platform except the loss of the flag.
///     </para>
///     <para>
///         <c>--vixen-frames N</c> still applies, so CI can prove the whole stack — window, device,
///         document, traversal, indirect draw, present — starts, runs and stops without a validation
///         error or a hang.
///     </para>
///     <para>
///         ⚠ <b><c>--vixen-capture</c> writes nothing here, and that is this sample's shape rather
///         than a bug in the flag.</b> <see cref="VirtualGeometryGame.OnConfigure" /> sets
///         <c>Graphics.Enabled = false</c> and owns its own device, swapchain and present, so there
///         is no <c>AppGraphics</c> to read a frame back out of. Under <c>--vixen-headless</c> the
///         window cannot present and the sample says so and draws nothing — see
///         <c>SampleLog.NoWindow</c>. Samples 03 and 13 are the ones whose picture is a file.
///     </para>
/// </remarks>
static class Program {
    static int Main(string[] arguments) => VixenApp.Run<VirtualGeometryGame>(arguments);
}
