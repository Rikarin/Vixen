// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

#nullable disable
using System.Xml.Serialization;

namespace Vixen.Core.Syntax.Generator.Model;

public class TreeType {
    [XmlAttribute] public string Name { get; set; }

    [XmlAttribute] public string Base { get; set; }

    [XmlAttribute] public string SkipConvenienceFactories { get; set; }

    [XmlElement] public Comment TypeComment { get; set; }

    [XmlElement] public Comment FactoryComment { get; set; }

    [XmlElement(ElementName = "Field", Type = typeof(Field))]
    [XmlElement(ElementName = "Choice", Type = typeof(Choice))]
    [XmlElement(ElementName = "Sequence", Type = typeof(Sequence))]
    public List<TreeTypeChild> Children { get; set; } = [];
}
