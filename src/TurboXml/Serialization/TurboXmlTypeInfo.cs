using System;
using System.IO;

namespace TurboXml.Serialization;

/// <summary>
/// Contains generated deserialization entry points for a model type.
/// </summary>
/// <typeparam name="T">The model type.</typeparam>
public sealed class TurboXmlTypeInfo<T>
{
    private readonly Func<string, T> _deserializeString;
    private readonly Func<Stream, T> _deserializeStream;
    private readonly Func<string, string, T[]>? _deserializeStringArray;
    private readonly Func<Stream, string, T[]>? _deserializeStreamArray;

    /// <summary>
    /// Initializes a new instance of the <see cref="TurboXmlTypeInfo{T}"/> class.
    /// </summary>
    /// <param name="deserializeString">The generated string deserializer.</param>
    /// <param name="deserializeStream">The generated stream deserializer.</param>
    /// <param name="deserializeStringArray">The optional generated string array deserializer.</param>
    /// <param name="deserializeStreamArray">The optional generated stream array deserializer.</param>
    public TurboXmlTypeInfo(
        Func<string, T> deserializeString,
        Func<Stream, T> deserializeStream,
        Func<string, string, T[]>? deserializeStringArray = null,
        Func<Stream, string, T[]>? deserializeStreamArray = null)
    {
        ArgumentNullException.ThrowIfNull(deserializeString);
        ArgumentNullException.ThrowIfNull(deserializeStream);

        if ((deserializeStringArray is null) != (deserializeStreamArray is null))
        {
            throw new ArgumentException("Array deserializers must be provided together.");
        }

        _deserializeString = deserializeString;
        _deserializeStream = deserializeStream;
        _deserializeStringArray = deserializeStringArray;
        _deserializeStreamArray = deserializeStreamArray;
    }

    /// <summary>
    /// Deserializes one model instance from XML text.
    /// </summary>
    /// <param name="xml">The XML text to deserialize.</param>
    /// <returns>The deserialized model.</returns>
    public T Deserialize(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);
        return _deserializeString(xml);
    }

    /// <summary>
    /// Deserializes one model instance from an XML stream.
    /// </summary>
    /// <param name="stream">The XML stream to deserialize.</param>
    /// <returns>The deserialized model.</returns>
    public T Deserialize(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return _deserializeStream(stream);
    }

    /// <summary>
    /// Deserializes model instances from a root collection in XML text.
    /// </summary>
    /// <param name="xml">The XML text to deserialize.</param>
    /// <param name="itemElementName">The name of collection items to deserialize.</param>
    /// <returns>The deserialized models.</returns>
    /// <exception cref="NotSupportedException">The generated type does not support collection deserialization.</exception>
    public T[] DeserializeArray(string xml, string itemElementName)
    {
        ArgumentNullException.ThrowIfNull(xml);
        ArgumentException.ThrowIfNullOrEmpty(itemElementName);

        return _deserializeStringArray is null
            ? throw new NotSupportedException($"Collection deserialization is not generated for {typeof(T)}.")
            : _deserializeStringArray(xml, itemElementName);
    }

    /// <summary>
    /// Deserializes model instances from a root collection in an XML stream.
    /// </summary>
    /// <param name="stream">The XML stream to deserialize.</param>
    /// <param name="itemElementName">The name of collection items to deserialize.</param>
    /// <returns>The deserialized models.</returns>
    /// <exception cref="NotSupportedException">The generated type does not support collection deserialization.</exception>
    public T[] DeserializeArray(Stream stream, string itemElementName)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentException.ThrowIfNullOrEmpty(itemElementName);

        return _deserializeStreamArray is null
            ? throw new NotSupportedException($"Collection deserialization is not generated for {typeof(T)}.")
            : _deserializeStreamArray(stream, itemElementName);
    }
}
