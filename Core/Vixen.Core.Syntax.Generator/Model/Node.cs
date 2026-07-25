#nullable disable
using System.Xml.Serialization;

namespace Vixen.Core.Syntax.Generator.Model;

public class Node : TreeType {
    [XmlAttribute] public string Root { get; set; }

    [XmlAttribute] public string Errors { get; set; }

    [XmlElement(ElementName = "Kind", Type = typeof(Kind))] public List<Kind> Kinds { get; set; } = [];

    public List<Field> Fields { get; } = [];
}
