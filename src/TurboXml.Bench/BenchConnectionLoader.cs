using System.Text;
using System.Xml;
using System.Xml.Serialization;
using BenchmarkDotNet.Attributes;
using TurboXml.Serialization;

namespace TurboXml.Bench;

[MemoryDiagnoser(displayGenColumns: false)]
public class BenchConnectionLoaderString
{
    [ParamsAllValues]
    public ConnectionLoaderScenario Scenario { get; set; }

    private string _xml = null!;

    [GlobalSetup]
    public void Setup()
    {
        _xml = ConnectionLoaderFixture.CreateXml(Scenario);
    }

    [Benchmark(Baseline = true, Description = "Custom XmlReader loader")]
    public int CustomXmlReader()
    {
        return ConnectionLoaderFixture.LoadWithCustomLoader(_xml, Scenario);
    }

    [Benchmark(Description = "Generated TurboXml reader")]
    public int GeneratedTurboXml()
    {
        return ConnectionLoaderFixture.LoadWithTurboXml(_xml, Scenario);
    }
}

[MemoryDiagnoser(displayGenColumns: false)]
public class BenchConnectionLoaderStream
{
    [ParamsAllValues]
    public ConnectionLoaderScenario Scenario { get; set; }

    private byte[] _xml = null!;

    [GlobalSetup]
    public void Setup()
    {
        _xml = Encoding.UTF8.GetBytes(ConnectionLoaderFixture.CreateXml(Scenario));
    }

    [Benchmark(Baseline = true, Description = "Custom XmlReader loader")]
    public int CustomXmlReader()
    {
        using var stream = new MemoryStream(_xml, writable: false);
        return ConnectionLoaderFixture.LoadWithCustomLoader(stream, Scenario);
    }

    [Benchmark(Description = "Generated TurboXml reader")]
    public int GeneratedTurboXml()
    {
        using var stream = new MemoryStream(_xml, writable: false);
        return ConnectionLoaderFixture.LoadWithTurboXml(stream, Scenario);
    }
}

public enum ConnectionLoaderScenario
{
    KnownSingle,
    ConnectionArray,
    UnknownExtension
}

