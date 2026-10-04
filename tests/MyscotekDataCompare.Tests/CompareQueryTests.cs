using System;
using System.Linq;
using System.Xml.Linq;
using MyscotekDataCompare.Core.Services;
using Xunit;

namespace MyscotekDataCompare.Tests
{
    /// <summary>SPEC 5.3: the view's FetchXML rewritten for a compare run.</summary>
    public class CompareQueryTests
    {
        private const string View =
            "<fetch version=\"1.0\" output-format=\"xml-platform\" mapping=\"logical\" no-lock=\"true\" count=\"50\" page=\"3\" " +
            "paging-cookie=\"&lt;cookie page=&quot;2&quot; /&gt;\" top=\"10\" returntotalrecordcount=\"true\">" +
            "<entity name=\"account\">" +
            "<attribute name=\"name\" /><attribute name=\"accountnumber\" /><attribute name=\"telephone1\" alias=\"phone\" />" +
            "<order attribute=\"name\" descending=\"false\" />" +
            "<filter type=\"and\"><condition attribute=\"statecode\" operator=\"eq\" value=\"0\" />" +
            "<filter type=\"or\"><condition attribute=\"name\" operator=\"like\" value=\"A%\" /><condition attribute=\"name\" operator=\"null\" /></filter></filter>" +
            "<link-entity name=\"contact\" from=\"contactid\" to=\"primarycontactid\" link-type=\"outer\" alias=\"pc\">" +
            "<attribute name=\"emailaddress1\" /><attribute name=\"fullname\" alias=\"contactname\" />" +
            "<filter><condition attribute=\"statecode\" operator=\"eq\" value=\"0\" /></filter>" +
            "<link-entity name=\"systemuser\" from=\"systemuserid\" to=\"ownerid\" alias=\"pco\"><attribute name=\"fullname\" /></link-entity>" +
            "</link-entity>" +
            "</entity></fetch>";

        private static XElement Fetch(CompareQuery query) => XElement.Parse(query.FetchXml);

        private static XElement MainEntity(CompareQuery query) => Fetch(query).Element("entity");

        [Fact]
        public void The_main_entity_attributes_become_all_attributes_and_everything_else_is_kept()
        {
            CompareQuery query = FetchXmlHelper.PrepareCompareQuery(View, "accountid");

            XElement entity = MainEntity(query);
            Assert.Equal("account", query.EntityName);
            Assert.Equal("all-attributes", entity.Elements().First().Name.LocalName);   // first, once
            Assert.Single(entity.Elements("all-attributes"));
            Assert.Empty(entity.Elements("attribute"));
            Assert.Equal("name", (string)Assert.Single(entity.Elements("order")).Attribute("attribute"));

            // Filters untouched, nested ones included.
            XElement filter = Assert.Single(entity.Elements("filter"));
            Assert.Equal(XElement.Parse(View).Element("entity").Element("filter").ToString(), filter.ToString());

            // Link-entities untouched: their attributes feed the grid's linked columns.
            XElement link = Assert.Single(entity.Elements("link-entity"));
            Assert.Equal(XElement.Parse(View).Element("entity").Element("link-entity").ToString(), link.ToString());
            Assert.Equal(new[] { "emailaddress1", "fullname" }, link.Elements("attribute").Select(a => (string)a.Attribute("name")));
            Assert.Equal("fullname", (string)link.Element("link-entity").Element("attribute").Attribute("name"));
        }

        [Fact]
        public void Paging_attributes_are_stripped_and_the_other_fetch_attributes_kept()
        {
            XElement fetch = Fetch(FetchXmlHelper.PrepareCompareQuery(View, "accountid"));

            foreach (string stripped in new[] { "count", "page", "paging-cookie", "top", "returntotalrecordcount" })
                Assert.Null(fetch.Attribute(stripped));
            Assert.Equal(("1.0", "xml-platform", "logical", "true"),
                ((string)fetch.Attribute("version"), (string)fetch.Attribute("output-format"), (string)fetch.Attribute("mapping"), (string)fetch.Attribute("no-lock")));
        }

        [Fact]
        public void The_primary_key_is_always_returned_once_the_query_is_paged()
        {
            CompareQuery query = FetchXmlHelper.PrepareCompareQuery(
                "<fetch><entity name=\"account\"><attribute name=\"name\" /></entity></fetch>", "accountid");

            string paged = FetchXmlHelper.ApplyPaging(query.FetchXml, 2, 5000, "<cookie page=\"1\" />");

            XElement entity = XElement.Parse(paged).Element("entity");
            Assert.NotNull(entity.Element("all-attributes"));   // all-attributes includes the primary key
            Assert.Equal(paged, FetchXmlHelper.EnsureAttribute(paged, "accountid"));   // nothing to add
            Assert.Equal("5000", (string)XElement.Parse(paged).Attribute("count"));
        }

