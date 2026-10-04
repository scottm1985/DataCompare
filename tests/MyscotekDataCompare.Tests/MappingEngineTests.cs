using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using MyscotekDataCompare.Core;
using MyscotekDataCompare.Core.Schema;
using MyscotekDataCompare.Tests.Fakes;
using Xunit;
using static MyscotekDataCompare.Tests.Fakes.TestData;

namespace MyscotekDataCompare.Tests
{
    /// <summary>
    /// SPEC 5.9: an entity mapped to a differently named table of the secondary - the secondary side of the run,
    /// the column counterparts, the rewritten view and its fallback, the detail pane and the grid cells.
    /// </summary>
    public class MappingEngineTests
    {
        private static readonly Guid Same = Id(1), Diff = Id(2), Gone = Id(3), Added = Id(4);

        /// <summary>
        /// The secondary's table: account was migrated into new_account (key new_accountid). accountnumber went to
        /// new_accountnumber; numberofemployees became a text column; description stayed text (Memo -> String);
        /// many primary columns have no counterpart (creditonhold, industrycode, new_tags...); new_extra is new.
        /// </summary>
        internal static FakeSchemaProvider MigratedSchema(string primaryId = null)
        {
            var schema = new FakeSchemaProvider();
            schema.Entity("new_account", "name", primaryId).EntityDisplayName("Migrated account")
                .String("new_accountnumber")
                .Money("revenue").Derived("revenue_base", "revenue", AttributeTypeCode.Money)
                .String("numberofemployees")
                .String("description")
                .Lookup("primarycontactid", "contact")
                .Lookup("parentaccountid", "new_account")
                .Lookup("transactioncurrencyid", "transactioncurrency")
                .String("new_extra")
                .Owner().State().SystemAttributes();
            return schema;
        }

        internal static EntityMapping AccountMapping() =>
            new EntityMapping("account", "new_account", new ColumnMapping("accountnumber", "new_accountnumber"));

        /// <summary>A migrated account in the secondary: name, active.</summary>
        internal static Entity Migrated(Guid id, string name, params (string Name, object Value)[] attributes)
        {
            Entity record = Record("new_account", id, ("name", name), ("statecode", Opt(0)), ("statuscode", Opt(1)));
            foreach ((string attribute, object value) in attributes) record[attribute] = value;
            return record;
        }

        private static Harness Mapped()
        {
            var h = new Harness { SecondarySchema = MigratedSchema() };
            h.Options.EntityMappings.Add(AccountMapping());
            return h;
        }

        /// <summary>One record of each status, the secondary's in new_account.</summary>
        private static Harness FourStatuses()
        {
            Harness h = Mapped();
            h.Primary.Add(Account(Same, "A Same", ("accountnumber", "1")));
            h.Secondary.Add(Migrated(Same, "A Same", ("new_accountnumber", "1")));
            h.Primary.Add(Account(Diff, "B Diff", ("accountnumber", "2")));
            h.Secondary.Add(Migrated(Diff, "B Diff", ("new_accountnumber", "2X")));
            h.Primary.Add(Account(Gone, "C Missing"));
            h.Secondary.Add(Migrated(Added, "D Extra", ("new_accountnumber", "4")));
            return h;
        }

        // ---- the secondary side of the run ----

        [Fact]
        public void A_mapped_entity_is_read_from_the_secondary_table_and_every_status_is_decided()
        {
            Harness h = FourStatuses();

            CompareResult result = h.Run();

            Assert.Equal(new[] { (Same, RowStatus.Match), (Diff, RowStatus.Different), (Gone, RowStatus.Missing), (Added, RowStatus.Extra) },
                result.Rows.Select(r => (r.Id, r.Status)));
            Assert.Equal(new[] { "accountnumber" }, Harness.Row(result, Diff).DifferingAttributes);
            Assert.Equal("account", FakeFetchXml.EntityName(Assert.Single(h.Primary.Fetches)));
            Assert.Equal("new_account", FakeFetchXml.EntityName(Assert.Single(h.Secondary.Fetches)));
            Assert.Equal(("new_account", "new_account"), (result.SecondaryEntityLogicalName, result.Mapping.SecondaryEntity));
            Assert.Equal("account", result.EntityLogicalName);
            Assert.Empty(h.Primary.Writes);
            Assert.Empty(h.Secondary.Writes);
        }