public static class ConnectionLoaderFixture
{
    public static string CreateXml(ConnectionLoaderScenario scenario)
    {
        return scenario switch
        {
            ConnectionLoaderScenario.KnownSingle => CreateConnection(1, includeUnknown: false),
            ConnectionLoaderScenario.ConnectionArray => "<Connections>" + CreateConnection(1, includeUnknown: false) + CreateConnection(2, includeUnknown: false) + CreateConnection(3, includeUnknown: false) + "</Connections>",
            ConnectionLoaderScenario.UnknownExtension => CreateConnection(1, includeUnknown: true),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
    }

    public static int LoadWithCustomLoader(string xml, ConnectionLoaderScenario scenario)
    {
        using var reader = XmlReader.Create(new StringReader(xml), Settings);
        reader.MoveToContent();
        return scenario == ConnectionLoaderScenario.ConnectionArray
            ? Checksum(CustomConnectionLoader.LoadArray(reader, "Connection"))
            : Checksum(CustomConnectionLoader.Load(reader));
    }

    public static int LoadWithCustomLoader(Stream stream, ConnectionLoaderScenario scenario)
    {
        using var reader = XmlReader.Create(stream, Settings);
        reader.MoveToContent();
        return scenario == ConnectionLoaderScenario.ConnectionArray
            ? Checksum(CustomConnectionLoader.LoadArray(reader, "Connection"))
            : Checksum(CustomConnectionLoader.Load(reader));
    }

    public static int LoadWithTurboXml(string xml, ConnectionLoaderScenario scenario)
    {
        return scenario == ConnectionLoaderScenario.ConnectionArray
            ? Checksum(TurboXmlSerializer.DeserializeArray(xml, "Connection", ConnectionLoaderContext.Default.ConnectionBenchModelTypeInfo))
            : Checksum(TurboXmlSerializer.Deserialize(xml, ConnectionLoaderContext.Default.ConnectionBenchModelTypeInfo));
    }

    public static int LoadWithTurboXml(Stream stream, ConnectionLoaderScenario scenario)
    {
        return scenario == ConnectionLoaderScenario.ConnectionArray
            ? Checksum(TurboXmlSerializer.DeserializeArray(stream, "Connection", ConnectionLoaderContext.Default.ConnectionBenchModelTypeInfo))
            : Checksum(TurboXmlSerializer.Deserialize(stream, ConnectionLoaderContext.Default.ConnectionBenchModelTypeInfo));
    }

    private static int Checksum(ConnectionBenchModel connection)
    {
        return connection.Id
               + connection.Name.Length
               + connection.Host.Length
               + (int)connection.Protocol
               + (connection.Enabled ? 1 : 0)
               + (connection.UnknownProperties?.Length ?? 0);
    }

    private static int Checksum(ConnectionBenchModel[] connections)
    {
        var checksum = 0;
        foreach (var connection in connections)
        {
            checksum += Checksum(connection);
        }

        return checksum;
    }

    private static string CreateConnection(int id, bool includeUnknown)
    {
        var extension = includeUnknown
            ? "<ForwardCompatible key=\"value\"><Child>text</Child><![CDATA[cdata]]><!--comment--></ForwardCompatible>"
            : string.Empty;
        return $"""
                <Connection id="{id}">
                  <Name>Production {id}</Name>
                  <Host>rdm-{id}.example.com</Host>
                  <Description>Primary production endpoint</Description>
                  <Folder>Infrastructure</Folder>
                  <Username>administrator</Username>
                  <Domain>example</Domain>
                  <Port>3389</Port>
                  <Enabled>true</Enabled>
                  <Protocol>Rdp</Protocol>
                  <Timeout>30</Timeout>
                  <RetryCount>3</RetryCount>
                  <UseGateway>true</UseGateway>
                  <GatewayHost>gateway.example.com</GatewayHost>
                  <GatewayPort>443</GatewayPort>
                  <Color>Blue</Color>
                  <Tags>production;windows</Tags>
                  <CreatedBy>benchmark</CreatedBy>
                  <UpdatedBy>benchmark</UpdatedBy>
                  <Stamp><Ignored>legacy</Ignored></Stamp>
                  {extension}
                </Connection>
                """;
    }

    private static readonly XmlReaderSettings Settings = new()
    {
        DtdProcessing = DtdProcessing.Ignore,
        CheckCharacters = false
    };
}

[TurboXmlSerializable(typeof(ConnectionBenchModel))]
[TurboXmlSkipUnknownElement(typeof(ConnectionBenchModel), "Stamp")]
internal sealed partial class ConnectionLoaderContext : TurboXmlSerializerContext
{
    public static ConnectionLoaderContext Default { get; } = new();
}

[XmlRoot("Connection")]
public sealed class ConnectionBenchModel
{
    [XmlAttribute("id")]
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Host { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string Folder { get; set; } = string.Empty;

    public string Username { get; set; } = string.Empty;

    public string Domain { get; set; } = string.Empty;

    public int Port { get; set; }

    public bool Enabled { get; set; }

    public ConnectionProtocol Protocol { get; set; }

    public int Timeout { get; set; }

    public int RetryCount { get; set; }

    public bool UseGateway { get; set; }

    public string GatewayHost { get; set; } = string.Empty;

    public int GatewayPort { get; set; }

    public string Color { get; set; } = string.Empty;

    public string Tags { get; set; } = string.Empty;

    public string CreatedBy { get; set; } = string.Empty;

    public string UpdatedBy { get; set; } = string.Empty;

    [XmlAnyElement]
    public XmlElement[]? UnknownProperties { get; set; }
}

public enum ConnectionProtocol
{
    Rdp,
    Ssh
}

internal static class CustomConnectionLoader
{
    public static ConnectionBenchModel Load(XmlReader reader)
    {
        var result = new ConnectionBenchModel
        {
            Id = reader.GetAttribute("id") is { } id ? XmlConvert.ToInt32(id) : 0
        };
        if (reader.IsEmptyElement)
        {
            reader.Read();
            return result;
        }

        var rootName = reader.Name;
        List<XmlElement>? unknownElements = null;
        reader.Read();
        while (!reader.EOF)
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                    if (reader.Name == "Stamp")
                    {
                        reader.Skip();
                        break;
                    }

                    if (!TryLoadKnownProperty(reader, result))
                    {
                        var document = new XmlDocument();
                        document.LoadXml(reader.ReadOuterXml());
                        unknownElements ??= [];
                        unknownElements.Add(document.DocumentElement!);
                    }
                    break;
                case XmlNodeType.EndElement when reader.Name == rootName:
                    reader.Read();
                    if (unknownElements is { Count: > 0 })
                    {
                        result.UnknownProperties = unknownElements.ToArray();
                    }
                    return result;
                default:
                    reader.Read();
                    break;
            }
        }

