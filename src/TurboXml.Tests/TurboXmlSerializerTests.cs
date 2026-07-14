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
                             <Image>AQIDBA==</Image>
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
        Assert.IsNotNull(framework.Image);
        Assert.IsNotNull(generated.Image);
        CollectionAssert.AreEqual(framework.Image, generated.Image);
        Assert.IsNotNull(framework.UnknownProperties);
        Assert.IsNotNull(generated.UnknownProperties);
        Assert.AreEqual(framework.UnknownProperties[0].OuterXml, generated.UnknownProperties[0].OuterXml);
    }

    [TestMethod]
    public void Deserialize_RecursivelyMapsRdmStyleConnectionGraph()
    {
        const string xml = """
                           <Connection id="100">
                             <Name>Production</Name>
                             <Settings>
                               <Host>rdm.example.com</Host>
                               <Credential><User>administrator</User><Domain>example</Domain></Credential>
                               <Endpoints>
                                 <Endpoint><Host>gateway.example.com</Host><Port>443</Port></Endpoint>
                                 <Endpoint><Host>fallback.example.com</Host><Port>8443</Port></Endpoint>
                               </Endpoints>
                               <VendorOptions key="value"><Option>text</Option><![CDATA[cdata]]><!--comment--></VendorOptions>
                               <SkipNested><Ignored>legacy</Ignored></SkipNested>
                             </Settings>
                             <Tags><Tag>production</Tag><Tag>windows</Tag></Tags>
                             <LegacyConnectionData><Ignored>legacy</Ignored></LegacyConnectionData>
                             <Children>
                               <Connection id="101">
                                 <Name>Child</Name>
                                 <Settings><Host>child.example.com</Host></Settings>
                                 <Tags><Tag>child</Tag></Tags>
                                 <Children>
                                   <Connection id="102"><Name>Grandchild</Name></Connection>
                                 </Children>
                                 <ChildExtension level="1"><Value>preserved</Value></ChildExtension>
                               </Connection>
                             </Children>
                           </Connection>
                           """;

        var result = TurboXmlSerializer.Deserialize(xml, RecursiveConnectionFixtureContext.Default.ConnectionTypeInfo);

        Assert.AreEqual(100, result.Id);
        Assert.AreEqual("Production", result.Name);
        var settings = result.Settings ?? throw new InvalidOperationException("Settings were not deserialized.");
        var credential = settings.Credential ?? throw new InvalidOperationException("Credential was not deserialized.");
        var endpoints = settings.Endpoints ?? throw new InvalidOperationException("Endpoints were not deserialized.");
        var settingsUnknownProperties = settings.UnknownProperties ?? throw new InvalidOperationException("Unknown settings were not captured.");
        var tags = result.Tags ?? throw new InvalidOperationException("Tags were not deserialized.");
        var children = result.Children ?? throw new InvalidOperationException("Children were not deserialized.");
        Assert.HasCount(2, endpoints);
        Assert.HasCount(1, settingsUnknownProperties);
        Assert.HasCount(2, tags);
        Assert.HasCount(1, children);

        var child = children[0];
        var childChildren = child.Children ?? throw new InvalidOperationException("Grandchildren were not deserialized.");
        var childUnknownProperties = child.UnknownProperties ?? throw new InvalidOperationException("Unknown child properties were not captured.");
        Assert.HasCount(1, childChildren);
        Assert.HasCount(1, childUnknownProperties);

        Assert.AreEqual("rdm.example.com", settings.Host);
        Assert.AreEqual("administrator", credential.User);
        Assert.AreEqual(8443, endpoints[1].Port);
        Assert.AreEqual("VendorOptions", settingsUnknownProperties[0].Name);
        Assert.AreEqual("value", settingsUnknownProperties[0].GetAttribute("key"));
        Assert.IsTrue(settingsUnknownProperties[0].OuterXml.Contains("<Option>text</Option>", StringComparison.Ordinal));
        Assert.IsTrue(settingsUnknownProperties[0].OuterXml.Contains("<![CDATA[cdata]]>", StringComparison.Ordinal));
        Assert.IsTrue(settingsUnknownProperties[0].OuterXml.Contains("<!--comment-->", StringComparison.Ordinal));
        Assert.AreEqual("windows", tags[1]);
        Assert.AreEqual("Child", child.Name);
        Assert.AreEqual("Grandchild", childChildren[0].Name);
        Assert.AreEqual("ChildExtension", childUnknownProperties[0].Name);
        Assert.IsNull(result.UnknownProperties);

        var rootArray = TurboXmlSerializer.DeserializeArray(
            "<Connections>" + xml + "<Connection id=\"103\"><Name>Second</Name></Connection></Connections>",
            "Connection",
            RecursiveConnectionFixtureContext.Default.ConnectionTypeInfo);

        Assert.HasCount(2, rootArray);
        Assert.AreEqual("Production", rootArray[0].Name);
        Assert.AreEqual("Second", rootArray[1].Name);
    }

    [TestMethod]
    public void Deserialize_EmitsTypeInfoForNestedModel()
    {
        const string xml = """<Settings><Host>settings.example.com</Host></Settings>""";

        var result = TurboXmlSerializer.Deserialize(xml, RecursiveConnectionFixtureContext.Default.ConnectionSettingsFixtureTypeInfo);

        Assert.AreEqual("settings.example.com", result.Host);
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

    public byte[]? Image { get; set; }

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

[TurboXmlSerializable(typeof(Connection))]
[TurboXmlSkipUnknownElement(typeof(Connection), "LegacyConnectionData")]
[TurboXmlSkipUnknownElement(typeof(ConnectionSettingsFixture), "SkipNested")]
internal sealed partial class RecursiveConnectionFixtureContext : TurboXmlSerializerContext
{
    public static RecursiveConnectionFixtureContext Default { get; } = new();
}

[XmlRoot("Connection")]
public sealed class Connection
{
    [XmlAttribute("id")]
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public ConnectionSettingsFixture? Settings { get; set; }

    [XmlArray("Tags")]
    [XmlArrayItem("Tag")]
    public List<string>? Tags { get; set; }

    [XmlArray("Children")]
    [XmlArrayItem("Connection")]
    public Connection[]? Children { get; set; }

    [XmlAnyElement]
    public XmlElement[]? UnknownProperties { get; set; }
}

[XmlRoot("Settings")]
public sealed class ConnectionSettingsFixture
{
    public string Host { get; set; } = string.Empty;

    public CredentialFixture? Credential { get; set; }

    [XmlArray("Endpoints")]
    [XmlArrayItem("Endpoint")]
    public List<ConnectionEndpointFixture>? Endpoints { get; set; }

    [XmlAnyElement]
    public XmlElement[]? UnknownProperties { get; set; }
}

public sealed class CredentialFixture
{
    public string User { get; set; } = string.Empty;

    public string Domain { get; set; } = string.Empty;
}

public sealed class ConnectionEndpointFixture
{
    public string Host { get; set; } = string.Empty;

    public int Port { get; set; }
}
