using System;
using System.Collections.Generic;
using System.Linq;
using MyscotekDataCompare.Core.Schema;

namespace MyscotekDataCompare.Core
{
    /// <summary>
    /// One pair of columns of an <see cref="EntityMapping"/>: a primary column whose data was migrated into a
    /// differently named secondary column. XmlSerializer-friendly (the settings store the mappings).
    /// </summary>
    public sealed class ColumnMapping
    {
        public ColumnMapping()
        {
        }

        public ColumnMapping(string primaryColumn, string secondaryColumn)
        {
            PrimaryColumn = primaryColumn;
            SecondaryColumn = secondaryColumn;
        }

        /// <summary>The column's logical name in the PRIMARY entity.</summary>
        public string PrimaryColumn { get; set; }

        /// <summary>The logical name of its counterpart in the SECONDARY entity.</summary>
        public string SecondaryColumn { get; set; }

        public ColumnMapping Clone() => new ColumnMapping(PrimaryColumn, SecondaryColumn);

        public override string ToString() => $"{PrimaryColumn} -> {SecondaryColumn}";
    }

    /// <summary>
    /// Data migrated into a differently named table (SPEC 1, 5.9): the records of <see cref="PrimaryEntity"/>
    /// are compared with the records of <see cref="SecondaryEntity"/> in the secondary, still matched on their
    /// GUID. Columns match by identical logical name; <see cref="Columns"/> lists the pairs whose names differ.
    /// A global list of these lives in the settings (<see cref="CompareOptions.EntityMappings"/>).
    /// XmlSerializer-friendly.
    /// </summary>
    public sealed class EntityMapping
    {
        public EntityMapping()
        {
        }

        public EntityMapping(string primaryEntity, string secondaryEntity, params ColumnMapping[] columns)
        {
            PrimaryEntity = primaryEntity;
            SecondaryEntity = secondaryEntity;
            if (columns != null) Columns.AddRange(columns.Where(c => c != null));
        }

        /// <summary>The entity's logical name in the PRIMARY environment (the entity picked in the tool).</summary>
        public string PrimaryEntity { get; set; }

        /// <summary>The logical name of the table the data went to in the SECONDARY environment.</summary>
        public string SecondaryEntity { get; set; }

        /// <summary>The column pairs whose names differ (the others match by identical logical name).</summary>
        public List<ColumnMapping> Columns { get; set; } = new List<ColumnMapping>();

        /// <summary>Both entity names are set: the mapping can apply.</summary>
        public bool IsComplete() => !string.IsNullOrWhiteSpace(PrimaryEntity) && !string.IsNullOrWhiteSpace(SecondaryEntity);

        /// <summary>True when this mapping is for <paramref name="primaryEntity"/> (case-insensitive, trimmed).</summary>
        public bool AppliesTo(string primaryEntity) =>
            IsComplete() && string.Equals(Normalize(PrimaryEntity), Normalize(primaryEntity), StringComparison.Ordinal);

        /// <summary>
        /// The usable column pairs: both names set, trimmed and lower-cased, the first pair of each primary
        /// column (later pairs of the same primary column are ignored), in their order.
        /// </summary>
        public IList<ColumnMapping> UsableColumns()
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var pairs = new List<ColumnMapping>();
            foreach (ColumnMapping pair in Columns ?? new List<ColumnMapping>())
            {
                if (pair == null || string.IsNullOrWhiteSpace(pair.PrimaryColumn) || string.IsNullOrWhiteSpace(pair.SecondaryColumn)) continue;
                string primary = Normalize(pair.PrimaryColumn);
                if (seen.Add(primary)) pairs.Add(new ColumnMapping(primary, Normalize(pair.SecondaryColumn)));
            }
            return pairs;
        }

        /// <summary>A deep copy.</summary>
        public EntityMapping Clone() =>
            new EntityMapping(PrimaryEntity, SecondaryEntity, (Columns ?? new List<ColumnMapping>()).Where(c => c != null).Select(c => c.Clone()).ToArray());

        /// <summary>
        /// The same mapping with trimmed, lower-case names and only the usable column pairs (what the dialog
        /// saves); null when an entity name is missing.
        /// </summary>
        public EntityMapping Normalized() =>
            IsComplete() ? new EntityMapping(Normalize(PrimaryEntity), Normalize(SecondaryEntity), UsableColumns().ToArray()) : null;

