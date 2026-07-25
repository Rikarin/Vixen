#nullable disable
using System.Xml.Serialization;

namespace SyntaxGenerator.Model;

public class Node : TreeType {
    [XmlAttribute]
    public string Root;

    [XmlAttribute]
    public string Errors;

    [XmlElement(ElementName = "Kind", Type = typeof(Kind))]
    public List<Kind> Kinds = [];

    public readonly List<Field> Fields = [];
}
