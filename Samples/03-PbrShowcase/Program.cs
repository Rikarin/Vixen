// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.App;
using Vixen.Samples.PbrShowcase;

// The terrain is generated content with its binary committed — TerrainSeed says why — and this is
// the regeneration step: run from the sample's directory after changing the seed's numbers.
if (args is ["--regenerate-terrain", ..]) {
    Console.WriteLine($"Wrote {TerrainSeed.Write(Environment.CurrentDirectory)}.");

    return 0;
}

// Everything VixenApp.Run does is a public call you can inline and edit. See
// docs/plan/17: nothing in the boot path is inaccessible.
return VixenApp.Run<PbrShowcaseGame>(args);
