using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xrm.Sdk;
using MyscotekDataCompare.Core.Schema;
using MyscotekDataCompare.Core.Services;

namespace MyscotekDataCompare.Core
{
    /// <summary>
    /// The result of <see cref="CompareEngine.Compare"/> (SPEC 5.7): every row with its status, the counts,
    /// and what the UI needs to show a row - the grid's cell text and the detail pane's lines.
    /// </summary>
    public sealed class CompareResult
    {
        public CompareResult(EntitySchema schema, CompareOptions options, List<RowComparison> rows, CompareSummary summary,
                             IReadOnlyDictionary<string, string> columnAliases = null, EntityMap mapping = null)
        {
            Schema = schema ?? throw new ArgumentNullException(nameof(schema));
            Options = (options ?? new CompareOptions()).Clone();
            Rows = rows ?? new List<RowComparison>();
            Summary = summary ?? new CompareSummary();
            ColumnAliases = columnAliases ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Mapping = mapping;
        }

        /// <summary>The compared entity's logical name.</summary>
        public string EntityLogicalName => Schema.LogicalName;

        /// <summary>The primary key the rows are matched on.</summary>
        public string PrimaryIdAttribute => Schema.PrimaryIdAttribute;

        /// <summary>The PRIMARY metadata of the entity (display names, types, what is compared).</summary>
        public EntitySchema Schema { get; }

        /// <summary>A copy of the options the statuses were decided with (editing the original changes nothing here).</summary>
        public CompareOptions Options { get; }

        /// <summary>
        /// The rows, deterministic order: the primary view's rows in its order, then the rows only the
        /// secondary's view returned, in its order. A cancelled run holds only the rows it decided.
        /// </summary>
        public List<RowComparison> Rows { get; }

        public CompareSummary Summary { get; }

        /// <summary>Aliases the view gave main-entity attributes (alias -> attribute), see <see cref="CompareQuery.MainEntityAliases"/>.</summary>
        public IReadOnlyDictionary<string, string> ColumnAliases { get; }

        /// <summary>
        /// The entity mapping the records were read with (SPEC 5.9): the secondary entity, its primary key and
        /// the column counterparts. Null when the secondary has the same entity. <see cref="Reevaluate"/> keeps it:
        /// another mapping needs a new comparison.
        /// </summary>
        public EntityMap Mapping { get; }

        /// <summary>The entity read in the secondary: the mapped table, else the same entity.</summary>
        public string SecondaryEntityLogicalName => Mapping?.SecondaryEntity ?? EntityLogicalName;

        /// <summary>The secondary entity's primary key (the mapped table's own), else the same as <see cref="PrimaryIdAttribute"/>.</summary>
        public string SecondaryPrimaryIdAttribute => Mapping?.SecondaryIdAttribute ?? PrimaryIdAttribute;

        /// <summary>
        /// The grid text of a view column for a row: <see cref="CellFormatter.Format"/> of the row's
        /// <see cref="RowComparison.DisplayRecord"/> (the primary record, or the secondary one for a row only
        /// the secondary's view returned). A column named after an alias of a main-entity attribute shows
        /// that attribute. For a mapped entity a secondary record's cells are read under the columns'
        /// counterparts (SPEC 5.9): blank for a column without one.
        /// </summary>
        public string CellText(RowComparison row, string column)
        {
            Entity record = row?.DisplayRecord;
            if (record == null || string.IsNullOrEmpty(column)) return string.Empty;
            if (Mapping != null && !ReferenceEquals(record, row.Primary))
            {
                column = SecondaryColumnOf(column);
                return column == null ? string.Empty : CellFormatter.Format(record, column);
            }
            if (!record.Attributes.ContainsKey(column) && !record.FormattedValues.ContainsKey(column)
                && ColumnAliases.TryGetValue(column, out string attribute))
            {
                column = attribute;
            }
            return CellFormatter.Format(record, column);
        }

        /// <summary>
        /// A view column (primary names) as the secondary record of a mapped entity holds it: a main-entity
        /// column (or alias) by its counterpart, null when it has none; a linked column of a link on the mapped
        /// entity itself by its attribute's secondary name; any other linked column unchanged.
        /// </summary>
        private string SecondaryColumnOf(string column)
        {
            int dot = column.IndexOf('.');
            if (dot > 0)
            {
                string alias = column.Substring(0, dot);
                return Mapping.LinkAliases.Contains(alias, StringComparer.OrdinalIgnoreCase)
                    ? alias + "." + Mapping.QueryColumn(column.Substring(dot + 1))
                    : column;
            }
            string attribute = ColumnAliases.TryGetValue(column, out string aliased) ? aliased : column;
            return Mapping.SecondaryColumn(attribute);
        }

        /// <summary>The detail pane's lines for a row (<see cref="DetailBuilder.Build(RowComparison, EntitySchema, CompareOptions, EntityMap)"/> with this result's schema, options and mapping).</summary>
        public IList<ColumnComparison> GetDetails(RowComparison row) => DetailBuilder.Build(row, Schema, Options, Mapping);

        /// <summary>
        /// The same rows re-decided with other options (e.g. the user edited the ignored attributes): every
        /// row found in both environments is compared again in memory, nothing is read from either
        /// environment. Missing and Extra rows, the order and every count but Matching and Different are kept.
        /// The ignored attributes and the prefix filter of <paramref name="options"/> apply; its entity mappings
        /// do not - the rows were read with <see cref="Mapping"/>, which the new result keeps.
        /// </summary>
        public CompareResult Reevaluate(CompareOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            var plan = new ComparePlan(Schema, options, Mapping);
            var rows = new List<RowComparison>(Rows.Count);
            CompareSummary summary = Summary.Clone();
            summary.Matching = 0;
            summary.Different = 0;
            foreach (RowComparison row in Rows)
            {
                if (row.Primary == null || row.Secondary == null)
                {
                    rows.Add(row);
                    continue;
                }
                RowComparison decided = Classify(plan, row.Id, row.Primary, row.Secondary, row.InPrimaryView, row.InSecondaryView);
                if (decided.Status == RowStatus.Match) summary.Matching++;
                else summary.Different++;
                rows.Add(decided);
            }
            return new CompareResult(Schema, options, rows, summary, ColumnAliases, Mapping);
        }

        /// <summary>The row of a record found in both environments: Match, or Different with the differing attributes.</summary>
        internal static RowComparison Classify(ComparePlan plan, Guid id, Entity primary, Entity secondary, bool inPrimaryView, bool inSecondaryView)
        {
            IReadOnlyList<string> differing = plan.DifferingAttributes(primary, secondary);
            return new RowComparison(id, differing.Count == 0 ? RowStatus.Match : RowStatus.Different, primary, secondary, differing,
                inPrimaryView, inSecondaryView);
        }

        /// <summary>The rows with the given status, in order.</summary>
        public IEnumerable<RowComparison> RowsWithStatus(RowStatus status) => Rows.Where(r => r.Status == status);
    }
}
