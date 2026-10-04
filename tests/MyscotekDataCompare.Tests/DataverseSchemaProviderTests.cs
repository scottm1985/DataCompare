using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceModel;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using MyscotekDataCompare.Core.Schema;
using MyscotekDataCompare.Tests.Fakes;
using Xunit;

namespace MyscotekDataCompare.Tests
{
    public class DataverseSchemaProviderTests
    {
        [Fact]
        public void FromMetadata_maps_the_entity_and_its_attributes()
        {
            EntitySchema schema = DataverseSchemaProvider.FromMetadata(AccountMetadata());

            Assert.Equal("account", schema.LogicalName);
            Assert.Equal("accountid", schema.PrimaryIdAttribute);
            Assert.Equal("name", schema.PrimaryNameAttribute);
            Assert.Equal("Account", schema.DisplayName);
            Assert.Equal("Accounts", schema.DisplayCollectionName);
            Assert.False(schema.IsIntersect);
            Assert.False(schema.IsPrivate);

            AttributeSchema name = schema.Attributes["NAME"];   // keyed case-insensitively
            Assert.Same(name, schema.Attribute("Name"));
            Assert.Null(schema.Attribute("new_missing"));
            Assert.Equal("Account Name", name.DisplayName);
            Assert.Equal("new_readonly", schema.Attributes["new_readonly"].DisplayName);   // no label: the logical name
            Assert.Equal(AttributeTypeCode.String, name.AttributeType);
            Assert.True(name.IsValidForRead);
            Assert.True(name.IsValidForCreate);
            Assert.True(name.IsValidForUpdate);
            Assert.Null(name.AttributeOf);
            Assert.Equal(0, name.SourceType);
            Assert.False(name.IsFile || name.IsMultiSelect || name.IsImage);

            Assert.Equal(new[] { "contact" }, schema.Attributes["primarycontactid"].LookupTargets);
            Assert.Equal(AttributeTypeCode.Lookup, schema.Attributes["primarycontactid"].AttributeType);
            Assert.Equal(new[] { "systemuser", "team" }, schema.Attributes["ownerid"].LookupTargets);
            Assert.Equal(AttributeTypeCode.State, schema.Attributes["statecode"].AttributeType);
            Assert.True(schema.Attributes["new_document"].IsFile);
            Assert.True(schema.Attributes["new_tags"].IsMultiSelect);
            Assert.Equal(AttributeTypeCode.Virtual, schema.Attributes["new_tags"].AttributeType);
            Assert.True(schema.Attributes["entityimage"].IsImage);
            Assert.Equal("revenue", schema.Attributes["revenue_base"].AttributeOf);
            Assert.Equal(1, schema.Attributes["new_calc"].SourceType);
            Assert.False(schema.Attributes["new_readonly"].IsValidForCreate);   // null is treated as false
            Assert.False(schema.Attributes["new_readonly"].IsValidForUpdate);
            Assert.True(schema.Attributes["new_readonly"].IsValidForRead);      // ...but null IsValidForRead is readable
            Assert.False(schema.Attributes["new_hidden"].IsValidForRead);
            Assert.Empty(schema.Attributes["name"].LookupTargets);
        }

        [Fact]
        public void FromMetadata_maps_the_private_flag_and_a_missing_plural_name()
        {
            EntityMetadata metadata = AccountMetadata().With("IsPrivate", true);
            metadata.DisplayCollectionName = null;

            EntitySchema schema = DataverseSchemaProvider.FromMetadata(metadata);

            Assert.True(schema.IsPrivate);
            Assert.Null(schema.DisplayCollectionName);
        }

        [Fact]
        public void FromMetadata_without_attributes_gives_an_empty_attribute_map()
        {
            EntitySchema schema = DataverseSchemaProvider.FromMetadata(AccountMetadata().With("Attributes", null));

            Assert.Empty(schema.Attributes);
            Assert.Throws<ArgumentNullException>(() => DataverseSchemaProvider.FromMetadata(null));
        }

