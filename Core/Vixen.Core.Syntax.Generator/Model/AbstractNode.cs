// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

#nullable disable
namespace Vixen.Core.Syntax.Generator.Model;

public class AbstractNode : TreeType {
    public List<Field> Fields { get; } = [];
}