        return result;
    }

    public static ConnectionBenchModel[] LoadArray(XmlReader reader, string itemElementName)
    {
        if (reader.IsEmptyElement)
        {
            reader.Read();
            return [];
        }

        var rootName = reader.Name;
        var result = new List<ConnectionBenchModel>();
        reader.Read();
        while (!reader.EOF)
        {
            if (reader.NodeType == XmlNodeType.Element && reader.Name == itemElementName)
            {
                result.Add(Load(reader));
                continue;
            }

            if (reader.NodeType == XmlNodeType.EndElement && reader.Name == rootName)
            {
                reader.Read();
                return result.ToArray();
            }

            reader.Read();
        }

        return result.ToArray();
    }

    private static bool TryLoadKnownProperty(XmlReader reader, ConnectionBenchModel result)
    {
        switch (reader.Name)
        {
            case "Name": result.Name = reader.ReadElementContentAsString(); return true;
            case "Host": result.Host = reader.ReadElementContentAsString(); return true;
            case "Description": result.Description = reader.ReadElementContentAsString(); return true;
            case "Folder": result.Folder = reader.ReadElementContentAsString(); return true;
            case "Username": result.Username = reader.ReadElementContentAsString(); return true;
            case "Domain": result.Domain = reader.ReadElementContentAsString(); return true;
            case "Port": result.Port = reader.ReadElementContentAsInt(); return true;
            case "Enabled": result.Enabled = XmlConvert.ToBoolean(reader.ReadElementContentAsString()); return true;
            case "Protocol": result.Protocol = ReadProtocol(reader.ReadElementContentAsString()); return true;
            case "Timeout": result.Timeout = reader.ReadElementContentAsInt(); return true;
            case "RetryCount": result.RetryCount = reader.ReadElementContentAsInt(); return true;
            case "UseGateway": result.UseGateway = XmlConvert.ToBoolean(reader.ReadElementContentAsString()); return true;
            case "GatewayHost": result.GatewayHost = reader.ReadElementContentAsString(); return true;
            case "GatewayPort": result.GatewayPort = reader.ReadElementContentAsInt(); return true;
            case "Color": result.Color = reader.ReadElementContentAsString(); return true;
            case "Tags": result.Tags = reader.ReadElementContentAsString(); return true;
            case "CreatedBy": result.CreatedBy = reader.ReadElementContentAsString(); return true;
            case "UpdatedBy": result.UpdatedBy = reader.ReadElementContentAsString(); return true;
            default: return false;
        }
    }

    private static ConnectionProtocol ReadProtocol(string value)
    {
        return value switch
        {
            "Rdp" => ConnectionProtocol.Rdp,
            "Ssh" => ConnectionProtocol.Ssh,
            _ => throw new FormatException($"Unknown protocol '{value}'.")
        };
    }
}
