using Vixen.Raven.Cli;

var parseResult = RavenCommand.Create().Parse(args);

if (parseResult.Errors.Count > 0) {
    // Let the default action print the errors and the usage, but report a bad
    // command line as its own kind of failure rather than as a bad shader.
    parseResult.Invoke();
    return (int)ExitCode.UsageError;
}

return parseResult.Invoke();
