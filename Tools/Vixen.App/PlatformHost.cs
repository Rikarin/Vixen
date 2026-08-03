// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Platform;
using Vixen.Platform.Desktop;
using Vixen.Platform.Headless;

namespace Vixen.App;

/// <summary>Choosing a platform, and saying which one was chosen and why.</summary>
/// <remarks>
///     A function rather than a registry: there are two implementations that can run in this
///     process, the choice between them is one question, and a plugin model for something with two
///     answers would be machinery for its own sake. When Android, iOS and Web arrive they are
///     separate app heads with separate entry points — a phone cannot fall back to the desktop
///     platform, so there is nothing for a registry to select between there either.
/// </remarks>
public static class PlatformHost {
    /// <summary>This class as an <see cref="IPlatformFactory" />, which is what a builder takes.</summary>
    /// <remarks>
    ///     The static methods stay because they read better at a call site that is choosing
    ///     deliberately — <c>WithPlatform(PlatformHost.Create(config))</c> is a sentence. The
    ///     interface exists because <see cref="AppBuilder" /> is in <c>Core/</c> and cannot name
    ///     this class at all.
    /// </remarks>
    public static IPlatformFactory Instance { get; } = new Factory();

    /// <summary>Builds the platform an application asked for.</summary>
    /// <param name="config">What the application was configured as.</param>
    /// <returns>The platform, started.</returns>
    /// <remarks>
    ///     <para>
    ///         A configuration that asks for headless gets headless. Anything else tries the desktop
    ///         and <b>falls back</b> to headless if SDL is missing or there is no display server,
    ///         logging what happened — because the alternative is a build that runs fine on a
    ///         developer's machine and dies at startup in a container with a message about a
    ///         dynamic library.
    ///     </para>
    ///     <para>
    ///         The fallback is deliberately not silent and deliberately not the other way round: a
    ///         head that wanted a window and did not get one has to say so, or an operator who
    ///         mistyped <c>--vixen-headless</c> spends an afternoon wondering where the window went.
    ///     </para>
    /// </remarks>
    public static IPlatform Create(AppConfig config) {
        ArgumentNullException.ThrowIfNull(config);

        if (config.Headless) {
            return CreateHeadless(config);
        }

        try {
            return new DesktopPlatform(
                new() {
                    Organisation = config.Organisation,
                    Application = config.Name,
                    VideoDriver = config.VideoDriver
                }
            );
        } catch (PlatformNotSupportedException exception) {
            config.HeadlessFallbackReason = exception.Message;
            return CreateHeadless(config);
        }
    }

    static IPlatform CreateHeadless(AppConfig config) =>
        new HeadlessPlatform(new() { Organisation = config.Organisation, Application = config.Name });

    sealed class Factory : IPlatformFactory {
        public IPlatform Create(AppConfig config) => PlatformHost.Create(config);
    }
}