        /// <summary>True when both mappings say the same (names compared normalised, column pairs in order).</summary>
        public static bool SameMapping(EntityMapping a, EntityMapping b)
        {
            if (a == null || b == null) return a == null && b == null;
            if (!string.Equals(Normalize(a.PrimaryEntity), Normalize(b.PrimaryEntity), StringComparison.Ordinal)) return false;
            if (!string.Equals(Normalize(a.SecondaryEntity), Normalize(b.SecondaryEntity), StringComparison.Ordinal)) return false;
            IList<ColumnMapping> first = a.UsableColumns(), second = b.UsableColumns();
            return first.Count == second.Count
                   && first.Zip(second, (x, y) => x.PrimaryColumn == y.PrimaryColumn && x.SecondaryColumn == y.SecondaryColumn).All(same => same);
        }

        /// <summary>A logical name as the mappings compare it: trimmed, lower case ("" for null).</summary>
        public static string Normalize(string name) => (name ?? string.Empty).Trim().ToLowerInvariant();

        public override string ToString() =>
            $"{PrimaryEntity} -> {SecondaryEntity}" + (Columns != null && Columns.Count > 0 ? $" ({Columns.Count} column pair(s))" : string.Empty);
    }

    /// <summary>
    /// An <see cref="EntityMapping"/> resolved against both environments' metadata for one compare run (SPEC 5.9):
    /// which secondary column each primary column is compared with, and the secondary entity's own primary key.
    /// Kept with the result (<see cref="CompareResult.Mapping"/>): re-evaluation never changes it.
    /// </summary>
    public sealed class EntityMap
    {
        private readonly Dictionary<string, string> _pairs;
        private readonly Dictionary<string, string> _counterparts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly object _sync = new object();

        /// <param name="mapping">The mapping (copied, normalised).</param>
        /// <param name="primarySchema">The primary entity's metadata.</param>
        /// <param name="secondarySchema">The SECONDARY entity's metadata (its own primary key and columns).</param>
        public EntityMap(EntityMapping mapping, EntitySchema primarySchema, EntitySchema secondarySchema)
        {
            if (mapping == null) throw new ArgumentNullException(nameof(mapping));
            PrimarySchema = primarySchema ?? throw new ArgumentNullException(nameof(primarySchema));
            SecondarySchema = secondarySchema ?? throw new ArgumentNullException(nameof(secondarySchema));
            Mapping = mapping.Normalized() ?? throw new ArgumentException("The entity mapping needs both entity names.", nameof(mapping));
            _pairs = Mapping.Columns.ToDictionary(c => c.PrimaryColumn, c => c.SecondaryColumn, StringComparer.OrdinalIgnoreCase);
            ColumnPairs = new Dictionary<string, string>(_pairs, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>The mapping as applied (normalised copy).</summary>
        public EntityMapping Mapping { get; }

        public EntitySchema PrimarySchema { get; }

        /// <summary>The SECONDARY entity's metadata.</summary>
        public EntitySchema SecondarySchema { get; }

        public string PrimaryEntity => PrimarySchema.LogicalName;

        public string SecondaryEntity => SecondarySchema.LogicalName;

        public string PrimaryIdAttribute => PrimarySchema.PrimaryIdAttribute;

        /// <summary>The secondary entity's primary key (from the secondary metadata); the records still match on the GUID.</summary>
        public string SecondaryIdAttribute => SecondarySchema.PrimaryIdAttribute;

        /// <summary>The explicit column pairs (primary -> secondary, case-insensitive keys).</summary>
        public IReadOnlyDictionary<string, string> ColumnPairs { get; }

        /// <summary>
        /// Aliases of the view's link-entities on the mapped entity itself (e.g. a parent record of the same
        /// table): the secondary query renamed them too, so their linked columns are read under secondary names.
        /// </summary>
        public IReadOnlyCollection<string> LinkAliases { get; internal set; } = Array.Empty<string>();

        /// <summary>
        /// The secondary column a primary column is compared with and shown against: the secondary primary key
        /// for the primary key; the pair's secondary column for a mapped column; else the same logical name.
        /// Null - no counterpart - when the SECONDARY metadata does not have that column.
        /// </summary>
        public string SecondaryColumn(string primaryColumn)
        {
            if (string.IsNullOrEmpty(primaryColumn)) return null;
            lock (_sync)
            {
                if (_counterparts.TryGetValue(primaryColumn, out string cached)) return cached;
            }
            string candidate = QueryColumn(primaryColumn);
            AttributeSchema attribute = SecondarySchema.Attribute(candidate);
            string counterpart = attribute != null
                ? attribute.LogicalName ?? candidate
                : string.Equals(candidate, SecondaryIdAttribute, StringComparison.OrdinalIgnoreCase) ? SecondaryIdAttribute : null;
            lock (_sync)
            {
                _counterparts[primaryColumn] = counterpart;
            }
            return counterpart;
        }

        /// <summary>
        /// The name a primary column takes in the secondary's view query (SPEC 5.9): the secondary primary key
        /// for the primary key, the pair's secondary column for a mapped column, else the name unchanged (also
        /// when the secondary does not have it: the query then fails and the run falls back to lookups by id).
        /// </summary>
        public string QueryColumn(string primaryColumn)
        {
            if (string.IsNullOrEmpty(primaryColumn)) return primaryColumn;
            if (!string.IsNullOrEmpty(PrimaryIdAttribute) && string.Equals(primaryColumn, PrimaryIdAttribute, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(SecondaryIdAttribute))
            {
                return SecondaryIdAttribute;
            }
            return _pairs.TryGetValue(primaryColumn, out string secondary) ? secondary : primaryColumn;
        }

        /// <summary>The secondary metadata of a primary column's counterpart; null when it has none.</summary>
        public AttributeSchema SecondaryAttribute(string primaryColumn)
        {
            string counterpart = SecondaryColumn(primaryColumn);
            return counterpart == null ? null : SecondarySchema.Attribute(counterpart);
        }

        /// <summary>The column pairs whose secondary column the secondary metadata does not have (never compared).</summary>
        public IList<ColumnMapping> PairsWithoutSecondaryColumn() =>
            Mapping.Columns.Where(c => SecondarySchema.Attribute(c.SecondaryColumn) == null).ToList();

        /// <summary>The column pairs whose primary column the primary metadata does not have (they only rename the view's references).</summary>
        public IList<ColumnMapping> PairsWithoutPrimaryColumn() =>
            Mapping.Columns.Where(c => PrimarySchema.Attribute(c.PrimaryColumn) == null).ToList();

        /// <summary>
        /// True when two attribute types read differently enough to note it (SPEC 5.9): text (String/Memo),
        /// references (Lookup/Customer/Owner) and option sets (Picklist/State/Status) are one family each;
        /// a multi-select, image or file column is its own family; anything else is its type.
        /// </summary>
        public static bool TypesDiffer(AttributeSchema primary, AttributeSchema secondary) =>
            primary != null && secondary != null && !string.Equals(TypeFamily(primary), TypeFamily(secondary), StringComparison.Ordinal);

        internal static string TypeFamily(AttributeSchema attribute)
        {
            if (attribute.IsMultiSelect) return "multiselect";
            if (attribute.IsImage) return "image";
            if (attribute.IsFile) return "file";
            switch (attribute.AttributeType)
            {
                case Microsoft.Xrm.Sdk.Metadata.AttributeTypeCode.String:
                case Microsoft.Xrm.Sdk.Metadata.AttributeTypeCode.Memo:
                    return "text";
                case Microsoft.Xrm.Sdk.Metadata.AttributeTypeCode.Lookup:
                case Microsoft.Xrm.Sdk.Metadata.AttributeTypeCode.Customer:
                case Microsoft.Xrm.Sdk.Metadata.AttributeTypeCode.Owner:
                    return "reference";
                case Microsoft.Xrm.Sdk.Metadata.AttributeTypeCode.Picklist:
                case Microsoft.Xrm.Sdk.Metadata.AttributeTypeCode.State:
                case Microsoft.Xrm.Sdk.Metadata.AttributeTypeCode.Status:
                    return "option";
                default:
                    return attribute.AttributeType.ToString();
            }
        }

        public override string ToString() => $"{PrimaryEntity} -> {SecondaryEntity}";
    }
}
