using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using MyscotekDataCompare.Core.Schema;
using MyscotekDataCompare.Core.Services;

namespace MyscotekDataCompare.Core
{
    /// <summary>
    /// Compares the records of a view between the PRIMARY environment (where the data was migrated from)
    /// and the SECONDARY environment (where it was migrated to), matching them on the primary key GUID
    /// (SPEC 5). One instance per run is fine; it holds no state between runs.
    /// <para>
    /// Steps: (1) page the view query - every attribute of the entity, the view's filters, orders and
    /// link-entities - on the primary; (2) page the same query on the secondary; (3) look up by id in the
    /// secondary the primary records its view did not return (found: compared; not found: Missing);
    /// (4) look up by id in the primary the secondary records its view did not return (found: compared;
    /// not found: Extra); (5) compare every pair over the primary metadata's attributes.
    /// </para>
    /// <para>
    /// An entity with an entity mapping (<see cref="CompareOptions.EntityMappings"/>, SPEC 5.9) is read in the
    /// secondary from the mapped table: the view is rewritten for it (<see cref="FetchXmlHelper.MapToSecondary"/>),
    /// the lookups use its own primary key, and each primary column is compared with its counterpart there.
    /// </para>
    /// </summary>
    public sealed class CompareEngine
    {
        /// <summary>Progress is reported (and cancellation checked) every this many compared rows.</summary>
        public const int ProgressEveryRows = 1000;

        /// <summary>How many of the most frequent differing attributes the closing log line names.</summary>
        internal const int FrequentDifferencesLogged = 10;

        private readonly IOrganizationService _primary;
        private readonly IOrganizationService _secondary;
        private readonly ISchemaProvider _primarySchema;
        private readonly ISchemaProvider _secondarySchema;
        private readonly CompareOptions _options;
        private readonly ICompareLogger _logger;

        /// <param name="primary">The environment the data was migrated FROM (the XrmToolBox connection).</param>
        /// <param name="secondary">The environment the data was migrated TO (the additional connection).</param>
        /// <param name="primarySchema">Metadata of the PRIMARY environment: it decides which attributes are compared.</param>
        /// <param name="options">Ignored attributes, page size, lookup batch size; copied at the start of each run. Null: the defaults.</param>
        /// <param name="logger">Receives the log lines; may be null.</param>
        public CompareEngine(IOrganizationService primary, IOrganizationService secondary, ISchemaProvider primarySchema,
                             CompareOptions options, ICompareLogger logger)
            : this(primary, secondary, primarySchema, null, options, logger)
        {
        }

        /// <param name="primary">The environment the data was migrated FROM (the XrmToolBox connection).</param>
        /// <param name="secondary">The environment the data was migrated TO (the additional connection).</param>
        /// <param name="primarySchema">Metadata of the PRIMARY environment: it decides which attributes are compared.</param>
        /// <param name="secondarySchema">
        /// Metadata of the SECONDARY environment, read only for a mapped entity (its table's primary key and
        /// columns). Null: a <see cref="DataverseSchemaProvider"/> over <paramref name="secondary"/> when needed.
        /// </param>
        /// <param name="options">Ignored attributes, prefixes, mappings, page size, lookup batch size; copied at the start of each run. Null: the defaults.</param>
        /// <param name="logger">Receives the log lines; may be null.</param>
        public CompareEngine(IOrganizationService primary, IOrganizationService secondary, ISchemaProvider primarySchema,
                             ISchemaProvider secondarySchema, CompareOptions options, ICompareLogger logger)
        {
            _primary = primary ?? throw new ArgumentNullException(nameof(primary));
            _secondary = secondary ?? throw new ArgumentNullException(nameof(secondary));
            _primarySchema = primarySchema ?? throw new ArgumentNullException(nameof(primarySchema));
            _secondarySchema = secondarySchema;
            _options = options ?? new CompareOptions();
            _logger = logger;
        }

