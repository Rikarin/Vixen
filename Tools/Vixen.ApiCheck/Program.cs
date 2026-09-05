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

// Every input is read before anything is written, and the configuration is read with it. Half a
// rewritten set of baselines is worse than none: a refusal that arrives on the fourth assembly
// has already done the damage it names on the first three.
var configurations = new Dictionary<string, string?>(StringComparer.Ordinal);

foreach (var assemblyPath in assemblies) {
    if (!File.Exists(assemblyPath)) {
        Console.Error.WriteLine($"There is no assembly at '{assemblyPath}'. Build before checking.");

        return Usage;
    }

    configurations[assemblyPath] = AssemblyConfiguration.Read(assemblyPath);
}

if (update) {
    // ⚠ The one input this tool could not previously question. A baseline is a promise about a
    // Release package and `CheckApi` builds Release to make it; the tool takes a path, and
    // `bin/Debug` is what a developer — or an agent forbidden to run the gate — has lying around.
    // A `public const bool` whose value is `#if DEBUG` then rewrites itself in a diff of fifty
    // additions, and the gate fails on master. Refusing costs a rebuild; not refusing cost two
    // hand-reverted commits.
    var wrong = assemblies
        .Where(assembly => !AssemblyConfiguration.IsBaseline(configurations[assembly]))
        .ToList();

    if (wrong.Count > 0) {
        Console.Error.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"Refusing to rewrite a baseline from {wrong.Count} of {assemblies.Count} assemblies that "
                + $"were not built in {AssemblyConfiguration.Baseline}:"
            )
        );

        foreach (var assembly in wrong) {
            Console.Error.WriteLine($"  {assembly} — {AssemblyConfiguration.Describe(configurations[assembly])}");
        }

        Console.Error.WriteLine(
            "The baseline records the Release surface, because that is what Pack ships, and the two "
            + "configurations differ: a `public const bool` feature flag guarded by `#if DEBUG` has a "
            + "different *value*, and a const's value is part of the surface. Build Release and pass "
            + "bin/Release, or run `./build.sh CheckApi --update-api`, which does."
        );

        return Usage;
    }
}

var differing = 0;

foreach (var assemblyPath in assemblies) {
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

        // The configuration is in the line because the trap it guards is invisible in the diff:
        // one const's value among fifty additions reads as noise. A log that says which build every
        // rewritten baseline came from is the cheap half of the same answer.
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"{name}: {surface.Count} entries, {rebased.Count} unshipped, "
                + $"read from {AssemblyConfiguration.Describe(configurations[assemblyPath])}."
            )
        );

        continue;
    }

    if (!AssemblyConfiguration.IsBaseline(configurations[assemblyPath])) {
        // Not a failure — checking a Debug build is a reasonable thing to do while working, and the
        // gate never does it. But a difference it produces may be the configuration rather than the
        // API, and a reader who does not know that spends an hour on a `= true -> bool`.
        Console.Error.WriteLine(
            $"{name}: read from {AssemblyConfiguration.Describe(configurations[assemblyPath])}; the baseline "
            + $"records {AssemblyConfiguration.Baseline}. A difference below may be the configuration."
        );
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
                         that is gone becomes a *REMOVED* line. Refused unless every
                         assembly was built in Release: a baseline is a promise about a
                         packed assembly, and a `const` guarded by `#if DEBUG` has a
                         different value — which is part of the surface.
          --fold, -f     The release: fold Unshipped into Shipped and empty it. Run by
                         `nuke Release` at the tag, never as part of a check.
          --help, -h     This text.

        Exit codes: 0 the surfaces match, 1 they do not, 2 the arguments or the inputs are wrong.
        """
    );
}
