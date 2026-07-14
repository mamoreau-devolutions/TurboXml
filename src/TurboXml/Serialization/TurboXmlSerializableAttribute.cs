using System;

namespace TurboXml.Serialization;

/// <summary>
/// Declares a model type for a <see cref="TurboXmlSerializerContext"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class TurboXmlSerializableAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TurboXmlSerializableAttribute"/> class.
    /// </summary>
    /// <param name="type">The model type to deserialize.</param>
    public TurboXmlSerializableAttribute(Type type)
    {
        Type = type ?? throw new ArgumentNullException(nameof(type));
    }

    /// <summary>
    /// Gets the model type to deserialize.
    /// </summary>
    public Type Type { get; }
}
