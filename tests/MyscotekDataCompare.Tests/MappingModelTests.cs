using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Microsoft.Xrm.Sdk.Metadata;
using MyscotekDataCompare.Core;
using MyscotekDataCompare.Core.Schema;
using MyscotekDataCompare.Core.Services;
using MyscotekDataCompare.Tests.Fakes;
using Xunit;

namespace MyscotekDataCompare.Tests
{
    /// <summary>SPEC 5.9: the view rewritten for a mapped secondary table (<see cref="FetchXmlHelper.MapToSecondary"/>).</summary>
    public class SecondaryQueryTests
    {
        private static readonly Dictionary<string, string> Pairs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["accountnumber"] = "new_accountnumber",
            ["accountid"] = "new_accountid",
            ["contoso_contactid"] = "new_contactid",
            ["parentaccountid"] = "new_parentid"
        };

        private static string Map(string name) => Pairs.TryGetValue(name, out string mapped) ? mapped : name;

        private const string View =
            "<fetch version=\"1.0\" distinct=\"true\" mapping=\"logical\"><entity name=\"account\"><all-attributes />" +
            "<filter type=\"and\">" +
            "<condition attribute=\"accountnumber\" operator=\"not-null\" />" +
            "<filter type=\"or\"><condition attribute=\"name\" operator=\"like\" value=\"A%\" />" +
            "<condition attribute=\"accountnumber\" operator=\"eq\" valueof=\"accountid\" /></filter>" +
            "<condition entityname=\"pc\" attribute=\"accountnumber\" operator=\"null\" />" +
            "<condition entityname=\"parent\" attribute=\"accountnumber\" operator=\"not-null\" />" +
            "</filter>" +
            "<order attribute=\"accountnumber\" descending=\"true\" /><order attribute=\"accountid\" />" +
            "<link-entity name=\"contact\" from=\"contactid\" to=\"contoso_contactid\" alias=\"pc\" link-type=\"outer\">" +
            "<attribute name=\"accountnumber\" /><filter><condition attribute=\"accountnumber\" operator=\"null\" /></filter>" +
            "<link-entity name=\"account\" from=\"accountid\" to=\"parentcustomerid\" alias=\"pa\">" +
            "<attribute name=\"accountnumber\" /><filter><condition attribute=\"accountnumber\" operator=\"not-null\" /></filter></link-entity>" +
            "</link-entity>" +
            "<link-entity name=\"account\" from=\"accountid\" to=\"parentaccountid\" alias=\"parent\">" +
            "<attribute name=\"accountnumber\" alias=\"pnum\" />" +
            "<link-entity name=\"contact\" from=\"contactid\" to=\"accountnumber\" alias=\"x\"><attribute name=\"accountnumber\" /></link-entity>" +
            "</link-entity>" +
            "</entity></fetch>";

        private static XElement Rewrite(out SecondaryQuery query)
        {
            query = FetchXmlHelper.MapToSecondary(View, "account", "new_account", Map);
            return XElement.Parse(query.FetchXml).Element("entity");
        }

        private static XElement Link(XElement parent, string alias) => parent.Elements("link-entity").Single(l => (string)l.Attribute("alias") == alias);

        [Fact]
        public void The_main_entity_is_renamed_and_its_conditions_and_orders_are_mapped()
        {
            XElement entity = Rewrite(out _);

            Assert.Equal("new_account", (string)entity.Attribute("name"));
            Assert.NotNull(entity.Element("all-attributes"));
            List<XElement> conditions = entity.Element("filter").Elements("condition").ToList();
            Assert.Equal("new_accountnumber", (string)conditions[0].Attribute("attribute"));
            List<XElement> nested = entity.Element("filter").Element("filter").Elements("condition").ToList();
            Assert.Equal(("name", "A%"), ((string)nested[0].Attribute("attribute"), (string)nested[0].Attribute("value")));   // unmapped: unchanged
            Assert.Equal(("new_accountnumber", "new_accountid"), ((string)nested[1].Attribute("attribute"), (string)nested[1].Attribute("valueof")));
            Assert.Equal(new[] { "new_accountnumber", "new_accountid" }, entity.Elements("order").Select(o => (string)o.Attribute("attribute")));
            Assert.Equal("true", (string)entity.Elements("order").First().Attribute("descending"));
        }

