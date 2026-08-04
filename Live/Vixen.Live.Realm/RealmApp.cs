// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.App;

namespace Vixen.Live.Realms;

/// <summary>The one line a realm's <c>Program.cs</c> usually is.</summary>
/// <remarks>
///     <para>
///         <c>return RealmApp.Run&lt;MyRealm&gt;(args);</c> and nothing else — <c>VixenApp.Run</c>'s
///         shape, with a spec read from the command line first.
///     </para>
///     <para>
///         ⚠ <b>Doc 27 writes this as <c>VixenApp.RunRealm&lt;MyRealm&gt;</c>, and it cannot be.</b>
///         <c>VixenApp</c> lives in <c>Tools/Vixen.App</c>, which sits <em>below</em> <c>Live/</c>:
///         adding a member there would need <c>Vixen.App</c> to reference <c>Vixen.Live.Realm</c>,
///         which is the layer rule in the wrong direction, and a static class cannot be extended from
///         outside. So the entry point moved rather than the layering, and it mirrors the original
///         call for call.
///     </para>
///     <para>
///         The sequence is public at every step, for the reason doc 17 gives about the host in
///         general: a realm that wants control reads the spec itself, calls <c>Bind</c>, and builds
///         an <see cref="AppBuilder" /> — which is exactly what <see cref="Run{TRealm}" /> does and
///         all it does.
///     </para>
/// </remarks>
public static class RealmApp {
    /// <summary>What a process that was handed no spec exits with.</summary>
    /// <remarks>
    ///     Two rather than one: one is what <c>VixenApplication.Run</c> returns when a frame threw,
    ///     and a launcher restarting a realm wants to tell "it crashed" from "it was never a realm".
    ///     The second is not worth retrying.
    /// </remarks>
    public const int NotARealmExitCode = 2;

    /// <summary>Reads the spec, builds the application and runs it.</summary>
    /// <typeparam name="TRealm">The game's realm.</typeparam>
    /// <param name="arguments">The process arguments.</param>
    /// <returns>A process exit code.</returns>
    public static int Run<TRealm>(string[]? arguments = null) where TRealm : Realm, new() {
        if (!RealmSpec.TryRead(arguments, environment: null, out var spec, out var error)) {
            // To standard error, and in a sentence: this is what somebody reads at three in the
            // morning when a launcher's arguments are wrong, and it is the whole diagnosis.
            Console.Error.WriteLine($"This process is not a realm — {error}.");

            return NotARealmExitCode;
        }

        var realm = new TRealm();

        realm.Bind(spec!);

        using var application = Create(arguments).Build(realm);

        // Standard input is read on a thread of its own because reading it blocks, and it is a
        // background thread because the process must be able to exit while it is parked in a read
        // that will never return. RealmHost.Signal is the only thread-safe member for this reason.
        var reader = new Thread(() => ReadSignals(realm)) { IsBackground = true, Name = "realm-signals" };

        reader.Start();

        return application.Run();
    }

    /// <summary>Starts configuring a realm, on the backends <c>Vixen.App</c> ships.</summary>
    /// <param name="arguments">The process arguments.</param>
    /// <returns>A builder.</returns>
    /// <remarks>
    ///     The same <c>VixenApp.Create</c> a game uses. A realm's headlessness is a
    ///     <c>Realm.OnConfigure</c> decision rather than a different set of backends, which is what
    ///     makes "the server runs the same frame shape against a device that draws nothing" true
    ///     rather than aspirational.
    /// </remarks>
    public static AppBuilder Create(string[]? arguments = null) => VixenApp.Create(arguments);

    static void ReadSignals(Realm realm) {
        while (Console.In.ReadLine() is { } line) {
            try {
                realm.Host.Signal(line);
            } catch (InvalidOperationException) {
                // The host does not exist yet — the reader started before OnInitialise ran. A signal
                // that early is a launcher draining a realm that has not finished booting, and the
                // right answer is that the drain arrives with the next one rather than that the
                // reader dies.
            }
        }
    }
}
