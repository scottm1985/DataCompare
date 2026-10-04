using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceModel;
using System.Threading;
using System.Xml.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using MyscotekDataCompare.Core;
using MyscotekDataCompare.Tests.Fakes;
using Xunit;
using static MyscotekDataCompare.Tests.Fakes.TestData;

namespace MyscotekDataCompare.Tests
{
    /// <summary>SPEC 5.4-5.7: the compare run - statuses, the steps, the attribute rules, the result.</summary>
    public class CompareEngineTests
    {
        private static readonly Guid Same = Id(1), Diff = Id(2), Gone = Id(3), Added = Id(4);

        /// <summary>One record of each status: A Same (green), B Diff (amber), C Missing (red), D Extra (blue).</summary>
        private static Harness FourStatuses()
        {
            var h = new Harness();
            h.Both(Account(Same, "A Same", ("accountnumber", "1")));
            h.Primary.Add(Account(Diff, "B Diff", ("accountnumber", "2")));
            h.Secondary.Add(Account(Diff, "B Diff", ("accountnumber", "2X")));
            h.Primary.Add(Account(Gone, "C Missing"));
            h.Secondary.Add(Account(Added, "D Extra"));
            return h;
        }

        // ---- statuses ----

        [Fact]
        public void Every_row_gets_its_status_in_view_order_with_the_extras_last()
        {
            Harness h = FourStatuses();

            CompareResult result = h.Run();

            Assert.Equal(new[] { (Same, RowStatus.Match), (Diff, RowStatus.Different), (Gone, RowStatus.Missing), (Added, RowStatus.Extra) },
                result.Rows.Select(r => (r.Id, r.Status)));
            Assert.Equal(new[] { "accountnumber" }, Harness.Row(result, Diff).DifferingAttributes);
            Assert.Empty(Harness.Row(result, Same).DifferingAttributes);

            RowComparison missing = Harness.Row(result, Gone);
            Assert.NotNull(missing.Primary);
            Assert.Null(missing.Secondary);
            Assert.Equal((true, false), (missing.InPrimaryView, missing.InSecondaryView));
            RowComparison extra = Harness.Row(result, Added);
            Assert.Null(extra.Primary);
            Assert.NotNull(extra.Secondary);
            Assert.Equal((false, true), (extra.InPrimaryView, extra.InSecondaryView));
            Assert.Same(extra.Secondary, extra.DisplayRecord);
            Assert.All(result.Rows.Where(r => r.Primary != null && r.Secondary != null), r => Assert.Equal((true, true), (r.InPrimaryView, r.InSecondaryView)));
        }

        [Fact]
        public void The_summary_counts_add_up()
        {
            CompareResult result = FourStatuses().Run();
            CompareSummary s = result.Summary;

            Assert.Equal((3, 3), (s.PrimaryCount, s.SecondaryCount));
            Assert.Equal((1, 1, 1, 1, 0), (s.Matching, s.Different, s.Missing, s.Extra, s.Unchecked));
            Assert.Equal((0, 0, 0), (s.FoundByIdInSecondary, s.FoundByIdInPrimary, s.DuplicateRowsIgnored));
            Assert.Equal(4, s.Total);
            Assert.Equal(result.Rows.Count, s.Total);
            Assert.False(s.Cancelled);
            Assert.False(s.SecondaryQueryFailed);
            Assert.Null(s.SecondaryQueryError);
            Assert.True(s.Elapsed > TimeSpan.Zero);
            Assert.Equal(s.PrimaryCount + s.FoundByIdInPrimary, s.Matching + s.Different + s.Missing);
            Assert.Equal(s.SecondaryCount + s.FoundByIdInSecondary, s.Matching + s.Different + s.Extra);
        }

        [Fact]
        public void A_row_is_missing_only_after_the_lookup_by_id_and_extra_only_after_the_reverse_lookup()
        {
            Harness h = FourStatuses();

            h.Run();

            QueryExpression missingCheck = Assert.Single(h.Secondary.Queries);
            QueryExpression extraCheck = Assert.Single(h.Primary.Queries);
            Assert.Equal(new object[] { Gone }, missingCheck.Criteria.Conditions.Single().Values);
            Assert.Equal(new object[] { Added }, extraCheck.Criteria.Conditions.Single().Values);
        }

        [Fact]
        public void The_lookup_by_id_asks_for_every_column_by_primary_key()
        {
            Harness h = FourStatuses();

            h.Run();

            QueryExpression query = Assert.Single(h.Secondary.Queries);
            Assert.Equal("account", query.EntityName);
            Assert.True(query.ColumnSet.AllColumns);
            ConditionExpression condition = Assert.Single(query.Criteria.Conditions);
            Assert.Equal(("accountid", ConditionOperator.In), (condition.AttributeName, condition.Operator));
            Assert.All(condition.Values, v => Assert.IsType<Guid>(v));
            Assert.Empty(query.Criteria.Filters);
            Assert.Empty(query.LinkEntities);
            Assert.Empty(query.Orders);
        }

        [Fact]
        public void Both_environments_run_the_same_rewritten_view_query()
        {
            Harness h = FourStatuses();

            h.Run();

            string fetch = Assert.Single(h.Primary.Fetches);
            Assert.Equal(fetch, Assert.Single(h.Secondary.Fetches));
            XElement root = XElement.Parse(fetch);
            Assert.Equal(("5000", "1"), ((string)root.Attribute("count"), (string)root.Attribute("page")));
            Assert.Null(root.Attribute("paging-cookie"));
            XElement entity = root.Element("entity");
            Assert.NotNull(entity.Element("all-attributes"));
            Assert.Empty(entity.Elements("attribute"));
            Assert.Equal("emailaddress1", (string)entity.Element("link-entity").Element("attribute").Attribute("name"));
            Assert.Equal("statecode", (string)entity.Element("filter").Element("condition").Attribute("attribute"));
        }

