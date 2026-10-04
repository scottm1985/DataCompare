using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using MyscotekDataCompare.Core;
using MyscotekDataCompare.Core.Schema;
using MyscotekDataCompare.Core.Services;
using MyscotekDataCompare.Tests.Fakes;
using Xunit;
using CompareOptions = MyscotekDataCompare.Core.CompareOptions;   // System.Globalization has a CompareOptions too
using static MyscotekDataCompare.Tests.Fakes.TestData;

namespace MyscotekDataCompare.Tests
{
    /// <summary>SPEC 5.8: the detail pane's lines for a row.</summary>
    public class DetailBuilderTests
    {
        private static readonly Guid Row1 = Id(1);
        private static readonly EntitySchema Account = StandardSchema().GetEntity("account");

        private static IList<ColumnComparison> Details(Entity primary, Entity secondary, CompareOptions options = null)
        {
            RowStatus status = primary == null ? RowStatus.Extra : secondary == null ? RowStatus.Missing : RowStatus.Different;
            return DetailBuilder.Build(new RowComparison(Row1, status, primary, secondary, null, primary != null, secondary != null), Account, options);
        }

        private static ColumnComparison Line(IEnumerable<ColumnComparison> lines, string name) => lines.Single(l => l.LogicalName == name);

        [Fact]
        public void The_primary_key_comes_first_then_the_columns_by_display_name()
        {
            IList<ColumnComparison> lines = Details(TestData.Account(Row1, "Contoso"), TestData.Account(Row1, "Contoso"));

            ColumnComparison key = lines[0];
            Assert.Equal(("accountid", "Account", true, false, false, DetailBuilder.PrimaryKeyNote),
                (key.LogicalName, key.DisplayName, key.IsPrimaryKey, key.IsDifferent, key.IsCompared, key.Note));
            List<ColumnComparison> rest = lines.Skip(1).ToList();
            Assert.Equal(rest.OrderBy(l => l.DisplayName, StringComparer.CurrentCultureIgnoreCase).ThenBy(l => l.LogicalName, StringComparer.Ordinal).Select(l => l.LogicalName),
                rest.Select(l => l.LogicalName));
            Assert.Equal("Account Name", Line(lines, "name").DisplayName);
            Assert.Equal("new_bignumber", Line(lines, "new_bignumber").DisplayName);   // no label: the logical name
            Assert.Equal(AttributeTypeCode.BigInt, Line(lines, "new_bignumber").AttributeType);
        }

        [Fact]
        public void Every_readable_column_is_listed_but_derived_ones_only_with_a_value()
        {
            IList<ColumnComparison> lines = Details(
                TestData.Account(Row1, "Contoso", ("revenue_base", new Money(10m))),
                TestData.Account(Row1, "Contoso"));

            Assert.Contains(lines, l => l.LogicalName == "description");               // blank on both sides, still listed
            Assert.DoesNotContain(lines, l => l.LogicalName == "new_secret");           // not valid for read
            Assert.DoesNotContain(lines, l => l.LogicalName == "entityimage_url");      // derived, no value
            ColumnComparison derived = Line(lines, "revenue_base");
            Assert.Equal((true, false, false, "Derived from revenue"), (derived.IsDifferent, derived.IsCompared, derived.IsMismatch, derived.Note));
            Assert.Equal(Account.Attributes.Values.Count(a => a.IsValidForRead && a.AttributeOf == null) + 1, lines.Count);
        }

        [Fact]
        public void Ignored_columns_show_their_values_greyed_and_are_never_a_mismatch()
        {
            var monday = new DateTime(2026, 9, 28, 9, 0, 0, DateTimeKind.Utc);
            IList<ColumnComparison> lines = Details(
                TestData.Account(Row1, "Contoso", ("modifiedon", monday)).Formatted("modifiedon", "28/09/2026 10:00"),
                TestData.Account(Row1, "Contoso", ("modifiedon", monday.AddDays(1))).Formatted("modifiedon", "29/09/2026 10:00"));

            ColumnComparison modified = Line(lines, "modifiedon");
            Assert.Equal((true, true, false, false, DetailBuilder.IgnoredNote),
                (modified.IsIgnored, modified.IsDifferent, modified.IsCompared, modified.IsMismatch, modified.Note));
            Assert.Equal(("28/09/2026 10:00", "29/09/2026 10:00"), (modified.PrimaryText, modified.SecondaryText));
            Assert.Equal(monday, modified.PrimaryRaw);
        }

