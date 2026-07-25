// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

#nullable disable
using System.Xml.Serialization;

namespace Vixen.Core.Syntax.Generator.Model;

public class Choice : TreeTypeChild {
    // Note: 'Choice's should not be children of a 'Choice'.  It's not necessary, and the child
    // choice can just be inlined into the parent.
    [XmlElement(ElementName = "Field", Type = typeof(Field))]
    [XmlElement(ElementName = "Sequence", Type = typeof(Sequence))]
    public List<TreeTypeChild> Children { get; set; }

    [XmlAttribute] public bool Optional { get; set; }
}
