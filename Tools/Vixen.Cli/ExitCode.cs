// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Cli;

/// <summary>
///     What the process returns. Separating a bad command line from bad content lets a build script
///     tell "I invoked you wrong" from "the project is wrong", which is the difference between a
///     script to fix and an asset to fix.
/// </summary>
public enum ExitCode {
    /// <summary>It did what was asked.</summary>
    Success = 0,

    /// <summary>The project was read and something in it is wrong.</summary>
    Failed = 1,

    /// <summary>The command line was wrong, or there is no project where one was expected.</summary>
    UsageError = 2
}