        [Fact]
        public void A_custom_ignored_list_is_honoured()
        {
            var options = new CompareOptions();
            options.SetIgnoredAttributes("accountnumber");

            IList<ColumnComparison> lines = Details(
                TestData.Account(Row1, "Contoso", ("accountnumber", "1"), ("modifiedon", DateTime.UtcNow)),
                TestData.Account(Row1, "Contoso", ("accountnumber", "2")), options);

            Assert.False(Line(lines, "accountnumber").IsMismatch);
            Assert.True(Line(lines, "accountnumber").IsIgnored);
            Assert.True(Line(lines, "modifiedon").IsMismatch);
        }

        [Fact]
        public void The_mismatches_are_exactly_the_engine_s_differing_attributes()
        {
            var h = new Harness();
            h.Primary.Add(TestData.Account(Row1, "Contoso", ("accountnumber", "1"), ("revenue", new Money(5m)), ("revenue_base", new Money(5m)),
                ("modifiedon", DateTime.UtcNow), ("industrycode", Opt(1)), ("new_unknown", "x"), ("new_document", Id(9))));
            h.Secondary.Add(TestData.Account(Row1, "Contoso", ("accountnumber", "2"), ("revenue", new Money(6m)), ("revenue_base", new Money(6m)),
                ("industrycode", Opt(1)), ("new_tags", new OptionSetValueCollection { Opt(1) }), ("new_unknown", "y"), ("new_document", Id(8))));

            CompareResult result = h.Run();
            RowComparison row = Assert.Single(result.Rows);
            IList<ColumnComparison> lines = result.GetDetails(row);

            Assert.Equal(row.DifferingAttributes, lines.Where(l => l.IsMismatch).Select(l => l.LogicalName).OrderBy(n => n, StringComparer.Ordinal));
            Assert.Equal(new[] { "accountnumber", "new_tags", "revenue" }, row.DifferingAttributes);
            Assert.Equal(new[] { "accountnumber", "modifiedon", "new_tags", "new_unknown", "revenue", "revenue_base" },
                lines.Where(l => l.IsDifferent).Select(l => l.LogicalName).OrderBy(n => n, StringComparer.Ordinal));   // "differences only" shows these
        }

        [Fact]
        public void Columns_outside_the_primary_metadata_are_listed_but_linked_values_are_not()
        {
            Entity primary = TestData.Account(Row1, "Contoso", ("telephone1", "01234"));
            primary["pc.emailaddress1"] = new AliasedValue("contact", "emailaddress1", "a@example.com");
            primary["contactname"] = new AliasedValue("contact", "fullname", "Jane");
            Entity secondary = TestData.Account(Row1, "Contoso", ("new_onlyhere", 5));

            IList<ColumnComparison> lines = Details(primary, secondary);

            ColumnComparison phone = Line(lines, "telephone1");
            Assert.Equal((true, false, null, DetailBuilder.NotInMetadataNote, "telephone1"),
                (phone.IsDifferent, phone.IsCompared, phone.AttributeType, phone.Note, phone.DisplayName));
            Assert.Equal("5", Line(lines, "new_onlyhere").SecondaryText);
            Assert.DoesNotContain(lines, l => l.LogicalName.Contains(".") || l.LogicalName == "contactname");
        }

        [Fact]
        public void Values_are_shown_as_the_comparison_sees_them()
        {
            Guid jane = new Guid("aaaaaaaa-0000-0000-0000-000000000001");
            Entity primary = TestData.Account(Row1, "Contoso",
                    ("primarycontactid", Ref("contact", jane, "Jane Doe")),
                    ("parentaccountid", Ref("account", Id(70))),
                    ("industrycode", Opt(3)),
                    ("new_tags", new OptionSetValueCollection { Opt(1), Opt(3) }),
                    ("revenue", new Money(10m)),
                    ("entityimage", new byte[] { 1, 2, 3 }),
                    ("new_externalid", jane))
                .Formatted("industrycode", "Retail")
                .Formatted("new_tags", "Red; Blue")
                .Formatted("revenue", "£10.00");

            IList<ColumnComparison> lines = Details(primary, null);

            Assert.Equal("Jane Doe (aaaaaaaa-0000-0000-0000-000000000001)", Line(lines, "primarycontactid").PrimaryText);
            Assert.Equal(Id(70).ToString(), Line(lines, "parentaccountid").PrimaryText);
            Assert.Equal("Retail (3)", Line(lines, "industrycode").PrimaryText);
            Assert.Equal("Red; Blue (1, 3)", Line(lines, "new_tags").PrimaryText);
            Assert.Equal("£10.00", Line(lines, "revenue").PrimaryText);
            Assert.Matches(@"^\(image, 3 bytes\)$", Line(lines, "entityimage").PrimaryText);
            Assert.Equal(jane.ToString(), Line(lines, "new_externalid").PrimaryText);
            Assert.Equal(string.Empty, Line(lines, "description").PrimaryText);
        }

