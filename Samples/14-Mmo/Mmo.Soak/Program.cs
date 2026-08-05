// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace Vixen.Samples.Mmo.Soak;

/// <summary>Doc 27 and doc 28's shared exit criterion, measured rather than asserted.</summary>
/// <remarks>
///     Eight realms over three maps, five hundred connections, thirty minutes, continuous transfers
///     and a rolling upgrade in the middle. It exits non-zero when a budget is missed.
/// </remarks>
public static class Program {
    /// <summary>Runs it.</summary>
    /// <param name="args">Overrides for the defaults. <c>--help</c> lists them.</param>
    /// <returns>Zero if every budget held.</returns>
    public static int Main(string[] args) {
        var settings = SoakSettings.Default;

        for (var index = 0; index + 1 < args.Length; index += 2) {
            settings = args[index] switch {
                "--shards" => settings with { Shards = int.Parse(args[index + 1], CultureInfo.InvariantCulture) },
                "--players" => settings with { Players = int.Parse(args[index + 1], CultureInfo.InvariantCulture) },
                "--ticks" => settings with { Ticks = int.Parse(args[index + 1], CultureInfo.InvariantCulture) },
                "--seed" => settings with { Seed = ulong.Parse(args[index + 1], CultureInfo.InvariantCulture) },
                "--upgrade" => settings with { Upgrade = bool.Parse(args[index + 1]) },
                _ => settings
            };
        }

        if (args.Contains("--help")) {
            Console.WriteLine("usage: Mmo.Soak [--shards N] [--players N] [--ticks N] [--seed N] [--upgrade true|false]");

            return 0;
        }

        return new Soak(settings).Run();
    }
}
