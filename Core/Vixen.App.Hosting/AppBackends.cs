// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;
using Vixen.Graphics;
using Vixen.Platform;

namespace Vixen.App;

/// <summary>Where an application's platform comes from.</summary>
/// <remarks>
///     <para>
///         <b>The seam that lets the host live below the backends it boots on.</b> Choosing between
///         the desktop platform and the headless one means referencing both, and both are in
///         <c>Platform/</c> — which <c>CheckArchitecture</c> forbids a <c>Core/</c> assembly from
///         doing, because a reference upward makes the lower layer unusable without the higher one.
///         So the choice is asked for rather than made, and <c>PlatformHost</c> in the
///         <c>Vixen.App</c> package is the one implementation that ships.
///     </para>
///     <para>
///         ⚠ <b>One method, and no registry.</b> There are two platforms that can run in a desktop
///         process and the choice between them is one question. Android, iOS and Web are separate
///         app heads with separate entry points — a phone cannot fall back to the desktop platform
///         — so there is nothing for a registry to select between there either.
///     </para>
/// </remarks>
public interface IPlatformFactory {
    /// <summary>Builds the platform an application asked for.</summary>
    /// <param name="config">What the application was configured as.</param>
    /// <returns>The platform, started. The caller takes ownership.</returns>
    IPlatform Create(AppConfig config);
}

/// <summary>Where an application's graphics device comes from.</summary>
/// <remarks>
///     <para>
///         <see cref="IPlatformFactory" />'s counterpart, and there for the same reason: the two
///         backends an app head can boot on its own — Vulkan where there is a surface to present to,
///         Null where there is not — are both in <c>Platform/</c>.
///     </para>
///     <para>
///         ⚠ <b>Only device creation is behind this.</b> Building the swapchain is not: a swapchain
///         comes from <see cref="IGraphicsDevice.CreateSwapChain" />, which every backend already
///         implements, so routing it through here would have been an indirection with one possible
///         answer. See <see cref="AppGraphics.SwapChainFor" />, which is where it happens instead.
///     </para>
/// </remarks>
public interface IGraphicsBackend {
    /// <summary>Builds the device an application will draw with.</summary>
    /// <param name="options">
    ///     What the application asked for, including
    ///     <see cref="GraphicsOptions.Backends" /> — the ordered list of APIs to try. An empty list
    ///     means the implementation picks its own order.
    /// </param>
    /// <param name="window">The window it will present to, or <see langword="null" /> for none.</param>
    /// <param name="logs">Where the backend logs.</param>
    /// <param name="reason">
    ///     Why the device cannot present, when it cannot — no window, no surface, or what each
    ///     rejected candidate said. <see langword="null" /> when a presenting device was created.
    /// </param>
    /// <returns>
    ///     The device, or <see langword="null" /> when nothing in the preference list would open.
    /// </returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A device that draws nothing is not a failure</b> —
    ///         <a href="../../docs/plan/17-app-heads-and-shipping.md">doc 17</a> runs the dedicated
    ///         server on exactly that. An implementation asked for
    ///         <see cref="GraphicsBackend.Null" /> reports <paramref name="reason" /> and returns
    ///         something usable rather than throwing.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Null <i>is</i> a failure, and it is why the return type changed.</b> Once a
    ///         head can say "Vulkan only", "nothing in the list opened" became a real answer that
    ///         has to be distinguishable from "here is a device that will never draw". Returning
    ///         the Null device for both would silently grant the fall-through the operator
    ///         deliberately did not ask for.
    ///     </para>
    /// </remarks>
    IGraphicsDevice? Create(GraphicsOptions options, IWindow? window, ILoggerFactory? logs, out string? reason);
}
