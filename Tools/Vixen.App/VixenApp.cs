// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.App;

/// <summary>The one line an application's <c>Program.cs</c> usually is.</summary>
/// <remarks>
///     <para>
///         <c>return VixenApp.Run&lt;MyGame&gt;(args);</c> and nothing else. The sequence it runs
///         through is <see cref="Create" /> → <see cref="AppBuilder.Build" /> →
///         <see cref="VixenApplication.Run" />, all of which are public, so an application that
///         wants control inlines the three calls and edits the middle one rather than fighting a
///         host that will not let go.
///     </para>
///     <para>
///         That property is deliberate and worth protecting: <c>docs/plan/17</c> chose the
///         "the app is the executable" model precisely so that nothing in the boot path is a black
///         box, and a convenience method that hid steps would give the property away for the sake of
///         one line.
///     </para>
///     <para>
///         ⚠ <b>This is the only place the default backends are installed.</b> Everything else in
///         the host is in <c>Vixen.App.Hosting</c>, under <c>Core/</c>, which may not reference
///         <c>Platform/</c> — so an <see cref="AppBuilder" /> constructed directly has neither a
///         platform nor a device and says so. That is what makes this class three lines long and
///         what makes those three lines load-bearing.
///     </para>
/// </remarks>
public static class VixenApp {
    /// <summary>Builds and runs an application.</summary>
    /// <typeparam name="TGame">The application's <see cref="Game" />.</typeparam>
    /// <param name="arguments">The process arguments.</param>
    /// <returns>A process exit code.</returns>
    public static int Run<TGame>(string[]? arguments = null) where TGame : Game, new() {
        using var application = Create(arguments).Build(new TGame());
        return application.Run();
    }

    /// <summary>Starts configuring an application, on the backends this package ships.</summary>
    /// <param name="arguments">The process arguments.</param>
    /// <returns>A builder, with <see cref="PlatformHost" /> and <see cref="GraphicsHost" /> installed.</returns>
    public static AppBuilder Create(string[]? arguments = null) =>
        new AppBuilder(AppArguments.Parse(arguments))
            .WithPlatformFactory(PlatformHost.Instance)
            .WithGraphicsBackend(GraphicsHost.Instance);
}
