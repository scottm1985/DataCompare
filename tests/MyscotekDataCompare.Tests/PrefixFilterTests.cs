using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xrm.Sdk;
using MyscotekDataCompare.Core;
using MyscotekDataCompare.Tests.Fakes;
using Xunit;
using static MyscotekDataCompare.Tests.Fakes.TestData;

namespace MyscotekDataCompare.Tests
{
    /// <summary>SPEC 5.5: compare only the columns whose names start with given prefixes.</summary>
    public class PrefixFilterTests
    {
        private static readonly Guid Row1 = Id(1);

        /// <summary>One account whose name, new_externalid and new_tags differ between the environments.</summary>
        private static Harness ThreeDifferences()
        {
            var h = new Harness();
            h.Primary.Add(Account(Row1, "Contoso", ("new_externalid", Id(50)), ("new_tags", new OptionSetValueCollection { Opt(1) })));
            h.Secondary.Add(Account(Row1, "Contoso Ltd", ("new_externalid", Id(51)), ("new_tags", new OptionSetValueCollection { Opt(2) })));
            return h;
        }

        [Fact]
        public void Without_prefixes_every_column_is_compared()
        {
            Harness h = ThreeDifferences();

            CompareResult result = h.Run();

            Assert.False(h.Options.HasPrefixFilter);
            Assert.Equal(new[] { "name", "new_externalid", "new_tags" }, Assert.Single(result.Rows).DifferingAttributes);
            Assert.DoesNotContain(h.Log.Lines, l => l.Contains("prefix filter"));
        }

        [Fact]
        public void Only_the_columns_starting_with_a_prefix_are_compared()
        {
            Harness h = ThreeDifferences();
            h.Options.SetComparedPrefixes("new_");

            CompareResult result = h.Run();

            Assert.Equal(new[] { "new_externalid", "new_tags" }, Assert.Single(result.Rows).DifferingAttributes);
        }

        [Fact]
        public void A_row_whose_only_difference_is_outside_the_prefixes_matches()
        {
            var h = new Harness();
            h.Primary.Add(Account(Row1, "Contoso", ("new_externalid", Id(50))));
            h.Secondary.Add(Account(Row1, "Contoso Ltd", ("new_externalid", Id(50))));
            h.Options.SetComparedPrefixes("contoso_; new_");

            CompareResult result = h.Run();

            Assert.Equal(RowStatus.Match, Assert.Single(result.Rows).Status);
            Assert.Equal((1, 0), (result.Summary.Matching, result.Summary.Different));
        }

        [Fact]
        public void The_prefixes_match_whatever_the_case()
        {
            Harness h = ThreeDifferences();
            h.Options.ComparedPrefixes.Add("NEW_EXT");

            CompareResult result = h.Run();

            Assert.Equal(new[] { "new_externalid" }, Assert.Single(result.Rows).DifferingAttributes);
            Assert.True(h.Options.IsInPrefixFilter("New_ExternalId"));
            Assert.False(h.Options.IsInPrefixFilter("name"));
            Assert.False(h.Options.IsInPrefixFilter(null));
        }

        [Fact]
        public void The_ignored_attributes_still_apply_inside_the_prefixes()
        {
            Harness h = ThreeDifferences();
            h.Options.SetComparedPrefixes("new_");
            h.Options.IgnoredAttributes.Add("new_tags");

            CompareResult result = h.Run();

            Assert.Equal(new[] { "new_externalid" }, Assert.Single(result.Rows).DifferingAttributes);
        }