        [Fact]
        public void The_lookups_by_id_use_each_side_s_own_entity_and_primary_key()
        {
            Harness h = FourStatuses();

            h.Run();

            QueryExpression missingCheck = Assert.Single(h.Secondary.Queries);
            Assert.Equal("new_account", missingCheck.EntityName);
            ConditionExpression condition = Assert.Single(missingCheck.Criteria.Conditions);
            Assert.Equal(("new_accountid", ConditionOperator.In), (condition.AttributeName, condition.Operator));
            Assert.Equal(new object[] { Gone }, condition.Values);
            QueryExpression extraCheck = Assert.Single(h.Primary.Queries);
            Assert.Equal(("account", "accountid"), (extraCheck.EntityName, extraCheck.Criteria.Conditions.Single().AttributeName));
        }

        [Fact]
        public void The_secondary_primary_key_comes_from_the_secondary_metadata()
        {
            var h = new Harness { SecondarySchema = MigratedSchema(primaryId: "new_accountkey") };
            h.Options.EntityMappings.Add(AccountMapping());
            h.Secondary.PrimaryIdAttributes["new_account"] = "new_accountkey";
            h.Primary.Add(Account(Same, "A Inactive in the secondary"));
            h.Secondary.Add(Migrated(Same, "A Inactive in the secondary", ("statecode", Opt(1)), ("statuscode", Opt(2)))
                .Changed(("new_accountid", null), ("new_accountkey", Same)));   // outside the secondary's view: looked up by id
            h.Primary.Add(Account(Diff, "B Active"));
            h.Secondary.Add(Migrated(Diff, "B Active").Changed(("new_accountid", null), ("new_accountkey", Diff)));

            CompareResult result = h.Run();

            ConditionExpression lookup = Assert.Single(h.Secondary.Queries).Criteria.Conditions.Single();
            Assert.Equal(("new_accountkey", Same), (lookup.AttributeName, (Guid)lookup.Values.Single()));
            Assert.Equal(("new_accountkey", "accountid"), (result.SecondaryPrimaryIdAttribute, result.PrimaryIdAttribute));
            RowComparison found = Harness.Row(result, Same);
            Assert.Equal((RowStatus.Different, false), (found.Status, found.InSecondaryView));
            Assert.Equal(new[] { "statecode", "statuscode" }, found.DifferingAttributes);
            Assert.Equal(RowStatus.Match, Harness.Row(result, Diff).Status);
            ColumnComparison key = result.GetDetails(Harness.Row(result, Diff))[0];
            Assert.Equal(("accountid", "new_accountkey", DetailBuilder.PrimaryKeyNote, false),
                (key.LogicalName, key.SecondaryLogicalName, key.Note, key.IsDifferent));
            Assert.Equal(Diff.ToString(), key.SecondaryText);
            Assert.Contains("Entity mapping: account is compared with new_account in the secondary, matched on accountid = new_accountkey; " +
                            "columns matched by name except accountnumber -> new_accountnumber.", h.Log.Lines);
        }

        [Fact]
        public void Same_named_columns_are_compared_with_each_other()
        {
            Harness h = Mapped();
            h.Primary.Add(Account(Same, "Contoso", ("revenue", new Money(10m)), ("description", "x")));
            h.Secondary.Add(Migrated(Same, "Contoso Ltd", ("revenue", new Money(11m)), ("description", "x")));

            CompareResult result = h.Run();

            Assert.Equal(new[] { "name", "revenue" }, Assert.Single(result.Rows).DifferingAttributes);
        }

        [Fact]
        public void An_explicit_column_pair_compares_the_primary_column_with_the_paired_secondary_column()
        {
            Harness h = Mapped();
            h.Primary.Add(Account(Same, "Equal", ("accountnumber", "A-1")));
            h.Secondary.Add(Migrated(Same, "Equal", ("new_accountnumber", "A-1"), ("accountnumber", "something else")));
            h.Primary.Add(Account(Diff, "Unequal", ("accountnumber", "A-2")));
            h.Secondary.Add(Migrated(Diff, "Unequal", ("new_accountnumber", "A-2x")));

            CompareResult result = h.Run();

            Assert.Equal(RowStatus.Match, Harness.Row(result, Same).Status);
            Assert.Equal(new[] { "accountnumber" }, Harness.Row(result, Diff).DifferingAttributes);
            ColumnComparison line = result.GetDetails(Harness.Row(result, Diff)).Single(l => l.LogicalName == "accountnumber");
            Assert.Equal(("new_accountnumber", "A-2", "A-2x", true, null), (line.SecondaryLogicalName, line.PrimaryText, line.SecondaryText, line.IsMismatch, line.Note));
        }

