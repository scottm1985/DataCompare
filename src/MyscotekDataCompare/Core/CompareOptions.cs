using System;
using System.Collections.Generic;
using System.Linq;

namespace MyscotekDataCompare.Core
{
    /// <summary>Options for one <see cref="CompareEngine"/> run (SPEC 5.1).</summary>
    public sealed class CompareOptions
    {
        /// <summary>Records per page of the view query (the largest page Dataverse returns).</summary>
        public const int DefaultPageSize = 5000;

        /// <summary>The largest page size Dataverse accepts for a FetchXML query.</summary>
        public const int MaxPageSize = 5000;

        /// <summary>Ids per by-id lookup query (SPEC 5.4 steps 3 and 4).</summary>
        public const int DefaultLookupBatchSize = 500;

        /// <summary>The largest by-id batch: every id is a SQL parameter, and SQL Server takes about 2100 per query.</summary>
        public const int MaxLookupBatchSize = 2000;

        /// <summary>
        /// The attributes ignored by default (owner decision, SPEC 1): audit columns, the owner and owning
        /// columns, versionnumber, the time-zone/import bookkeeping columns and the record's currency
        /// (transactioncurrencyid: currency records are usually created afresh in each environment, so the
        /// lookup points at a different GUID by design). They are still shown in the detail pane (greyed), but
        /// a difference in them never makes a row Different.
        /// </summary>
        public static readonly IReadOnlyList<string> DefaultIgnoredAttributes = new[]
        {
            "createdon", "createdby", "createdonbehalfby", "modifiedon", "modifiedby", "modifiedonbehalfby",
            "overriddencreatedon", "versionnumber", "ownerid", "owninguser", "owningteam", "owningbusinessunit",
            "timezoneruleversionnumber", "utcconversiontimezonecode", "importsequencenumber", "transactioncurrencyid"
        };

        /// <summary><see cref="DefaultIgnoredAttributes"/> in the settings format (comma-separated).</summary>
        public static string DefaultIgnoredAttributeList => string.Join(",", DefaultIgnoredAttributes);