        /// <summary>
        /// Runs the comparison of the records <paramref name="viewFetchXml"/> returns. Never throws for a
        /// cancellation: a cancelled run returns the rows it could decide, with
        /// <see cref="CompareSummary.Cancelled"/> = true and the others counted in
        /// <see cref="CompareSummary.Unchecked"/>. A failing SECONDARY view query is not fatal either (see
        /// <see cref="CompareSummary.SecondaryQueryFailed"/>). Genuine failures - the entity missing from
        /// the primary metadata, invalid or aggregate FetchXML, a failing primary query or by-id lookup -
        /// are logged and thrown.
        /// </summary>
        public CompareResult Compare(string entityLogicalName, string viewFetchXml, IProgress<CompareProgress> progress,
                                     CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(entityLogicalName))
                throw new ArgumentException("An entity logical name is required.", nameof(entityLogicalName));
            if (string.IsNullOrWhiteSpace(viewFetchXml))
                throw new ArgumentException("The view's FetchXML is required.", nameof(viewFetchXml));

            CompareOptions options = _options.Clone();
            options.Validate();
            return new Run(this, options, progress, cancellationToken).Execute(entityLogicalName.Trim(), viewFetchXml);
        }

        /// <summary>One compare run: its state, steps and result.</summary>
        private sealed class Run
        {
            private const string PrimarySide = "Primary";
            private const string SecondarySide = "Secondary";

            private readonly CompareEngine _engine;
            private readonly CompareOptions _options;
            private readonly IProgress<CompareProgress> _progress;
            private readonly CancellationToken _token;
            private readonly Stopwatch _watch = Stopwatch.StartNew();
            private readonly CompareSummary _summary = new CompareSummary();

            private readonly OrderedRecords _primaryView = new OrderedRecords();
            private readonly OrderedRecords _secondaryView = new OrderedRecords();
            private readonly Dictionary<Guid, Entity> _foundInSecondary = new Dictionary<Guid, Entity>();
            private readonly Dictionary<Guid, Entity> _foundInPrimary = new Dictionary<Guid, Entity>();
            private readonly HashSet<Guid> _checkedInSecondary = new HashSet<Guid>();
            private readonly HashSet<Guid> _checkedInPrimary = new HashSet<Guid>();

            private EntitySchema _schema;
            private CompareQuery _query;
            private ComparePlan _plan;

            // The secondary side: the mapped table (SPEC 5.9) or the same entity.
            private EntityMap _map;
            private string _secondaryEntity;
            private string _secondaryIdAttribute;
            private string _secondaryFetchXml;

            public Run(CompareEngine engine, CompareOptions options, IProgress<CompareProgress> progress, CancellationToken token)
            {
                _engine = engine;
                _options = options;
                _progress = progress;
                _token = token;
            }

            public CompareResult Execute(string entity, string fetchXml)
            {
                try
                {
                    Prepare(entity, fetchXml);
                    bool completed = LoadView(_engine._primary, ComparePhase.LoadingPrimary, PrimarySide, _primaryView,
                                              _query.FetchXml, _schema.PrimaryIdAttribute)
                                     && LoadSecondaryView()
                                     && LookUpById(_engine._secondary, ComparePhase.CheckingMissing, SecondarySide,
                                                   _primaryView.Ids.Where(id => !_secondaryView.Contains(id)).ToList(),
                                                   _foundInSecondary, _checkedInSecondary, _secondaryEntity, _secondaryIdAttribute)
                                     && LookUpById(_engine._primary, ComparePhase.CheckingExtras, PrimarySide,
                                                   _secondaryView.Ids.Where(id => !_primaryView.Contains(id)).ToList(),
                                                   _foundInPrimary, _checkedInPrimary, _schema.LogicalName, _schema.PrimaryIdAttribute);
                    return Finish(cancelled: !completed);
                }
                catch (OperationCanceledException) when (_token.IsCancellationRequested && _plan != null)
                {
                    Log(LogLevel.Warning, "Cancelled while waiting for the server.");
                    return Finish(cancelled: true);
                }
                catch (Exception ex)
                {
                    Log(LogLevel.Error, "Compare failed: " + ex.Message);
                    throw;
                }
            }

