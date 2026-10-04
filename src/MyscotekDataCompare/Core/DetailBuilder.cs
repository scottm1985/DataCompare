using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using MyscotekDataCompare.Core.Schema;
using MyscotekDataCompare.Core.Services;

namespace MyscotekDataCompare.Core
{
    /// <summary>One line of the detail pane: a column of the selected row in both environments (SPEC 5.8).</summary>
    public sealed class ColumnComparison
    {
        /// <summary>The column's logical name in the primary (for a column only the secondary has: its secondary name).</summary>
        public string LogicalName { get; set; }

        /// <summary>
        /// The secondary column the line reads (SPEC 5.9): the same as <see cref="LogicalName"/> unless the entity is
        /// mapped - then the counterpart (the secondary primary key for the key line, the pair's column for a mapped
        /// column); null when the column has no counterpart in the secondary.
        /// </summary>
        public string SecondaryLogicalName { get; set; }

        /// <summary>The primary metadata's display name; the logical name when there is none.</summary>
        public string DisplayName { get; set; }

        /// <summary>The attribute type from the primary metadata; null for a column the metadata does not have.</summary>
        public AttributeTypeCode? AttributeType { get; set; }

        /// <summary>The primary record's raw value (null when absent, or for an Extra row).</summary>
        public object PrimaryRaw { get; set; }

        /// <summary>The secondary record's raw value (null when absent, or for a Missing row).</summary>
        public object SecondaryRaw { get; set; }

        /// <summary>The primary value as shown (<see cref="CellFormatter.FormatDetail"/>), "" for none.</summary>
        public string PrimaryText { get; set; }

        /// <summary>The secondary value as shown (<see cref="CellFormatter.FormatDetail"/>), "" for none.</summary>
        public string SecondaryText { get; set; }

        /// <summary>The two values differ under the value rules (SPEC 5.6), whether or not the column is compared.</summary>
        public bool IsDifferent { get; set; }

        /// <summary>The column is in the ignored list (shown greyed).</summary>
        public bool IsIgnored { get; set; }

        /// <summary>
        /// The column takes part in the row's status (not the primary key, not ignored, not derived, in the primary
        /// metadata and readable, inside the prefix filter, and with a counterpart for a mapped entity).
        /// </summary>
        public bool IsCompared { get; set; }

        /// <summary>The primary key column (always first, never different).</summary>
        public bool IsPrimaryKey { get; set; }

        /// <summary>A difference that makes the row Different: <see cref="IsCompared"/> and <see cref="IsDifferent"/> (shown amber).</summary>
        public bool IsMismatch => IsCompared && IsDifferent;

        /// <summary>
        /// Why a column is not compared ("Primary key", "Ignored", "Derived from revenue", "Not in the primary
        /// metadata", "Only in the secondary", "Outside the prefix filter", "No counterpart in the secondary"),
        /// or, on a compared line of a mapped entity, "Type differs: ..."; null for any other compared line.
        /// </summary>
        public string Note { get; set; }

        public override string ToString() => $"{LogicalName}: {PrimaryText} | {SecondaryText}{(IsMismatch ? " (different)" : string.Empty)}";
    }

    /// <summary>Builds the detail pane's lines for one row (SPEC 5.8).</summary>
    public static class DetailBuilder
    {
        /// <summary>Notes of the columns that are not compared.</summary>
        public const string PrimaryKeyNote = "Primary key";
        public const string IgnoredNote = "Ignored";
        public const string DerivedNotePrefix = "Derived from ";
        public const string NotInMetadataNote = "Not in the primary metadata";
        public const string OutsidePrefixFilterNote = "Outside the prefix filter";
        public const string NoCounterpartNote = "No counterpart in the secondary";
        public const string SecondaryOnlyNote = "Only in the secondary";

        /// <summary>The note of a compared column of a mapped entity whose two types differ: "Type differs: Lookup in the primary, String in the secondary".</summary>
        public const string TypeDiffersNotePrefix = "Type differs: ";

