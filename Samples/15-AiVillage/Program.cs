// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.App;

namespace Vixen.Samples.AiVillage;

/// <summary>The way in to the village.</summary>
/// <remarks>
///     <para>
///         Everything worth looking at here is drawn by <c>AiOverlaySystem</c> into the frame's
///         <c>DebugDraw</c>, so the sample needs a device and a surface like any other — and
///         <c>--vixen-frames N --vixen-capture &lt;dir&gt;</c> writes the last one to a PNG.
///     </para>
///     <para>
///         ⚠ <b>The decision log is the evidence, and it does not need a picture.</b> A run with
///         <c>--vixen-headless</c> and no capture path falls through to a device that draws nothing,
///         and every line of <c>SampleLog.Decided</c> is still produced — because what the sample is
///         asserting happened in <c>SystemPhase.Update</c>, several phases before anything drew.
///     </para>
///     <para>
///         ⚠ <b>This file used to defeat the flag that <see cref="AiVillageGame.OnConfigure" />
///         honours.</b> It built a <c>DesktopPlatform</c> and handed it to <c>WithPlatform</c>, which
///         <see cref="AppBuilder.Build" /> takes ahead of the factory — so <c>--vixen-headless</c>
///         was parsed, stored, and then never asked, and the SDL window opened on a run that had said
///         it wanted no display server. The stated reason was that SDL fixes a window's graphics API
///         at creation and the Vulkan flag has to be requested up front, which is true and already
///         covered: <c>DesktopPlatformOptions.RequestGpuSurface</c> defaults to
///         <see langword="true" />, and <see cref="PlatformHost.Create" /> leaves it there. Letting
///         the factory choose is what makes <c>AiVillageGame</c>'s <c>IsVisible = !config.Headless</c>
///         mean anything.
///     </para>
/// </remarks>
static class Program {
    static int Main(string[] arguments) => VixenApp.Run<AiVillageGame>(arguments);
}