        [Fact]
        public void Conditions_on_another_link_s_entity_pass_through_and_those_on_a_link_to_the_mapped_entity_are_mapped()
        {
            List<XElement> conditions = Rewrite(out _).Element("filter").Elements("condition").ToList();

            Assert.Equal(("pc", "accountnumber"), ((string)conditions[1].Attribute("entityname"), (string)conditions[1].Attribute("attribute")));
            Assert.Equal(("parent", "new_accountnumber"), ((string)conditions[2].Attribute("entityname"), (string)conditions[2].Attribute("attribute")));
        }

        [Fact]
        public void A_link_from_the_main_entity_has_its_to_mapped_and_keeps_its_own_columns()
        {
            XElement pc = Link(Rewrite(out _), "pc");

            Assert.Equal(("contact", "contactid", "new_contactid"), ((string)pc.Attribute("name"), (string)pc.Attribute("from"), (string)pc.Attribute("to")));
            Assert.Equal("accountnumber", (string)pc.Element("attribute").Attribute("name"));
            Assert.Equal("accountnumber", (string)pc.Element("filter").Element("condition").Attribute("attribute"));
        }

        [Fact]
        public void A_link_to_the_mapped_entity_itself_is_renamed_with_its_from_and_columns_mapped_at_any_depth()
        {
            XElement entity = Rewrite(out SecondaryQuery query);

            XElement parent = Link(entity, "parent");
            Assert.Equal(("new_account", "new_accountid", "new_parentid"), ((string)parent.Attribute("name"), (string)parent.Attribute("from"), (string)parent.Attribute("to")));
            Assert.Equal(("new_accountnumber", "pnum"), ((string)parent.Element("attribute").Attribute("name"), (string)parent.Element("attribute").Attribute("alias")));
            XElement x = Link(parent, "x");   // its own link's "to" names the mapped entity's column; the contact side stays
            Assert.Equal(("contact", "contactid", "new_accountnumber"), ((string)x.Attribute("name"), (string)x.Attribute("from"), (string)x.Attribute("to")));
            Assert.Equal("accountnumber", (string)x.Element("attribute").Attribute("name"));

            XElement pa = Link(Link(entity, "pc"), "pa");   // under the contact link: renamed, "to" (a contact column) kept
            Assert.Equal(("new_account", "new_accountid", "parentcustomerid"), ((string)pa.Attribute("name"), (string)pa.Attribute("from"), (string)pa.Attribute("to")));
            Assert.Equal("new_accountnumber", (string)pa.Element("attribute").Attribute("name"));
            Assert.Equal("new_accountnumber", (string)pa.Element("filter").Element("condition").Attribute("attribute"));

            Assert.Equal(new[] { "pa", "parent" }, query.LinkAliases.OrderBy(a => a, StringComparer.Ordinal));
        }

        [Fact]
        public void The_fetch_attributes_and_values_are_kept_and_without_a_column_map_only_the_entity_is_renamed()
        {
            SecondaryQuery query = FetchXmlHelper.MapToSecondary(View, "ACCOUNT", "new_account", null);
            XElement fetch = XElement.Parse(query.FetchXml);

            Assert.Equal(("1.0", "true", "logical"), ((string)fetch.Attribute("version"), (string)fetch.Attribute("distinct"), (string)fetch.Attribute("mapping")));
            Assert.Equal(View.Replace("name=\"account\"", "name=\"new_account\""), query.FetchXml);
        }

        [Fact]
        public void A_fetch_for_another_entity_or_without_names_is_refused()
        {
            Assert.Throws<ArgumentException>(() => FetchXmlHelper.MapToSecondary(View, "contact", "new_contact", Map));
            Assert.Throws<ArgumentException>(() => FetchXmlHelper.MapToSecondary(View, "account", " ", Map));
            Assert.Throws<ArgumentException>(() => FetchXmlHelper.MapToSecondary(View, null, "new_account", Map));
            Assert.Throws<ArgumentException>(() => FetchXmlHelper.MapToSecondary("<fetch><entity", "account", "new_account", Map));
        }

