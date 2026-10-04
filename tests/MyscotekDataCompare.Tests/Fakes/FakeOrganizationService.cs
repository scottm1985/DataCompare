using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.ServiceModel;
using System.Xml.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;

namespace MyscotekDataCompare.Tests.Fakes
{
    /// <summary>
    /// In-memory <see cref="IOrganizationService"/> keyed by (entity, id), trimmed from Data Copier's fake
    /// and extended for Data Compare. Every call - including the direct Create/Retrieve/Update/
    /// RetrieveMultiple methods - is recorded as an <see cref="OrganizationRequest"/> in <see cref="Executed"/>.
    /// <para>
    /// RetrieveMultiple evaluates over the store:
    /// a <see cref="QueryExpression"/> (AND-ed Equal / In / NotNull / Null conditions, orders, TopCount,
    /// PageInfo with a "&lt;cookie page="N" /&gt;" paging cookie), and a <see cref="FetchExpression"/>
    /// through a minimal FetchXML interpreter (<see cref="FakeFetchXml"/>: the main entity with
    /// &lt;all-attributes/&gt; or &lt;attribute&gt;s, nested and/or filters, the common condition operators, orders,
    /// inner/outer link-entities with their attributes returned as AliasedValues under "alias.attribute" and
    /// their filters, distinct, and count/page paging with the same cookie). Seeded FormattedValues are
    /// returned with the attributes they belong to. A <see cref="RetrieveMultipleHandler"/> that returns null
    /// falls through to that evaluation.
    /// </para>
    /// </summary>
    public sealed class FakeOrganizationService : IOrganizationService
    {
        public const int ObjectDoesNotExist = -2147220969;   // 0x80040217
        public const int DuplicateRecord = -2147220937;      // 0x80040237
        public const int GenericFailure = -2147220891;       // 0x80040265

        private readonly Dictionary<(string Entity, Guid Id), Entity> _store = new Dictionary<(string Entity, Guid Id), Entity>();
        private readonly Dictionary<(string Entity, Guid Id), long> _sequence = new Dictionary<(string Entity, Guid Id), long>();
        private long _nextSequence;

        /// <summary>Every request, in order.</summary>
        public List<OrganizationRequest> Executed { get; } = new List<OrganizationRequest>();

        /// <summary>
        /// RetrieveMultiple (QueryExpression or FetchExpression) on one of these entities throws the given
        /// exception, while Retrieve by id still works.
        /// </summary>
        public Dictionary<string, Exception> FailRetrieveMultiple { get; } = new Dictionary<string, Exception>(StringComparer.OrdinalIgnoreCase);

        /// <summary>FetchExpressions whose page number is in this set throw <see cref="FetchFailure"/> (e.g. a failure on page 2).</summary>
        public HashSet<int> FailFetchPages { get; } = new HashSet<int>();

        /// <summary>The exception <see cref="FailFetchPages"/> throws.</summary>
        public Exception FetchFailure { get; set; } = Fault(GenericFailure, "Simulated FetchXML failure");

        /// <summary>Retrieves that throw the given exception: key "entity" (all records of it) or "entity/guid" (one record).</summary>
        public Dictionary<string, Exception> FailRetrieves { get; } = new Dictionary<string, Exception>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Primary id attribute per entity when it is not "{entity}id".</summary>
        public Dictionary<string, string> PrimaryIdAttributes { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["email"] = "activityid", ["task"] = "activityid", ["phonecall"] = "activityid",
            ["appointment"] = "activityid", ["letter"] = "activityid", ["fax"] = "activityid",
            ["activitypointer"] = "activityid"
        };

        /// <summary>
        /// When false, a FetchExpression result carries no paging cookie (some queries come back without one);
        /// paging by page number still works.
        /// </summary>
        public bool ReturnPagingCookies { get; set; } = true;

        /// <summary>Called for every request after it is recorded and before it is processed.</summary>
        public Action<OrganizationRequest> BeforeExecute { get; set; }