        [Fact]
        public void The_detail_pane_greys_the_columns_outside_the_prefixes_and_still_flags_their_differences()
        {
            Harness h = ThreeDifferences();
            h.Options.SetComparedPrefixes("new_");
            h.Options.IgnoredAttributes.Add("new_tags");

            CompareResult result = h.Run();

            IList<ColumnComparison> lines = result.GetDetails(Assert.Single(result.Rows));
            ColumnComparison name = lines.Single(l => l.LogicalName == "name");
            Assert.Equal((false, true, false, DetailBuilder.OutsidePrefixFilterNote), (name.IsCompared, name.IsDifferent, name.IsMismatch, name.Note));
            Assert.Equal(DetailBuilder.IgnoredNote, lines.Single(l => l.LogicalName == "new_tags").Note);   // ignored wins
            ColumnComparison external = lines.Single(l => l.LogicalName == "new_externalid");
            Assert.Equal((true, true, null), (external.IsCompared, external.IsMismatch, external.Note));
            Assert.Equal(DetailBuilder.PrimaryKeyNote, lines[0].Note);
            Assert.Equal(DetailBuilder.DerivedNotePrefix + "primarycontactid",
                DetailBuilder.Build(new RowComparison(Row1, RowStatus.Match, Account(Row1, "A", ("primarycontactidname", "Ann")), null, null, true, false),
                    StandardSchema().GetEntity("account"), h.Options).Single(l => l.LogicalName == "primarycontactidname").Note);   // derived wins
        }

        [Fact]
        public void The_header_says_how_many_columns_the_prefixes_leave_out()
        {
            Harness h = ThreeDifferences();
            h.Options.SetComparedPrefixes("new_, contoso_");

            h.Run();

            string header = h.Log.Lines.Single(l => l.StartsWith("Comparing Account (account)", StringComparison.Ordinal));
            Assert.Matches(@": \d+ attributes compared, 16 ignored \(.*\), \d+ outside the prefix filter \(new_, contoso_\), \d+ derived attributes not compared\.$", header);
            int compared = int.Parse(header.Split(':')[1].Trim().Split(' ')[0]);
            Assert.Equal(6, compared);   // new_bignumber, new_calculated, new_document, new_externalid, new_reviewdate, new_tags (new_secret is unreadable)
        }

        [Fact]
        public void Reevaluate_applies_new_prefixes_without_reading_again()
        {
            Harness h = ThreeDifferences();
            h.Both(Account(Id(2), "Same"));
            CompareResult result = h.Run();
            int requests = h.Primary.Executed.Count + h.Secondary.Executed.Count;
            var options = new CompareOptions();
            options.SetComparedPrefixes("name");

            CompareResult byName = result.Reevaluate(options);
            options.SetComparedPrefixes("zz_");
            CompareResult none = result.Reevaluate(options);
            CompareResult all = none.Reevaluate(new CompareOptions());

            Assert.Equal(new[] { "name" }, Harness.Row(byName, Row1).DifferingAttributes);
            Assert.Equal(new[] { "name" }, byName.Options.ComparedPrefixes);
            Assert.Equal((2, 0), (none.Summary.Matching, none.Summary.Different));
            Assert.Equal((1, 1), (all.Summary.Matching, all.Summary.Different));
            Assert.Equal(new[] { "name", "new_externalid", "new_tags" }, Harness.Row(all, Row1).DifferingAttributes);
            Assert.Equal(requests, h.Primary.Executed.Count + h.Secondary.Executed.Count);
        }

        [Fact]
        public void Prefixes_parse_like_the_ignored_list_and_blank_entries_are_no_filter()
        {
            var options = new CompareOptions();
            options.SetComparedPrefixes(" CONTOSO_,new_ ;\r\ncontoso_ ");

            Assert.Equal(new[] { "contoso_", "new_" }, options.ComparedPrefixes);
            Assert.Equal("contoso_,new_", CompareOptions.FormatPrefixList(options.ComparedPrefixes));
            Assert.Equal(string.Empty, CompareOptions.FormatPrefixList(null));
            options.SetComparedPrefixes(null);
            Assert.Empty(options.ComparedPrefixes);
            options.ComparedPrefixes.Add("  ");
            Assert.False(options.HasPrefixFilter);
            Assert.True(options.IsInPrefixFilter("anything"));
            Assert.Empty(new CompareOptions().ComparedPrefixes);   // the default: every column
        }
    }
}
