// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Live.Realms;

namespace Vixen.Samples.Mmo.Realms;

/// <summary>One shard of one map.</summary>
public static class Program {
    /// <summary>Runs it.</summary>
    /// <param name="args">
    ///     A placement backend supplies one — <c>--realm-spec shard=…;map=…;port=…</c> — and
    ///     everything the process needs is in it. Handed nothing, it says so on standard error and
    ///     exits 2, which a launcher can tell from a crash and should not retry.
    /// </param>
    /// <returns>The exit code.</returns>
    public static int Main(string[] args) => RealmApp.Run<MmoRealm>(args);
}
