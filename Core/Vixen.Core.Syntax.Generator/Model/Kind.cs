// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Xml.Serialization;

namespace Vixen.Core.Syntax.Generator.Model;

public class Kind : IEquatable<Kind> {
    [XmlAttribute] public string? Name { get; set; }

    public override bool Equals(object? obj) => Equals(obj as Kind);

    public bool Equals(Kind? other) => Name == other?.Name;

    public override int GetHashCode() => Name == null ? 0 : Name.GetHashCode();
}
