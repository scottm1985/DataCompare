using System;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using MyscotekDataCompare.Tests.Fakes;
using Xunit;
using static MyscotekDataCompare.Tests.Fakes.TestData;

namespace MyscotekDataCompare.Tests
{
    /// <summary>
    /// The test double the engine tests rely on: FakeOrganizationService's FetchXML interpreter and its
    /// QueryExpression In support. If these are wrong, the engine tests prove nothing.
    /// </summary>
    public class FakeFetchXmlTests
    {
        private static FakeOrganizationService Seeded()
        {
            var service = new FakeOrganizationService();
            service.Add(Record("contact", Id(50), ("fullname", "Jane"), ("emailaddress1", "jane@example.com"), ("parentcustomerid", Ref("account", Id(1)))));
            service.Add(Record("contact", Id(51), ("fullname", "John"), ("parentcustomerid", Ref("account", Id(1)))));
            service.Add(Account(Id(1), "Contoso", ("primarycontactid", Ref("contact", Id(50)))).Formatted("statecode", "Active"));
            service.Add(Account(Id(2), "Fabrikam", ("statecode", Opt(1)), ("primarycontactid", Ref("contact", Id(51)))));
            service.Add(Account(Id(3), "Adventure Works"));
            return service;
        }

        private static EntityCollection Fetch(FakeOrganizationService service, string fetchXml) => service.RetrieveMultiple(new FetchExpression(fetchXml));

        [Fact]
        public void Filters_with_nested_or_and_orders_are_evaluated()
        {
            EntityCollection result = Fetch(Seeded(),
                "<fetch><entity name=\"account\"><attribute name=\"name\" /><order attribute=\"name\" descending=\"true\" />" +
                "<filter type=\"or\"><condition attribute=\"statecode\" operator=\"eq\" value=\"1\" />" +
                "<filter><condition attribute=\"name\" operator=\"like\" value=\"Con%\" /><condition attribute=\"accountid\" operator=\"in\"><value>{00000001-0000-0000-0000-000000000000}</value></condition></filter>" +
                "</filter></entity></fetch>");

            Assert.Equal(new[] { "Fabrikam", "Contoso" }, result.Entities.Select(e => e.GetAttributeValue<string>("name")));
            Assert.Equal(new[] { "name" }, result.Entities[0].Attributes.Keys);   // only the listed attribute
        }

        [Fact]
        public void All_attributes_return_every_value_and_the_formatted_values()
        {
            Entity contoso = Fetch(Seeded(), "<fetch><entity name=\"account\"><all-attributes /><filter><condition attribute=\"name\" operator=\"eq\" value=\"Contoso\" /></filter></entity></fetch>")
                .Entities.Single();

            Assert.Equal(Id(1), contoso.Id);
            Assert.Equal(Id(1), contoso["accountid"]);
            Assert.Equal("Active", contoso.FormattedValues["statecode"]);
            Assert.IsType<EntityReference>(contoso["primarycontactid"]);
        }

        [Fact]
        public void Outer_and_inner_links_return_aliased_values_and_link_filters_apply()
        {
            const string outer =
                "<fetch><entity name=\"account\"><attribute name=\"name\" /><order attribute=\"name\" />" +
                "<link-entity name=\"contact\" from=\"contactid\" to=\"primarycontactid\" link-type=\"outer\" alias=\"pc\"><attribute name=\"emailaddress1\" /><attribute name=\"fullname\" alias=\"who\" /></link-entity>" +
                "</entity></fetch>";
            const string inner =
                "<fetch><entity name=\"account\"><attribute name=\"name\" />" +
                "<link-entity name=\"contact\" from=\"contactid\" to=\"primarycontactid\" alias=\"pc\"><filter><condition attribute=\"emailaddress1\" operator=\"not-null\" /></filter></link-entity>" +
                "</entity></fetch>";
            FakeOrganizationService service = Seeded();

            EntityCollection outerResult = Fetch(service, outer);
            EntityCollection innerResult = Fetch(service, inner);

            Assert.Equal(new[] { "Adventure Works", "Contoso", "Fabrikam" }, outerResult.Entities.Select(e => e.GetAttributeValue<string>("name")));
            Entity contoso = outerResult.Entities[1];
            Assert.Equal("jane@example.com", ((AliasedValue)contoso["pc.emailaddress1"]).Value);
            Assert.Equal(("contact", "emailaddress1"), (((AliasedValue)contoso["pc.emailaddress1"]).EntityLogicalName, ((AliasedValue)contoso["pc.emailaddress1"]).AttributeLogicalName));
            Assert.Equal("Jane", ((AliasedValue)contoso["who"]).Value);
            Assert.False(outerResult.Entities[0].Contains("pc.emailaddress1"));   // outer link without a match
            Assert.Equal("Contoso", innerResult.Entities.Single().GetAttributeValue<string>("name"));
        }

        [Fact]
        public void A_one_to_many_link_repeats_the_row_unless_distinct()
        {
            const string link = "<link-entity name=\"contact\" from=\"parentcustomerid\" to=\"accountid\" alias=\"c\" />";
            FakeOrganizationService service = Seeded();

            Assert.Equal(2, Fetch(service, $"<fetch><entity name=\"account\"><attribute name=\"name\" />{link}</entity></fetch>").Entities.Count);
            Assert.Single(Fetch(service, $"<fetch distinct=\"true\"><entity name=\"account\"><attribute name=\"name\" />{link}</entity></fetch>").Entities);
        }

        [Fact]
        public void Paging_returns_pages_more_records_and_a_cookie()
        {
            FakeOrganizationService service = Seeded();
            const string paged = "<fetch count=\"2\" page=\"{0}\"><entity name=\"account\"><attribute name=\"name\" /><order attribute=\"name\" /></entity></fetch>";

            EntityCollection first = Fetch(service, string.Format(paged, 1));
            EntityCollection second = Fetch(service, string.Format(paged, 2));

            Assert.Equal((2, true, "<cookie page=\"1\" />"), (first.Entities.Count, first.MoreRecords, first.PagingCookie));
            Assert.Equal((1, false), (second.Entities.Count, second.MoreRecords));
            Assert.Equal("Fabrikam", second.Entities[0].GetAttributeValue<string>("name"));
        }

        [Fact]
        public void Unsupported_fetch_xml_throws_instead_of_passing_by_accident()
        {
            FakeOrganizationService service = Seeded();

            Assert.Throws<NotSupportedException>(() => Fetch(service, "<fetch aggregate=\"true\"><entity name=\"account\"><attribute name=\"name\" /></entity></fetch>"));
            Assert.Throws<NotSupportedException>(() => Fetch(service, "<fetch><entity name=\"account\"><filter><condition attribute=\"name\" operator=\"on-or-after\" value=\"x\" /></filter></entity></fetch>"));
            Assert.Throws<NotSupportedException>(() => Fetch(service,
                "<fetch><entity name=\"account\"><link-entity name=\"contact\" from=\"contactid\" to=\"primarycontactid\"><order attribute=\"fullname\" /></link-entity></entity></fetch>"));
        }

        [Fact]
        public void Query_expressions_support_the_in_operator()
        {
            FakeOrganizationService service = Seeded();
            var query = new QueryExpression("account") { ColumnSet = new ColumnSet(true) };
            query.Criteria.AddCondition("accountid", ConditionOperator.In, Id(1), Id(3), Id(99));

            EntityCollection result = service.RetrieveMultiple(query);

            Assert.Equal(new[] { Id(1), Id(3) }, result.Entities.Select(e => e.Id));
            Assert.Equal("Active", result.Entities[0].FormattedValues["statecode"]);
        }
    }
}