        [Theory]
        [InlineData(null, null, false)]                                          // standard table: no data provider
        [InlineData(null, "1d9bde74-9ebd-4da9-8ff5-aa74945b9f74", false)]        // elastic table: a provider, but rows in Dataverse
        [InlineData(null, "c9a7f5b6-2e3d-4a1b-9f80-7d6e5c4b3a21", true)]         // any other data provider: virtual
        [InlineData(null, "00000000-0000-0000-0000-000000000000", false)]        // an empty provider id is no provider
        [InlineData("Virtual", null, true)]                                      // the table type decides when present...
        [InlineData("virtual", "c9a7f5b6-2e3d-4a1b-9f80-7d6e5c4b3a21", true)]
        [InlineData("Standard", "c9a7f5b6-2e3d-4a1b-9f80-7d6e5c4b3a21", false)]  // ...over the provider id
        [InlineData("Elastic", "1d9bde74-9ebd-4da9-8ff5-aa74945b9f74", false)]
        [InlineData(" ", "c9a7f5b6-2e3d-4a1b-9f80-7d6e5c4b3a21", true)]          // a blank table type is no table type
        public void FromMetadata_detects_virtual_tables_by_table_type_else_by_data_provider(string tableType, string dataProviderId, bool expected)
        {
            EntityMetadata metadata = AccountMetadata();
            metadata.TableType = tableType;
            metadata.DataProviderId = dataProviderId == null ? (Guid?)null : new Guid(dataProviderId);

            Assert.Equal(expected, DataverseSchemaProvider.FromMetadata(metadata).IsVirtual);
            Assert.Equal(expected, DataverseSchemaProvider.IsVirtualTable(metadata));
        }

        [Fact]
        public void A_plain_entity_is_not_virtual_and_null_metadata_is_not_virtual()
        {
            Assert.False(DataverseSchemaProvider.FromMetadata(AccountMetadata()).IsVirtual);
            Assert.False(DataverseSchemaProvider.IsVirtualTable(null));
            Assert.Equal(new Guid("1d9bde74-9ebd-4da9-8ff5-aa74945b9f74"), DataverseSchemaProvider.ElasticTableDataProviderId);
        }

        [Fact]
        public void GetEntity_asks_once_per_entity_and_caches_a_missing_entity_as_null()
        {
            var service = new FakeOrganizationService
            {
                ExecuteHandler = request =>
                {
                    var retrieve = (RetrieveEntityRequest)request;
                    Assert.Equal(EntityFilters.Entity | EntityFilters.Attributes, retrieve.EntityFilters);   // no relationships: not needed to compare
                    Assert.False(retrieve.RetrieveAsIfPublished);
                    if (retrieve.LogicalName == "account")
                    {
                        var response = new RetrieveEntityResponse();
                        response.Results["EntityMetadata"] = AccountMetadata();
                        return response;
                    }
                    throw FakeOrganizationService.Fault(FakeOrganizationService.ObjectDoesNotExist,
                        $"Could not find an entity with specified entity name: {retrieve.LogicalName}");
                }
            };
            var provider = new DataverseSchemaProvider(service);

            EntitySchema account = provider.GetEntity("account");
            Assert.NotNull(account);
            Assert.Same(account, provider.GetEntity("ACCOUNT"));
            Assert.Null(provider.GetEntity("new_missing"));
            Assert.Null(provider.GetEntity("new_missing"));
            Assert.Equal(2, service.Executed.Count);
        }

        [Fact]
        public void GetEntity_rethrows_other_failures_and_does_not_cache_them()
        {
            int calls = 0;
            var service = new FakeOrganizationService
            {
                ExecuteHandler = request =>
                {
                    calls++;
                    throw FakeOrganizationService.Fault(FakeOrganizationService.GenericFailure, "Principal user is missing prvReadEntity privilege");
                }
            };
            var provider = new DataverseSchemaProvider(service);

            Assert.Throws<FaultException<OrganizationServiceFault>>(() => provider.GetEntity("account"));
            Assert.Throws<FaultException<OrganizationServiceFault>>(() => provider.GetEntity("account"));
            Assert.Equal(2, calls);
        }

