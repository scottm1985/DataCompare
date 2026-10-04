using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.Xrm.Sdk;
using MyscotekDataCompare.Core.Schema;

namespace MyscotekDataCompare.Core
{
    /// <summary>
    /// The value rules of the comparison (SPEC 5.6): when two attribute values - one from each
    /// environment - count as equal. An absent attribute and a null value are the same thing (Dataverse
    /// omits null attributes), and so are an empty string, an empty collection and an empty byte array.
    /// </summary>
    public static class ValueComparer
    {
        /// <summary>
        /// The type rules: EntityReference by logical name and id (the name is ignored); OptionSetValue by
        /// value; OptionSetValueCollection as a set of values; Money by its decimal value; DateTime as UTC
        /// (a Local value is converted, Utc and Unspecified are taken as UTC); string ordinal (no trimming);
        /// byte[] byte by byte; EntityCollection (party lists) as the multiset of its parties (partyid
        /// logical name + id, else the unresolved addressused; activityparty row ids ignored); an
        /// AliasedValue on either side is never compared (equal); anything else with Equals. Values of
        /// different types are different.
        /// </summary>
        public static bool AreEqual(object primary, object secondary)
        {
            if (primary is AliasedValue || secondary is AliasedValue) return true;

            bool primaryEmpty = IsEmpty(primary), secondaryEmpty = IsEmpty(secondary);
            if (primaryEmpty || secondaryEmpty) return primaryEmpty && secondaryEmpty;

            switch (primary)
            {
                case EntityReference reference:
                    return secondary is EntityReference other
                           && reference.Id == other.Id
                           && string.Equals(reference.LogicalName ?? string.Empty, other.LogicalName ?? string.Empty, StringComparison.OrdinalIgnoreCase);
                case OptionSetValue option:
                    return secondary is OptionSetValue otherOption && option.Value == otherOption.Value;
                case OptionSetValueCollection options:
                    return secondary is OptionSetValueCollection otherOptions
                           && new HashSet<int>(OptionValues(options)).SetEquals(OptionValues(otherOptions));
                case Money money:
                    return secondary is Money otherMoney && money.Value == otherMoney.Value;
                case DateTime date:
                    return secondary is DateTime otherDate && ToUtc(date) == ToUtc(otherDate);
                case string text:
                    return secondary is string otherText && string.Equals(text, otherText, StringComparison.Ordinal);
                case byte[] bytes:
                    return secondary is byte[] otherBytes && bytes.SequenceEqual(otherBytes);
                case EntityCollection collection:
                    return secondary is EntityCollection otherCollection && CollectionKeys(collection).SequenceEqual(CollectionKeys(otherCollection), StringComparer.Ordinal);
                default:
                    return primary.GetType() == secondary.GetType() && primary.Equals(secondary);
            }
        }

        /// <summary>
        /// <see cref="AreEqual(object, object)"/> with the attribute's metadata: a file column
        /// (<see cref="AttributeSchema.IsFile"/>) is compared by presence only - its value is the id of a
        /// file that differs between environments by design.
        /// </summary>
        public static bool AreEqual(object primary, object secondary, AttributeSchema attribute)
        {
            if (attribute != null && attribute.IsFile && !(primary is AliasedValue) && !(secondary is AliasedValue))
                return IsEmpty(primary) == IsEmpty(secondary);
            return AreEqual(primary, secondary);
        }

        /// <summary>True for null, "", an empty OptionSetValueCollection, EntityCollection or byte array.</summary>
        public static bool IsEmpty(object value)
        {
            switch (value)
            {
                case null: return true;
                case string text: return text.Length == 0;
                case OptionSetValueCollection options: return !options.Any(o => o != null);
                case EntityCollection collection: return collection.Entities == null || collection.Entities.Count == 0;
                case byte[] bytes: return bytes.Length == 0;
                default: return false;
            }
        }

        /// <summary>
        /// A culture-invariant rendering of a raw value that shows what the comparison looks at (e.g.
        /// "account 0d3e...", "2024-03-01 14:30:05.1234567 UTC", "\"Contoso \" (8 chars)"). The detail pane
        /// adds it when two different values would otherwise read the same (SPEC 5.8).
        /// </summary>
        public static string RawText(object value)
        {
            switch (value)
            {
                case null: return "null";
                case AliasedValue aliased: return RawText(aliased.Value);
                case EntityReference reference: return $"{reference.LogicalName} {reference.Id:D}";
                case OptionSetValue option: return option.Value.ToString(CultureInfo.InvariantCulture);
                case OptionSetValueCollection options:
                    return string.Join(", ", OptionValues(options).Distinct().OrderBy(v => v).Select(v => v.ToString(CultureInfo.InvariantCulture)));
                case Money money: return money.Value.ToString(CultureInfo.InvariantCulture);
                case DateTime date: return ToUtc(date).ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture) + " UTC";
                case string text:
                    return "\"" + text.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t") + "\" (" +
                           text.Length.ToString(CultureInfo.InvariantCulture) + (text.Length == 1 ? " char)" : " chars)");
                case byte[] bytes: return bytes.Length.ToString(CultureInfo.InvariantCulture) + " bytes";
                case EntityCollection collection: return string.Join("; ", CollectionKeys(collection));
                case double number: return number.ToString("R", CultureInfo.InvariantCulture);
                case IFormattable formattable: return formattable.ToString(null, CultureInfo.InvariantCulture);
                default: return value.ToString();
            }
        }

