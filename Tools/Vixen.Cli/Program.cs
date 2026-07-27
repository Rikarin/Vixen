// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Cli;

var parseResult = VixenCommand.Create().Parse(args);

if (parseResult.Errors.Count > 0) {
    // Let the default action print the errors and the usage, but report a bad command line as its
    // own kind of failure rather than as a bad project.
    parseResult.Invoke();
    return (int)ExitCode.UsageError;
}

return await parseResult.InvokeAsync();