        [Fact]
        public void A_missing_row_shows_only_the_primary_values()
        {
            IList<ColumnComparison> lines = Details(TestData.Account(Row1, "Contoso", ("accountnumber", "A-1")), null);

            Assert.All(lines, l => Assert.Equal(string.Empty, l.SecondaryText));
            Assert.All(lines, l => Assert.Null(l.SecondaryRaw));
            Assert.True(Line(lines, "accountnumber").IsMismatch);
            Assert.False(Line(lines, "description").IsDifferent);   // empty on both sides
            Assert.False(Line(lines, "accountid").IsDifferent);
        }

        [Fact]
        public void An_extra_row_shows_only_the_secondary_values()
        {
            IList<ColumnComparison> lines = Details(null, TestData.Account(Row1, "Contoso"));

            Assert.Equal(("", "Contoso", true), (Line(lines, "name").PrimaryText, Line(lines, "name").SecondaryText, Line(lines, "name").IsMismatch));
            Assert.All(lines, l => Assert.Null(l.PrimaryRaw));
        }

        [Fact]
        public void Different_values_that_read_the_same_get_their_raw_values()
        {
            var utc = new DateTime(2024, 3, 1, 14, 30, 5, DateTimeKind.Utc);
            IList<ColumnComparison> lines = Details(
                TestData.Account(Row1, "Contoso", ("new_reviewdate", utc), ("accountnumber", "AB-1"), ("description", "a\r\nb"))
                    .Formatted("new_reviewdate", "01/03/2024 14:30"),
                TestData.Account(Row1, "Contoso", ("new_reviewdate", utc.AddSeconds(20)), ("accountnumber", "AB-1 "), ("description", "a\nb"))
                    .Formatted("new_reviewdate", "01/03/2024 14:30"));

            Assert.Equal(("01/03/2024 14:30 [2024-03-01 14:30:05.0000000 UTC]", "01/03/2024 14:30 [2024-03-01 14:30:25.0000000 UTC]"),
                (Line(lines, "new_reviewdate").PrimaryText, Line(lines, "new_reviewdate").SecondaryText));
            Assert.Equal(("AB-1 [\"AB-1\" (4 chars)]", "AB-1  [\"AB-1 \" (5 chars)]"),
                (Line(lines, "accountnumber").PrimaryText, Line(lines, "accountnumber").SecondaryText));
            Assert.EndsWith("[\"a\\r\\nb\" (4 chars)]", Line(lines, "description").PrimaryText);
            Assert.Equal("Contoso", Line(lines, "name").PrimaryText);   // equal values keep their text
        }

        [Fact]
        public void Different_values_that_read_differently_keep_their_texts()
        {
            IList<ColumnComparison> lines = Details(
                TestData.Account(Row1, "Contoso", ("accountnumber", "AB-1")),
                TestData.Account(Row1, "Contoso", ("accountnumber", "AB-2")));

            Assert.Equal(("AB-1", "AB-2"), (Line(lines, "accountnumber").PrimaryText, Line(lines, "accountnumber").SecondaryText));
        }

        [Fact]
        public void The_result_builds_the_details_with_its_own_options()
        {
            var h = new Harness();
            h.Primary.Add(TestData.Account(Row1, "Contoso", ("accountnumber", "1")));
            h.Secondary.Add(TestData.Account(Row1, "Contoso", ("accountnumber", "2")));
            h.Options.SetIgnoredAttributes("accountnumber");

            CompareResult result = h.Run();
            h.Options.SetIgnoredAttributes(null);   // later edits do not change the result

            ColumnComparison line = Line(result.GetDetails(Assert.Single(result.Rows)), "accountnumber");
            Assert.Equal((true, false), (line.IsIgnored, line.IsMismatch));
        }

