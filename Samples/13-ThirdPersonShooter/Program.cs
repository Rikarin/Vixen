// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.App;
using Vixen.Samples.ThirdPersonShooter;

// The committed heightfield's generator, run from the sample's own directory. The binary is
// committed because a .vxterrain is what the content build imports, and asking every checkout to run
// a generator before its first build is a step somebody skips — TerrainSeed says the rest.
if (args is ["--regenerate-terrain", ..]) {
    Console.WriteLine($"Wrote {TerrainSeed.Write(Environment.CurrentDirectory)}.");

    return 0;
}

// Everything VixenApp.Run does is a public call you can inline and edit. See
// docs/plan/17: nothing in the boot path is inaccessible.
return VixenApp.Run<ThirdPersonShooterGame>(args);