        /// <summary>A DateTime as UTC: Local is converted; Utc and Unspecified keep their ticks.</summary>
        internal static DateTime ToUtc(DateTime value) =>
            value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);

        private static IEnumerable<int> OptionValues(OptionSetValueCollection options) => options.Where(o => o != null).Select(o => o.Value);

        /// <summary>
        /// One key per entity of the collection, sorted: a party's partyid ("ref:logicalname:id"), else its
        /// unresolved address ("address:..."), else the row itself ("row:logicalname:id").
        /// </summary>
        private static IEnumerable<string> CollectionKeys(EntityCollection collection) =>
            ((IEnumerable<Entity>)collection.Entities ?? Enumerable.Empty<Entity>())
                .Where(e => e != null)
                .Select(PartyKey)
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToList();

        private static string PartyKey(Entity party)
        {
            if (party.Attributes.TryGetValue("partyid", out object value) && value is EntityReference reference)
                return "ref:" + (reference.LogicalName ?? string.Empty).ToLowerInvariant() + ":" + reference.Id.ToString("D");
            if (party.Attributes.TryGetValue("addressused", out object address) && address is string text && text.Length > 0)
                return "address:" + text;
            return "row:" + (party.LogicalName ?? string.Empty).ToLowerInvariant() + ":" + party.Id.ToString("D");
        }
    }

    /// <summary>
    /// Which attributes decide a row's status (SPEC 5.5): every attribute of the PRIMARY entity's metadata
    /// that is valid for read, is not derived from another attribute (AttributeOf), is not the primary key,
    /// is not ignored, passes the prefix filter and - for a mapped entity (SPEC 5.9) - has a counterpart in
    /// the secondary entity's metadata. Shared by the engine, <see cref="CompareResult.Reevaluate"/> and
    /// <see cref="DetailBuilder"/> so a row is Different exactly when its detail shows a mismatch.
    /// </summary>
    internal sealed class ComparePlan
    {
        private readonly string[] _secondaryNames;

        public ComparePlan(EntitySchema schema, CompareOptions options, EntityMap map = null)
        {
            Schema = schema ?? throw new ArgumentNullException(nameof(schema));
            Options = options ?? throw new ArgumentNullException(nameof(options));
            Map = map;
            Compared = (schema.Attributes ?? new Dictionary<string, AttributeSchema>())
                .Values
                .Where(a => a != null && IsCompared(a))
                .OrderBy(a => a.LogicalName, StringComparer.Ordinal)
                .ToList();
            _secondaryNames = Compared.Select(a => SecondaryName(a.LogicalName)).ToArray();
        }

        public EntitySchema Schema { get; }

        public CompareOptions Options { get; }

        /// <summary>The entity mapping of the run; null when the secondary has the same entity.</summary>
        public EntityMap Map { get; }

        /// <summary>The compared attributes, by logical name (ordinal).</summary>
        public IReadOnlyList<AttributeSchema> Compared { get; }

        public bool IsPrimaryKey(string attribute) =>
            !string.IsNullOrEmpty(Schema.PrimaryIdAttribute) && string.Equals(attribute, Schema.PrimaryIdAttribute, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The secondary attribute a primary attribute is read from: the same name without a mapping; with one,
        /// its counterpart (<see cref="EntityMap.SecondaryColumn"/>), null when it has none.
        /// </summary>
        public string SecondaryName(string primaryAttribute) => Map == null ? primaryAttribute : Map.SecondaryColumn(primaryAttribute);

        /// <summary>True when the attribute takes part in the status decision.</summary>
        public bool IsCompared(AttributeSchema attribute) =>
            attribute != null && !string.IsNullOrEmpty(attribute.LogicalName) && attribute.IsValidForRead && attribute.AttributeOf == null
            && !IsPrimaryKey(attribute.LogicalName) && !Options.IsIgnored(attribute.LogicalName)
            && Options.IsInPrefixFilter(attribute.LogicalName)
            && (Map == null || Map.SecondaryColumn(attribute.LogicalName) != null);

        /// <summary>
        /// The compared attributes whose values differ between the two records (primary logical names, ordinal
        /// order); the secondary value is read under the counterpart's name for a mapped entity. Linked-entity
        /// values ('.' in the key, AliasedValue) are never looked at: only metadata attributes are.
        /// </summary>
        public IReadOnlyList<string> DifferingAttributes(Entity primary, Entity secondary)
        {
            List<string> differing = null;
            for (int i = 0; i < Compared.Count; i++)
            {
                AttributeSchema attribute = Compared[i];
                object first = Value(primary, attribute.LogicalName);
                object second = Value(secondary, _secondaryNames[i]);
                if (ValueComparer.AreEqual(first, second, attribute)) continue;
                (differing ?? (differing = new List<string>())).Add(attribute.LogicalName);
            }
            return (IReadOnlyList<string>)differing ?? Array.Empty<string>();
        }

        /// <summary>
        /// The attribute's value, or null when the record is null or does not hold it. Keys are matched
        /// exactly: Dataverse returns lower-case logical names, as the metadata has them.
        /// </summary>
        internal static object Value(Entity record, string attribute) =>
            record != null && !string.IsNullOrEmpty(attribute) && record.Attributes.TryGetValue(attribute, out object value) ? value : null;
    }
}
