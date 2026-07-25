
namespace Vixen.Raven.Cli;

/// <summary>
///     What the process returns. Separating a bad command line from a bad shader
///     lets a build script tell "I invoked you wrong" from "the shader is wrong".
/// </summary>
public enum ExitCode {
    /// <summary>Everything compiled and was written.</summary>
    Success = 0,

    /// <summary>The input was read but produced errors.</summary>
    CompilationFailed = 1,

    /// <summary>The command line was wrong, or an input/output path was unusable.</summary>
    UsageError = 2
}
