using System.Xml;
using System.Xml.Serialization;
using TurboXml.Serialization;

namespace TurboXml.Tests;

[TestClass]
public sealed class TurboXmlSerializerTests
{
    [TestMethod]
    public void Deserialize_MapsKnownValuesAndPreservesUnknownElements()
    {
        const string xml = """
                           <Connection id="42">
                             <Name>Production</Name>
                             <Owner>ops</Owner>
                             <Enabled>true</Enabled>
                             <Protocol>Ssh</Protocol>
                             <Stamp><Ignored>value</Ignored></Stamp>
                             <ForwardCompatible key="value"><Child>text</Child><![CDATA[cdata]]><!--comment--></ForwardCompatible>
                           </Connection>
                           """;

        var result = TurboXmlSerializer.Deserialize(xml, ConnectionFixtureContext.Default.ConnectionFixtureTypeInfo);

        Assert.AreEqual(42, result.Id);
        Assert.AreEqual("Production", result.Name);
        Assert.AreEqual("ops", result.Owner);
        Assert.IsTrue(result.Enabled);
        Assert.AreEqual(ConnectionProtocol.Ssh, result.Protocol);
        Assert.IsNotNull(result.UnknownProperties);
        Assert.HasCount(1, result.UnknownProperties);
        Assert.AreEqual("ForwardCompatible", result.UnknownProperties[0].Name);
        Assert.AreEqual("value", result.UnknownProperties[0].GetAttribute("key"));
        Assert.IsTrue(result.UnknownProperties[0].OuterXml.Contains("<Child>text</Child>", StringComparison.Ordinal));
        Assert.IsTrue(result.UnknownProperties[0].OuterXml.Contains("<![CDATA[cdata]]>", StringComparison.Ordinal));
        Assert.IsTrue(result.UnknownProperties[0].OuterXml.Contains("<!--comment-->", StringComparison.Ordinal));
    }

    [TestMethod]
    public void DeserializeArray_UsesTheConfiguredItemElement()
    {
        const string xml = """
                           <Connections>
                             <Ignored />
                             <Connection id="1"><Name>First</Name><Enabled>true</Enabled><Protocol>Rdp</Protocol></Connection>
                             <Connection id="2"><Name>Second</Name><Enabled>false</Enabled><Protocol>Ssh</Protocol></Connection>
                           </Connections>
                           """;

        var results = TurboXmlSerializer.DeserializeArray(xml, "Connection", ConnectionFixtureContext.Default.ConnectionFixtureTypeInfo);

        Assert.HasCount(2, results);
        Assert.AreEqual("First", results[0].Name);
        Assert.AreEqual(ConnectionProtocol.Ssh, results[1].Protocol);
    }

    [TestMethod]
    public void DeserializeStream_MapsTheSameModel()
    {
        const string xml = """<Connection id="42"><Name>Production</Name><Enabled>true</Enabled><Protocol>Ssh</Protocol></Connection>""";
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml));

        var result = TurboXmlSerializer.Deserialize(stream, ConnectionFixtureContext.Default.ConnectionFixtureTypeInfo);

        Assert.AreEqual(42, result.Id);
        Assert.AreEqual("Production", result.Name);
        Assert.IsTrue(result.Enabled);
        Assert.AreEqual(ConnectionProtocol.Ssh, result.Protocol);
    }

    [TestMethod]
    public void Deserialize_MatchesXmlSerializerForSupportedValues()
    {
        const string xml = """
                           <Connection id="7">
                             <Name>Compatibility</Name>
                             <Enabled>false</Enabled>
                             <Protocol>Rdp</Protocol>
                             <ForwardCompatible key="value"><Child>text</Child></ForwardCompatible>
                           </Connection>
                           """;

        var generated = TurboXmlSerializer.Deserialize(xml, ConnectionFixtureContext.Default.ConnectionFixtureTypeInfo);
        var serializer = new XmlSerializer(typeof(ConnectionFixture));
        using var reader = new StringReader(xml);
        var framework = (ConnectionFixture)serializer.Deserialize(reader)!;

        Assert.AreEqual(framework.Id, generated.Id);
        Assert.AreEqual(framework.Name, generated.Name);
        Assert.AreEqual(framework.Enabled, generated.Enabled);
        Assert.AreEqual(framework.Protocol, generated.Protocol);
        Assert.IsNotNull(framework.UnknownProperties);
        Assert.IsNotNull(generated.UnknownProperties);
        Assert.AreEqual(framework.UnknownProperties[0].OuterXml, generated.UnknownProperties[0].OuterXml);
    }

}

[TurboXmlSerializable(typeof(ConnectionFixture))]
[TurboXmlSkipUnknownElement(typeof(ConnectionFixture), "Stamp")]
internal sealed partial class ConnectionFixtureContext : TurboXmlSerializerContext
{
    public static ConnectionFixtureContext Default { get; } = new();
}

[XmlRoot("Connection")]
public sealed class ConnectionFixture : ConnectionFixtureBase
{
    [XmlAttribute("id")]
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public bool Enabled { get; set; }

    public ConnectionProtocol Protocol { get; set; }

    [XmlAnyElement]
    public XmlElement[]? UnknownProperties { get; set; }
}

public class ConnectionFixtureBase
{
    public string Owner { get; set; } = string.Empty;
}

public enum ConnectionProtocol
{
    Rdp,
    Ssh
}
