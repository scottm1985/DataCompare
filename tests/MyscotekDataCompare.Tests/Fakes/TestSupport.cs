using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using MyscotekDataCompare.Core;

namespace MyscotekDataCompare.Tests.Fakes
{
    /// <summary>Collects log lines for assertions.</summary>
    public sealed class ListLogger : ICompareLogger
    {
        private readonly object _sync = new object();

        public List<(LogLevel Level, string Message)> Entries { get; } = new List<(LogLevel Level, string Message)>();

        public void Log(LogLevel level, string message)
        {
            lock (_sync) Entries.Add((level, message));
        }

        public IReadOnlyList<string> Lines
        {
            get { lock (_sync) return Entries.Select(e => e.Message).ToList(); }
        }

        public IReadOnlyList<string> Messages(LogLevel level)
        {
            lock (_sync) return Entries.Where(e => e.Level == level).Select(e => e.Message).ToList();
        }

        /// <summary>Whole log, one "[Level] message" per line, for assertion failure messages.</summary>
        public string Dump()
        {
            lock (_sync) return string.Join(Environment.NewLine, Entries.Select(e => $"[{e.Level}] {e.Message}"));
        }
    }

    /// <summary>Synchronous IProgress (Progress&lt;T&gt; would post asynchronously).</summary>
    public sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;
        public SyncProgress(Action<T> handler) { _handler = handler; }
        public void Report(T value) => _handler(value);
    }

    /// <summary>Builders for test records, schemas and views.</summary>
    public static class TestData
    {
        private static readonly Dictionary<string, string> ActivityIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["email"] = "activityid", ["task"] = "activityid", ["phonecall"] = "activityid", ["activitypointer"] = "activityid"
        };

        /// <summary>A deterministic id: Id(1) = 00000001-0000-0000-0000-000000000000.</summary>
        public static Guid Id(int n) => new Guid(n, 0, 0, new byte[8]);

        /// <summary>A record as a query returns it: primary id attribute included.</summary>
        public static Entity Record(string entity, Guid id, params (string Name, object Value)[] attributes)
        {
            var record = new Entity(entity) { Id = id };
            record[ActivityIds.TryGetValue(entity, out string primaryId) ? primaryId : entity + "id"] = id;
            foreach ((string name, object value) in attributes) record[name] = value;
            return record;
        }

        /// <summary>An ACTIVE account (statecode 0) named <paramref name="name"/>, plus <paramref name="attributes"/>.</summary>
        public static Entity Account(Guid id, string name, params (string Name, object Value)[] attributes)
        {
            Entity account = Record("account", id, ("name", name), ("statecode", Opt(0)), ("statuscode", Opt(1)));
            foreach ((string attribute, object value) in attributes)
            {
                if (value == null) account.Attributes.Remove(attribute);
                else account[attribute] = value;
            }
            return account;
        }

        /// <summary>A copy of a record (attributes and formatted values) with some attributes changed (null removes one).</summary>
        public static Entity Changed(this Entity record, params (string Name, object Value)[] attributes)
        {
            Entity copy = FakeOrganizationService.Clone(record);
            foreach ((string name, object value) in attributes)
            {
                if (value == null) copy.Attributes.Remove(name);
                else copy[name] = value;
            }
            return copy;
        }

        /// <summary>Sets a formatted value on a record (returns it).</summary>
        public static Entity Formatted(this Entity record, string attribute, string text)
        {
            record.FormattedValues[attribute] = text;
            return record;
        }

        public static EntityReference Ref(string entity, Guid id, string name = null) => new EntityReference(entity, id) { Name = name };

        public static OptionSetValue Opt(int value) => new OptionSetValue(value);

        /// <summary>An activityparty as the platform returns it (with its own row id, which the comparison ignores).</summary>
        public static Entity Party(EntityReference partyId, int participationTypeMask = 2, string addressUsed = null)
        {
            var party = new Entity("activityparty") { Id = Guid.NewGuid() };
            party["activitypartyid"] = party.Id;
            party["participationtypemask"] = new OptionSetValue(participationTypeMask);
            if (partyId != null) party["partyid"] = partyId;
            if (addressUsed != null) party["addressused"] = addressUsed;
            return party;
        }

        public static EntityCollection Parties(params Entity[] parties) => new EntityCollection(parties.ToList()) { EntityName = "activityparty" };

        /// <summary>The "Active Accounts"-like view most engine tests use: name, number and the primary contact's email.</summary>
        public const string AccountView =
            "<fetch version=\"1.0\" output-format=\"xml-platform\" mapping=\"logical\" distinct=\"false\">" +
            "<entity name=\"account\">" +
            "<attribute name=\"name\" /><attribute name=\"accountnumber\" /><attribute name=\"accountid\" />" +
            "<order attribute=\"name\" descending=\"false\" />" +
            "<filter type=\"and\"><condition attribute=\"statecode\" operator=\"eq\" value=\"0\" /></filter>" +
            "<link-entity name=\"contact\" from=\"contactid\" to=\"primarycontactid\" link-type=\"outer\" alias=\"pc\">" +
            "<attribute name=\"emailaddress1\" /></link-entity>" +
            "</entity></fetch>";

        /// <summary>Every account, ordered by name.</summary>
        public const string AllAccountsView =
            "<fetch><entity name=\"account\"><attribute name=\"name\" /><order attribute=\"name\" /></entity></fetch>";

        /// <summary>The schema most engine tests use (the primary metadata).</summary>
        public static FakeSchemaProvider StandardSchema()
        {
            var schema = new FakeSchemaProvider();
            schema.Entity("account", "name").EntityDisplayName("Account", "Accounts").Label("name", "Account Name").Label("accountid", "Account")
                    .String("accountnumber").Label("accountnumber", "Account Number")
                    .Money("revenue").Label("revenue", "Annual Revenue")
                    .Derived("revenue_base", "revenue", AttributeTypeCode.Money).Label("revenue_base", "Annual Revenue (Base)")
                    .Int("numberofemployees").Label("numberofemployees", "Number of Employees")
                    .BigInt("new_bignumber")
                    .Decimal("exchangerate").Label("exchangerate", "Exchange Rate")
                    .Double("address1_latitude").Label("address1_latitude", "Address 1: Latitude")
                    .Bool("creditonhold").Label("creditonhold", "Credit Hold")
                    .Picklist("industrycode").Label("industrycode", "Industry")
                    .MultiSelect("new_tags").Label("new_tags", "Tags")
                    .Memo("description").Label("description", "Description")
                    .Guid("new_externalid")
                    .Image("entityimage").Label("entityimage", "Default Image")
                    .Derived("entityimage_url", "entityimage").Derived("entityimage_timestamp", "entityimage", AttributeTypeCode.BigInt)
                    .File("new_document").Label("new_document", "Document")
                    .DateTime("new_reviewdate").Label("new_reviewdate", "Review Date")
                    .Calculated("new_calculated")
                    .Unreadable("new_secret")
                    .Lookup("primarycontactid", "contact").Label("primarycontactid", "Primary Contact")
                    .Derived("primarycontactidname", "primarycontactid")
                    .Lookup("parentaccountid", "account").Label("parentaccountid", "Parent Account")
                    .Lookup("transactioncurrencyid", "transactioncurrency").Label("transactioncurrencyid", "Currency")
                    .Owner().Label("ownerid", "Owner")
                    .State().Label("statecode", "Status").Label("statuscode", "Status Reason")
                    .SystemAttributes().Label("createdon", "Created On").Label("modifiedon", "Modified On")
                .Entity("contact", "fullname").EntityDisplayName("Contact", "Contacts")
                    .String("firstname").String("lastname").String("emailaddress1")
                    .Customer("parentcustomerid")
                    .Owner().State().SystemAttributes()
                .Entity("email", "subject", "activityid").EntityDisplayName("Email", "Email Messages")
                    .Memo("description")
                    .PartyList("to", "account", "contact", "systemuser")
                    .PartyList("from", "systemuser", "queue")
                    .Lookup("regardingobjectid", "account", "contact")
                    .Owner().State().SystemAttributes();
            return schema;
        }
    }

    /// <summary>Wires a CompareEngine to fakes: primary/secondary services, the primary schema, options, logger, progress.</summary>
    public sealed class Harness
    {
        public FakeOrganizationService Primary { get; } = new FakeOrganizationService();
        public FakeOrganizationService Secondary { get; } = new FakeOrganizationService();
        public FakeSchemaProvider Schema { get; set; } = TestData.StandardSchema();

        /// <summary>The SECONDARY metadata (read only for a mapped entity); empty by default.</summary>
        public FakeSchemaProvider SecondarySchema { get; set; } = new FakeSchemaProvider();
        public CompareOptions Options { get; } = new CompareOptions();
        public ListLogger Log { get; } = new ListLogger();
        public List<CompareProgress> Progress { get; } = new List<CompareProgress>();

        /// <summary>Called with every progress report, after it is recorded (e.g. to cancel at a given point).</summary>
        public Action<CompareProgress> OnProgress { get; set; }

        /// <summary>Seeds the same record in both environments.</summary>
        public Harness Both(params Entity[] records)
        {
            foreach (Entity record in records)
            {
                Primary.Add(record);
                Secondary.Add(record);
            }
            return this;
        }

        public CompareResult Run(string fetchXml = TestData.AccountView, string entity = "account") =>
            Run(CancellationToken.None, fetchXml, entity);

        public CompareResult Run(CancellationToken token, string fetchXml = TestData.AccountView, string entity = "account") =>
            new CompareEngine(Primary, Secondary, Schema, SecondarySchema, Options, Log).Compare(entity, fetchXml, new SyncProgress<CompareProgress>(p =>
            {
                Progress.Add(p);
                OnProgress?.Invoke(p);
            }), token);

        /// <summary>The row with the given id (exactly one).</summary>
        public static RowComparison Row(CompareResult result, Guid id) => result.Rows.Single(r => r.Id == id);
    }

    /// <summary>Sets SDK metadata properties that only have internal setters.</summary>
    public static class Reflect
    {
        public static T With<T>(this T target, string property, object value)
        {
            PropertyInfo info = target.GetType().GetProperty(property, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                                ?? throw new ArgumentException($"{target.GetType().Name} has no property {property}.");
            info.SetValue(target, value);
            return target;
        }
    }
}