            /// <summary>
            /// Reads the primary metadata and rewrites the view (SPEC 5.3); for a mapped entity also reads the
            /// secondary table's metadata and rewrites the view for it (SPEC 5.9). Logs the header.
            /// </summary>
            private void Prepare(string entity, string fetchXml)
            {
                _schema = _engine._primarySchema.GetEntity(entity)
                          ?? throw new InvalidOperationException($"The entity {entity} does not exist in the primary environment.");
                if (string.IsNullOrWhiteSpace(_schema.PrimaryIdAttribute))
                    throw new InvalidOperationException($"The primary metadata of {entity} has no primary id attribute.");

                _query = FetchXmlHelper.PrepareCompareQuery(fetchXml, _schema.PrimaryIdAttribute);
                if (!string.Equals(_query.EntityName, entity, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException($"The view's FetchXML is for {_query.EntityName}, not {entity}.", nameof(fetchXml));
                PrepareSecondary(entity);
                _plan = new ComparePlan(_schema, _options, _map);

                var all = (_schema.Attributes ?? new Dictionary<string, AttributeSchema>()).Values.Where(a => a != null).ToList();
                List<string> ignored = all.Where(a => _options.IsIgnored(a.LogicalName)).Select(a => a.LogicalName)
                                          .OrderBy(n => n, StringComparer.Ordinal).ToList();
                int derived = all.Count(a => a.AttributeOf != null && a.IsValidForRead && !_options.IsIgnored(a.LogicalName));
                // What would be compared without the prefix filter and the mapping.
                List<AttributeSchema> candidates = all.Where(a => !string.IsNullOrEmpty(a.LogicalName) && a.IsValidForRead && a.AttributeOf == null
                                                                  && !_plan.IsPrimaryKey(a.LogicalName) && !_options.IsIgnored(a.LogicalName)).ToList();
                int outsidePrefixes = candidates.Count(a => !_options.IsInPrefixFilter(a.LogicalName));
                List<string> withoutCounterpart = _map == null
                    ? new List<string>()
                    : candidates.Where(a => _options.IsInPrefixFilter(a.LogicalName) && _map.SecondaryColumn(a.LogicalName) == null)
                                .Select(a => a.LogicalName).OrderBy(n => n, StringComparer.Ordinal).ToList();
                Log(LogLevel.Info, $"Comparing {_schema.DisplayName} ({_schema.LogicalName}) records matched on {_schema.PrimaryIdAttribute}: " +
                                   $"{_plan.Compared.Count} attributes compared, {ignored.Count} ignored" +
                                   (ignored.Count > 0 ? " (" + string.Join(", ", ignored) + ")" : string.Empty) +
                                   (_options.HasPrefixFilter
                                       ? $", {outsidePrefixes} outside the prefix filter ({string.Join(", ", _options.ComparedPrefixes.Where(p => !string.IsNullOrWhiteSpace(p)))})"
                                       : string.Empty) +
                                   (_map != null ? $", {withoutCounterpart.Count} without a counterpart in {_map.SecondaryEntity}" : string.Empty) +
                                   $", {derived} derived attributes not compared.");
                if (withoutCounterpart.Count > 0)
                    Log(LogLevel.Info, $"Not compared, no counterpart in {_map.SecondaryEntity}: {string.Join(", ", withoutCounterpart)}.");
            }

            /// <summary>
            /// The secondary side of the run: the same entity and query, or - when the entity is mapped - the
            /// mapped table, its own primary key (secondary metadata) and the view rewritten for it.
            /// </summary>
            private void PrepareSecondary(string entity)
            {
                EntityMapping mapping = _options.FindMapping(entity);
                if (mapping == null)
                {
                    _secondaryEntity = _schema.LogicalName;
                    _secondaryIdAttribute = _schema.PrimaryIdAttribute;
                    _secondaryFetchXml = _query.FetchXml;
                    return;
                }

                string target = EntityMapping.Normalize(mapping.SecondaryEntity);
                ISchemaProvider secondarySchema = _engine._secondarySchema ?? new DataverseSchemaProvider(_engine._secondary);
                EntitySchema secondary = secondarySchema.GetEntity(target)
                                         ?? throw new InvalidOperationException($"The entity {target} (mapped from {entity}) does not exist in the secondary environment.");
                if (string.IsNullOrWhiteSpace(secondary.PrimaryIdAttribute))
                    throw new InvalidOperationException($"The secondary metadata of {target} has no primary id attribute.");

                _map = new EntityMap(mapping, _schema, secondary);
                SecondaryQuery query = FetchXmlHelper.MapToSecondary(_query.FetchXml, _query.EntityName, _map.SecondaryEntity, _map.QueryColumn);
                _map.LinkAliases = query.LinkAliases;
                _secondaryEntity = _map.SecondaryEntity;
                _secondaryIdAttribute = _map.SecondaryIdAttribute;
                _secondaryFetchXml = query.FetchXml;

                IList<ColumnMapping> pairs = _map.Mapping.Columns;
                Log(LogLevel.Info, $"Entity mapping: {_schema.LogicalName} is compared with {_map.SecondaryEntity} in the secondary, matched on " +
                                   $"{_schema.PrimaryIdAttribute} = {_map.SecondaryIdAttribute}; " +
                                   (pairs.Count == 0
                                       ? "columns matched by name."
                                       : "columns matched by name except " + string.Join(", ", pairs.Select(p => p.PrimaryColumn + " -> " + p.SecondaryColumn)) + "."));
                IList<ColumnMapping> noTarget = _map.PairsWithoutSecondaryColumn();
                if (noTarget.Count > 0)
                    Log(LogLevel.Warning, $"Entity mapping: {_map.SecondaryEntity} has no column {string.Join(", ", noTarget.Select(p => p.SecondaryColumn))} " +
                                          $"(paired with {string.Join(", ", noTarget.Select(p => p.PrimaryColumn))}): not compared.");
                IList<ColumnMapping> noSource = _map.PairsWithoutPrimaryColumn();
                if (noSource.Count > 0)
                    Log(LogLevel.Warning, $"Entity mapping: {_schema.LogicalName} has no column {string.Join(", ", noSource.Select(p => p.PrimaryColumn))}: " +
                                          "the pair only renames the view's references to it.");
            }

            /// <summary>Step 1 or 2: pages the view query on one side. False when cancelled.</summary>
            private bool LoadView(IOrganizationService service, ComparePhase phase, string side, OrderedRecords into,
                                  string fetchXml, string idAttribute)
            {
                int size = _options.PageSize;
                Report(phase, 0, 0, null, $"{side}: reading the view...");
                Log(LogLevel.Info, $"{side}: reading the view, {size} records a page" +
                                   (_query.IsDistinct ? " by page number (distinct view: no paging cookie)." : "."));
                var watch = Stopwatch.StartNew();
                int page = 0, duplicates = 0, withoutId = 0;
                string cookie = null;
                bool noCookieLogged = false;
                while (true)
                {
                    if (_token.IsCancellationRequested)
                    {
                        Log(LogLevel.Warning, $"{side}: cancelled after {page} page(s), {into.Count} records read.");
                        return false;
                    }
                    page++;
                    RecordPage result = RecordPager.Fetch(service, fetchXml, page, size, _query.IsDistinct ? null : cookie);
                    IList<Entity> entities = (IList<Entity>)result.Entities?.Entities ?? Array.Empty<Entity>();
                    foreach (Entity record in entities)
                    {
                        Guid id = IdOf(record, idAttribute);
                        if (id == Guid.Empty) withoutId++;
                        else if (!into.Add(id, record)) duplicates++;
                    }
                    Report(phase, page, into.Count, null, $"{side}: page {page}, {into.Count} records");
                    Log(LogLevel.Info, $"{side}: page {page}: {entities.Count} rows ({into.Count} records so far).");

                    if (!result.MoreRecords) break;
                    if (entities.Count == 0)
                    {
                        Log(LogLevel.Warning, $"{side}: the server reported more records but returned an empty page {page}; reading stopped there.");
                        break;
                    }
                    if (!_query.IsDistinct && string.IsNullOrEmpty(result.PagingCookie) && !noCookieLogged)
                    {
                        Log(LogLevel.Info, $"{side}: the server returned no paging cookie; the next pages are read by page number.");
                        noCookieLogged = true;
                    }
                    cookie = result.PagingCookie;
                }

                if (duplicates > 0)
                {
                    _summary.DuplicateRowsIgnored += duplicates;
                    Log(LogLevel.Warning, $"{side}: {duplicates} rows repeated a record already read (a 1:N linked entity in the view?); each record is compared once.");
                }
                if (withoutId > 0) Log(LogLevel.Warning, $"{side}: {withoutId} rows without an id were ignored.");
                Log(LogLevel.Info, $"{side}: {into.Count} records in {page} page(s) ({Seconds(watch.Elapsed)}).");
                return true;
            }

            /// <summary>Step 2. A failing secondary query is a warning: the rows read so far are kept (SPEC 5.4).</summary>
            private bool LoadSecondaryView()
            {
                try
                {
                    return LoadView(_engine._secondary, ComparePhase.LoadingSecondary, SecondarySide, _secondaryView,
                                    _secondaryFetchXml, _secondaryIdAttribute);
                }
                catch (Exception ex) when (!(ex is OperationCanceledException && _token.IsCancellationRequested))
                {
                    _summary.SecondaryQueryFailed = true;
                    _summary.SecondaryQueryError = ex.Message;
                    string what = _map == null ? "the view query" : $"the view query rewritten for {_map.SecondaryEntity}";
                    Log(LogLevel.Warning, $"Secondary: {what} failed ({ex.Message}). The {_secondaryView.Count} records read before it failed are kept; " +
                                          "every primary record not among them is looked up by id, but extra records the secondary's view would have returned may be missed.");
                    return true;
                }
            }

            /// <summary>
            /// Step 3 or 4: retrieves <paramref name="ids"/> from <paramref name="service"/> by primary key
            /// (QueryExpression, ConditionOperator.In, ColumnSet(true)), a batch at a time. False when cancelled.
            /// </summary>
            private bool LookUpById(IOrganizationService service, ComparePhase phase, string side, IList<Guid> ids,
                                    Dictionary<Guid, Entity> found, HashSet<Guid> checkedIds, string entity, string idAttribute)
            {
                string what = phase == ComparePhase.CheckingMissing
                    ? "primary records the secondary's view did not return"
                    : "secondary records the primary's view did not return";
                Report(phase, 0, 0, ids.Count, $"{side}: looking up {ids.Count} records by id...");
                if (ids.Count == 0)
                {
                    Log(LogLevel.Info, $"{side}: no {what}.");
                    return true;
                }

                int size = _options.LookupBatchSize;
                int batches = (ids.Count + size - 1) / size;
                Log(LogLevel.Info, $"{side}: looking up by id the {ids.Count} {what} ({batches} quer{(batches == 1 ? "y" : "ies")} of up to {size} ids).");
                var watch = Stopwatch.StartNew();
                int batch = 0;
                for (int start = 0; start < ids.Count; start += size)
                {
                    if (_token.IsCancellationRequested)
                    {
                        Log(LogLevel.Warning, $"{side}: cancelled after {checkedIds.Count} of {ids.Count} ids were looked up.");
                        return false;
                    }
                    batch++;
                    List<Guid> chunk = ids.Skip(start).Take(size).ToList();
                    var wanted = new HashSet<Guid>(chunk);
                    var query = new QueryExpression(entity) { ColumnSet = new ColumnSet(true) };
                    query.Criteria.AddCondition(idAttribute, ConditionOperator.In, chunk.Cast<object>().ToArray());
                    EntityCollection result = service.RetrieveMultiple(query);
                    foreach (Entity record in (IEnumerable<Entity>)result?.Entities ?? Enumerable.Empty<Entity>())
                    {
                        Guid id = IdOf(record, idAttribute);
                        if (wanted.Contains(id) && !found.ContainsKey(id)) found[id] = record;
                    }
                    foreach (Guid id in chunk) checkedIds.Add(id);
                    Report(phase, batch, checkedIds.Count, ids.Count, $"{side}: {checkedIds.Count} of {ids.Count} looked up by id");
                }

                int foundCount = ids.Count(found.ContainsKey);
                Log(LogLevel.Info, $"{side}: {foundCount} of the {ids.Count} found by id, {ids.Count - foundCount} not found ({Seconds(watch.Elapsed)}).");
                return true;
            }

            /// <summary>Step 5 and the result: classifies every row whose status can be decided.</summary>
            private CompareResult Finish(bool cancelled)
            {
                var rows = new List<RowComparison>(_primaryView.Count + _secondaryView.Count);
                List<Guid> secondaryOnly = _secondaryView.Ids.Where(id => !_primaryView.Contains(id)).ToList();
                int total = _primaryView.Count + secondaryOnly.Count;
                int processed = 0, notChecked = 0;
                // A run cancelled before this step still compares the pairs it has (it is quick): the partial
                // result is then as useful as it can be. Cancelling during this step stops it.
                bool stopped = false;
                Report(ComparePhase.Comparing, 0, 0, total, $"Comparing {total} records...");

                void Step()
                {
                    processed++;
                    if (processed % ProgressEveryRows != 0) return;
                    Report(ComparePhase.Comparing, 0, processed, total, $"Compared {processed} of {total} records");
                    if (!cancelled && !stopped && _token.IsCancellationRequested)
                    {
                        stopped = true;
                        Log(LogLevel.Warning, $"Cancelled while comparing, after {processed} of {total} records.");
                    }
                }

                foreach (Guid id in _primaryView.Ids)
                {
                    Entity primary = _primaryView.Get(id);
                    if (stopped) notChecked++;
                    else if (_secondaryView.TryGet(id, out Entity secondary)) rows.Add(CompareResult.Classify(_plan, id, primary, secondary, true, true));
                    else if (_foundInSecondary.TryGetValue(id, out secondary)) rows.Add(CompareResult.Classify(_plan, id, primary, secondary, true, false));
                    else if (_checkedInSecondary.Contains(id)) rows.Add(new RowComparison(id, RowStatus.Missing, primary, null, null, true, false));
                    else notChecked++;
                    Step();
                }
                foreach (Guid id in secondaryOnly)
                {
                    Entity secondary = _secondaryView.Get(id);
                    if (stopped) notChecked++;
                    else if (_foundInPrimary.TryGetValue(id, out Entity primary)) rows.Add(CompareResult.Classify(_plan, id, primary, secondary, false, true));
                    else if (_checkedInPrimary.Contains(id)) rows.Add(new RowComparison(id, RowStatus.Extra, null, secondary, null, false, true));
                    else notChecked++;
                    Step();
                }

                _summary.PrimaryCount = _primaryView.Count;
                _summary.SecondaryCount = _secondaryView.Count;
                _summary.FoundByIdInSecondary = _foundInSecondary.Count;
                _summary.FoundByIdInPrimary = _foundInPrimary.Count;
                _summary.Matching = rows.Count(r => r.Status == RowStatus.Match);
                _summary.Different = rows.Count(r => r.Status == RowStatus.Different);
                _summary.Missing = rows.Count(r => r.Status == RowStatus.Missing);
                _summary.Extra = rows.Count(r => r.Status == RowStatus.Extra);
                _summary.Unchecked = notChecked;
                _summary.Cancelled = cancelled || stopped;
                _summary.Elapsed = _watch.Elapsed;

                LogSummary(rows);
                Report(ComparePhase.Done, 0, rows.Count, rows.Count,
                       (_summary.Cancelled ? "Cancelled: " : "Done: ") + Counts(_summary));
                return new CompareResult(_schema, _options, rows, _summary, _query.MainEntityAliases, _map);
            }

            private void LogSummary(List<RowComparison> rows)
            {
                CompareSummary s = _summary;
                if (s.Cancelled)
                    Log(LogLevel.Warning, $"Cancelled by user: partial result, {s.Unchecked} record(s) not checked.");
                Log(s.Cancelled || s.SecondaryQueryFailed ? LogLevel.Warning : LogLevel.Success, "Result: " + Counts(s) + ".");
                Log(LogLevel.Info, $"Primary view: {s.PrimaryCount} records; secondary view: {s.SecondaryCount} records; " +
                                   $"found by id: {s.FoundByIdInSecondary} in the secondary, {s.FoundByIdInPrimary} in the primary.");

                List<string> frequent = rows
                    .SelectMany(r => r.DifferingAttributes)
                    .GroupBy(a => a, StringComparer.Ordinal)
                    .OrderByDescending(g => g.Count())
                    .ThenBy(g => g.Key, StringComparer.Ordinal)
                    .Take(FrequentDifferencesLogged)
                    .Select(g => $"{g.Key} ({g.Count()})")
                    .ToList();
                if (frequent.Count > 0) Log(LogLevel.Info, "Most frequent differences: " + string.Join(", ", frequent) + ".");
                Log(LogLevel.Info, $"Elapsed: {Seconds(s.Elapsed)}.");
            }

            private static string Counts(CompareSummary s) =>
                $"{s.Total} rows - matching {s.Matching}, different {s.Different}, missing {s.Missing}, extra {s.Extra}" +
                (s.Unchecked > 0 ? $", not checked {s.Unchecked}" : string.Empty);

            /// <summary>A record's id: Entity.Id, else its primary id attribute (the secondary's own for a mapped table).</summary>
            private static Guid IdOf(Entity record, string idAttribute)
            {
                if (record == null) return Guid.Empty;
                if (record.Id != Guid.Empty) return record.Id;
                return record.Attributes.TryGetValue(idAttribute, out object value) && value is Guid id ? id : Guid.Empty;
            }

            private void Report(ComparePhase phase, int page, int soFar, int? total, string message) =>
                _progress?.Report(new CompareProgress { Phase = phase, PageNumber = page, RecordsSoFar = soFar, Total = total, Message = message });

            private void Log(LogLevel level, string message) => _engine._logger?.Log(level, message);

            private static string Seconds(TimeSpan elapsed) => elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";
        }

        /// <summary>Records by id in the order they were first read.</summary>
        private sealed class OrderedRecords
        {
            private readonly Dictionary<Guid, Entity> _records = new Dictionary<Guid, Entity>();
            private readonly List<Guid> _order = new List<Guid>();

            public int Count => _order.Count;

            public IEnumerable<Guid> Ids => _order;

            /// <summary>Adds a record; false (and nothing changes) when the id was read already.</summary>
            public bool Add(Guid id, Entity record)
            {
                if (_records.ContainsKey(id)) return false;
                _records[id] = record;
                _order.Add(id);
                return true;
            }

            public bool Contains(Guid id) => _records.ContainsKey(id);

            public bool TryGet(Guid id, out Entity record) => _records.TryGetValue(id, out record);

            public Entity Get(Guid id) => _records[id];
        }
    }
}
