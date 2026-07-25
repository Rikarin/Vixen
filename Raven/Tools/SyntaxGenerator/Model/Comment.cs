using System.Xml;
using System.Xml.Serialization;

namespace SyntaxGenerator.Model;

#nullable disable

public class Comment {
    [XmlAnyElement]
    public XmlElement[] Body;
}
