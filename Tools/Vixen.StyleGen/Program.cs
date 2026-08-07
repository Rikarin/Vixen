// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.StyleGen;

// The whole of the entry point. Everything worth testing is in `Arguments` and `StyleGenRunner`,
// which is deliberate: a build step whose behaviour lives in `Main` is one whose tests have to spawn
// a process to find out what it did.
var request = Arguments.Parse(args, out var problem);

if (request is null) {
    // ⚠ MSBuild's canonical diagnostic shape — `subcategory code: text` on standard error — so that
    // this lands in an IDE's error list and in a CI log's summary rather than scrolling past as
    // prose from a subprocess. The same reason `Vixen.Sdk.targets` asks the CLI for `--format
    // msbuild`.
    Console.Error.WriteLine($"Vixen.StyleGen : error VXSTYLE001: {problem}");
    return 2;
}

var result = StyleGenRunner.Run(request);

foreach (var error in result.Errors) {
    Console.Error.WriteLine($"Vixen.StyleGen : error VXSTYLE002: {error}");
}

if (result.Errors.Count > 0) {
    // ⚠ Nothing is written on failure, and it matters which way round that is. A half-written
    // stylesheet left in obj/ is one a later incremental build considers up to date — so the next
    // build succeeds against the broken output and the error appears once, in a log nobody kept.
    return 1;
}

StyleGenRunner.Write(request, result);

Console.WriteLine(
    $"Vixen.StyleGen: {result.RuleCount} utility rules from {request.Scan.Count} scanned files"
    + $" ({result.Unrecognised.Count} candidates were not utilities)."
);

return 0;