        [Fact]
        public void An_existing_all_attributes_is_not_duplicated()
        {
            CompareQuery query = FetchXmlHelper.PrepareCompareQuery(
                "<fetch><entity name=\"account\"><all-attributes /><attribute name=\"name\" /><order attribute=\"name\" /></entity></fetch>", "accountid");

            XElement entity = MainEntity(query);
            Assert.Single(entity.Elements("all-attributes"));
            Assert.Empty(entity.Elements("attribute"));
        }

        [Fact]
        public void A_non_distinct_view_is_not_given_an_extra_order()
        {
            CompareQuery query = FetchXmlHelper.PrepareCompareQuery(View, "accountid");

            Assert.False(query.IsDistinct);
            Assert.DoesNotContain(MainEntity(query).Elements("order"), o => (string)o.Attribute("attribute") == "accountid");
        }

        [Theory]
        [InlineData("true")]
        [InlineData("True")]
        [InlineData("1")]
        public void A_distinct_view_is_flagged_and_ordered_by_the_primary_key_last(string distinct)
        {
            CompareQuery query = FetchXmlHelper.PrepareCompareQuery(
                $"<fetch distinct=\"{distinct}\"><entity name=\"account\"><attribute name=\"name\" /><order attribute=\"name\" />" +
                "<link-entity name=\"contact\" from=\"parentcustomerid\" to=\"accountid\" alias=\"c\" /></entity></fetch>", "accountid");

            Assert.True(query.IsDistinct);
            Assert.Equal(new[] { ("name", (string)null), ("accountid", "false") },
                MainEntity(query).Elements("order").Select(o => ((string)o.Attribute("attribute"), (string)o.Attribute("descending"))));
            Assert.Equal(distinct, (string)Fetch(query).Attribute("distinct"));   // kept
        }

        [Fact]
        public void A_distinct_view_without_orders_or_already_ordered_by_the_key_gets_one_key_order()
        {
            CompareQuery unordered = FetchXmlHelper.PrepareCompareQuery(
                "<fetch distinct=\"true\"><entity name=\"account\"><attribute name=\"name\" /></entity></fetch>", "accountid");
            CompareQuery ordered = FetchXmlHelper.PrepareCompareQuery(
                "<fetch distinct=\"true\"><entity name=\"account\"><order attribute=\"AccountId\" descending=\"true\" /></entity></fetch>", "accountid");

            Assert.Equal("accountid", (string)Assert.Single(MainEntity(unordered).Elements("order")).Attribute("attribute"));
            Assert.Equal("true", (string)Assert.Single(MainEntity(ordered).Elements("order")).Attribute("descending"));
        }

        [Fact]
        public void Aliases_of_main_entity_attributes_are_kept_for_the_grid()
        {
            CompareQuery query = FetchXmlHelper.PrepareCompareQuery(View, "accountid");

            Assert.Equal("telephone1", query.MainEntityAliases["PHONE"]);   // case-insensitive
            Assert.Single(query.MainEntityAliases);                          // link-entity aliases are not main-entity ones
        }

        [Fact]
        public void An_aggregate_view_cannot_be_compared()
        {
            var error = Assert.Throws<NotSupportedException>(() => FetchXmlHelper.PrepareCompareQuery(
                "<fetch aggregate=\"true\"><entity name=\"account\"><attribute name=\"accountid\" alias=\"n\" aggregate=\"count\" /></entity></fetch>", "accountid"));
            Assert.Contains("aggregate", error.Message);
        }

        [Theory]
        [InlineData("")]
        [InlineData("not xml")]
        [InlineData("<grid />")]
        [InlineData("<fetch />")]
        [InlineData("<fetch><entity /></fetch>")]
        public void Missing_or_invalid_fetch_xml_is_rejected(string fetchXml)
        {
            Assert.Throws<ArgumentException>(() => FetchXmlHelper.PrepareCompareQuery(fetchXml, "accountid"));
        }

        [Fact]
        public void A_primary_key_is_required()
        {
            Assert.Throws<ArgumentException>(() => FetchXmlHelper.PrepareCompareQuery(View, " "));
        }

        [Fact]
        public void The_rewritten_fetch_is_one_line_without_formatting()
        {
            CompareQuery query = FetchXmlHelper.PrepareCompareQuery(View, "accountid");

            Assert.DoesNotContain("\n", query.FetchXml);
            Assert.StartsWith("<fetch version=\"1.0\"", query.FetchXml);
        }
    }
}
