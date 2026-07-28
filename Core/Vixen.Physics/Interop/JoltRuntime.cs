// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using JoltPhysicsSharp;

namespace Vixen.Physics.Interop;

/// <summary>
///     Owns the one-per-process half of Jolt: the allocator, the factory and the registered shape
///     types, which <c>JPH_Init</c> sets up and <c>JPH_Shutdown</c> tears down.
/// </summary>
/// <remarks>
///     <para>
///         Jolt's initialisation is global, not per <see cref="PhysicsWorld" />, and calling it twice
///         leaks the first factory while calling shutdown while a world is alive frees shape types
///         out from under it. Both are native crashes with no managed stack, so this counts instead:
///         the first world in takes the runtime up, the last one out takes it down, and a test that
///         builds and disposes forty worlds in a row does not care which order they happened in.
///     </para>
///     <para>
///         <b>Trace and assert are routed once, at the first init.</b> Jolt writes to stdout by
///         default, which in a shipped game is nowhere and in a test run is interleaved with
///         everything else. <see cref="Trace" /> and <see cref="AssertFailed" /> let the host point
///         them at a logger; both are read on native threads, so they are set before the first world
///         exists and never changed after.
///     </para>
/// </remarks>
public static class JoltRuntime {
    static readonly Lock Gate = new();

    static int references;
    static Action<string>? trace;
    static Func<string, string, string, uint, bool>? assertFailed;

    /// <summary>How many live objects are holding the runtime up.</summary>
    /// <remarks>For a test that wants to assert the count fell back to zero.</remarks>
    public static int ReferenceCount {
        get {
            lock (Gate) {
                return references;
            }
        }
    }

    /// <summary>
    ///     Where Jolt's trace output goes. Set it before the first <see cref="PhysicsWorld" /> exists.
    /// </summary>
    /// <remarks>
    ///     Called on whichever thread Jolt happened to be on, including its job threads, so the
    ///     handler has to be safe to call concurrently. A logger is; a <c>List&lt;string&gt;</c> is not.
    /// </remarks>
    public static Action<string>? Trace {
        get {
            lock (Gate) {
                return trace;
            }
        }
        set {
            lock (Gate) {
                trace = value;
            }
        }
    }

    /// <summary>
    ///     What to do when a Jolt assertion fails: the expression, the message, the file and the line.
    ///     Returning <see langword="true" /> breaks into the debugger.
    /// </summary>
    /// <remarks>
    ///     Only present in a Jolt built with assertions on. The shipped native package is not, so
    ///     this fires in exactly the situation where a native crash was going to happen anyway — its
    ///     value is turning that into a message that names the file.
    /// </remarks>
    public static Func<string, string, string, uint, bool>? AssertFailed {
        get {
            lock (Gate) {
                return assertFailed;
            }
        }
        set {
            lock (Gate) {
                assertFailed = value;
            }
        }
    }

    /// <summary>Takes the runtime up if it is not already, and adds a reference to it.</summary>
    /// <exception cref="PhysicsInitializationException">Jolt refused to initialise.</exception>
    /// <remarks>Every call must be matched by a <see cref="Release" />.</remarks>
    public static void Acquire() {
        lock (Gate) {
            if (references == 0) {
                if (!Foundation.Init(doublePrecision: false)) {
                    throw new PhysicsInitializationException(
                        "Jolt refused to initialise. The usual cause is that the native library could "
                        + "not be loaded — see Vixen.Physics/README.md § Platforms for which runtime "
                        + "identifiers JoltPhysics.Native ships."
                    );
                }

                Foundation.SetTraceHandler(OnTrace);
                Foundation.SetAssertFailureHandler(OnAssertFailed);
            }

            references++;
        }
    }

    /// <summary>Drops a reference, and takes the runtime down when the last one goes.</summary>
    public static void Release() {
        lock (Gate) {
            if (references == 0) {
                return;
            }

            references--;

            if (references == 0) {
                Foundation.Shutdown();
            }
        }
    }

    static void OnTrace(string message) => Trace?.Invoke(message);

    static bool OnAssertFailed(string expression, string message, string file, uint line) =>
        AssertFailed?.Invoke(expression, message, file, line) ?? false;
}
