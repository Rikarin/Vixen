// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Shaders.Generators;

namespace Tests;

/// <summary>Raven's reflection for <c>Fixtures/Lighting.rvn</c>, read once.</summary>
static class Fixture {
    static readonly Lazy<ShaderReflection> Parsed = new(
        () => ReflectionReader.Read(File.ReadAllText(Path.Combine("Fixtures", "Lighting.reflect.json")))
    );

    public static ShaderReflection Reflection => Parsed.Value;
}
