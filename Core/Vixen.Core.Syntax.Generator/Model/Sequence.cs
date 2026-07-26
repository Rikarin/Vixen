// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

#nullable disable
using System.Xml.Serialization;

namespace Vixen.Core.Syntax.Generator.Model;

public class Sequence : TreeTypeChild {
    // Note: 'Sequence's should not be children of a 'Sequence'.  It's not necessary, and the
    // child choice can just be inlined into the parent.
    [XmlElement(ElementName = "Field", Type = typeof(Field))]
    [XmlElement(ElementName = "Choice", Type = typeof(Choice))]
    public List<TreeTypeChild> Children { get; set; }
}
