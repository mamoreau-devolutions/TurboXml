using System.Text;
using System.Xml;
using System.Xml.Serialization;
using BenchmarkDotNet.Attributes;
using TurboXml;
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

[MemoryDiagnoser(displayGenColumns: false)]
public class BenchTurboXmlSerializerString
{
    [ParamsAllValues]
    public ConnectionLoaderScenario Scenario { get; set; }

    private string _xml = null!;

    [GlobalSetup]
    public void Setup()
    {
        _xml = ConnectionLoaderFixture.CreateXml(Scenario);
        ConnectionLoaderFixture.ValidateTurboXmlCallback(_xml, Scenario);
    }

    [Benchmark(Baseline = true, Description = "TurboXml callback loader")]
    public int TurboXmlCallback()
    {
        return ConnectionLoaderFixture.LoadWithTurboXmlCallback(_xml, Scenario);
    }

    [Benchmark(Description = "Generated TurboXml reader")]
    public int GeneratedTurboXml()
    {
        return ConnectionLoaderFixture.LoadWithTurboXml(_xml, Scenario);
    }
}

[MemoryDiagnoser(displayGenColumns: false)]
public class BenchTurboXmlSerializerStream
{
    [ParamsAllValues]
    public ConnectionLoaderScenario Scenario { get; set; }

    private byte[] _xml = null!;

    [GlobalSetup]
    public void Setup()
    {
        _xml = Encoding.UTF8.GetBytes(ConnectionLoaderFixture.CreateXml(Scenario));
        ConnectionLoaderFixture.ValidateTurboXmlCallback(Encoding.UTF8.GetString(_xml), Scenario);
    }

