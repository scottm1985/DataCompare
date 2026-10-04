using System;
using System.Collections.Generic;

namespace MyscotekDataCompare.Core.Schema
{
    /// <summary>
    /// The slice of an entity's metadata the compare engine and the UI need (built from EntityMetadata).
    /// Data Compare reads the metadata of the PRIMARY environment only (SPEC 5.5).
    /// </summary>
    public sealed class EntitySchema
    {
        public string LogicalName, PrimaryIdAttribute, PrimaryNameAttribute, DisplayName;

        /// <summary>The plural display name (e.g. "Contacts"); null when there is none.</summary>
        public string DisplayCollectionName;

        public bool IsIntersect;

        /// <summary>A private (internal system) entity.</summary>
        public bool IsPrivate;

        /// <summary>A virtual table: its rows live in an external data source (compared like any other entity).</summary>
        public bool IsVirtual;

        /// <summary>Keyed by attribute logical name, OrdinalIgnoreCase.</summary>
        public IReadOnlyDictionary<string, AttributeSchema> Attributes =
            new Dictionary<string, AttributeSchema>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The attribute's metadata, or null when the entity has no such attribute.</summary>
        public AttributeSchema Attribute(string logicalName) =>
            logicalName != null && Attributes != null && Attributes.TryGetValue(logicalName, out AttributeSchema attribute) ? attribute : null;
    }
}
