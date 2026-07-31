// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.ApiCheck;

// The subject of `nuke CheckApi`. Given the assemblies, it reads the public surface of each and
// compares it with the baseline committed beside the project that produced it. Nothing here knows
// which assemblies matter — that is the build's decision, and duplicating it in two places is how
// the two stop agreeing.

const int Success = 0;
const int Differences = 1;
const int Usage = 2;

// How many differing entries are printed per assembly before the rest are counted instead. The
// first run against a repository with no baselines produces tens of thousands, and a console that
// scrolls for a minute is a console nobody reads the top of.
const int MaxReported = 20;

var update = false;
var fold = false;
var assemblies = new List<string>();

foreach (var argument in args) {
    switch (argument) {
        case "--update" or "-u":
            update = true;
            break;

        case "--fold" or "-f":
            fold = true;
            break;

        case "--help" or "-h":
            PrintUsage();
            return Success;

        default:
            if (argument.StartsWith('-')) {
                Console.Error.WriteLine($"Unrecognised argument '{argument}'.");
                PrintUsage();

                return Usage;
            }

            assemblies.Add(argument);
            break;
    }
}

if (assemblies.Count == 0) {
    Console.Error.WriteLine("No assemblies given.");
    PrintUsage();

    return Usage;
}

var differing = 0;

foreach (var assemblyPath in assemblies) {
    if (!File.Exists(assemblyPath)) {
        Console.Error.WriteLine($"There is no assembly at '{assemblyPath}'. Build before checking.");

        return Usage;
    }

    var name = Path.GetFileNameWithoutExtension(assemblyPath);
    var directory = ApiBaseline.DirectoryFor(assemblyPath);
    var shippedPath = Path.Combine(directory, ApiBaseline.ShippedFileName);
    var unshippedPath = Path.Combine(directory, ApiBaseline.UnshippedFileName);

    var surface = ApiSurfaceReader.Read(assemblyPath);
    var shipped = ApiBaseline.Read(shippedPath);

    if (fold) {
        // The release ritual: what was approved becomes what was shipped, and the approval file
        // starts again empty. `Approved` is the same method the check uses to decide what is
        // allowed, so the fold cannot promise something the gate would not have accepted.
        var unshipped = ApiBaseline.Read(unshippedPath);
        var approved = ApiBaseline.Approved(shipped, unshipped);

        ApiBaseline.Write(shippedPath, [.. approved]);
        ApiBaseline.Write(unshippedPath, []);

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{name}: folded {unshipped.Count} entries in, {approved.Count} shipped."));

        continue;
    }

    if (update) {
        // Written even when it would be empty, and never rewritten from the surface. An absent
        // Shipped file and an empty one say different things — "this project is not covered" and
        // "this project has published nothing yet" — and only the second is true here.
        if (!File.Exists(shippedPath)) {
            ApiBaseline.Write(shippedPath, []);
        }

        var rebased = ApiBaseline.Rebase(surface, shipped);
        ApiBaseline.Write(unshippedPath, rebased);

        Console.WriteLine(
            string.Create(CultureInfo.InvariantCulture, $"{name}: {surface.Count} entries, {rebased.Count} unshipped.")
        );

        continue;
    }

    if (!File.Exists(shippedPath) && !File.Exists(unshippedPath)) {
        Console.Error.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"{name}: no API baseline. {surface.Count} public entries are approved by nothing."
            )
        );

        differing++;

        continue;
    }

    var difference = ApiBaseline.Compare(surface, shipped, ApiBaseline.Read(unshippedPath));

    if (difference.IsEmpty) {
        continue;
    }

    differing++;

    Console.Error.WriteLine(
        string.Create(
            CultureInfo.InvariantCulture,
            $"{name}: {difference.Added.Count} unapproved addition(s), {difference.Removed.Count} removal(s)."
        )
    );

    Report('+', difference.Added);
    Report('-', difference.Removed);
}

if (fold) {
    Console.WriteLine(
        string.Create(CultureInfo.InvariantCulture, $"Folded the API baselines of {assemblies.Count} assemblies.")
    );

    Console.WriteLine("Everything approved is now shipped. From here a removal is a breaking change.");

    return Success;
}

if (update) {
    Console.WriteLine(
        string.Create(CultureInfo.InvariantCulture, $"Rewrote the API baselines of {assemblies.Count} assemblies.")
    );

    Console.WriteLine("Read the diff before committing: an approval nobody looked at approves whatever was there.");

    return Success;
}

if (differing > 0) {
    Console.Error.WriteLine(
        string.Create(
            CultureInfo.InvariantCulture,
            $"{differing} of {assemblies.Count} assemblies differ from their committed public API."
        )
    );

    Console.Error.WriteLine(
        "An addition needs a line in PublicAPI.Unshipped.txt and a reason; a removal needs to be a "
        + "decision. Regenerate both with `./build.sh CheckApi --update-api`, then review the diff."
    );

    return Differences;
}

Console.WriteLine(
    string.Create(
        CultureInfo.InvariantCulture,
        $"Checked {assemblies.Count} assemblies; every public surface matches its baseline."
    )
);

return Success;

void Report(char sign, IReadOnlyList<string> entries) {
    foreach (var entry in entries.Take(MaxReported)) {
        Console.Error.WriteLine($"  {sign} {entry}");
    }

    if (entries.Count > MaxReported) {
        Console.Error.WriteLine(
            string.Create(CultureInfo.InvariantCulture, $"  {sign} … and {entries.Count - MaxReported} more.")
        );
    }
}

void PrintUsage() {
    Console.WriteLine(
        """
        vixen-api-check [--update | --fold] <assembly> [<assembly> …]

          Compares the public surface of each assembly with PublicAPI.Shipped.txt and
          PublicAPI.Unshipped.txt beside the project that produced it.

          --update, -u   Rewrite PublicAPI.Unshipped.txt from what the assemblies contain
                         instead of failing. Shipped API is never rewritten; a shipped entry
                         that is gone becomes a *REMOVED* line.
          --fold, -f     The release: fold Unshipped into Shipped and empty it. Run by
                         `nuke Release` at the tag, never as part of a check.
          --help, -h     This text.

        Exit codes: 0 the surfaces match, 1 they do not, 2 the arguments or the inputs are wrong.
        """
    );
}