        [Fact]
        public void Nothing_is_ever_written_to_either_environment()
        {
            Harness h = FourStatuses();

            h.Run();

            Assert.Empty(h.Primary.Writes);
            Assert.Empty(h.Secondary.Writes);
            Assert.All(h.Primary.Executed.Concat(h.Secondary.Executed), r => Assert.IsType<RetrieveMultipleRequest>(r));
        }

        [Fact]
        public void Empty_views_give_an_empty_result_without_lookups()
        {
            var h = new Harness();

            CompareResult result = h.Run();

            Assert.Empty(result.Rows);
            Assert.Equal(0, result.Summary.Total);
            Assert.Empty(h.Primary.Queries);
            Assert.Empty(h.Secondary.Queries);
            Assert.Equal(ComparePhase.Done, h.Progress.Last().Phase);
        }

        // ---- the attribute rules ----

        [Fact]
        public void A_null_value_and_an_absent_attribute_are_equal()
        {
            var h = new Harness();
            h.Primary.Add(Account(Same, "Contoso").Changed(("description", (object)null), ("accountnumber", "")));
            h.Primary.Get("account", Same)["description"] = null;   // explicitly null, as some callers return it
            h.Secondary.Add(Account(Same, "Contoso"));

            CompareResult result = h.Run();

            Assert.Equal(RowStatus.Match, Assert.Single(result.Rows).Status);
        }

        [Fact]
        public void A_value_on_one_side_only_is_a_difference_either_way()
        {
            var h = new Harness();
            h.Primary.Add(Account(Id(1), "A", ("accountnumber", "100")));
            h.Secondary.Add(Account(Id(1), "A"));
            h.Primary.Add(Account(Id(2), "B"));
            h.Secondary.Add(Account(Id(2), "B", ("telephone1", "x"), ("description", "set in the secondary")));

            CompareResult result = h.Run();

            Assert.Equal(new[] { "accountnumber" }, Harness.Row(result, Id(1)).DifferingAttributes);
            Assert.Equal(new[] { "description" }, Harness.Row(result, Id(2)).DifferingAttributes);   // telephone1 is not in the primary metadata
        }

        [Fact]
        public void The_default_ignored_attributes_never_make_a_row_different()
        {
            var h = new Harness();
            var now = new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);
            h.Primary.Add(Account(Same, "Contoso",
                ("createdon", now), ("modifiedon", now), ("createdby", Ref("systemuser", Id(10))), ("modifiedby", Ref("systemuser", Id(10))),
                ("ownerid", Ref("systemuser", Id(10))), ("owninguser", Ref("systemuser", Id(10))), ("owningbusinessunit", Ref("businessunit", Id(11))),
                ("versionnumber", 100L), ("importsequencenumber", 1), ("overriddencreatedon", now), ("timezoneruleversionnumber", 4),
                ("utcconversiontimezonecode", 85)));
            h.Secondary.Add(Account(Same, "Contoso",
                ("createdon", now.AddDays(1)), ("modifiedon", now.AddDays(1)), ("createdby", Ref("systemuser", Id(20))), ("modifiedby", Ref("systemuser", Id(20))),
                ("ownerid", Ref("team", Id(21))), ("owningteam", Ref("team", Id(21))), ("owningbusinessunit", Ref("businessunit", Id(22))),
                ("versionnumber", 900L), ("createdonbehalfby", Ref("systemuser", Id(23))), ("modifiedonbehalfby", Ref("systemuser", Id(23)))));

            CompareResult result = h.Run();

            Assert.Equal(RowStatus.Match, Assert.Single(result.Rows).Status);
        }