        [Fact]
        public void Arguments_are_checked_and_null_options_mean_the_defaults()
        {
            var row = new RowComparison(Row1, RowStatus.Match, TestData.Account(Row1, "A"), TestData.Account(Row1, "A"), null, true, true);

            Assert.Throws<ArgumentNullException>(() => DetailBuilder.Build(null, Account, null));
            Assert.Throws<ArgumentNullException>(() => DetailBuilder.Build(row, null, null));
            Assert.True(Line(DetailBuilder.Build(row, Account, null), "modifiedon").IsIgnored);
            Assert.Empty(row.DifferingAttributes);
        }

        [Fact]
        public void ReadsTheSame_ignores_white_space_only()
        {
            Assert.True(DetailBuilder.ReadsTheSame("a b", "a  b "));
            Assert.True(DetailBuilder.ReadsTheSame(null, ""));
            Assert.False(DetailBuilder.ReadsTheSame("a", "A"));
        }
    }

    /// <summary>SPEC 5.8: <see cref="CellFormatter.FormatDetail"/>.</summary>
    public class CellFormatterDetailTests
    {
        [Fact]
        public void Lookups_show_name_and_id_with_the_formatted_name_first()
        {
            Guid id = new Guid("bbbbbbbb-0000-0000-0000-000000000002");
            var record = new Entity("account")
            {
                ["primarycontactid"] = new EntityReference("contact", id) { Name = "Jane" },
                ["ownerid"] = new EntityReference("systemuser", id)
            };
            record.FormattedValues["ownerid"] = "Scott";

            Assert.Equal("Jane (bbbbbbbb-0000-0000-0000-000000000002)", CellFormatter.FormatDetail(record, "primarycontactid"));
            Assert.Equal("Scott (bbbbbbbb-0000-0000-0000-000000000002)", CellFormatter.FormatDetail(record, "ownerid"));
        }

        [Fact]
        public void Option_sets_show_label_and_value_or_just_the_value()
        {
            var record = new Entity("account") { ["industrycode"] = new OptionSetValue(3), ["statuscode"] = new OptionSetValue(1) };
            record.FormattedValues["industrycode"] = "Retail";

            Assert.Equal("Retail (3)", CellFormatter.FormatDetail(record, "industrycode"));
            Assert.Equal("1", CellFormatter.FormatDetail(record, "statuscode"));
        }

        [Fact]
        public void Party_lists_show_each_party_with_its_id_or_its_address()
        {
            Guid id = new Guid("cccccccc-0000-0000-0000-000000000003");
            var record = new Entity("email")
            {
                ["to"] = Parties(Party(Ref("contact", id, "Jane")), Party(null, addressUsed: "x@example.com"), Party(null))
            };

            Assert.Equal("Jane (cccccccc-0000-0000-0000-000000000003); x@example.com", CellFormatter.FormatDetail(record, "to"));
        }

        [Fact]
        public void Other_values_use_the_formatted_value_then_the_grid_format()
        {
            CultureInfo original = CultureInfo.CurrentCulture;
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            try
            {
                var record = new Entity("account")
                {
                    ["revenue"] = new Money(1234.5m),
                    ["creditonhold"] = true,
                    ["numberofemployees"] = 42,
                    ["entityimage"] = new byte[2048],
                    ["c.fullname"] = new AliasedValue("contact", "fullname", "Jane"),
                    ["description"] = null
                };
                record.FormattedValues["creditonhold"] = "Yes";

                Assert.Equal("1,234.50", CellFormatter.FormatDetail(record, "revenue"));
                Assert.Equal("Yes", CellFormatter.FormatDetail(record, "creditonhold"));
                Assert.Equal("42", CellFormatter.FormatDetail(record, "numberofemployees"));
                Assert.Equal("(image, 2,048 bytes)", CellFormatter.FormatDetail(record, "entityimage"));
                Assert.Equal("Jane", CellFormatter.FormatDetail(record, "c.fullname"));
                Assert.Equal(string.Empty, CellFormatter.FormatDetail(record, "description"));
                Assert.Equal(string.Empty, CellFormatter.FormatDetail(record, "missing"));
                Assert.Equal(string.Empty, CellFormatter.FormatDetail(null, "revenue"));
                Assert.Equal(string.Empty, CellFormatter.FormatDetail(record, null));
            }
            finally
            {
                CultureInfo.CurrentCulture = original;
            }
        }
    }
}
