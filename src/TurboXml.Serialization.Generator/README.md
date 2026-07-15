# TurboXml Serialization Generator

Reference this package as an analyzer alongside the `TurboXml` runtime package. Declare a partial `TurboXmlSerializerContext` with `TurboXmlSerializableAttribute` entries to generate type information and deserializers.

`TurboXmlFieldBackedPropertyAttribute` optionally configures a context to discover source-visible fields marked by another generator's attribute. Give it the marker's metadata name and optionally its property-name and additional-attributes named arguments; the generated reader assigns the public property emitted by that generator without reflection. XML serialization attributes placed on the backing field are honored, and a configured additional-attributes collection may supply restricted `XmlElement("name")` overrides.