    [Benchmark(Baseline = true, Description = "TurboXml callback loader")]
    public int TurboXmlCallback()
    {
        using var stream = new MemoryStream(_xml, writable: false);
        return ConnectionLoaderFixture.LoadWithTurboXmlCallback(stream, Scenario);
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
    UnknownExtension,
    BinaryPayload
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
            ConnectionLoaderScenario.BinaryPayload => CreateConnection(1, includeUnknown: false, includeBinaryPayload: true),
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

    public static int LoadWithTurboXmlCallback(string xml, ConnectionLoaderScenario scenario)
    {
        var handler = new CustomConnectionLoader.TurboXmlCallbackConnectionLoader(scenario == ConnectionLoaderScenario.ConnectionArray);
        XmlParser.Parse(xml, ref handler);
        return scenario == ConnectionLoaderScenario.ConnectionArray
            ? Checksum(handler.GetArrayResult())
            : Checksum(handler.GetResult());
    }

    public static int LoadWithTurboXmlCallback(Stream stream, ConnectionLoaderScenario scenario)
    {
        var handler = new CustomConnectionLoader.TurboXmlCallbackConnectionLoader(scenario == ConnectionLoaderScenario.ConnectionArray);
        XmlParser.Parse(stream, ref handler);
        return scenario == ConnectionLoaderScenario.ConnectionArray
            ? Checksum(handler.GetArrayResult())
            : Checksum(handler.GetResult());
    }

    public static void ValidateTurboXmlCallback(string xml, ConnectionLoaderScenario scenario)
    {
        var generated = LoadWithTurboXml(xml, scenario);
        var callback = LoadWithTurboXmlCallback(xml, scenario);
        if (callback != generated)
        {
            throw new InvalidOperationException($"TurboXml callback and generated readers produced different checksums: {callback} and {generated}.");
        }
    }

    private static int Checksum(ConnectionBenchModel connection)
    {
        return connection.Id
               + connection.Name.Length
               + connection.Host.Length
               + (int)connection.Protocol
               + (connection.Enabled ? 1 : 0)
               + (connection.Image?.Length ?? 0)
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

    private static string CreateConnection(int id, bool includeUnknown, bool includeBinaryPayload = false)
    {
        var extension = includeUnknown
            ? "<ForwardCompatible key=\"value\"><Child>text</Child><![CDATA[cdata]]><!--comment--></ForwardCompatible>"
            : string.Empty;
        var image = includeBinaryPayload
            ? "<Image>VGhpcyBpcyBhIHJlYWxpc3RpYyBjb25uZWN0aW9uIGltYWdlIHBheWxvYWQu</Image>"
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
                  {image}
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

    public byte[]? Image { get; set; }

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

    internal struct TurboXmlCallbackConnectionLoader : IXmlReadHandler
        {
            private readonly bool _isArray;
            private int _depth;
            private int _connectionDepth;
            private int _skipDepth;
            private ConnectionBenchModel? _current;
            private ConnectionBenchModel? _result;
            private List<ConnectionBenchModel>? _results;
            private string? _currentMember;
            private XmlDocument? _unknownDocument;
            private List<XmlElement>? _unknownStack;
            private List<XmlElement>? _unknownElements;

            public TurboXmlCallbackConnectionLoader(bool isArray)
            {
                _isArray = isArray;
            }

            public void OnBeginTag(ReadOnlySpan<char> name, int line, int column)
            {
                _depth++;
                if (_unknownStack is not null)
                {
                    AddUnknownElement(name);
                    return;
                }

                if (_skipDepth != 0)
                {
                    return;
                }

                if (_current is null)
                {
                    if ((!_isArray && _depth == 1 && name.SequenceEqual("Connection".AsSpan()))
                        || (_isArray && _depth == 2 && name.SequenceEqual("Connection".AsSpan())))
                    {
                        _current = new ConnectionBenchModel();
                        _connectionDepth = _depth;
                    }

                    return;
                }

                if (_depth != _connectionDepth + 1)
                {
                    return;
                }

                _currentMember = name.ToString();
                if (_currentMember == "Stamp")
                {
                    _currentMember = null;
                    _skipDepth = _depth;
                }
                else if (!IsKnownMember(_currentMember))
                {
                    _currentMember = null;
                    BeginUnknownElement(name);
                }
            }

            public void OnAttribute(ReadOnlySpan<char> name, ReadOnlySpan<char> value, int nameLine, int nameColumn, int valueLine, int valueColumn)
            {
                if (_unknownStack is not null)
                {
                    _unknownStack[^1].SetAttribute(name.ToString(), value.ToString());
                    return;
                }

                if (_current is not null
                    && _depth == _connectionDepth
                    && name.SequenceEqual("id".AsSpan()))
                {
                    _current.Id = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                }
            }

            public void OnText(ReadOnlySpan<char> text, int line, int column)
            {
                if (_unknownStack is not null)
                {
                    _unknownStack[^1].AppendChild(_unknownDocument!.CreateTextNode(text.ToString()));
                    return;
                }

                if (_current is not null
                    && _currentMember is not null
                    && _depth == _connectionDepth + 1)
                {
                    SetCurrentMember(text);
                }
            }

            public void OnCData(ReadOnlySpan<char> cdata, int line, int column)
            {
                if (_unknownStack is not null)
                {
                    _unknownStack[^1].AppendChild(_unknownDocument!.CreateCDataSection(cdata.ToString()));
                }
            }

            public void OnComment(ReadOnlySpan<char> comment, int line, int column)
            {
                if (_unknownStack is not null)
                {
                    _unknownStack[^1].AppendChild(_unknownDocument!.CreateComment(comment.ToString()));
                }
            }

            public void OnEndTag(ReadOnlySpan<char> name, int line, int column) => EndElement();

            public void OnEndTagEmpty() => EndElement();

            public ConnectionBenchModel GetResult() => _result ?? throw new XmlException("No Connection element was found.");

            public ConnectionBenchModel[] GetArrayResult() => _results?.ToArray() ?? [];

            private static bool IsKnownMember(string name)
            {
                return name is "Name" or "Host" or "Description" or "Folder" or "Username" or "Domain"
                    or "Port" or "Enabled" or "Protocol" or "Timeout" or "RetryCount" or "UseGateway"
                    or "GatewayHost" or "GatewayPort" or "Color" or "Tags" or "CreatedBy" or "UpdatedBy"
                    or "Image";
            }

            private void SetCurrentMember(ReadOnlySpan<char> value)
            {
                switch (_currentMember)
                {
                    case "Name": _current!.Name = value.ToString(); break;
                    case "Host": _current!.Host = value.ToString(); break;
                    case "Description": _current!.Description = value.ToString(); break;
                    case "Folder": _current!.Folder = value.ToString(); break;
                    case "Username": _current!.Username = value.ToString(); break;
                    case "Domain": _current!.Domain = value.ToString(); break;
                    case "Port": _current!.Port = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
                    case "Enabled": _current!.Enabled = XmlConvert.ToBoolean(value.ToString()); break;
                    case "Protocol": _current!.Protocol = value.SequenceEqual("Rdp".AsSpan()) ? ConnectionProtocol.Rdp : ConnectionProtocol.Ssh; break;
                    case "Timeout": _current!.Timeout = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
                    case "RetryCount": _current!.RetryCount = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
                    case "UseGateway": _current!.UseGateway = XmlConvert.ToBoolean(value.ToString()); break;
                    case "GatewayHost": _current!.GatewayHost = value.ToString(); break;
                    case "GatewayPort": _current!.GatewayPort = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
                    case "Color": _current!.Color = value.ToString(); break;
                    case "Tags": _current!.Tags = value.ToString(); break;
                    case "CreatedBy": _current!.CreatedBy = value.ToString(); break;
                    case "UpdatedBy": _current!.UpdatedBy = value.ToString(); break;
                    case "Image": _current!.Image = Convert.FromBase64String(value.ToString()); break;
                }
            }

            private void BeginUnknownElement(ReadOnlySpan<char> name)
            {
                _unknownDocument = new XmlDocument();
                var element = _unknownDocument.CreateElement(name.ToString());
                _unknownDocument.AppendChild(element);
                _unknownStack = [element];
            }

            private void AddUnknownElement(ReadOnlySpan<char> name)
            {
                var element = _unknownDocument!.CreateElement(name.ToString());
                _unknownStack![^1].AppendChild(element);
                _unknownStack.Add(element);
            }

            private void EndElement()
            {
                if (_unknownStack is not null)
                {
                    var element = _unknownStack[^1];
                    _unknownStack.RemoveAt(_unknownStack.Count - 1);
                    if (_unknownStack.Count == 0)
                    {
                        (_unknownElements ??= []).Add(element);
                        _unknownStack = null;
                        _unknownDocument = null;
                    }

                    _depth--;
                    return;
                }

                if (_skipDepth != 0)
                {
                    if (_depth == _skipDepth)
                    {
                        _skipDepth = 0;
                    }

                    _depth--;
                    return;
                }

                if (_current is not null && _depth == _connectionDepth + 1)
                {
                    _currentMember = null;
                }

                if (_current is not null && _depth == _connectionDepth)
                {
                    if (_unknownElements is { Count: > 0 })
                    {
                        _current.UnknownProperties = _unknownElements.ToArray();
                    }

                    if (_isArray)
                    {
                        (_results ??= []).Add(_current);
                    }
                    else
                    {
                        _result = _current;
                    }

                    _current = null;
                    _unknownElements = null;
                }

                _depth--;
            }
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
            case "Image": result.Image = Convert.FromBase64String(reader.ReadElementContentAsString()); return true;
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
