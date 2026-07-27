// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

// The gate's subject is the assemblies, not this file. Every runtime assembly is rooted in the
// project file, so ILC compiles all of them and reports everything it cannot compile ahead of
// time — which is the question the phase asks. This entry point exists because a native binary
// needs one, and it runs so that the gate proves the result executes rather than merely links.

using Vixen.Core;

Console.WriteLine($"Vixen AOT probe: {ObjectId.Empty}");
return 0;