        [Fact]
        public void A_column_without_a_counterpart_is_shown_but_not_compared()
        {
            Harness h = Mapped();
            h.Primary.Add(Account(Same, "Contoso", ("creditonhold", true), ("industrycode", Opt(3))));
            h.Secondary.Add(Migrated(Same, "Contoso", ("creditonhold", false)));   // a value under a name the metadata does not know

            CompareResult result = h.Run();

            RowComparison row = Assert.Single(result.Rows);
            Assert.Equal(RowStatus.Match, row.Status);
            IList<ColumnComparison> lines = result.GetDetails(row);
            ColumnComparison credit = lines.Single(l => l.LogicalName == "creditonhold" && l.AttributeType != null);
            Assert.Equal((null, false, DetailBuilder.NoCounterpartNote, "", true),
                (credit.SecondaryLogicalName, credit.IsCompared, credit.Note, credit.SecondaryText, credit.IsDifferent));
            Assert.Equal(DetailBuilder.NoCounterpartNote, lines.Single(l => l.LogicalName == "industrycode").Note);
            // The secondary's own value under that name is nobody's counterpart: listed apart, not compared.
            ColumnComparison stray = lines.Single(l => l.LogicalName == "creditonhold" && l.AttributeType == null);
            Assert.Equal((DetailBuilder.SecondaryOnlyNote, "", false), (stray.Note, stray.PrimaryText, stray.IsCompared));
            Assert.Contains(h.Log.Lines, l => l.StartsWith("Not compared, no counterpart in new_account: ", StringComparison.Ordinal) && l.Contains("creditonhold"));
            Assert.Contains(h.Log.Lines, l => l.Contains(" without a counterpart in new_account, "));
        }

        [Fact]
        public void A_pair_to_a_column_the_secondary_does_not_have_is_logged_and_not_compared()
        {
            Harness h = Mapped();
            h.Options.EntityMappings[0].Columns.Add(new ColumnMapping("telephone1", "new_extra"));                 // no primary column
            h.Options.EntityMappings[0].Columns.Add(new ColumnMapping("new_reviewdate", "new_missingcolumn"));     // no secondary column
            h.Primary.Add(Account(Same, "Contoso", ("new_reviewdate", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc))));
            h.Secondary.Add(Migrated(Same, "Contoso"));

            CompareResult result = h.Run();

            Assert.Equal(RowStatus.Match, Assert.Single(result.Rows).Status);
            Assert.Equal(DetailBuilder.NoCounterpartNote, result.GetDetails(result.Rows[0]).Single(l => l.LogicalName == "new_reviewdate").Note);
            Assert.Contains("Entity mapping: new_account has no column new_missingcolumn (paired with new_reviewdate): not compared.", h.Log.Messages(LogLevel.Warning));
            Assert.Contains("Entity mapping: account has no column telephone1: the pair only renames the view's references to it.", h.Log.Messages(LogLevel.Warning));
        }

        [Fact]
        public void A_type_that_differs_is_compared_by_the_value_rules_and_noted_in_the_detail()
        {
            Harness h = Mapped();
            h.Primary.Add(Account(Same, "Contoso", ("numberofemployees", 5), ("description", "same text")));
            h.Secondary.Add(Migrated(Same, "Contoso", ("numberofemployees", "5"), ("description", "same text")));

            CompareResult result = h.Run();

            RowComparison row = Assert.Single(result.Rows);
            Assert.Equal(new[] { "numberofemployees" }, row.DifferingAttributes);   // an int and a string are different values
            IList<ColumnComparison> lines = result.GetDetails(row);
            ColumnComparison employees = lines.Single(l => l.LogicalName == "numberofemployees");
            Assert.True(employees.IsMismatch);
            Assert.Equal(DetailBuilder.TypeDiffersNotePrefix + "Integer in the primary, String in the secondary", employees.Note);
            Assert.Null(lines.Single(l => l.LogicalName == "description").Note);   // Memo and String are both text
        }

