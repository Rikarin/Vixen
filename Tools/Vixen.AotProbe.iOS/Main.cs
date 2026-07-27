// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Foundation;
using Vixen.Core;

namespace Vixen.AotProbe.Ios;

/// <summary>The iOS half of the AOT gate. See ../Vixen.AotProbe/README.md.</summary>
public static class Entry {
    /// <summary>
    ///     Touches the iOS platform assembly and the engine. The first is not decoration: without a
    ///     reference to it the managed registrar fails the link with <c>MT0099: No platform
    ///     assembly!</c>, which is a confusing way to be told that a console <c>Main</c> is not an
    ///     iOS application.
    /// </summary>
    public static void Main() {
        using var text = new NSString(ObjectId.Empty.ToString());
        Console.WriteLine(text);
    }
}