        /// <summary>
        /// Every column of the row: the primary key first, then by display name (then logical name). Listed
        /// are every attribute of the PRIMARY metadata that is valid for read, a derived attribute
        /// (AttributeOf) only when either record has a value for it, and any other attribute either record
        /// holds that the metadata does not know (never a linked-entity value: '.' in the key or an
        /// AliasedValue). When two different values would read the same (equal texts once white space is
        /// removed), both texts get the raw values appended in brackets (<see cref="ValueComparer.RawText"/>),
        /// e.g. dates whose formatted values hide the seconds, or a trailing space.
        /// </summary>
        public static IList<ColumnComparison> Build(RowComparison row, EntitySchema schema, CompareOptions options) =>
            Build(row, schema, options, null);

        /// <summary>
        /// <see cref="Build(RowComparison, EntitySchema, CompareOptions)"/> for a mapped entity (SPEC 5.9): each
        /// primary column is shown against its counterpart in the secondary entity (<paramref name="map"/>), a
        /// column without one is shown but not compared, and the secondary's own columns that are nobody's
        /// counterpart are listed as "Only in the secondary". Null <paramref name="map"/>: the same entity on both sides.
        /// </summary>
        public static IList<ColumnComparison> Build(RowComparison row, EntitySchema schema, CompareOptions options, EntityMap map)
        {
            if (row == null) throw new ArgumentNullException(nameof(row));
            if (schema == null) throw new ArgumentNullException(nameof(schema));
            return Build(row, new ComparePlan(schema, options ?? new CompareOptions(), map));
        }

        internal static IList<ColumnComparison> Build(RowComparison row, ComparePlan plan)
        {
            EntitySchema schema = plan.Schema;
            EntityMap map = plan.Map;
            var lines = new List<ColumnComparison>();
            var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);    // primary names shown
            var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // secondary names shown (mapped entity)

            foreach (AttributeSchema attribute in (schema.Attributes ?? new Dictionary<string, AttributeSchema>()).Values)
            {
                if (attribute == null || string.IsNullOrEmpty(attribute.LogicalName) || !attribute.IsValidForRead) continue;
                string secondaryName = plan.SecondaryName(attribute.LogicalName);
                if (attribute.AttributeOf != null && !HasValue(row, attribute.LogicalName, secondaryName)) continue;
                listed.Add(attribute.LogicalName);
                if (secondaryName != null) covered.Add(secondaryName);
                lines.Add(Line(row, plan, attribute.LogicalName, secondaryName, attribute));
            }

            // The primary key, even when the metadata does not list it.
            if (!string.IsNullOrEmpty(schema.PrimaryIdAttribute) && listed.Add(schema.PrimaryIdAttribute))
            {
                string secondaryKey = plan.SecondaryName(schema.PrimaryIdAttribute);
                if (secondaryKey != null) covered.Add(secondaryKey);
                lines.Add(Line(row, plan, schema.PrimaryIdAttribute, secondaryKey, null));
            }

            foreach (string name in RecordAttributes(row.Primary))
            {
                if (!listed.Add(name)) continue;
                // Not in the primary metadata: shown against its pair's column or the same name.
                string secondaryName = map == null ? name : map.QueryColumn(name);
                covered.Add(secondaryName);
                lines.Add(Line(row, plan, name, secondaryName, null));
            }

            foreach (string name in RecordAttributes(row.Secondary))
            {
                if (map == null)
                {
                    if (listed.Add(name)) lines.Add(Line(row, plan, name, name, null));
                }
                else if (covered.Add(name))
                {
                    lines.Add(Line(row, plan, null, name, null));   // nobody's counterpart: only in the secondary
                }
            }