        [Fact]
        public void The_detail_lists_each_secondary_column_once_and_the_secondary_only_ones_apart()
        {
            Harness h = Mapped();
            h.Primary.Add(Account(Same, "Contoso", ("accountnumber", "A-1")));
            h.Secondary.Add(Migrated(Same, "Contoso", ("new_accountnumber", "A-1"), ("new_extra", "only here")));

            CompareResult result = h.Run();

            IList<ColumnComparison> lines = result.GetDetails(Assert.Single(result.Rows));
            Assert.DoesNotContain(lines, l => l.LogicalName == "new_accountnumber");   // shown on the accountnumber line
            Assert.Single(lines, l => l.SecondaryLogicalName == "new_accountnumber");
            ColumnComparison extra = lines.Single(l => l.LogicalName == "new_extra");
            Assert.Equal(("", "only here", DetailBuilder.SecondaryOnlyNote, false), (extra.PrimaryText, extra.SecondaryText, extra.Note, extra.IsCompared));
            ColumnComparison key = lines[0];
            Assert.Equal(("accountid", "new_accountid"), (key.LogicalName, key.SecondaryLogicalName));
            Assert.Single(lines, l => l.SecondaryLogicalName == "new_accountid");
            Assert.Equal(RowStatus.Match, result.Rows[0].Status);
        }

        // ---- the grid cells of secondary records ----

        [Fact]
        public void An_extra_row_shows_the_secondary_values_under_the_view_s_primary_columns()
        {
            Harness h = FourStatuses();
            h.Secondary.Get("new_account", Added).Formatted("statuscode", "Active");

            CompareResult result = h.Run();

            RowComparison extra = Harness.Row(result, Added);
            Assert.Same(extra.Secondary, extra.DisplayRecord);
            Assert.Equal("D Extra", result.CellText(extra, "name"));
            Assert.Equal("4", result.CellText(extra, "accountnumber"));   // read from new_accountnumber
            Assert.Equal("Active", result.CellText(extra, "statuscode"));
            Assert.Equal(string.Empty, result.CellText(extra, "creditonhold"));   // no counterpart: blank
            Assert.Equal("2", result.CellText(Harness.Row(result, Diff), "accountnumber"));   // primary rows show the primary record
        }

        [Fact]
        public void A_row_only_the_secondary_view_returned_but_found_in_the_primary_shows_mapped_secondary_values()
        {
            Harness h = Mapped();
            h.Primary.Add(Account(Same, "Inactive", ("statecode", Opt(1)), ("accountnumber", "P-1")));
            h.Secondary.Add(Migrated(Same, "Inactive", ("new_accountnumber", "S-1")));

            CompareResult result = h.Run();

            RowComparison row = Assert.Single(result.Rows);
            Assert.Equal((false, true, RowStatus.Different), (row.InPrimaryView, row.InSecondaryView, row.Status));
            Assert.Equal("S-1", result.CellText(row, "accountnumber"));
        }

        [Fact]
        public void Linked_columns_of_a_link_on_the_mapped_entity_itself_are_read_under_their_secondary_names()
        {
            EntitySchema primary = StandardSchema().GetEntity("account");
            var map = new EntityMap(AccountMapping(), primary, MigratedSchema().GetEntity("new_account")) { LinkAliases = new[] { "parent" } };
            Entity secondary = Migrated(Added, "Child");
            secondary["parent.new_accountnumber"] = new AliasedValue("new_account", "new_accountnumber", "P-9");
            secondary["pc.emailaddress1"] = new AliasedValue("contact", "emailaddress1", "a@b.example");
            var row = new RowComparison(Added, RowStatus.Extra, null, secondary, null, false, true);
            var result = new CompareResult(primary, null, new List<RowComparison> { row }, new CompareSummary { Extra = 1 },
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["acctnum"] = "accountnumber" }, map);
            secondary["new_accountnumber"] = "N-1";