        public CompareOptions()
        {
            IgnoredAttributes = new HashSet<string>(DefaultIgnoredAttributes, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Attribute logical names that never make a row Different (case-insensitive). Defaults to
        /// <see cref="DefaultIgnoredAttributes"/>.
        /// </summary>
        public HashSet<string> IgnoredAttributes { get; }

        /// <summary>Records per page of the view query, 1..<see cref="MaxPageSize"/>. Default 5000.</summary>
        public int PageSize { get; set; } = DefaultPageSize;

        /// <summary>Ids per by-id lookup query (QueryExpression with ConditionOperator.In), 1..<see cref="MaxLookupBatchSize"/>. Default 500.</summary>
        public int LookupBatchSize { get; set; } = DefaultLookupBatchSize;

        /// <summary>
        /// Only the primary attributes whose logical name starts with one of these prefixes are compared
        /// (case-insensitive, SPEC 5.5); the others are shown in the detail pane, greyed. Empty (the default):
        /// every attribute is compared. The ignored list still applies on top.
        /// </summary>
        public List<string> ComparedPrefixes { get; } = new List<string>();

        /// <summary>
        /// The global entity mappings (SPEC 5.9): an entity with a mapping is compared with the mapped table of
        /// the secondary. Applied by <see cref="CompareEngine"/> only; <see cref="CompareResult.Reevaluate"/> keeps
        /// the mapping its result was read with.
        /// </summary>
        public List<EntityMapping> EntityMappings { get; } = new List<EntityMapping>();

        /// <summary>True when <paramref name="attribute"/> is in <see cref="IgnoredAttributes"/>.</summary>
        public bool IsIgnored(string attribute) => !string.IsNullOrEmpty(attribute) && IgnoredAttributes.Contains(attribute);

        /// <summary>True when <see cref="ComparedPrefixes"/> holds at least one (non-blank) prefix.</summary>
        public bool HasPrefixFilter => ComparedPrefixes.Any(p => !string.IsNullOrWhiteSpace(p));

        /// <summary>
        /// True when <paramref name="attribute"/> passes the prefix filter: there is none, or the name starts with
        /// one of <see cref="ComparedPrefixes"/> (case-insensitive).
        /// </summary>
        public bool IsInPrefixFilter(string attribute)
        {
            if (!HasPrefixFilter) return true;
            if (string.IsNullOrEmpty(attribute)) return false;
            foreach (string prefix in ComparedPrefixes)
            {
                if (!string.IsNullOrWhiteSpace(prefix) && attribute.StartsWith(prefix.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>
        /// Replaces <see cref="ComparedPrefixes"/> with the entries of a comma/semicolon/whitespace separated list
        /// (the settings format; trimmed, lower-cased, without duplicates). Null or blank: no prefix filter.
        /// </summary>
        public void SetComparedPrefixes(string list)
        {
            ComparedPrefixes.Clear();
            ComparedPrefixes.AddRange(ParseAttributeList(list));
        }

        /// <summary>A prefix list in the settings format: comma-separated, lower-case, in the order given.</summary>
        public static string FormatPrefixList(IEnumerable<string> prefixes) =>
            string.Join(",", ParseAttributeList(string.Join(",", prefixes ?? Enumerable.Empty<string>())));

        /// <summary>
        /// The first complete mapping of <paramref name="primaryEntity"/> in <see cref="EntityMappings"/>; null
        /// when the entity is not mapped.
        /// </summary>
        public EntityMapping FindMapping(string primaryEntity) =>
            string.IsNullOrWhiteSpace(primaryEntity) ? null : EntityMappings.FirstOrDefault(m => m != null && m.AppliesTo(primaryEntity));

        /// <summary>
        /// Replaces <see cref="IgnoredAttributes"/> with the entries of a comma/semicolon/whitespace separated
        /// list (the settings format). Entries are trimmed and lower-cased. NULL restores the defaults; an
        /// empty or blank list means "ignore nothing" (the user cleared the list on purpose).
        /// </summary>
        public void SetIgnoredAttributes(string list)
        {
            IgnoredAttributes.Clear();
            IEnumerable<string> names = list == null ? DefaultIgnoredAttributes : ParseAttributeList(list);
            foreach (string name in names) IgnoredAttributes.Add(name);
        }

        /// <summary>
        /// The entries of a comma/semicolon/whitespace separated attribute list, trimmed, lower-cased,
        /// without duplicates, in their first-seen order. Null or blank gives an empty list.
        /// </summary>
        public static IList<string> ParseAttributeList(string list)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var names = new List<string>();
            foreach (string part in (list ?? string.Empty).Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string name = part.Trim().ToLowerInvariant();
                if (name.Length > 0 && seen.Add(name)) names.Add(name);
            }
            return names;
        }

        /// <summary>An attribute list in the settings format: comma-separated, sorted, lower-case.</summary>
        public static string FormatAttributeList(IEnumerable<string> names) =>
            string.Join(",", (names ?? Enumerable.Empty<string>())
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n.Trim().ToLowerInvariant())
                .Distinct(StringComparer.Ordinal)
                .OrderBy(n => n, StringComparer.Ordinal));

        /// <summary>A deep copy (the engine keeps one with each result, so later edits do not change it): the ignored list, the prefixes and the mappings too.</summary>
        public CompareOptions Clone()
        {
            var copy = new CompareOptions { PageSize = PageSize, LookupBatchSize = LookupBatchSize };
            copy.IgnoredAttributes.Clear();
            foreach (string name in IgnoredAttributes) copy.IgnoredAttributes.Add(name);
            copy.ComparedPrefixes.AddRange(ComparedPrefixes);
            copy.EntityMappings.AddRange(EntityMappings.Where(m => m != null).Select(m => m.Clone()));
            return copy;
        }

        /// <summary>Throws when <see cref="PageSize"/> or <see cref="LookupBatchSize"/> is out of range.</summary>
        internal void Validate()
        {
            if (PageSize < 1 || PageSize > MaxPageSize)
                throw new ArgumentOutOfRangeException(nameof(PageSize), PageSize, $"The page size must be between 1 and {MaxPageSize}.");
            if (LookupBatchSize < 1 || LookupBatchSize > MaxLookupBatchSize)
                throw new ArgumentOutOfRangeException(nameof(LookupBatchSize), LookupBatchSize, $"The lookup batch size must be between 1 and {MaxLookupBatchSize}.");
        }
    }
}