        [Fact]
        public void The_prepared_compare_query_of_a_distinct_view_orders_by_the_secondary_key()
        {
            CompareQuery prepared = FetchXmlHelper.PrepareCompareQuery(
                "<fetch distinct=\"true\" count=\"50\"><entity name=\"account\"><attribute name=\"name\" /></entity></fetch>", "accountid");

            SecondaryQuery query = FetchXmlHelper.MapToSecondary(prepared.FetchXml, prepared.EntityName, "new_account", Map);

            XElement entity = XElement.Parse(query.FetchXml).Element("entity");
            Assert.Equal("new_accountid", (string)entity.Element("order").Attribute("attribute"));
            Assert.Null(XElement.Parse(query.FetchXml).Attribute("count"));
            Assert.Empty(query.LinkAliases);
        }
    }

    /// <summary>SPEC 5.9: the mapping model - <see cref="EntityMapping"/>, <see cref="ColumnMapping"/> and the resolved <see cref="EntityMap"/>.</summary>
    public class EntityMappingTests
    {
        private static EntityMap Map(params ColumnMapping[] pairs) =>
            new EntityMap(new EntityMapping("account", "new_account", pairs),
                TestData.StandardSchema().GetEntity("account"), MappingEngineTests.MigratedSchema().GetEntity("new_account"));

        [Fact]
        public void A_mapping_applies_to_its_primary_entity_once_both_names_are_set()
        {
            var mapping = new EntityMapping(" Account ", "new_account");

            Assert.True(mapping.IsComplete());
            Assert.True(mapping.AppliesTo("ACCOUNT"));
            Assert.False(mapping.AppliesTo("contact"));
            Assert.False(new EntityMapping("account", "").AppliesTo("account"));
            Assert.False(new EntityMapping(null, "new_account").IsComplete());
            Assert.Null(new EntityMapping("account", " ").Normalized());
        }

        [Fact]
        public void The_usable_column_pairs_are_normalised_complete_and_one_per_primary_column()
        {
            var mapping = new EntityMapping("Account", "New_Account",
                new ColumnMapping(" Name ", "New_Name"), new ColumnMapping("name", "other"), new ColumnMapping("x", ""), null,
                new ColumnMapping("", "y"), new ColumnMapping("AccountNumber", "new_number"));

            Assert.Equal(new[] { ("name", "new_name"), ("accountnumber", "new_number") },
                mapping.UsableColumns().Select(c => (c.PrimaryColumn, c.SecondaryColumn)));
            EntityMapping normalized = mapping.Normalized();
            Assert.Equal(("account", "new_account", 2), (normalized.PrimaryEntity, normalized.SecondaryEntity, normalized.Columns.Count));
            Assert.Equal("Account -> New_Account (5 column pair(s))", mapping.ToString());
        }

        [Fact]
        public void Clone_is_deep_and_SameMapping_compares_the_normalised_content()
        {
            var mapping = new EntityMapping("account", "new_account", new ColumnMapping("name", "new_name"));
            EntityMapping copy = mapping.Clone();
            copy.Columns[0].SecondaryColumn = "changed";

            Assert.Equal("new_name", mapping.Columns[0].SecondaryColumn);
            Assert.True(EntityMapping.SameMapping(mapping, new EntityMapping(" ACCOUNT", "New_Account ", new ColumnMapping("Name", "NEW_NAME"))));
            Assert.False(EntityMapping.SameMapping(mapping, copy));
            Assert.False(EntityMapping.SameMapping(mapping, new EntityMapping("account", "new_account")));
            Assert.False(EntityMapping.SameMapping(mapping, new EntityMapping("account", "other_account", new ColumnMapping("name", "new_name"))));
            Assert.False(EntityMapping.SameMapping(mapping, null));
            Assert.True(EntityMapping.SameMapping(null, null));
        }

        [Fact]
        public void The_counterpart_of_a_column_is_its_pair_s_column_or_the_same_name_when_the_secondary_has_it()
        {
            EntityMap map = Map(new ColumnMapping("accountnumber", "new_accountnumber"), new ColumnMapping("creditonhold", "new_nosuchcolumn"));

            Assert.Equal("new_accountid", map.SecondaryColumn("accountid"));            // the key: the secondary's own
            Assert.Equal("new_accountnumber", map.SecondaryColumn("AccountNumber"));
            Assert.Equal("name", map.SecondaryColumn("name"));
            Assert.Null(map.SecondaryColumn("industrycode"));                           // the secondary has no such column
            Assert.Null(map.SecondaryColumn("creditonhold"));                           // paired with a column it does not have
            Assert.Null(map.SecondaryColumn(null));
            Assert.Equal("new_nosuchcolumn", map.QueryColumn("creditonhold"));          // the view is still renamed
            Assert.Equal("industrycode", map.QueryColumn("industrycode"));              // unmapped names pass through
            Assert.Equal("new_accountid", map.QueryColumn("accountid"));
            Assert.Equal(AttributeTypeCode.String, map.SecondaryAttribute("numberofemployees").AttributeType);
            Assert.Null(map.SecondaryAttribute("industrycode"));
            Assert.Equal(new[] { "new_nosuchcolumn" }, map.PairsWithoutSecondaryColumn().Select(p => p.SecondaryColumn));
            Assert.Empty(map.PairsWithoutPrimaryColumn());
            Assert.Equal(("account", "new_account", "accountid", "new_accountid"), (map.PrimaryEntity, map.SecondaryEntity, map.PrimaryIdAttribute, map.SecondaryIdAttribute));
        }

        [Theory]
        [InlineData(AttributeTypeCode.String, AttributeTypeCode.Memo, false)]
        [InlineData(AttributeTypeCode.Lookup, AttributeTypeCode.Customer, false)]
        [InlineData(AttributeTypeCode.Owner, AttributeTypeCode.Lookup, false)]
        [InlineData(AttributeTypeCode.Picklist, AttributeTypeCode.Status, false)]
        [InlineData(AttributeTypeCode.Integer, AttributeTypeCode.Integer, false)]
        [InlineData(AttributeTypeCode.Lookup, AttributeTypeCode.String, true)]
        [InlineData(AttributeTypeCode.Integer, AttributeTypeCode.BigInt, true)]
        [InlineData(AttributeTypeCode.Money, AttributeTypeCode.Decimal, true)]
        public void Types_differ_only_across_families(AttributeTypeCode primary, AttributeTypeCode secondary, bool differ)
        {
            Assert.Equal(differ, EntityMap.TypesDiffer(new AttributeSchema { AttributeType = primary }, new AttributeSchema { AttributeType = secondary }));
        }

        [Fact]
        public void A_multi_select_image_or_file_column_is_its_own_type_family()
        {
            var multi = new AttributeSchema { AttributeType = AttributeTypeCode.Virtual, IsMultiSelect = true };
            var image = new AttributeSchema { AttributeType = AttributeTypeCode.Virtual, IsImage = true };
            var file = new AttributeSchema { AttributeType = AttributeTypeCode.Virtual, IsFile = true };

            Assert.True(EntityMap.TypesDiffer(multi, image));
            Assert.True(EntityMap.TypesDiffer(image, file));
            Assert.False(EntityMap.TypesDiffer(multi, new AttributeSchema { AttributeType = AttributeTypeCode.Virtual, IsMultiSelect = true }));
            Assert.False(EntityMap.TypesDiffer(multi, null));
        }

        [Fact]
        public void An_entity_map_needs_both_entity_names_and_both_schemas()
        {
            EntitySchema schema = TestData.StandardSchema().GetEntity("account");

            Assert.Throws<ArgumentException>(() => new EntityMap(new EntityMapping("account", ""), schema, schema));
            Assert.Throws<ArgumentNullException>(() => new EntityMap(null, schema, schema));
            Assert.Throws<ArgumentNullException>(() => new EntityMap(new EntityMapping("account", "x"), null, schema));
            Assert.Throws<ArgumentNullException>(() => new EntityMap(new EntityMapping("account", "x"), schema, null));
        }

        [Fact]
        public void The_options_find_the_first_complete_mapping_and_clone_the_mappings_and_prefixes_deeply()
        {
            var options = new CompareOptions();
            options.EntityMappings.Add(new EntityMapping("account", ""));
            options.EntityMappings.Add(null);
            options.EntityMappings.Add(new EntityMapping("ACCOUNT", "new_account", new ColumnMapping("name", "new_name")));
            options.EntityMappings.Add(new EntityMapping("account", "second_choice"));
            options.SetComparedPrefixes("contoso_, new_");

            Assert.Equal("new_account", options.FindMapping("account").SecondaryEntity);
            Assert.Null(options.FindMapping("contact"));
            Assert.Null(options.FindMapping(" "));

            CompareOptions copy = options.Clone();
            copy.EntityMappings[1].Columns[0].SecondaryColumn = "changed";
            copy.ComparedPrefixes.Add("x_");
            Assert.Equal("new_name", options.FindMapping("account").Columns[0].SecondaryColumn);
            Assert.Equal(new[] { "contoso_", "new_" }, options.ComparedPrefixes);
            Assert.Equal(3, copy.EntityMappings.Count);   // the null entry is not copied
        }
    }
}
