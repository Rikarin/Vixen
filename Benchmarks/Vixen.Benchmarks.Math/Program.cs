// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using BenchmarkDotNet.Running;

// Run everything, or a subset:  dotnet run -c Release -- --filter *Matrix*
// The Nuke `Benchmark` target runs this and diffs the result against a committed baseline.
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);

/// <summary>The benchmark host. A type only so the assembly reference above has something to name.</summary>
public partial class Program;
