using System.Runtime.CompilerServices;

namespace Vixen.Raven;

public static class ExceptionUtilities {
    internal static Exception Unreachable([CallerFilePath] string? path = null, [CallerLineNumber] int line = 0)
        => new InvalidOperationException($"This program location is thought to be unreachable. File='{path}' Line={line}");
}
