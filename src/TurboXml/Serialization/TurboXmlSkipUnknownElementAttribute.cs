using System;

namespace TurboXml.Serialization;

/// <summary>
/// Declares an element that a generated deserializer must skip instead of preserving as an unknown element.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class TurboXmlSkipUnknownElementAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TurboXmlSkipUnknownElementAttribute"/> class.
    /// </summary>
    /// <param name="type">The model type that owns the unknown element collection.</param>
    /// <param name="elementName">The element name to skip.</param>
    public TurboXmlSkipUnknownElementAttribute(Type type, string elementName)
    {
        Type = type ?? throw new ArgumentNullException(nameof(type));
        ArgumentException.ThrowIfNullOrEmpty(elementName);
        ElementName = elementName;
    }

    /// <summary>
    /// Gets the model type that owns the unknown element collection.
    /// </summary>
    public Type Type { get; }

    /// <summary>
    /// Gets the element name to skip.
    /// </summary>
    public string ElementName { get; }
}
