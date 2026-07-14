using System;
using System.IO;

namespace TurboXml.Serialization;

/// <summary>
/// Deserializes XML through generated TurboXml type information.
/// </summary>
public static class TurboXmlSerializer
{
    /// <summary>
    /// Deserializes one model instance from XML text.
    /// </summary>
    /// <typeparam name="T">The model type.</typeparam>
    /// <param name="xml">The XML text to deserialize.</param>
    /// <param name="typeInfo">The generated type information.</param>
    /// <returns>The deserialized model.</returns>
    public static T Deserialize<T>(string xml, TurboXmlTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        return typeInfo.Deserialize(xml);
    }

    /// <summary>
    /// Deserializes one model instance from an XML stream.
    /// </summary>
    /// <typeparam name="T">The model type.</typeparam>
    /// <param name="stream">The XML stream to deserialize.</param>
    /// <param name="typeInfo">The generated type information.</param>
    /// <returns>The deserialized model.</returns>
    public static T Deserialize<T>(Stream stream, TurboXmlTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        return typeInfo.Deserialize(stream);
    }

    /// <summary>
    /// Deserializes model instances from a root collection in XML text.
    /// </summary>
    /// <typeparam name="T">The model type.</typeparam>
    /// <param name="xml">The XML text to deserialize.</param>
    /// <param name="itemElementName">The name of collection items to deserialize.</param>
    /// <param name="typeInfo">The generated type information.</param>
    /// <returns>The deserialized models.</returns>
    public static T[] DeserializeArray<T>(string xml, string itemElementName, TurboXmlTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        return typeInfo.DeserializeArray(xml, itemElementName);
    }

    /// <summary>
    /// Deserializes model instances from a root collection in an XML stream.
    /// </summary>
    /// <typeparam name="T">The model type.</typeparam>
    /// <param name="stream">The XML stream to deserialize.</param>
    /// <param name="itemElementName">The name of collection items to deserialize.</param>
    /// <param name="typeInfo">The generated type information.</param>
    /// <returns>The deserialized models.</returns>
    public static T[] DeserializeArray<T>(Stream stream, string itemElementName, TurboXmlTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        return typeInfo.DeserializeArray(stream, itemElementName);
    }
}