            return lines
                .OrderBy(l => l.IsPrimaryKey ? 0 : 1)
                .ThenBy(l => l.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(l => l.LogicalName, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>
        /// One line: <paramref name="primaryName"/> of the primary record (null: a column only the secondary has)
        /// against <paramref name="secondaryName"/> of the secondary record (null: no counterpart).
        /// </summary>
        private static ColumnComparison Line(RowComparison row, ComparePlan plan, string primaryName, string secondaryName, AttributeSchema attribute)
        {
            string name = primaryName ?? secondaryName;
            object primary = primaryName == null ? null : ComparePlan.Value(row.Primary, primaryName);
            object secondary = secondaryName == null ? null : ComparePlan.Value(row.Secondary, secondaryName);
            bool isKey = primaryName != null && plan.IsPrimaryKey(primaryName);
            bool ignored = plan.Options.IsIgnored(name);

            var line = new ColumnComparison
            {
                LogicalName = name,
                SecondaryLogicalName = secondaryName,
                DisplayName = !string.IsNullOrWhiteSpace(attribute?.DisplayName) ? attribute.DisplayName : name,
                AttributeType = attribute?.AttributeType,
                PrimaryRaw = primary,
                SecondaryRaw = secondary,
                PrimaryText = primaryName == null ? string.Empty : CellFormatter.FormatDetail(row.Primary, primaryName),
                SecondaryText = secondaryName == null ? string.Empty : CellFormatter.FormatDetail(row.Secondary, secondaryName),
                IsPrimaryKey = isKey,
                IsIgnored = ignored,
                IsCompared = attribute != null && plan.IsCompared(attribute),
                IsDifferent = !isKey && !ValueComparer.AreEqual(primary, secondary, attribute)
            };

            if (isKey) line.Note = PrimaryKeyNote;
            else if (ignored) line.Note = IgnoredNote;
            else if (attribute == null) line.Note = primaryName == null ? SecondaryOnlyNote : NotInMetadataNote;
            else if (attribute.AttributeOf != null) line.Note = DerivedNotePrefix + attribute.AttributeOf;
            else if (!plan.Options.IsInPrefixFilter(attribute.LogicalName)) line.Note = OutsidePrefixFilterNote;
            else if (secondaryName == null) line.Note = NoCounterpartNote;
            else if (plan.Map != null)
            {
                AttributeSchema secondaryAttribute = plan.Map.SecondarySchema.Attribute(secondaryName);
                if (EntityMap.TypesDiffer(attribute, secondaryAttribute))
                    line.Note = TypeDiffersNotePrefix + attribute.AttributeType + " in the primary, " + secondaryAttribute.AttributeType + " in the secondary";
            }

            if (line.IsDifferent && ReadsTheSame(line.PrimaryText, line.SecondaryText))
            {
                line.PrimaryText = WithRaw(line.PrimaryText, primary);
                line.SecondaryText = WithRaw(line.SecondaryText, secondary);
            }
            return line;
        }

        /// <summary>
        /// True when two texts look alike on screen: equal once every white-space character is removed
        /// (trailing spaces, doubled spaces, CR LF against LF...).
        /// </summary>
        internal static bool ReadsTheSame(string first, string second) =>
            string.Equals(WithoutWhiteSpace(first), WithoutWhiteSpace(second), StringComparison.Ordinal);

        private static string WithoutWhiteSpace(string text) =>
            string.IsNullOrEmpty(text) ? string.Empty : new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray());

        private static string WithRaw(string text, object value)
        {
            string raw = "[" + ValueComparer.RawText(value) + "]";
            return string.IsNullOrEmpty(text) ? raw : text + " " + raw;
        }

        private static bool HasValue(RowComparison row, string primaryName, string secondaryName) =>
            !ValueComparer.IsEmpty(ComparePlan.Value(row.Primary, primaryName))
            || (secondaryName != null && !ValueComparer.IsEmpty(ComparePlan.Value(row.Secondary, secondaryName)));

        /// <summary>The record's own attributes that hold a value: not linked-entity values.</summary>
        private static IEnumerable<string> RecordAttributes(Entity record) =>
            record == null
                ? Enumerable.Empty<string>()
                : record.Attributes
                    .Where(p => !string.IsNullOrEmpty(p.Key) && p.Key.IndexOf('.') < 0 && !(p.Value is AliasedValue) && !ValueComparer.IsEmpty(p.Value))
                    .Select(p => p.Key)
                    .ToList();
    }
}
