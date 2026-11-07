// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Interchange;

/// <summary>A material as a written file names it: what it is called, and its numbers.</summary>
/// <param name="Name">What it is called, which is what a reader matches it by.</param>
/// <param name="Factors">Its metallic-roughness numbers.</param>
public sealed record ModelWriterMaterial(string Name, ModelMaterialFactors Factors);