        /// <summary>Optional override for any request; return null to fall through to the default handling.</summary>
        public Func<OrganizationRequest, OrganizationResponse> ExecuteHandler { get; set; }

        /// <summary>Optional override for RetrieveMultiple. Returning null falls through to the evaluation over the store.</summary>
        public Func<QueryBase, EntityCollection> RetrieveMultipleHandler { get; set; }

        // ---- seeding and inspection ----

        /// <summary>Seeds a record (cloned, FormattedValues included).</summary>
        public FakeOrganizationService Add(Entity entity)
        {
            if (entity.Id == Guid.Empty) throw new ArgumentException("Seeded records need an id.");
            var key = Key(entity.LogicalName, entity.Id);
            _store[key] = Clone(entity);
            if (!_sequence.ContainsKey(key)) _sequence[key] = _nextSequence++;
            return this;
        }

        public FakeOrganizationService AddRange(IEnumerable<Entity> entities)
        {
            foreach (Entity entity in entities) Add(entity);
            return this;
        }

        public Entity Get(string entity, Guid id) => _store.TryGetValue(Key(entity, id), out Entity stored) ? stored : null;

        public bool Contains(string entity, Guid id) => _store.ContainsKey(Key(entity, id));

        public IReadOnlyList<RetrieveRequest> Retrieves => Executed.OfType<RetrieveRequest>().ToList();

        /// <summary>The QueryExpressions run, in order.</summary>
        public IReadOnlyList<QueryExpression> Queries =>
            Executed.OfType<RetrieveMultipleRequest>().Select(r => r.Query).OfType<QueryExpression>().ToList();

        /// <summary>The FetchXML of every FetchExpression run, in order.</summary>
        public IReadOnlyList<string> Fetches =>
            Executed.OfType<RetrieveMultipleRequest>().Select(r => r.Query).OfType<FetchExpression>().Select(f => f.Query).ToList();

        /// <summary>Create, Update and Delete requests (the compare engine must never issue any).</summary>
        public IReadOnlyList<OrganizationRequest> Writes =>
            Executed.Where(r => r is CreateRequest || r is UpdateRequest || r is DeleteRequest || r is AssociateRequest || r is DisassociateRequest).ToList();

        public static FaultException<OrganizationServiceFault> Fault(int errorCode, string message) =>
            new FaultException<OrganizationServiceFault>(
                new OrganizationServiceFault { ErrorCode = errorCode, Message = message },
                new FaultReason(message));

        // ---- IOrganizationService ----

        public Guid Create(Entity entity) => (Guid)Execute(new CreateRequest { Target = entity }).Results["id"];

        public Entity Retrieve(string entityName, Guid id, ColumnSet columnSet) =>
            (Entity)Execute(new RetrieveRequest { Target = new EntityReference(entityName, id), ColumnSet = columnSet }).Results["Entity"];

        public void Update(Entity entity) => Execute(new UpdateRequest { Target = entity });

        public void Delete(string entityName, Guid id) => Execute(new DeleteRequest { Target = new EntityReference(entityName, id) });

        public EntityCollection RetrieveMultiple(QueryBase query) =>
            (EntityCollection)Execute(new RetrieveMultipleRequest { Query = query }).Results["EntityCollection"];

        public void Associate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities) =>
            Execute(new AssociateRequest { Target = new EntityReference(entityName, entityId), Relationship = relationship, RelatedEntities = relatedEntities });

