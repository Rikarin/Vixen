// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Android.App;
using Android.Content.PM;
using Vixen.App;
using Vixen.Core.Diagnostics;
using Vixen.Platform.Android;

namespace Vixen.Samples.HelloTriangle;

/// <summary>The Android head: the same game, and a different owner of the frame loop.</summary>
/// <remarks>
///     <para>
///         <b><c>ConfigurationChanges</c> is the line that matters.</b> Without it Android destroys
///         and recreates the activity on every rotation — a new process-lifetime, a new device, a
///         new swapchain, and a visible stall — because the default assumption is that layouts are
///         rebuilt from resources. A game owns its own drawing and wants the rotation as an event,
///         which is what this asks for: the activity survives and the surface is resized, so the
///         path taken is the same `WindowResized` a desktop window takes when it is dragged.
///     </para>
///     <para>
///         <c>ScreenSize</c> and <c>SmallestScreenSize</c> are in the list as well as
///         <c>Orientation</c>, because on API 13 and later a rotation reports as a size change and
///         declaring only the orientation silently gets the recreate anyway.
///     </para>
/// </remarks>
[Activity(
    Label = "Hello Triangle",
    MainLauncher = true,
    Exported = true,
    ScreenOrientation = ScreenOrientation.FullUser,
    ConfigurationChanges = ConfigChanges.Orientation
        | ConfigChanges.ScreenSize
        | ConfigChanges.SmallestScreenSize
        | ConfigChanges.ScreenLayout
        | ConfigChanges.UiMode
        | ConfigChanges.Density
)]
public sealed class MainActivity : AndroidActivityHost {
    VixenApplication? application;

    /// <inheritdoc />
    /// <remarks>
    ///     <para>
    ///         The same three lines as the iOS head. What differs is invisible from here and entirely
    ///         in <see cref="TriangleGame" />: the surface does not exist yet when this returns, and
    ///         it will go away again every time the activity stops.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>WithLoggerProvider</c> rather than
    ///         <c>WithServices(… LoggerFactory.AddProvider …)</c>, which is what this used to say.</b>
    ///         Service callbacks run last, so the sink was installed after the platform, the mounts,
    ///         the workers, the engine and the whole graphics build had logged — and <c>logcat</c> is
    ///         the only log there is on a phone, so the half a bring-up is about was the half that
    ///         never arrived (#1197). Built from the host's own filter, so
    ///         <c>vixen.log.yaml</c> reaches it like it reaches the console.
    ///     </para>
    /// </remarks>
    protected override Action Start(AndroidPlatform platform) {
        application = VixenApp.Create([])
            .WithPlatform(platform)
            .WithLoggerProvider(levels => new PlatformSink(filter: levels))
            .Build(new TriangleGame());

        return application.RunFrame;
    }
}
