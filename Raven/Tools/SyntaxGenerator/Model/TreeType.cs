#nullable disable
using System.Xml.Serialization;

namespace SyntaxGenerator.Model;

public class TreeType {
    [XmlAttribute]
    public string Name;

    [XmlAttribute]
    public string Base;

    [XmlAttribute]
    public string SkipConvenienceFactories;

    [XmlElement]
    public Comment TypeComment;

    [XmlElement]
    public Comment FactoryComment;

    [XmlElement(ElementName = "Field", Type = typeof(Field))]
    [XmlElement(ElementName = "Choice", Type = typeof(Choice))]
    [XmlElement(ElementName = "Sequence", Type = typeof(Sequence))]
    public List<TreeTypeChild> Children = [];
}