        public void Disassociate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities) =>
            Execute(new DisassociateRequest { Target = new EntityReference(entityName, entityId), Relationship = relationship, RelatedEntities = relatedEntities });

        public OrganizationResponse Execute(OrganizationRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            Executed.Add(request);
            BeforeExecute?.Invoke(request);

            OrganizationResponse handled = ExecuteHandler?.Invoke(request);
            if (handled != null) return handled;

            switch (request)
            {
                case CreateRequest create: return DoCreate(create.Target);
                case UpdateRequest update: return DoUpdate(update.Target);
                case RetrieveRequest retrieve: return DoRetrieve(retrieve.Target, retrieve.ColumnSet);
                case RetrieveMultipleRequest multiple: return DoRetrieveMultiple(multiple.Query);
                case DeleteRequest delete:
                    if (!_store.Remove(Key(delete.Target.LogicalName, delete.Target.Id))) throw NotFound(delete.Target.LogicalName, delete.Target.Id);
                    return new DeleteResponse();
                default:
                    throw new NotSupportedException($"FakeOrganizationService does not support {request.GetType().Name} ({request.RequestName}).");
            }
        }

        // ---- request handling ----

        private OrganizationResponse DoCreate(Entity target)
        {
            Guid id = target.Id != Guid.Empty ? target.Id : Guid.NewGuid();
            if (_store.ContainsKey(Key(target.LogicalName, id))) throw Fault(DuplicateRecord, "Cannot insert duplicate key.");
            Entity stored = Clone(target);
            stored.Id = id;
            Add(stored);
            var response = new CreateResponse();
            response.Results["id"] = id;
            return response;
        }

        private OrganizationResponse DoUpdate(Entity target)
        {
            if (!_store.TryGetValue(Key(target.LogicalName, target.Id), out Entity stored)) throw NotFound(target.LogicalName, target.Id);
            foreach (KeyValuePair<string, object> pair in target.Attributes) stored[pair.Key] = pair.Value;
            return new UpdateResponse();
        }

        private OrganizationResponse DoRetrieve(EntityReference target, ColumnSet columns)
        {
            if (FailRetrieves.TryGetValue(target.LogicalName + "/" + target.Id.ToString("D"), out Exception failure)
                || FailRetrieves.TryGetValue(target.LogicalName ?? string.Empty, out failure))
            {
                throw failure;
            }
            if (!_store.TryGetValue(Key(target.LogicalName, target.Id), out Entity stored)) throw NotFound(target.LogicalName, target.Id);

            var response = new RetrieveResponse();
            response.Results["Entity"] = Project(stored, columns);
            return response;
        }

        private OrganizationResponse DoRetrieveMultiple(QueryBase query)
        {
            string entityName = query is QueryExpression expression ? expression.EntityName
                : query is FetchExpression fetch ? FakeFetchXml.EntityName(fetch.Query)
                : null;
            if (FailRetrieveMultiple.TryGetValue(entityName ?? string.Empty, out Exception failure)) throw failure;

            EntityCollection collection = RetrieveMultipleHandler?.Invoke(query);
            if (collection == null)
            {
                switch (query)
                {
                    case QueryExpression queryExpression:
                        collection = Evaluate(queryExpression);
                        break;
                    case FetchExpression fetchExpression:
                        int page = FakeFetchXml.PageNumber(fetchExpression.Query);
                        if (FailFetchPages.Contains(page)) throw FetchFailure;
                        collection = new FakeFetchXml(this).Evaluate(fetchExpression.Query);
                        if (!ReturnPagingCookies) collection.PagingCookie = null;
                        break;
                    default:
                        throw new NotSupportedException($"Set RetrieveMultipleHandler to answer a {query?.GetType().Name}.");
                }
            }

            var response = new RetrieveMultipleResponse();
            response.Results["EntityCollection"] = collection;
            return response;
        }

        /// <summary>
        /// Supports AND-ed Equal / In / NotNull / Null conditions, orders (by the stored values; the primary id
        /// by the record id), ColumnSet, TopCount and paging: with PageInfo.Count set, page PageNumber of
        /// that size, MoreRecords, and a paging cookie "&lt;cookie page="N" /&gt;".
        /// </summary>
        private EntityCollection Evaluate(QueryExpression query)
        {
            if (query.Criteria.Filters.Count > 0 || query.LinkEntities.Count > 0 || query.Criteria.FilterOperator != LogicalOperator.And)
                throw new NotSupportedException("Nested filters, OR filters and link-entities are not supported by the fake's QueryExpression evaluation.");

            IEnumerable<Entity> matches = Records(query.EntityName)
                .Where(e => query.Criteria.Conditions.All(c => ConditionMatches(e, c)));
            IOrderedEnumerable<Entity> ordered = null;
            foreach (OrderExpression order in query.Orders)
            {
                Func<Entity, object> key = e => IsPrimaryId(e.LogicalName, order.AttributeName)
                    ? e.Id
                    : e.Attributes.TryGetValue(order.AttributeName, out object value) ? SortValue(value) : null;
                bool descending = order.OrderType == OrderType.Descending;
                ordered = ordered == null
                    ? (descending ? matches.OrderByDescending(key, Comparer<object>.Default) : matches.OrderBy(key, Comparer<object>.Default))
                    : (descending ? ordered.ThenByDescending(key, Comparer<object>.Default) : ordered.ThenBy(key, Comparer<object>.Default));
            }
            List<Entity> rows = (ordered ?? matches).Select(e => Project(e, query.ColumnSet)).ToList();
            if (query.TopCount.HasValue) rows = rows.Take(query.TopCount.Value).ToList();

            PagingInfo paging = query.PageInfo;
            if (paging == null || paging.Count <= 0) return new EntityCollection(rows) { EntityName = query.EntityName };
            int page = Math.Max(1, paging.PageNumber);
            return new EntityCollection(rows.Skip((page - 1) * paging.Count).Take(paging.Count).ToList())
            {
                EntityName = query.EntityName,
                MoreRecords = rows.Count > page * paging.Count,
                PagingCookie = $"<cookie page=\"{page}\" />"
            };
        }

        /// <summary>The stored records of an entity, in the order they were first seeded or created.</summary>
        internal IEnumerable<Entity> Records(string entity) =>
            _store
                .Where(p => string.Equals(p.Key.Entity, (entity ?? string.Empty).ToLowerInvariant(), StringComparison.Ordinal))
                .OrderBy(p => _sequence.TryGetValue(p.Key, out long sequence) ? sequence : long.MaxValue)
                .Select(p => p.Value);

        internal static object SortValue(object value)
        {
            switch (value)
            {
                case EntityReference reference: return reference.Id;
                case OptionSetValue option: return option.Value;
                case Money money: return money.Value;
                default: return value;
            }
        }

        private bool ConditionMatches(Entity entity, ConditionExpression condition)
        {
            object actual = IsPrimaryId(entity.LogicalName, condition.AttributeName)
                ? entity.Id
                : entity.Attributes.TryGetValue(condition.AttributeName, out object value) ? value : null;

            switch (condition.Operator)
            {
                case ConditionOperator.Equal:
                    return condition.Values.Count > 0 && ValuesEqual(actual, condition.Values[0]);
                case ConditionOperator.In:
                    return condition.Values.Any(v => ValuesEqual(actual, v));
                case ConditionOperator.NotNull:
                    return actual != null;
                case ConditionOperator.Null:
                    return actual == null;
                default:
                    throw new NotSupportedException($"Condition operator {condition.Operator} is not supported by the fake.");
            }
        }

        private static bool ValuesEqual(object actual, object expected)
        {
            switch (actual)
            {
                case null: return expected == null;
                case EntityReference reference: return Equals(reference.Id, expected);
                case OptionSetValue option: return Equals(option.Value, expected);
                case string text: return string.Equals(text, expected as string, StringComparison.OrdinalIgnoreCase);
                default: return Equals(actual, expected);
            }
        }

        internal Entity Project(Entity stored, ColumnSet columns)
        {
            var copy = new Entity(stored.LogicalName) { Id = stored.Id };
            if (columns == null || columns.AllColumns)
            {
                foreach (KeyValuePair<string, object> pair in stored.Attributes) copy[pair.Key] = pair.Value;
                foreach (KeyValuePair<string, string> pair in stored.FormattedValues) copy.FormattedValues[pair.Key] = pair.Value;
                return copy;
            }
            foreach (string column in columns.Columns)
            {
                if (stored.Attributes.TryGetValue(column, out object value)) copy[column] = value;
                else if (IsPrimaryId(stored.LogicalName, column)) copy[column] = stored.Id;
                if (stored.FormattedValues.TryGetValue(column, out string formatted)) copy.FormattedValues[column] = formatted;
            }
            return copy;
        }

        internal string PrimaryIdOf(string entity) => PrimaryIdAttributes.TryGetValue(entity ?? string.Empty, out string mapped) ? mapped : entity + "id";

        internal bool IsPrimaryId(string entity, string attribute) =>
            string.Equals(attribute, PrimaryIdOf(entity), StringComparison.OrdinalIgnoreCase);

        private static (string Entity, Guid Id) Key(string entity, Guid id) => ((entity ?? string.Empty).ToLowerInvariant(), id);

        private static FaultException<OrganizationServiceFault> NotFound(string entity, Guid id) =>
            Fault(ObjectDoesNotExist, $"Entity '{entity}' With Id = {id} Does Not Exist");

        internal static Entity Clone(Entity entity)
        {
            var copy = new Entity(entity.LogicalName) { Id = entity.Id };
            foreach (KeyValuePair<string, object> pair in entity.Attributes) copy[pair.Key] = pair.Value;
            foreach (KeyValuePair<string, string> pair in entity.FormattedValues) copy.FormattedValues[pair.Key] = pair.Value;
            return copy;
        }
    }

    /// <summary>
    /// A minimal FetchXML interpreter over a <see cref="FakeOrganizationService"/>'s store - enough for the
    /// queries Data Compare runs: the main &lt;entity&gt; with &lt;all-attributes/&gt; or &lt;attribute&gt;s (an
    /// aliased one is returned as an AliasedValue under its alias), &lt;filter type="and|or"&gt; (nested) with
    /// &lt;condition attribute operator value&gt; (eq, ne/neq, null, not-null, in/not-in with &lt;value&gt;s, like/
    /// not-like with %, gt/ge/lt/le; entityname="alias" for a linked record), &lt;order attribute descending&gt;
    /// on the main entity, &lt;link-entity name from to alias link-type="inner|outer"&gt; (nested, any
    /// cardinality: a 1:N link repeats the main row) with its attributes as AliasedValues under
    /// "alias.attribute" and its own filters, distinct="true" (identical rows once), and count/page paging
    /// with MoreRecords and a "&lt;cookie page="N" /&gt;" paging cookie. Everything else throws
    /// NotSupportedException, so a test cannot pass by accident.
    /// </summary>
    public sealed class FakeFetchXml
    {
        private readonly FakeOrganizationService _service;

        public FakeFetchXml(FakeOrganizationService service)
        {
            _service = service;
        }

        public static string EntityName(string fetchXml) =>
            (string)XElement.Parse(fetchXml).Elements().FirstOrDefault(e => e.Name.LocalName == "entity")?.Attribute("name");

        public static int PageNumber(string fetchXml) =>
            int.TryParse((string)XElement.Parse(fetchXml).Attribute("page"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int page) ? page : 1;

        /// <summary>A joined row: the main record and the linked record (or null for an outer link without a match) per link alias.</summary>
        private sealed class Row
        {
            public Entity Main;
            public Dictionary<string, Entity> Linked = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase);

            public Row With(string alias, Entity linked)
            {
                var copy = new Row { Main = Main, Linked = new Dictionary<string, Entity>(Linked, StringComparer.OrdinalIgnoreCase) };
                copy.Linked[alias] = linked;
                return copy;
            }
        }

        public EntityCollection Evaluate(string fetchXml)
        {
            XElement fetch = XElement.Parse(fetchXml);
            if (fetch.Name.LocalName != "fetch") throw new NotSupportedException("The FetchXML root must be <fetch>.");
            if (string.Equals((string)fetch.Attribute("aggregate"), "true", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("Aggregate FetchXML is not supported by the fake.");
            XElement entity = fetch.Elements().Single(e => e.Name.LocalName == "entity");
            string entityName = (string)entity.Attribute("name");

            foreach (XElement child in entity.Elements())
            {
                switch (child.Name.LocalName)
                {
                    case "attribute": case "all-attributes": case "filter": case "order": case "link-entity": break;
                    default: throw new NotSupportedException($"<{child.Name.LocalName}> is not supported by the fake.");
                }
            }

            List<Row> rows = _service.Records(entityName).Select(e => new Row { Main = e }).ToList();
            rows = Join(rows, entity, entityName, null);
            rows = rows.Where(r => entity.Elements().Where(e => e.Name.LocalName == "filter").All(f => FilterMatches(r, f, null))).ToList();

            // Orders on the main entity (link-entity orders are not supported: they would be ignored silently otherwise).
            if (entity.Descendants().Any(e => e.Name.LocalName == "order" && e.Parent != entity))
                throw new NotSupportedException("Orders inside link-entities are not supported by the fake.");
            IOrderedEnumerable<Row> ordered = null;
            foreach (XElement order in entity.Elements().Where(e => e.Name.LocalName == "order"))
            {
                string attribute = (string)order.Attribute("attribute");
                if (string.IsNullOrEmpty(attribute)) throw new NotSupportedException("<order alias> is not supported by the fake.");
                bool descending = string.Equals((string)order.Attribute("descending"), "true", StringComparison.OrdinalIgnoreCase);
                Func<Row, object> key = r => _service.IsPrimaryId(r.Main.LogicalName, attribute)
                    ? r.Main.Id
                    : r.Main.Attributes.TryGetValue(attribute, out object value) ? FakeOrganizationService.SortValue(value) : null;
                ordered = ordered == null
                    ? (descending ? rows.OrderByDescending(key, Comparer<object>.Default) : rows.OrderBy(key, Comparer<object>.Default))
                    : (descending ? ordered.ThenByDescending(key, Comparer<object>.Default) : ordered.ThenBy(key, Comparer<object>.Default));
            }

            List<Entity> results = (ordered ?? (IEnumerable<Row>)rows).Select(r => Project(r, entity)).ToList();
            if (string.Equals((string)fetch.Attribute("distinct"), "true", StringComparison.OrdinalIgnoreCase))
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                results = results.Where(e => seen.Add(Signature(e))).ToList();
            }
            if (int.TryParse((string)fetch.Attribute("top"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int top))
                results = results.Take(top).ToList();

            if (!int.TryParse((string)fetch.Attribute("count"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int count) || count <= 0)
                return new EntityCollection(results) { EntityName = entityName };
            int page = PageNumber(fetchXml);
            return new EntityCollection(results.Skip((page - 1) * count).Take(count).ToList())
            {
                EntityName = entityName,
                MoreRecords = results.Count > page * count,
                PagingCookie = $"<cookie page=\"{page}\" />"
            };
        }

        /// <summary>Joins the link-entities under <paramref name="parent"/> (recursively) onto every row.</summary>
        private List<Row> Join(List<Row> rows, XElement parent, string parentEntity, string parentAlias)
        {
            foreach (XElement link in parent.Elements().Where(e => e.Name.LocalName == "link-entity"))
            {
                string name = (string)link.Attribute("name");
                string from = (string)link.Attribute("from");
                string to = (string)link.Attribute("to");
                string alias = (string)link.Attribute("alias") ?? name;
                string type = (string)link.Attribute("link-type") ?? "inner";
                if (type != "inner" && type != "outer") throw new NotSupportedException($"link-type=\"{type}\" is not supported by the fake.");
                List<Entity> candidates = _service.Records(name).ToList();
                List<XElement> filters = link.Elements().Where(e => e.Name.LocalName == "filter").ToList();

                var joined = new List<Row>();
                foreach (Row row in rows)
                {
                    Entity parentRecord = parentAlias == null ? row.Main : row.Linked[parentAlias];
                    List<Entity> matches = parentRecord == null
                        ? new List<Entity>()
                        : candidates.Where(c => Same(Value(c, from), Value(parentRecord, to))).ToList();
                    List<Row> extended = matches.Select(m => row.With(alias, m))
                        .Where(r => filters.All(f => FilterMatches(r, f, alias)))
                        .ToList();
                    if (extended.Count > 0) joined.AddRange(extended);
                    else if (type == "outer") joined.Add(row.With(alias, null));
                }
                rows = Join(joined, link, name, alias);
            }
            return rows;
        }

        private object Value(Entity record, string attribute)
        {
            if (record == null || string.IsNullOrEmpty(attribute)) return null;
            if (_service.IsPrimaryId(record.LogicalName, attribute)) return record.Id;
            return record.Attributes.TryGetValue(attribute, out object value) ? value : null;
        }

        private static bool Same(object first, object second)
        {
            Guid? a = AsGuid(first), b = AsGuid(second);
            return a.HasValue && b.HasValue && a.Value == b.Value;
        }

        private static Guid? AsGuid(object value)
        {
            switch (value)
            {
                case Guid id: return id;
                case EntityReference reference: return reference.Id;
                default: return null;
            }
        }

        private bool FilterMatches(Row row, XElement filter, string alias)
        {
            bool or = string.Equals((string)filter.Attribute("type"), "or", StringComparison.OrdinalIgnoreCase);
            var results = new List<bool>();
            foreach (XElement child in filter.Elements())
            {
                if (child.Name.LocalName == "filter") results.Add(FilterMatches(row, child, alias));
                else if (child.Name.LocalName == "condition") results.Add(ConditionMatches(row, child, alias));
                else throw new NotSupportedException($"<{child.Name.LocalName}> inside <filter> is not supported by the fake.");
            }
            return results.Count == 0 || (or ? results.Any(r => r) : results.All(r => r));
        }

        private bool ConditionMatches(Row row, XElement condition, string alias)
        {
            string entityAlias = (string)condition.Attribute("entityname") ?? alias;
            Entity record = entityAlias == null ? row.Main : row.Linked.TryGetValue(entityAlias, out Entity linked) ? linked : null;
            object actual = Value(record, (string)condition.Attribute("attribute"));
            string op = ((string)condition.Attribute("operator") ?? "eq").ToLowerInvariant();
            List<string> values = condition.Attribute("value") != null
                ? new List<string> { (string)condition.Attribute("value") }
                : condition.Elements().Where(e => e.Name.LocalName == "value").Select(e => e.Value).ToList();
            string text = Text(actual);

            switch (op)
            {
                case "eq": return actual != null && values.Any(v => SameText(text, v));
                case "ne": case "neq": return actual == null || !values.Any(v => SameText(text, v));
                case "null": return actual == null;
                case "not-null": return actual != null;
                case "in": return actual != null && values.Any(v => SameText(text, v));
                case "not-in": return actual == null || !values.Any(v => SameText(text, v));
                case "like": return actual != null && Like(text, values[0]);
                case "not-like": return actual == null || !Like(text, values[0]);
                case "gt": return actual != null && Compare(actual, values[0]) > 0;
                case "ge": return actual != null && Compare(actual, values[0]) >= 0;
                case "lt": return actual != null && Compare(actual, values[0]) < 0;
                case "le": return actual != null && Compare(actual, values[0]) <= 0;
                default: throw new NotSupportedException($"Condition operator \"{op}\" is not supported by the fake.");
            }
        }

        /// <summary>A value as FetchXML compares it: ids without braces, option values, 1/0 for booleans.</summary>
        private static string Text(object value)
        {
            switch (value)
            {
                case null: return null;
                case EntityReference reference: return reference.Id.ToString("D");
                case Guid id: return id.ToString("D");
                case OptionSetValue option: return option.Value.ToString(CultureInfo.InvariantCulture);
                case Money money: return money.Value.ToString(CultureInfo.InvariantCulture);
                case bool flag: return flag ? "1" : "0";
                case DateTime date: return date.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
                case IFormattable formattable: return formattable.ToString(null, CultureInfo.InvariantCulture);
                default: return value.ToString();
            }
        }

        private static bool SameText(string actual, string expected)
        {
            if (actual == null) return false;
            if (Guid.TryParse(actual, out Guid a) && Guid.TryParse(expected, out Guid b)) return a == b;
            if (expected == "true") expected = "1";
            if (expected == "false") expected = "0";
            return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
        }

        private static bool Like(string actual, string pattern)
        {
            string regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("%", ".*").Replace("_", ".") + "$";
            return System.Text.RegularExpressions.Regex.IsMatch(actual ?? string.Empty, regex, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        private static int Compare(object actual, string expected)
        {
            switch (FakeOrganizationService.SortValue(actual))
            {
                case int number: return number.CompareTo(int.Parse(expected, CultureInfo.InvariantCulture));
                case decimal number: return number.CompareTo(decimal.Parse(expected, CultureInfo.InvariantCulture));
                case DateTime date: return date.CompareTo(DateTime.Parse(expected, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal));
                default: return string.Compare(Text(actual), expected, StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>The returned entity of a row: the main entity's attributes (all or listed) and the linked ones as AliasedValues.</summary>
        private Entity Project(Row row, XElement entity)
        {
            var result = new Entity(row.Main.LogicalName) { Id = row.Main.Id };
            bool all = entity.Elements().Any(e => e.Name.LocalName == "all-attributes");
            if (all)
            {
                foreach (KeyValuePair<string, object> pair in row.Main.Attributes) result[pair.Key] = pair.Value;
                foreach (KeyValuePair<string, string> pair in row.Main.FormattedValues) result.FormattedValues[pair.Key] = pair.Value;
                result[_service.PrimaryIdOf(row.Main.LogicalName)] = row.Main.Id;
            }
            foreach (XElement attribute in entity.Elements().Where(e => e.Name.LocalName == "attribute"))
            {
                string name = (string)attribute.Attribute("name");
                string alias = (string)attribute.Attribute("alias");
                object value = Value(row.Main, name);
                if (value == null) continue;
                if (!string.IsNullOrEmpty(alias))
                {
                    result[alias] = new AliasedValue(row.Main.LogicalName, name, value);
                }
                else
                {
                    result[name] = value;
                    if (row.Main.FormattedValues.TryGetValue(name, out string formatted)) result.FormattedValues[name] = formatted;
                }
            }
            AddLinked(result, row, entity);
            return result;
        }

        private void AddLinked(Entity result, Row row, XElement parent)
        {
            foreach (XElement link in parent.Elements().Where(e => e.Name.LocalName == "link-entity"))
            {
                string name = (string)link.Attribute("name");
                string alias = (string)link.Attribute("alias") ?? name;
                Entity linked = row.Linked.TryGetValue(alias, out Entity found) ? found : null;
                if (linked != null)
                {
                    IEnumerable<string> names = link.Elements().Any(e => e.Name.LocalName == "all-attributes")
                        ? linked.Attributes.Keys.ToList()
                        : link.Elements().Where(e => e.Name.LocalName == "attribute").Select(e => (string)e.Attribute("name"));
                    foreach (XElement attribute in link.Elements().Where(e => e.Name.LocalName == "attribute" && e.Attribute("alias") != null))
                    {
                        object value = Value(linked, (string)attribute.Attribute("name"));
                        if (value != null) result[(string)attribute.Attribute("alias")] = new AliasedValue(name, (string)attribute.Attribute("name"), value);
                    }
                    foreach (string attributeName in names.Where(n => !link.Elements().Any(e => e.Name.LocalName == "attribute" && (string)e.Attribute("name") == n && e.Attribute("alias") != null)))
                    {
                        object value = Value(linked, attributeName);
                        if (value == null) continue;
                        result[alias + "." + attributeName] = new AliasedValue(name, attributeName, value);
                        if (linked.FormattedValues.TryGetValue(attributeName, out string formatted)) result.FormattedValues[alias + "." + attributeName] = formatted;
                    }
                }
                AddLinked(result, row, link);
            }
        }

        /// <summary>A row's identity for distinct: the id and every value (raw, invariant).</summary>
        private static string Signature(Entity entity) =>
            entity.Id.ToString("D") + "|" + string.Join("|", entity.Attributes.OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => p.Key + "=" + Text(p.Value is AliasedValue aliased ? aliased.Value : p.Value)));
    }
}