        [Fact]
        public void The_ignored_list_can_be_changed()
        {
            var h = new Harness();
            h.Primary.Add(Account(Same, "Contoso", ("accountnumber", "1"), ("modifiedon", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc))));
            h.Secondary.Add(Account(Same, "Contoso", ("accountnumber", "2"), ("modifiedon", new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc))));
            h.Options.SetIgnoredAttributes("AccountNumber");

            RowComparison row = Assert.Single(h.Run().Rows);

            Assert.Equal(RowStatus.Different, row.Status);
            Assert.Equal(new[] { "modifiedon" }, row.DifferingAttributes);   // no longer ignored; accountnumber is
        }

        [Fact]
        public void Derived_unreadable_and_unknown_attributes_and_linked_values_are_not_compared()
        {
            var h = new Harness();
            h.Both(Record("contact", Id(50), ("fullname", "Jane"), ("emailaddress1", "jane@primary.example")));
            h.Secondary.Add(Record("contact", Id(50), ("fullname", "Jane"), ("emailaddress1", "jane@secondary.example")));
            h.Primary.Add(Account(Same, "Contoso",
                ("revenue_base", new Money(10m)), ("entityimage_url", "/Image/download.aspx?Timestamp=1"), ("entityimage_timestamp", 1L),
                ("primarycontactid", Ref("contact", Id(50), "Jane")), ("primarycontactidname", "Jane"), ("new_secret", "a"), ("new_unknown", "a")));
            h.Secondary.Add(Account(Same, "Contoso",
                ("revenue_base", new Money(12m)), ("entityimage_url", "/Image/download.aspx?Timestamp=2"), ("entityimage_timestamp", 2L),
                ("primarycontactid", Ref("contact", Id(50), "Jane Renamed")), ("primarycontactidname", "Jane Renamed"), ("new_secret", "b"),
                ("new_unknown", "b")));

            CompareResult result = h.Run();

            RowComparison row = Assert.Single(result.Rows);
            Assert.Equal(RowStatus.Match, row.Status);
            Assert.Equal("jane@primary.example", result.CellText(row, "pc.emailaddress1"));   // the linked column differs, but it is the contact's
        }

        public static IEnumerable<object[]> TypeRuleCases()
        {
            var utc = new DateTime(2024, 3, 1, 14, 30, 5, DateTimeKind.Utc);
            object[] Case(string attribute, object primary, object secondary, bool different) => new[] { attribute, primary, secondary, (object)different };

            yield return Case("primarycontactid", Ref("contact", Id(9), "Jane"), Ref("contact", Id(9), "Janet"), false);
            yield return Case("primarycontactid", Ref("contact", Id(9)), Ref("contact", Id(8)), true);
            yield return Case("parentaccountid", Ref("account", Id(9)), null, true);
            yield return Case("industrycode", Opt(1), Opt(1), false);
            yield return Case("industrycode", Opt(1), Opt(2), true);
            yield return Case("new_tags", new OptionSetValueCollection { Opt(1), Opt(3) }, new OptionSetValueCollection { Opt(3), Opt(1) }, false);
            yield return Case("new_tags", new OptionSetValueCollection { Opt(1), Opt(3) }, new OptionSetValueCollection { Opt(1) }, true);
            yield return Case("revenue", new Money(10.5m), new Money(10.50m), false);
            yield return Case("revenue", new Money(10.5m), new Money(11m), true);
            yield return Case("new_reviewdate", utc, utc.ToLocalTime(), false);
            yield return Case("new_reviewdate", utc, utc.AddSeconds(1), true);
            yield return Case("exchangerate", 1.0m, 1.00m, false);
            yield return Case("exchangerate", 1.0m, 1.1m, true);
            yield return Case("address1_latitude", 51.5d, 51.5d, false);
            yield return Case("address1_latitude", 51.5d, 51.6d, true);
            yield return Case("numberofemployees", 10, 10, false);
            yield return Case("numberofemployees", 10, 11, true);
            yield return Case("new_bignumber", 5L, 5L, false);
            yield return Case("new_bignumber", 5L, 6L, true);
            yield return Case("creditonhold", false, false, false);
            yield return Case("creditonhold", false, true, true);
            yield return Case("creditonhold", false, null, true);
            yield return Case("new_externalid", Id(7), Id(7), false);
            yield return Case("new_externalid", Id(7), Id(6), true);
            yield return Case("accountnumber", "AB-1", "AB-1", false);
            yield return Case("accountnumber", "AB-1", "ab-1", true);
            yield return Case("accountnumber", "AB-1", "AB-1 ", true);
            yield return Case("description", null, string.Empty, false);
            yield return Case("entityimage", new byte[] { 1, 2 }, new byte[] { 1, 2 }, false);
            yield return Case("entityimage", new byte[] { 1, 2 }, new byte[] { 1, 3 }, true);
            yield return Case("new_document", Id(5), Id(6), false);   // a file on both sides: compared by presence
            yield return Case("new_document", Id(5), null, true);
            yield return Case("new_calculated", "x", "y", true);       // calculated columns are compared
            yield return Case("statecode", Opt(0), Opt(0), false);
        }

        [Theory]
        [MemberData(nameof(TypeRuleCases))]
        public void The_type_rules_decide_the_status(string attribute, object primary, object secondary, bool different)
        {
            var h = new Harness();
            h.Primary.Add(Account(Same, "Contoso").Changed((attribute, primary)));
            h.Secondary.Add(Account(Same, "Contoso").Changed((attribute, secondary)));

            RowComparison row = Assert.Single(h.Run(TestData.AllAccountsView).Rows);

            Assert.Equal(different ? RowStatus.Different : RowStatus.Match, row.Status);
            Assert.Equal(different ? new[] { attribute } : Array.Empty<string>(), row.DifferingAttributes);
        }

        [Fact]
        public void Differing_attributes_are_listed_in_ordinal_order()
        {
            var h = new Harness();
            h.Primary.Add(Account(Same, "Contoso", ("revenue", new Money(1m)), ("accountnumber", "1"), ("description", "d")));
            h.Secondary.Add(Account(Same, "Contoso", ("revenue", new Money(2m)), ("accountnumber", "2"), ("description", "e")));

            RowComparison row = Assert.Single(h.Run().Rows);

            Assert.Equal(new[] { "accountnumber", "description", "revenue" }, row.DifferingAttributes);
        }

        [Fact]
        public void Party_lists_of_activities_are_compared_by_their_parties()
        {
            var h = new Harness();
            Guid jane = Id(50), contoso = Id(60);
            h.Primary.Add(Record("email", Id(1), ("subject", "Hello"), ("to", Parties(Party(Ref("contact", jane)), Party(Ref("account", contoso))))));
            h.Secondary.Add(Record("email", Id(1), ("subject", "Hello"), ("to", Parties(Party(Ref("account", contoso, "Contoso")), Party(Ref("contact", jane))))));
            h.Primary.Add(Record("email", Id(2), ("subject", "Bye"), ("to", Parties(Party(Ref("contact", jane))))));
            h.Secondary.Add(Record("email", Id(2), ("subject", "Bye"), ("to", Parties(Party(Ref("contact", jane)), Party(null, addressUsed: "x@example.com")))));

            CompareResult result = h.Run("<fetch><entity name=\"email\"><attribute name=\"subject\" /><order attribute=\"subject\" /></entity></fetch>", "email");

            Assert.Equal(RowStatus.Match, Harness.Row(result, Id(1)).Status);
            Assert.Equal(new[] { "to" }, Harness.Row(result, Id(2)).DifferingAttributes);
            Assert.Empty(h.Secondary.Queries);   // both emails are in both views: nothing to look up
        }

        // ---- step 3: primary rows the secondary's view did not return ----

        [Fact]
        public void A_secondary_record_outside_the_view_filter_is_found_by_id_and_compared()
        {
            var h = new Harness();
            h.Primary.Add(Account(Same, "Contoso"));
            h.Secondary.Add(Account(Same, "Contoso", ("statecode", Opt(1)), ("statuscode", Opt(2))));   // inactive: the view leaves it out

            CompareResult result = h.Run();

            RowComparison row = Assert.Single(result.Rows);
            Assert.Equal(RowStatus.Different, row.Status);
            Assert.Equal(new[] { "statecode", "statuscode" }, row.DifferingAttributes);
            Assert.Equal((true, false), (row.InPrimaryView, row.InSecondaryView));
            Assert.Equal((1, 0, 0), (result.Summary.PrimaryCount, result.Summary.SecondaryCount, result.Summary.Missing));
            Assert.Equal(1, result.Summary.FoundByIdInSecondary);
            Assert.Same(row.Primary, row.DisplayRecord);
        }

        [Fact]
        public void A_record_found_by_id_matches_when_only_linked_data_kept_it_out_of_the_view()
        {
            const string withContactEmail =
                "<fetch><entity name=\"account\"><attribute name=\"name\" /><order attribute=\"name\" />" +
                "<link-entity name=\"contact\" from=\"contactid\" to=\"primarycontactid\" alias=\"pc\">" +
                "<attribute name=\"emailaddress1\" /><filter><condition attribute=\"emailaddress1\" operator=\"not-null\" /></filter>" +
                "</link-entity></entity></fetch>";
            var h = new Harness();
            h.Both(Account(Same, "Contoso", ("primarycontactid", Ref("contact", Id(50)))));
            h.Primary.Add(Record("contact", Id(50), ("emailaddress1", "jane@example.com")));
            h.Secondary.Add(Record("contact", Id(50)));   // no email in the secondary: its view leaves the account out

            CompareResult result = h.Run(withContactEmail);

            RowComparison row = Assert.Single(result.Rows);
            Assert.Equal(RowStatus.Match, row.Status);
            Assert.False(row.InSecondaryView);
            Assert.Equal("jane@example.com", result.CellText(row, "pc.emailaddress1"));
        }

        [Fact]
        public void Lookups_by_id_are_batched_500_ids_a_query()
        {
            var h = new Harness();
            for (int i = 1; i <= 1201; i++)
            {
                h.Primary.Add(Account(Id(i), "Account " + i.ToString("0000")));
                h.Secondary.Add(Account(Id(i), "Account " + i.ToString("0000"), ("statecode", Opt(1))));   // none in the secondary's view
            }

            CompareResult result = h.Run();

            Assert.Equal(new[] { 500, 500, 201 }, h.Secondary.Queries.Select(q => q.Criteria.Conditions.Single().Values.Count));
            Assert.Equal(1201, h.Secondary.Queries.SelectMany(q => q.Criteria.Conditions.Single().Values).Distinct().Count());
            Assert.Equal((1201, 1201), (result.Summary.FoundByIdInSecondary, result.Summary.Different));
            Assert.Equal(new[] { (1, 500), (2, 1000), (3, 1201) },
                h.Progress.Where(p => p.Phase == ComparePhase.CheckingMissing && p.PageNumber > 0).Select(p => (p.PageNumber, p.RecordsSoFar)));
            Assert.All(h.Progress.Where(p => p.Phase == ComparePhase.CheckingMissing), p => Assert.Equal(1201, p.Total));
        }

        [Fact]
        public void The_lookup_batch_size_can_be_changed()
        {
            var h = new Harness();
            for (int i = 1; i <= 5; i++) h.Primary.Add(Account(Id(i), "Account " + i));
            h.Options.LookupBatchSize = 2;

            CompareResult result = h.Run();

            Assert.Equal(new[] { 2, 2, 1 }, h.Secondary.Queries.Select(q => q.Criteria.Conditions.Single().Values.Count));
            Assert.Equal(5, result.Summary.Missing);
        }

        // ---- step 4: secondary rows the primary's view did not return ----

        [Fact]
        public void A_secondary_view_row_that_exists_outside_the_primary_view_is_compared_not_extra()
        {
            var h = new Harness();
            h.Primary.Add(Account(Same, "Contoso", ("statecode", Opt(1)), ("statuscode", Opt(2))));   // inactive in the primary
            h.Secondary.Add(Account(Same, "Contoso"));                                              // active in the secondary

            CompareResult result = h.Run();

            RowComparison row = Assert.Single(result.Rows);
            Assert.Equal(RowStatus.Different, row.Status);
            Assert.Equal((false, true), (row.InPrimaryView, row.InSecondaryView));
            Assert.Same(row.Secondary, row.DisplayRecord);   // the record a view returned
            Assert.Equal((0, 1, 0), (result.Summary.PrimaryCount, result.Summary.FoundByIdInPrimary, result.Summary.Extra));
        }

        [Fact]
        public void Rows_only_the_secondary_view_returned_follow_the_primary_rows_in_secondary_order()
        {
            var h = new Harness();
            h.Both(Account(Id(1), "M"));
            h.Secondary.Add(Account(Id(2), "A Extra"));
            h.Primary.Add(Account(Id(3), "B", ("statecode", Opt(1))));
            h.Secondary.Add(Account(Id(3), "B"));
            h.Secondary.Add(Account(Id(4), "Z Extra"));

            CompareResult result = h.Run();

            Assert.Equal(new[] { Id(1), Id(2), Id(3), Id(4) }, result.Rows.Select(r => r.Id));
            Assert.Equal(new[] { RowStatus.Match, RowStatus.Extra, RowStatus.Different, RowStatus.Extra }, result.Rows.Select(r => r.Status));
        }

        // ---- paging ----

        [Fact]
        public void Both_views_are_read_page_by_page_with_the_paging_cookie()
        {
            var h = new Harness();
            for (int i = 1; i <= 5; i++) h.Both(Account(Id(i), "Account " + i));
            h.Options.PageSize = 2;

            CompareResult result = h.Run();

            Assert.Equal(5, result.Summary.Matching);
            foreach (FakeOrganizationService side in new[] { h.Primary, h.Secondary })
            {
                List<XElement> fetches = side.Fetches.Select(XElement.Parse).ToList();
                Assert.Equal(new[] { "1", "2", "3" }, fetches.Select(f => (string)f.Attribute("page")));
                Assert.All(fetches, f => Assert.Equal("2", (string)f.Attribute("count")));
                Assert.Equal(new[] { null, "<cookie page=\"1\" />", "<cookie page=\"2\" />" }, fetches.Select(f => (string)f.Attribute("paging-cookie")));
            }
            Assert.Equal(new[] { (1, 2), (2, 4), (3, 5) },
                h.Progress.Where(p => p.Phase == ComparePhase.LoadingPrimary && p.PageNumber > 0).Select(p => (p.PageNumber, p.RecordsSoFar)));
            Assert.All(h.Progress.Where(p => p.Phase == ComparePhase.LoadingPrimary || p.Phase == ComparePhase.LoadingSecondary), p => Assert.Null(p.Total));
            Assert.Contains("Primary: 5 records in 3 page(s)", h.Log.Lines.First(l => l.StartsWith("Primary: 5 records", StringComparison.Ordinal)));
        }

        [Fact]
        public void A_distinct_view_is_read_by_page_number_without_a_paging_cookie()
        {
            const string distinctView =
                "<fetch distinct=\"true\"><entity name=\"account\"><attribute name=\"name\" /><order attribute=\"name\" /></entity></fetch>";
            var h = new Harness();
            for (int i = 1; i <= 5; i++) h.Both(Account(Id(i), "Account " + i));
            h.Options.PageSize = 2;

            CompareResult result = h.Run(distinctView);

            Assert.Equal(5, result.Summary.Matching);
            List<XElement> fetches = h.Primary.Fetches.Select(XElement.Parse).ToList();
            Assert.Equal(new[] { "1", "2", "3" }, fetches.Select(f => (string)f.Attribute("page")));
            Assert.All(fetches, f => Assert.Null(f.Attribute("paging-cookie")));
            Assert.All(fetches, f => Assert.Equal(new[] { "name", "accountid" }, f.Element("entity").Elements("order").Select(o => (string)o.Attribute("attribute"))));
            Assert.Contains(h.Log.Lines, l => l.Contains("by page number (distinct view: no paging cookie)"));
        }

        [Fact]
        public void Without_a_paging_cookie_from_the_server_the_pages_are_read_by_number()
        {
            var h = new Harness();
            for (int i = 1; i <= 5; i++) h.Both(Account(Id(i), "Account " + i));
            h.Options.PageSize = 2;
            h.Primary.ReturnPagingCookies = false;

            CompareResult result = h.Run();

            Assert.Equal(5, result.Summary.Matching);
            Assert.Equal(3, h.Primary.Fetches.Count);
            Assert.All(h.Primary.Fetches, f => Assert.Null(XElement.Parse(f).Attribute("paging-cookie")));
            Assert.Single(h.Log.Lines, l => l.Contains("Primary: the server returned no paging cookie"));
        }

        [Fact]
        public void A_record_repeated_by_a_one_to_many_link_is_compared_once()
        {
            const string withContacts =
                "<fetch><entity name=\"account\"><attribute name=\"name\" />" +
                "<link-entity name=\"contact\" from=\"parentcustomerid\" to=\"accountid\" alias=\"c\"><attribute name=\"fullname\" /></link-entity>" +
                "</entity></fetch>";
            var h = new Harness();
            h.Both(Account(Same, "Contoso"),
                   Record("contact", Id(50), ("fullname", "Jane"), ("parentcustomerid", Ref("account", Same))),
                   Record("contact", Id(51), ("fullname", "John"), ("parentcustomerid", Ref("account", Same))));

            CompareResult result = h.Run(withContacts);

            Assert.Equal(RowStatus.Match, Assert.Single(result.Rows).Status);
            Assert.Equal((1, 1, 2), (result.Summary.PrimaryCount, result.Summary.SecondaryCount, result.Summary.DuplicateRowsIgnored));
            Assert.Equal(2, h.Log.Messages(LogLevel.Warning).Count(l => l.Contains("repeated a record already read")));
        }

        // ---- failures ----

        [Fact]
        public void A_failing_secondary_view_is_a_warning_and_every_primary_row_is_looked_up_by_id()
        {
            var h = new Harness();
            for (int i = 1; i <= 3; i++) h.Both(Account(Id(i), "Account " + i));
            h.Secondary.FailFetchPages.Add(1);
            h.Secondary.FetchFailure = FakeOrganizationService.Fault(FakeOrganizationService.GenericFailure, "'contact' entity doesn't contain attribute with Name = 'emailaddress1'");

            CompareResult result = h.Run();

            Assert.Equal(3, result.Summary.Matching);
            Assert.True(result.Summary.SecondaryQueryFailed);
            Assert.Contains("emailaddress1", result.Summary.SecondaryQueryError);
            Assert.Equal((0, 3), (result.Summary.SecondaryCount, result.Summary.FoundByIdInSecondary));
            Assert.Contains(h.Log.Messages(LogLevel.Warning), l => l.StartsWith("Secondary: the view query failed", StringComparison.Ordinal));
            Assert.Contains(h.Log.Messages(LogLevel.Warning), l => l.StartsWith("Result: ", StringComparison.Ordinal));   // not a clean success
        }

        [Fact]
        public void The_rows_read_before_the_secondary_view_failed_are_kept()
        {
            var h = new Harness();
            h.Secondary.Add(Account(Added, "A Extra"));
            foreach (string name in new[] { "B", "C", "D", "E" }) h.Both(Account(Guid.NewGuid(), name));
            h.Options.PageSize = 2;
            h.Secondary.FailFetchPages.Add(2);

            CompareResult result = h.Run();

            Assert.True(result.Summary.SecondaryQueryFailed);
            Assert.Equal((2, 3), (result.Summary.SecondaryCount, result.Summary.FoundByIdInSecondary));   // page 1 = A Extra, B
            Assert.Equal((4, 1), (result.Summary.Matching, result.Summary.Extra));
            Assert.Equal(Added, result.Rows.Last().Id);
        }

        [Fact]
        public void A_failing_primary_view_fails_the_run_and_is_logged()
        {
            var h = new Harness();
            h.Primary.FailFetchPages.Add(1);

            var error = Assert.Throws<FaultException<OrganizationServiceFault>>(() => h.Run());

            Assert.Contains("Compare failed: " + error.Message, h.Log.Messages(LogLevel.Error));
            Assert.Empty(h.Secondary.Executed);
        }

        [Fact]
        public void A_secondary_without_the_entity_fails_the_run_at_the_lookup_by_id()
        {
            var h = new Harness();
            h.Primary.Add(Account(Same, "Contoso"));
            h.Secondary.FailRetrieveMultiple["account"] = FakeOrganizationService.Fault(FakeOrganizationService.GenericFailure, "Could not find an entity with specified entity name: account");

            Assert.Throws<FaultException<OrganizationServiceFault>>(() => h.Run());

            Assert.Contains(h.Log.Messages(LogLevel.Warning), l => l.StartsWith("Secondary: the view query failed", StringComparison.Ordinal));
            Assert.Contains(h.Log.Messages(LogLevel.Error), l => l.StartsWith("Compare failed: Could not find an entity", StringComparison.Ordinal));
        }

        [Fact]
        public void An_entity_missing_from_the_primary_metadata_fails_before_any_query()
        {
            var h = new Harness();
            h.Schema.RemoveEntity("account");

            var error = Assert.Throws<InvalidOperationException>(() => h.Run());

            Assert.Contains("account does not exist in the primary environment", error.Message);
            Assert.Empty(h.Primary.Executed);
            Assert.Single(h.Log.Messages(LogLevel.Error));
        }

        [Fact]
        public void A_view_of_another_entity_or_an_aggregate_view_is_refused()
        {
            var h = new Harness();

            Assert.Throws<ArgumentException>(() => h.Run("<fetch><entity name=\"contact\"><attribute name=\"fullname\" /></entity></fetch>"));
            Assert.Throws<NotSupportedException>(() => h.Run("<fetch aggregate=\"true\"><entity name=\"account\"><attribute name=\"accountid\" alias=\"n\" aggregate=\"count\" /></entity></fetch>"));
            Assert.Empty(h.Primary.Executed);
        }

        [Theory]
        [InlineData(0, 500)]
        [InlineData(5001, 500)]
        [InlineData(5000, 0)]
        [InlineData(5000, 2001)]
        public void Out_of_range_page_or_batch_sizes_are_refused_before_any_query(int pageSize, int batchSize)
        {
            var h = new Harness();
            h.Options.PageSize = pageSize;
            h.Options.LookupBatchSize = batchSize;

            Assert.Throws<ArgumentOutOfRangeException>(() => h.Run());
            Assert.Empty(h.Primary.Executed);
        }

        [Fact]
        public void The_arguments_are_checked()
        {
            var service = new FakeOrganizationService();
            var schema = new FakeSchemaProvider();
            Assert.Throws<ArgumentNullException>(() => new CompareEngine(null, service, schema, null, null));
            Assert.Throws<ArgumentNullException>(() => new CompareEngine(service, null, schema, null, null));
            Assert.Throws<ArgumentNullException>(() => new CompareEngine(service, service, null, null, null));

            var engine = new CompareEngine(service, service, schema, null, null);   // default options, no logger
            Assert.Throws<ArgumentException>(() => engine.Compare(" ", TestData.AccountView, null, CancellationToken.None));
            Assert.Throws<ArgumentException>(() => engine.Compare("account", "", null, CancellationToken.None));
        }

        [Fact]
        public void Runs_without_a_logger_or_progress()
        {
            Harness h = FourStatuses();

            CompareResult result = new CompareEngine(h.Primary, h.Secondary, h.Schema, null, null)
                .Compare("account", TestData.AccountView, null, CancellationToken.None);

            Assert.Equal(4, result.Summary.Total);
        }

        // ---- cancellation ----

        [Fact]
        public void Cancelling_while_the_primary_is_read_returns_an_empty_partial_result()
        {
            var h = new Harness();
            for (int i = 1; i <= 5; i++) h.Both(Account(Id(i), "Account " + i));
            h.Options.PageSize = 2;
            using (var cts = new CancellationTokenSource())
            {
                h.Primary.BeforeExecute = r => { if (h.Primary.Fetches.Count == 2) cts.Cancel(); };

                CompareResult result = h.Run(cts.Token);

                Assert.True(result.Summary.Cancelled);
                Assert.Empty(result.Rows);
                Assert.Equal((4, 4), (result.Summary.PrimaryCount, result.Summary.Unchecked));
                Assert.Empty(h.Secondary.Executed);
                Assert.Equal(ComparePhase.Done, h.Progress.Last().Phase);
                Assert.StartsWith("Cancelled: ", h.Progress.Last().Message);
            }
        }

        [Fact]
        public void Cancelling_while_the_secondary_is_read_keeps_the_rows_already_matched()
        {
            var h = new Harness();
            for (int i = 1; i <= 5; i++) h.Both(Account(Id(i), "Account " + i));
            h.Options.PageSize = 2;
            using (var cts = new CancellationTokenSource())
            {
                h.Secondary.BeforeExecute = r => { if (h.Secondary.Fetches.Count == 2) cts.Cancel(); };

                CompareResult result = h.Run(cts.Token);

                Assert.True(result.Summary.Cancelled);
                Assert.Equal(new[] { Id(1), Id(2), Id(3), Id(4) }, result.Rows.Select(r => r.Id));
                Assert.All(result.Rows, r => Assert.Equal(RowStatus.Match, r.Status));
                Assert.Equal(1, result.Summary.Unchecked);
                Assert.Empty(h.Secondary.Queries);   // no lookup by id after the cancellation
                Assert.Contains(h.Log.Messages(LogLevel.Warning), l => l.StartsWith("Cancelled by user: partial result, 1 record(s) not checked", StringComparison.Ordinal));
            }
        }

        [Fact]
        public void Cancelling_between_lookup_batches_keeps_the_batches_done()
        {
            var h = new Harness();
            for (int i = 1; i <= 5; i++)
            {
                h.Primary.Add(Account(Id(i), "Account " + i));
                h.Secondary.Add(Account(Id(i), "Account " + i, ("statecode", Opt(1))));
            }
            h.Options.LookupBatchSize = 2;
            using (var cts = new CancellationTokenSource())
            {
                h.Secondary.BeforeExecute = r => { if (r is RetrieveMultipleRequest m && m.Query is QueryExpression) cts.Cancel(); };

                CompareResult result = h.Run(cts.Token);

                Assert.True(result.Summary.Cancelled);
                Assert.Single(h.Secondary.Queries);
                Assert.Equal(new[] { Id(1), Id(2) }, result.Rows.Select(r => r.Id));
                Assert.All(result.Rows, r => Assert.Equal(RowStatus.Different, r.Status));
                Assert.Equal(3, result.Summary.Unchecked);
                Assert.Empty(h.Primary.Queries);
            }
        }

        [Fact]
        public void Cancelling_while_comparing_stops_at_the_next_thousand_rows()
        {
            var h = new Harness();
            for (int i = 1; i <= 2500; i++) h.Both(Account(Id(i), "Account " + i.ToString("0000")));
            using (var cts = new CancellationTokenSource())
            {
                h.OnProgress = p => { if (p.Phase == ComparePhase.Comparing && p.RecordsSoFar == 1000) cts.Cancel(); };

                CompareResult result = h.Run(cts.Token);

                Assert.True(result.Summary.Cancelled);
                Assert.Equal(1000, result.Rows.Count);
                Assert.Equal((1000, 1500), (result.Summary.Matching, result.Summary.Unchecked));
                Assert.Contains(h.Log.Messages(LogLevel.Warning), l => l.StartsWith("Cancelled while comparing, after 1000 of 2500", StringComparison.Ordinal));
            }
        }

        [Fact]
        public void A_server_call_cancelled_with_the_run_counts_as_a_cancellation()
        {
            var h = new Harness();
            h.Both(Account(Same, "Contoso"));
            using (var cts = new CancellationTokenSource())
            {
                h.Secondary.BeforeExecute = r =>
                {
                    cts.Cancel();
                    throw new OperationCanceledException(cts.Token);
                };

                CompareResult result = h.Run(cts.Token);

                Assert.True(result.Summary.Cancelled);
                Assert.Equal(1, result.Summary.Unchecked);
                Assert.False(result.Summary.SecondaryQueryFailed);
            }
        }

        // ---- progress and log ----

        [Fact]
        public void Progress_reports_every_phase_in_order_and_ends_with_Done()
        {
            Harness h = FourStatuses();

            CompareResult result = h.Run();

            List<ComparePhase> phases = h.Progress.Select(p => p.Phase).ToList();
            Assert.Equal(new[] { ComparePhase.LoadingPrimary, ComparePhase.LoadingSecondary, ComparePhase.CheckingMissing, ComparePhase.CheckingExtras,
                                 ComparePhase.Comparing, ComparePhase.Done }, phases.Distinct());
            Assert.Equal(phases.OrderBy(p => p), phases);   // never goes back
            CompareProgress done = h.Progress.Last();
            Assert.Equal((4, 4), (done.RecordsSoFar, done.Total.Value));
            Assert.Equal("Done: 4 rows - matching 1, different 1, missing 1, extra 1", done.Message);
            Assert.All(h.Progress, p => Assert.False(string.IsNullOrEmpty(p.Message)));
            Assert.Equal(1, h.Progress.First(p => p.Phase == ComparePhase.CheckingMissing).Total);
            Assert.Equal(4, h.Progress.First(p => p.Phase == ComparePhase.Comparing).Total);
            Assert.Equal(result.Summary.Total, done.RecordsSoFar);
        }

        [Fact]
        public void Comparing_reports_progress_every_thousand_rows()
        {
            var h = new Harness();
            for (int i = 1; i <= 2100; i++) h.Both(Account(Id(i), "Account " + i.ToString("0000")));

            h.Run();

            Assert.Equal(new[] { 0, 1000, 2000 }, h.Progress.Where(p => p.Phase == ComparePhase.Comparing).Select(p => p.RecordsSoFar));
        }

        [Fact]
        public void The_log_has_a_header_the_page_counts_and_the_result()
        {
            Harness h = FourStatuses();

            h.Run();

            IReadOnlyList<string> lines = h.Log.Lines;
            Assert.StartsWith("Comparing Account (account) records matched on accountid: ", lines[0]);
            Assert.Contains("ignored (createdby, createdon, createdonbehalfby, importsequencenumber, modifiedby, modifiedon", lines[0]);
            Assert.Contains("Primary: page 1: 3 rows (3 records so far).", lines);
            Assert.Contains("Secondary: page 1: 3 rows (3 records so far).", lines);
            Assert.Contains(lines, l => l.StartsWith("Secondary: 0 of the 1 found by id, 1 not found", StringComparison.Ordinal));
            Assert.Contains(lines, l => l.StartsWith("Primary: 0 of the 1 found by id, 1 not found", StringComparison.Ordinal));
            Assert.Contains("Result: 4 rows - matching 1, different 1, missing 1, extra 1.", h.Log.Messages(LogLevel.Success));
            Assert.Contains("Most frequent differences: accountnumber (1).", lines);
            Assert.StartsWith("Elapsed: ", lines.Last());
            Assert.Empty(h.Log.Messages(LogLevel.Error));
        }

        // ---- the result ----

        [Fact]
        public void The_result_keeps_a_copy_of_the_options_and_the_primary_schema()
        {
            Harness h = FourStatuses();

            CompareResult result = h.Run();
            h.Options.IgnoredAttributes.Add("accountnumber");

            Assert.False(result.Options.IsIgnored("accountnumber"));
            Assert.Equal(("account", "accountid"), (result.EntityLogicalName, result.PrimaryIdAttribute));
            Assert.Same(h.Schema.GetEntity("account"), result.Schema);
            Assert.Equal(new[] { Diff }, result.RowsWithStatus(RowStatus.Different).Select(r => r.Id));
        }

        [Fact]
        public void Reevaluate_redecides_the_pairs_in_memory_with_other_options()
        {
            Harness h = FourStatuses();
            CompareResult result = h.Run();
            int requests = h.Primary.Executed.Count + h.Secondary.Executed.Count;
            var options = new CompareOptions();
            options.IgnoredAttributes.Add("accountnumber");

            CompareResult again = result.Reevaluate(options);

            Assert.Equal(requests, h.Primary.Executed.Count + h.Secondary.Executed.Count);   // nothing read again
            Assert.Equal(new[] { RowStatus.Match, RowStatus.Match, RowStatus.Missing, RowStatus.Extra }, again.Rows.Select(r => r.Status));
            Assert.Equal((2, 0, 1, 1), (again.Summary.Matching, again.Summary.Different, again.Summary.Missing, again.Summary.Extra));
            Assert.Equal(result.Summary.PrimaryCount, again.Summary.PrimaryCount);
            Assert.Equal((1, 1), (result.Summary.Matching, result.Summary.Different));   // the original is unchanged
            Assert.True(again.Options.IsIgnored("accountnumber"));
            Assert.Same(result.Rows[2], again.Rows[2]);
            Assert.Throws<ArgumentNullException>(() => result.Reevaluate(null));
        }

        [Fact]
        public void Grid_cells_come_from_the_record_a_view_returned()
        {
            var h = new Harness();
            h.Both(Record("contact", Id(50), ("emailaddress1", "jane@example.com")));
            h.Both(Account(Same, "Contoso", ("primarycontactid", Ref("contact", Id(50), "Jane"))));
            h.Secondary.Add(Account(Added, "New in secondary", ("accountnumber", "S-1"), ("primarycontactid", Ref("contact", Id(50), "Jane"))));
            h.Primary.Get("account", Same).FormattedValues["primarycontactid"] = "Jane Doe";

            CompareResult result = h.Run();

            RowComparison same = Harness.Row(result, Same), extra = Harness.Row(result, Added);
            Assert.Equal("Contoso", result.CellText(same, "name"));
            Assert.Equal("jane@example.com", result.CellText(same, "pc.emailaddress1"));
            Assert.Equal("Jane Doe", result.CellText(same, "primarycontactid"));   // formatted value first
            Assert.Equal("S-1", result.CellText(extra, "accountnumber"));           // from the secondary
            Assert.Equal("jane@example.com", result.CellText(extra, "pc.emailaddress1"));
            Assert.Equal(string.Empty, result.CellText(same, "accountnumber"));
            Assert.Equal(string.Empty, result.CellText(null, "name"));
            Assert.Equal(string.Empty, result.CellText(same, null));
        }

        [Fact]
        public void A_column_named_after_a_main_entity_alias_shows_the_attribute()
        {
            const string aliased = "<fetch><entity name=\"account\"><attribute name=\"accountnumber\" alias=\"num\" /><attribute name=\"name\" /></entity></fetch>";
            var h = new Harness();
            h.Both(Account(Same, "Contoso", ("accountnumber", "A-1")));

            CompareResult result = h.Run(aliased);

            Assert.Equal("A-1", result.CellText(Assert.Single(result.Rows), "num"));
            Assert.Equal("accountnumber", result.ColumnAliases["num"]);
        }
    }
}