            Assert.Equal("P-9", result.CellText(row, "parent.accountnumber"));
            Assert.Equal("a@b.example", result.CellText(row, "pc.emailaddress1"));   // another entity's link: unchanged
            Assert.Equal("N-1", result.CellText(row, "acctnum"));                    // a main-entity alias, then its counterpart
        }

        // ---- the rewritten view and its fallback ----

        [Fact]
        public void The_secondary_runs_the_view_rewritten_with_the_column_pairs()
        {
            Harness h = Mapped();
            h.Primary.Add(Account(Same, "Numbered", ("accountnumber", "A-1")));
            h.Primary.Add(Account(Diff, "Unnumbered"));
            h.Secondary.Add(Migrated(Same, "Numbered", ("new_accountnumber", "A-1")));
            h.Secondary.Add(Migrated(Diff, "Unnumbered"));
            const string view = "<fetch><entity name=\"account\"><attribute name=\"name\" /><order attribute=\"accountnumber\" />" +
                                "<filter><condition attribute=\"accountnumber\" operator=\"not-null\" /></filter></entity></fetch>";

            CompareResult result = h.Run(view);

            XElement entity = XElement.Parse(Assert.Single(h.Secondary.Fetches)).Element("entity");
            Assert.Equal("new_account", (string)entity.Attribute("name"));
            Assert.Equal("new_accountnumber", (string)entity.Element("filter").Element("condition").Attribute("attribute"));
            Assert.Equal("new_accountnumber", (string)entity.Element("order").Attribute("attribute"));
            Assert.Equal("accountnumber", (string)XElement.Parse(Assert.Single(h.Primary.Fetches)).Element("entity").Element("order").Attribute("attribute"));
            Assert.Equal(new[] { (Same, RowStatus.Match) }, result.Rows.Select(r => (r.Id, r.Status)));   // the secondary's view applied its filter too
            Assert.Equal((1, 1, 0), (result.Summary.PrimaryCount, result.Summary.SecondaryCount, result.Summary.FoundByIdInSecondary));
        }

        [Fact]
        public void A_failing_rewritten_query_is_a_warning_and_every_primary_row_is_still_checked_by_id()
        {
            Harness h = FourStatuses();
            h.Secondary.FailFetchPages.Add(1);

            CompareResult result = h.Run();

            Assert.Equal(new[] { (Same, RowStatus.Match), (Diff, RowStatus.Different), (Gone, RowStatus.Missing) }, result.Rows.Select(r => (r.Id, r.Status)));
            CompareSummary s = result.Summary;
            Assert.True(s.SecondaryQueryFailed);
            Assert.Equal("Simulated FetchXML failure", s.SecondaryQueryError);
            Assert.Equal((0, 2, 0), (s.Extra, s.FoundByIdInSecondary, s.SecondaryCount));   // extras skipped
            Assert.Contains(h.Log.Messages(LogLevel.Warning),
                l => l.StartsWith("Secondary: the view query rewritten for new_account failed (Simulated FetchXML failure).", StringComparison.Ordinal));
            Assert.Contains(h.Log.Messages(LogLevel.Warning), l => l.StartsWith("Result: 3 rows", StringComparison.Ordinal));
            QueryExpression lookup = Assert.Single(h.Secondary.Queries);
            Assert.Equal(new object[] { Same, Diff, Gone }, lookup.Criteria.Conditions.Single().Values);
        }

        [Fact]
        public void A_mapped_table_the_secondary_does_not_have_fails_the_run()
        {
            Harness h = Mapped();
            h.SecondarySchema = new FakeSchemaProvider();
            h.Primary.Add(Account(Same, "Contoso"));

            var ex = Assert.Throws<InvalidOperationException>(() => h.Run());

            Assert.Equal("The entity new_account (mapped from account) does not exist in the secondary environment.", ex.Message);
            Assert.Contains("Compare failed: " + ex.Message, h.Log.Messages(LogLevel.Error));
            Assert.Empty(h.Secondary.Executed);
        }

        [Fact]
        public void Without_a_secondary_schema_provider_the_secondary_metadata_is_read_from_the_secondary_service()
        {
            var h = new Harness();
            h.Options.EntityMappings.Add(AccountMapping());
            h.Secondary.ExecuteHandler = request =>
            {
                if (!(request is RetrieveEntityRequest retrieve)) return null;
                Assert.Equal("new_account", retrieve.LogicalName);
                var response = new RetrieveEntityResponse();
                response.Results["EntityMetadata"] = new EntityMetadata { LogicalName = "new_account", SchemaName = "new_Account" }
                    .With("PrimaryIdAttribute", "new_accountid")
                    .With("PrimaryNameAttribute", "name")
                    .With("Attributes", new AttributeMetadata[]
                    {
                        new UniqueIdentifierAttributeMetadata { LogicalName = "new_accountid" },
                        new StringAttributeMetadata { LogicalName = "name" },
                        new StringAttributeMetadata { LogicalName = "new_accountnumber" }
                    });
                return response;
            };
            h.Primary.Add(Account(Same, "Contoso", ("accountnumber", "A-1")));
            h.Secondary.Add(Migrated(Same, "Contoso", ("new_accountnumber", "A-1x")));

            CompareResult result = new CompareEngine(h.Primary, h.Secondary, h.Schema, h.Options, h.Log)
                .Compare("account", AccountView, null, System.Threading.CancellationToken.None);

            Assert.Single(h.Secondary.Executed.OfType<RetrieveEntityRequest>());
            Assert.Equal(new[] { "accountnumber" }, Assert.Single(result.Rows).DifferingAttributes);
        }

        // ---- what is (not) mapped ----

        [Fact]
        public void An_unmapped_entity_never_reads_the_secondary_metadata()
        {
            var h = new Harness { SecondarySchema = MigratedSchema() };
            h.Options.EntityMappings.Add(new EntityMapping("contact", "new_contact"));
            h.Options.EntityMappings.Add(new EntityMapping("account", " "));   // incomplete: never applies
            h.Both(Account(Same, "Contoso"));

            CompareResult result = h.Run();

            Assert.Null(result.Mapping);
            Assert.Equal(("account", "accountid"), (result.SecondaryEntityLogicalName, result.SecondaryPrimaryIdAttribute));
            Assert.Empty(h.SecondarySchema.Requests);
            Assert.Equal("account", FakeFetchXml.EntityName(Assert.Single(h.Secondary.Fetches)));
            Assert.DoesNotContain(h.Log.Lines, l => l.StartsWith("Entity mapping", StringComparison.Ordinal));
        }

        [Fact]
        public void The_mapping_matches_the_entity_whatever_its_case_and_spacing()
        {
            var h = new Harness { SecondarySchema = MigratedSchema() };
            h.Options.EntityMappings.Add(new EntityMapping(" Account ", "NEW_Account", new ColumnMapping(" AccountNumber ", "New_AccountNumber")));
            h.Primary.Add(Account(Same, "Contoso", ("accountnumber", "A-1")));
            h.Secondary.Add(Migrated(Same, "Contoso", ("new_accountnumber", "A-1")));

            CompareResult result = h.Run();

            Assert.Equal(RowStatus.Match, Assert.Single(result.Rows).Status);
            Assert.Equal(new[] { "new_account" }, h.SecondarySchema.Requests);
            Assert.Equal("new_accountnumber", result.Mapping.ColumnPairs["accountnumber"]);
        }

        [Fact]
        public void A_mapped_run_keeps_the_summary_invariants()
        {
            Harness h = FourStatuses();
            h.Primary.Add(Account(Id(5), "E Inactive", ("statecode", Opt(1))));
            h.Secondary.Add(Migrated(Id(5), "E Inactive"));   // in the secondary's view only: found by id in the primary

            CompareSummary s = h.Run().Summary;

            Assert.Equal((3, 4, 1, 0), (s.PrimaryCount, s.SecondaryCount, s.FoundByIdInPrimary, s.FoundByIdInSecondary));
            Assert.Equal((1, 2, 1, 1), (s.Matching, s.Different, s.Missing, s.Extra));
            Assert.Equal(s.PrimaryCount + s.FoundByIdInPrimary, s.Matching + s.Different + s.Missing);
            Assert.Equal(s.SecondaryCount + s.FoundByIdInSecondary, s.Matching + s.Different + s.Extra);
        }

        [Fact]
        public void Reevaluate_keeps_the_mapping_the_rows_were_read_with()
        {
            Harness h = FourStatuses();
            CompareResult result = h.Run();
            var options = new CompareOptions();   // no mappings at all: they do not apply to a re-evaluation
            options.IgnoredAttributes.Add("accountnumber");

            CompareResult updated = result.Reevaluate(options);

            Assert.Same(result.Mapping, updated.Mapping);
            Assert.Equal(RowStatus.Match, Harness.Row(updated, Diff).Status);
            Assert.Equal("new_accountnumber", updated.GetDetails(Harness.Row(updated, Diff)).Single(l => l.LogicalName == "accountnumber").SecondaryLogicalName);
            Assert.Equal("4", updated.CellText(Harness.Row(updated, Added), "accountnumber"));
        }
    }
}