        [Theory]
        [InlineData("Could not find an entity with specified entity name: foo", true)]
        [InlineData("The entity with a name = 'foo' with namemapping = 'Logical' was not found in the MetadataCache.", true)]
        [InlineData("The entity with a name = 'foo' was not found in the MetadataCache.", true)]   // on-premises 9.1
        [InlineData("Entity 'foo' not found", true)]
        [InlineData("Could not find entity foo", true)]
        [InlineData("Entity foo does not exist", true)]
        [InlineData("Principal user is missing prvReadEntity privilege", false)]
        [InlineData("Generic SQL error.", false)]
        public void Missing_entity_faults_are_recognised_by_their_message(string message, bool expected)
        {
            Assert.Equal(expected, DataverseSchemaProvider.IsEntityMissing(FakeOrganizationService.Fault(0, message)));
            Assert.Equal(expected, DataverseSchemaProvider.IsEntityMissing(FakeOrganizationService.Fault(0, message), "foo"));
        }

        [Fact]
        public void A_not_found_fault_about_something_else_is_not_a_missing_entity()
        {
            Assert.False(DataverseSchemaProvider.IsEntityMissing(FakeOrganizationService.Fault(0, "Plug-in assembly not found"), "foo"));
        }

        [Fact]
        public void GetEntity_caches_an_entity_reported_not_found_as_missing()
        {
            var service = new FakeOrganizationService
            {
                ExecuteHandler = request => throw FakeOrganizationService.Fault(0, $"Entity '{((RetrieveEntityRequest)request).LogicalName}' not found")
            };
            var provider = new DataverseSchemaProvider(service);

            Assert.Null(provider.GetEntity("new_gone"));
            Assert.Null(provider.GetEntity("new_gone"));
            Assert.Single(service.Executed);
        }

        [Fact]
        public void Non_fault_exceptions_are_never_treated_as_a_missing_entity()
        {
            Assert.False(DataverseSchemaProvider.IsEntityMissing(new System.TimeoutException("Could not find the server")));
        }

        internal static EntityMetadata AccountMetadata()
        {
            var state = new StateAttributeMetadata
            {
                LogicalName = "statecode",
                IsValidForCreate = false,
                IsValidForUpdate = true,
                OptionSet = new OptionSetMetadata(new OptionMetadataCollection(new List<OptionMetadata>
                {
                    new StateOptionMetadata { Value = 0, DefaultStatus = 1 },
                    new StateOptionMetadata { Value = 1, DefaultStatus = 2 }
                }))
            };

            var attributes = new AttributeMetadata[]
            {
                new UniqueIdentifierAttributeMetadata { LogicalName = "accountid", IsValidForCreate = true, IsValidForUpdate = false },
                new StringAttributeMetadata { LogicalName = "name", DisplayName = new Label("Account Name", 1033), IsValidForCreate = true, IsValidForUpdate = true },
                new StringAttributeMetadata { LogicalName = "accountnumber", DisplayName = new Label("Account Number", 1033), IsValidForCreate = true, IsValidForUpdate = true },
                new LookupAttributeMetadata { LogicalName = "primarycontactid", DisplayName = new Label("Primary Contact", 1033), Targets = new[] { "contact" }, IsValidForCreate = true, IsValidForUpdate = true },
                new LookupAttributeMetadata { LogicalName = "ownerid", Targets = new[] { "systemuser", "team" }, IsValidForCreate = true, IsValidForUpdate = true },
                state,
                new StatusAttributeMetadata { LogicalName = "statuscode", IsValidForCreate = true, IsValidForUpdate = true },
                new FileAttributeMetadata { LogicalName = "new_document" },
                new MultiSelectPicklistAttributeMetadata { LogicalName = "new_tags", IsValidForCreate = true, IsValidForUpdate = true },
                new ImageAttributeMetadata { LogicalName = "entityimage", IsValidForCreate = true, IsValidForUpdate = true },
                new MoneyAttributeMetadata { LogicalName = "revenue_base" }.With("AttributeOf", "revenue"),
                new StringAttributeMetadata { LogicalName = "new_calc", SourceType = 1 },
                new StringAttributeMetadata { LogicalName = "new_readonly" },
                new StringAttributeMetadata { LogicalName = "new_hidden" }.With("IsValidForRead", false)
            };

            return new EntityMetadata
                {
                    LogicalName = "account", SchemaName = "Account", DisplayName = new Label("Account", 1033), DisplayCollectionName = new Label("Accounts", 1033)
                }
                .With("PrimaryIdAttribute", "accountid")
                .With("PrimaryNameAttribute", "name")
                .With("IsIntersect", false)
                .With("Attributes", attributes);
        }
    }
}
