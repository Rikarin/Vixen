// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Raven.Cli;
using Vixen.Raven.Transpile;

// ⚠ Before the command is built, not inside the handler: `--target` is validated by
// `AcceptOnlyFromAmong(TargetBackends.Names)`, which is read at construction. Registering later
// leaves `essl` working and `--target essl` refused as an unknown value — a difference that shows
// up only in the parse error.
EsslBackend.Register();

var parseResult = RavenCommand.Create().Parse(args);

if (parseResult.Errors.Count > 0) {
    // Let the default action print the errors and the usage, but report a bad
    // command line as its own kind of failure rather than as a bad shader.
    parseResult.Invoke();
    return (int)ExitCode.UsageError;
}

return parseResult.Invoke();
