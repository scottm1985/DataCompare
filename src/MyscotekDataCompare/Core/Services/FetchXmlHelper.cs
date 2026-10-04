using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace MyscotekDataCompare.Core.Services
{
    /// <summary>
    /// The view query of a compare run (SPEC 5.3): the view's FetchXML rewritten to return every attribute
    /// of the main entity, without paging attributes. Built by <see cref="FetchXmlHelper.PrepareCompareQuery"/>.
    /// </summary>
    public sealed class CompareQuery
    {
        internal CompareQuery(string entityName, string fetchXml, bool isDistinct, IReadOnlyDictionary<string, string> mainEntityAliases)
        {
            EntityName = entityName;
            FetchXml = fetchXml;
            IsDistinct = isDistinct;
            MainEntityAliases = mainEntityAliases;
        }

        /// <summary>The main entity of the fetch (as written in the view).</summary>
        public string EntityName { get; }

        /// <summary>The rewritten FetchXML, without count/page/paging-cookie/top/returntotalrecordcount.</summary>
        public string FetchXml { get; }

        /// <summary>
        /// True for a distinct="true" view: it is paged by page number WITHOUT a paging cookie (Dataverse
        /// paging cookies are not reliable with distinct), and ordered by the primary key last so the pages
        /// are stable.
        /// </summary>
        public bool IsDistinct { get; }

        /// <summary>
        /// Aliases the view gave main-entity attributes (alias -> attribute logical name, case-insensitive
        /// keys). Those &lt;attribute&gt; elements are replaced by &lt;all-attributes/&gt;, so a view column named
        /// after such an alias is shown from the attribute itself (<see cref="CompareResult.CellText"/>).
        /// </summary>
        public IReadOnlyDictionary<string, string> MainEntityAliases { get; }
    }

    /// <summary>
    /// The compare query rewritten for a SECONDARY whose table is named differently (SPEC 5.9), built by
    /// <see cref="FetchXmlHelper.MapToSecondary"/>.
    /// </summary>
    public sealed class SecondaryQuery
    {
        internal SecondaryQuery(string fetchXml, IReadOnlyCollection<string> linkAliases)
        {
            FetchXml = fetchXml;
            LinkAliases = linkAliases;
        }

        /// <summary>The FetchXML to run on the secondary.</summary>
        public string FetchXml { get; }

        /// <summary>The aliases of the link-entities on the mapped entity itself (renamed too; their columns mapped).</summary>
        public IReadOnlyCollection<string> LinkAliases { get; }
    }

    /// <summary>FetchXML manipulation: paging the view queries and rewriting a view for a compare run.</summary>
    public static class FetchXmlHelper
    {
        /// <summary>The root &lt;fetch&gt; attributes a compare query removes: the engine pages the query itself.</summary>
        internal static readonly IReadOnlyList<string> StrippedFetchAttributes =
            new[] { "count", "page", "paging-cookie", "top", "returntotalrecordcount" };

        /// <summary>
        /// Rewrites a view's FetchXML for a compare run (SPEC 5.3): the main &lt;entity&gt;'s &lt;attribute&gt;
        /// (and any &lt;all-attributes&gt;) elements are replaced by one &lt;all-attributes/&gt; - the comparison
        /// covers every attribute - while its filters, orders and link-entities (with their own attributes,
        /// which feed the grid's linked columns) are kept unchanged; count, page, paging-cookie, top and
        /// returntotalrecordcount are removed from &lt;fetch&gt;. A distinct="true" view also gets an order on
        /// <paramref name="primaryIdAttribute"/> last (unless it already orders by it) because it is paged by
        /// page number. Throws ArgumentException for missing or invalid FetchXML or a fetch without an
        /// &lt;entity&gt;, and NotSupportedException for an aggregate view (its rows are not records).
        /// </summary>
        public static CompareQuery PrepareCompareQuery(string fetchXml, string primaryIdAttribute)
        {
            if (string.IsNullOrWhiteSpace(primaryIdAttribute))
                throw new ArgumentException("The primary id attribute is required.", nameof(primaryIdAttribute));

            XElement fetch = ParseFetch(fetchXml);
            if (IsTrue((string)fetch.Attribute("aggregate")))
                throw new NotSupportedException("The view is an aggregate query (aggregate=\"true\"): its rows are totals, not records, so it cannot be compared.");
            XElement entity = fetch.Elements().FirstOrDefault(e => e.Name.LocalName == "entity");
            string entityName = ((string)entity?.Attribute("name"))?.Trim();
            if (string.IsNullOrEmpty(entityName))
                throw new ArgumentException("The FetchXML has no <entity name=\"...\"> element.", nameof(fetchXml));

            foreach (string name in StrippedFetchAttributes) fetch.SetAttributeValue(name, null);

            var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (XElement attribute in entity.Elements().Where(e => e.Name.LocalName == "attribute").ToList())
            {
                string alias = ((string)attribute.Attribute("alias"))?.Trim();
                string name = ((string)attribute.Attribute("name"))?.Trim();
                if (!string.IsNullOrEmpty(alias) && !string.IsNullOrEmpty(name) && !aliases.ContainsKey(alias)) aliases[alias] = name;
                attribute.Remove();
            }
            foreach (XElement all in entity.Elements().Where(e => e.Name.LocalName == "all-attributes").ToList()) all.Remove();
            entity.AddFirst(new XElement(entity.Name.Namespace + "all-attributes"));

            bool distinct = IsTrue((string)fetch.Attribute("distinct"));
            if (distinct)
            {
                List<XElement> orders = entity.Elements().Where(e => e.Name.LocalName == "order").ToList();
                if (!orders.Any(o => string.Equals(((string)o.Attribute("attribute"))?.Trim(), primaryIdAttribute, StringComparison.OrdinalIgnoreCase)))
                {
                    var order = new XElement(entity.Name.Namespace + "order", new XAttribute("attribute", primaryIdAttribute), new XAttribute("descending", "false"));
                    if (orders.Count > 0) orders[orders.Count - 1].AddAfterSelf(order);
                    else entity.Add(order);
                }
            }

            return new CompareQuery(entityName, fetch.ToString(SaveOptions.DisableFormatting), distinct, aliases);
        }

        /// <summary>
        /// Rewrites a (prepared) compare query for the SECONDARY when the entity is mapped to a differently named
        /// table there (SPEC 5.9). The main &lt;entity name&gt; becomes <paramref name="secondaryEntity"/>; inside the
        /// main entity's scope - the &lt;entity&gt; itself and any &lt;link-entity&gt; on the primary entity too (a
        /// parent record of the same table, renamed and with its <c>from</c> mapped) - every attribute reference is
        /// passed through <paramref name="mapColumn"/>: &lt;attribute name&gt;, &lt;condition attribute&gt; and
        /// <c>valueof</c> (a condition with <c>entityname</c> belongs to that link's scope), &lt;order attribute&gt;
        /// and the <c>to</c> of the scope's child link-entities. Everything else - other link-entities, their
        /// attributes, filters and <c>from</c>, values, paging - passes through unchanged. A null
        /// <paramref name="mapColumn"/> maps nothing; it should return names it does not map unchanged.
        /// Throws ArgumentException for invalid FetchXML or a fetch that is not for <paramref name="primaryEntity"/>.
        /// </summary>
        public static SecondaryQuery MapToSecondary(string fetchXml, string primaryEntity, string secondaryEntity, Func<string, string> mapColumn)
        {
            if (string.IsNullOrWhiteSpace(primaryEntity)) throw new ArgumentException("The primary entity is required.", nameof(primaryEntity));
            if (string.IsNullOrWhiteSpace(secondaryEntity)) throw new ArgumentException("The secondary entity is required.", nameof(secondaryEntity));

            XElement fetch = ParseFetch(fetchXml);
            XElement entity = fetch.Elements().FirstOrDefault(e => e.Name.LocalName == "entity");
            string entityName = ((string)entity?.Attribute("name"))?.Trim();
            if (!IsEntity(entityName, primaryEntity))
                throw new ArgumentException($"The FetchXML is for {entityName ?? "no entity"}, not {primaryEntity}.", nameof(fetchXml));

            var mapper = new SecondaryMapper(primaryEntity.Trim(), secondaryEntity.Trim(), mapColumn ?? (name => name));
            mapper.CollectScopeAliases(entity);
            entity.SetAttributeValue("name", secondaryEntity.Trim());
            mapper.MapScope(entity);
            return new SecondaryQuery(fetch.ToString(SaveOptions.DisableFormatting), mapper.ScopeAliases.ToList());
        }

        private static bool IsEntity(string name, string entity) =>
            string.Equals(name?.Trim(), entity?.Trim(), StringComparison.OrdinalIgnoreCase);

        /// <summary>The walk of <see cref="MapToSecondary"/>.</summary>
        private sealed class SecondaryMapper
        {
            private readonly string _primaryEntity;
            private readonly string _secondaryEntity;
            private readonly Func<string, string> _map;

            public SecondaryMapper(string primaryEntity, string secondaryEntity, Func<string, string> map)
            {
                _primaryEntity = primaryEntity;
                _secondaryEntity = secondaryEntity;
                _map = map;
            }

            /// <summary>Aliases (or names, when a link has no alias) of the link-entities on the primary entity itself.</summary>
            public HashSet<string> ScopeAliases { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public void CollectScopeAliases(XElement entity)
            {
                foreach (XElement link in entity.Descendants().Where(e => e.Name.LocalName == "link-entity"))
                {
                    if (IsEntity((string)link.Attribute("name"), _primaryEntity)) ScopeAliases.Add(AliasOf(link));
                }
            }

            /// <summary>The children of an element whose table is the mapped entity (the main entity or a link on it).</summary>
            public void MapScope(XElement scope)
            {
                foreach (XElement child in scope.Elements())
                {
                    switch (child.Name.LocalName)
                    {
                        case "attribute":
                            Map(child, "name");
                            break;
                        case "order":
                            if (InScope((string)child.Attribute("entityname"), inScope: true)) Map(child, "attribute");
                            break;
                        case "filter":
                            MapFilter(child, inScope: true);
                            break;
                        case "link-entity":
                            MapLink(child, parentInScope: true);
                            break;
                    }
                }
            }

            private void MapLink(XElement link, bool parentInScope)
            {
                if (parentInScope) Map(link, "to");   // "to" names the parent's attribute
                if (IsEntity((string)link.Attribute("name"), _primaryEntity))
                {
                    link.SetAttributeValue("name", _secondaryEntity);
                    Map(link, "from");                // "from" names the link entity's own attribute
                    MapScope(link);
                    return;
                }
                foreach (XElement child in link.Elements())
                {
                    if (child.Name.LocalName == "filter") MapFilter(child, inScope: false);
                    else if (child.Name.LocalName == "link-entity") MapLink(child, parentInScope: false);
                    else if (child.Name.LocalName == "order" && InScope((string)child.Attribute("entityname"), inScope: false)) Map(child, "attribute");
                }
            }

            private void MapFilter(XElement filter, bool inScope)
            {
                foreach (XElement child in filter.Elements())
                {
                    if (child.Name.LocalName == "filter")
                    {
                        MapFilter(child, inScope);
                    }
                    else if (child.Name.LocalName == "condition" && InScope((string)child.Attribute("entityname"), inScope))
                    {
                        Map(child, "attribute");
                        Map(child, "valueof");
                    }
                }
            }

            /// <summary>An element with entityname belongs to that link; without it, to the element it sits in.</summary>
            private bool InScope(string entityName, bool inScope) =>
                string.IsNullOrWhiteSpace(entityName) ? inScope : ScopeAliases.Contains(entityName.Trim());

            private void Map(XElement element, string attributeName)
            {
                string value = ((string)element.Attribute(attributeName))?.Trim();
                if (string.IsNullOrEmpty(value) || value.IndexOf('.') >= 0) return;
                string mapped = _map(value);
                if (!string.IsNullOrWhiteSpace(mapped) && !string.Equals(mapped, value, StringComparison.Ordinal))
                    element.SetAttributeValue(attributeName, mapped.Trim());
            }

            private static string AliasOf(XElement link) =>
                ((string)link.Attribute("alias"))?.Trim() is string alias && alias.Length > 0 ? alias : ((string)link.Attribute("name"))?.Trim();
        }

        private static bool IsTrue(string value) =>
            string.Equals(value?.Trim(), "true", StringComparison.OrdinalIgnoreCase) || string.Equals(value?.Trim(), "1", StringComparison.Ordinal);

        /// <summary>
        /// Returns a new fetch string with <c>count</c>, <c>page</c> and <c>paging-cookie</c> set on the
        /// root &lt;fetch&gt; (the cookie is XML-escaped; a null/empty cookie removes the attribute) and
        /// any <c>top</c> attribute removed (top cannot be combined with paging).
        /// </summary>
        public static string ApplyPaging(string fetchXml, int page, int count, string pagingCookie)
        {
            if (page < 1) throw new ArgumentOutOfRangeException(nameof(page), page, "The page number starts at 1.");
            if (count < 1) throw new ArgumentOutOfRangeException(nameof(count), count, "The page size must be at least 1.");

            XElement fetch = ParseFetch(fetchXml);
            fetch.SetAttributeValue("top", null);
            fetch.SetAttributeValue("count", count.ToString(CultureInfo.InvariantCulture));
            fetch.SetAttributeValue("page", page.ToString(CultureInfo.InvariantCulture));
            fetch.SetAttributeValue("paging-cookie", string.IsNullOrEmpty(pagingCookie) ? null : pagingCookie);
            return fetch.ToString(SaveOptions.DisableFormatting);
        }

        /// <summary>
        /// Makes sure the main &lt;entity&gt; returns <paramref name="attributeName"/> (e.g. the primary
        /// id, so every grid row has its id): adds &lt;attribute name=".."/&gt; unless the entity uses
        /// &lt;all-attributes/&gt; or already lists it. Returns the input unchanged when nothing is added.
        /// </summary>
        public static string EnsureAttribute(string fetchXml, string attributeName)
        {
            if (string.IsNullOrWhiteSpace(attributeName)) return fetchXml;

            XElement fetch = ParseFetch(fetchXml);
            XElement entity = fetch.Elements().FirstOrDefault(e => e.Name.LocalName == "entity");
            if (entity == null) return fetchXml;

            if (entity.Elements().Any(e => e.Name.LocalName == "all-attributes")) return fetchXml;
            var attributes = entity.Elements().Where(e => e.Name.LocalName == "attribute").ToList();
            if (attributes.Any(a => string.Equals((string)a.Attribute("name"), attributeName, StringComparison.OrdinalIgnoreCase))) return fetchXml;

            var added = new XElement(entity.Name.Namespace + "attribute", new XAttribute("name", attributeName));
            if (attributes.Count > 0) attributes[attributes.Count - 1].AddAfterSelf(added);
            else entity.AddFirst(added);
            return fetch.ToString(SaveOptions.DisableFormatting);
        }

        private static XElement ParseFetch(string fetchXml)
        {
            if (string.IsNullOrWhiteSpace(fetchXml)) throw new ArgumentException("FetchXML is required.", nameof(fetchXml));
            XDocument document;
            try
            {
                document = XDocument.Parse(fetchXml);
            }
            catch (XmlException ex)
            {
                throw new ArgumentException("The FetchXML is not valid XML: " + ex.Message, nameof(fetchXml), ex);
            }
            XElement root = document.Root;
            if (root == null || root.Name.LocalName != "fetch")
                throw new ArgumentException("The FetchXML root element must be <fetch>.", nameof(fetchXml));
            return root;
        }
    }
}
