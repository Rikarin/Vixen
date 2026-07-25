// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Xml;
using System.Xml.Serialization;

namespace Vixen.Core.Syntax.Generator.Model;

#nullable disable

public class Comment {
    [XmlAnyElement] public XmlElement[] Body { get; set; }
}
