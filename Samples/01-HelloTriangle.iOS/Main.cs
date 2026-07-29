// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Foundation;
using UIKit;
using Vixen.App;
using Vixen.Core.Diagnostics;
using Vixen.Platform.Ios;

namespace Vixen.Samples.HelloTriangle;

/// <summary>The iOS head: the same game, and a different owner of the frame loop.</summary>
/// <remarks>
///     Everything specific to this platform is in the two classes below, and between them they are
///     shorter than the desktop <c>Program.cs</c>. That is the claim doc 17 makes about the app-head
///     model, tested here for the first time on a platform that cannot run a loop.
/// </remarks>
public static class Entry {
    /// <summary>Hands the process to UIKit, which never gives it back.</summary>
    public static void Main(string[] arguments) => UIApplication.Main(arguments, null, typeof(TriangleHost));
}

/// <summary>Builds the application and says what to run each frame.</summary>
/// <remarks>
///     Registered as <c>AppDelegate</c> for Objective-C, which is the name UIKit's tooling and every
///     iOS crash report expect, while the C# type is named for what it is. The two need not match:
///     <c>UIApplication.Main</c> is given the type, and the attribute is what the runtime registers.
/// </remarks>
[Register("AppDelegate")]
public sealed class TriangleHost : IosApplicationHost {
    VixenApplication? application;

    /// <inheritdoc />
    /// <remarks>
    ///     <para>
    ///         No <c>--vixen-*</c> arguments: an iOS application is not launched from a shell, so the
    ///         host is given an empty set and everything it would have read from the command line
    ///         comes from <c>OnConfigure</c> instead.
    ///     </para>
    ///     <para>
    ///         The window is created inside <c>Build</c>, by the host, from the
    ///         <c>AppConfig.Window</c> the game asked for — the same path the desktop takes. What
    ///         differs is that the <c>CAMetalLayer</c> behind it has no drawable until UIKit has laid
    ///         the view out, which is why <see cref="TriangleGame" /> waits for one instead of
    ///         building a device here.
    ///     </para>
    /// </remarks>
    protected override Action Start(IosPlatform platform) {
        application = VixenApp.Create([])
            .WithPlatform(platform)
            .WithServices(services => services.LoggerFactory.AddProvider(new PlatformSink()))
            .Build(new TriangleGame());

        return application.RunFrame;
    }
}
