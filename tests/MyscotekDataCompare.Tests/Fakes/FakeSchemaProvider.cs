using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xrm.Sdk.Metadata;
using MyscotekDataCompare.Core.Schema;

namespace MyscotekDataCompare.Tests.Fakes
{
    /// <summary>
    /// In-memory <see cref="ISchemaProvider"/> with a fluent builder:
    /// <c>new FakeSchemaProvider().Entity("account").Lookup("primarycontactid", "contact").String("name")...</c>
    /// Unknown entities return null (like a missing entity); <see cref="Failures"/> throw. Every request
    /// is recorded (thread-safe: the UI reads metadata on worker threads).
    /// </summary>
    public sealed class FakeSchemaProvider : ISchemaProvider
    {
        private readonly Dictionary<string, EntitySchemaBuilder> _entities =
            new Dictionary<string, EntitySchemaBuilder>(StringComparer.OrdinalIgnoreCase);
        private readonly object _sync = new object();

        /// <summary>Logical names passed to <see cref="GetEntity"/>, in order.</summary>
        public List<string> Requests { get; } = new List<string>();

        /// <summary>GetEntity of one of these entities throws the given exception (metadata that cannot be read).</summary>
        public Dictionary<string, Exception> Failures { get; } = new Dictionary<string, Exception>(StringComparer.OrdinalIgnoreCase);

        public EntitySchema GetEntity(string logicalName)
        {
            lock (_sync)
            {
                Requests.Add(logicalName);
                if (logicalName != null && Failures.TryGetValue(logicalName, out Exception failure)) throw failure;
                return logicalName != null && _entities.TryGetValue(logicalName, out EntitySchemaBuilder builder) ? builder.Schema : null;
            }
        }

        /// <summary>The requests made so far (a copy, safe to enumerate while worker threads ask).</summary>
        public IReadOnlyList<string> RequestsSoFar()
        {
            lock (_sync)
            {
                return Requests.ToList();
            }
        }

        /// <summary>Starts (or replaces) an entity. The primary id attribute is added automatically, and
        /// the primary name attribute as a String when one is given.</summary>
        public EntitySchemaBuilder Entity(string logicalName, string primaryName = "name", string primaryId = null)
        {
            var builder = new EntitySchemaBuilder(this, logicalName, primaryId ?? logicalName + "id", primaryName);
            _entities[logicalName] = builder;
            return builder;
        }

        /// <summary>Continues building an entity already defined.</summary>
        public EntitySchemaBuilder Edit(string logicalName) => _entities[logicalName];

        public FakeSchemaProvider RemoveEntity(string logicalName)
        {
            _entities.Remove(logicalName);
            return this;
        }

        public FakeSchemaProvider RemoveAttribute(string logicalName, string attribute)
        {
            _entities[logicalName].Remove(attribute);
            return this;
        }
    }

    /// <summary>Fluent builder for one <see cref="EntitySchema"/> of a <see cref="FakeSchemaProvider"/>.</summary>
    public sealed class EntitySchemaBuilder
    {
        private readonly FakeSchemaProvider _owner;
        private readonly Dictionary<string, AttributeSchema> _attributes = new Dictionary<string, AttributeSchema>(StringComparer.OrdinalIgnoreCase);

        internal EntitySchemaBuilder(FakeSchemaProvider owner, string logicalName, string primaryId, string primaryName)
        {
            _owner = owner;
            Schema = new EntitySchema
            {
                LogicalName = logicalName,
                PrimaryIdAttribute = primaryId,
                PrimaryNameAttribute = primaryName,
                DisplayName = logicalName,
                Attributes = _attributes
            };
            Attribute(primaryId, AttributeTypeCode.Uniqueidentifier, create: true, update: false);
            if (primaryName != null) String(primaryName);
        }

        public EntitySchema Schema { get; }

        /// <summary>Adds (or replaces) an attribute; <paramref name="configure"/> can set the other flags.</summary>
        public EntitySchemaBuilder Attribute(string name, AttributeTypeCode type, bool create = true, bool update = true, Action<AttributeSchema> configure = null)
        {
            var attribute = new AttributeSchema
            {
                LogicalName = name,
                AttributeType = type,
                IsValidForCreate = create,
                IsValidForUpdate = update
            };
            configure?.Invoke(attribute);
            _attributes[name] = attribute;
            return this;
        }

        public EntitySchemaBuilder String(string name, bool create = true, bool update = true) => Attribute(name, AttributeTypeCode.String, create, update);
        public EntitySchemaBuilder Memo(string name) => Attribute(name, AttributeTypeCode.Memo);
        public EntitySchemaBuilder Int(string name) => Attribute(name, AttributeTypeCode.Integer);
        public EntitySchemaBuilder BigInt(string name) => Attribute(name, AttributeTypeCode.BigInt);
        public EntitySchemaBuilder Decimal(string name) => Attribute(name, AttributeTypeCode.Decimal);
        public EntitySchemaBuilder Double(string name) => Attribute(name, AttributeTypeCode.Double);
        public EntitySchemaBuilder Money(string name) => Attribute(name, AttributeTypeCode.Money);
        public EntitySchemaBuilder Bool(string name) => Attribute(name, AttributeTypeCode.Boolean);
        public EntitySchemaBuilder Picklist(string name) => Attribute(name, AttributeTypeCode.Picklist);
        public EntitySchemaBuilder Guid(string name) => Attribute(name, AttributeTypeCode.Uniqueidentifier);
        public EntitySchemaBuilder DateTime(string name, bool create = true, bool update = true) => Attribute(name, AttributeTypeCode.DateTime, create, update);

        public EntitySchemaBuilder MultiSelect(string name) => Attribute(name, AttributeTypeCode.Virtual, configure: a => a.IsMultiSelect = true);
        public EntitySchemaBuilder Image(string name) => Attribute(name, AttributeTypeCode.Virtual, configure: a => a.IsImage = true);
        public EntitySchemaBuilder File(string name) => Attribute(name, AttributeTypeCode.Virtual, configure: a => a.IsFile = true);
        public EntitySchemaBuilder Calculated(string name) => Attribute(name, AttributeTypeCode.String, configure: a => a.SourceType = 1);
        public EntitySchemaBuilder Derived(string name, string attributeOf, AttributeTypeCode type = AttributeTypeCode.String) =>
            Attribute(name, type, configure: a => a.AttributeOf = attributeOf);
        public EntitySchemaBuilder Unreadable(string name) => Attribute(name, AttributeTypeCode.String, configure: a => a.IsValidForRead = false);

        public EntitySchemaBuilder Lookup(string name, params string[] targets) =>
            Attribute(name, AttributeTypeCode.Lookup, configure: a => a.LookupTargets = targets);

        public EntitySchemaBuilder Customer(string name) =>
            Attribute(name, AttributeTypeCode.Customer, configure: a => a.LookupTargets = new[] { "account", "contact" });

        public EntitySchemaBuilder PartyList(string name, params string[] targets) =>
            Attribute(name, AttributeTypeCode.PartyList, configure: a => a.LookupTargets = targets);

        /// <summary>ownerid (Owner: systemuser/team) plus the read-only owning* lookups.</summary>
        public EntitySchemaBuilder Owner() =>
            Attribute("ownerid", AttributeTypeCode.Owner, configure: a => a.LookupTargets = new[] { "systemuser", "team" })
            .Attribute("owninguser", AttributeTypeCode.Lookup, create: false, update: false, configure: a => a.LookupTargets = new[] { "systemuser" })
            .Attribute("owningteam", AttributeTypeCode.Lookup, create: false, update: false, configure: a => a.LookupTargets = new[] { "team" })
            .Attribute("owningbusinessunit", AttributeTypeCode.Lookup, create: false, update: false, configure: a => a.LookupTargets = new[] { "businessunit" });

        /// <summary>statecode + statuscode.</summary>
        public EntitySchemaBuilder State() =>
            Attribute("statecode", AttributeTypeCode.State, create: false, update: true)
            .Attribute("statuscode", AttributeTypeCode.Status, create: true, update: true);

        /// <summary>Marks the entity as a virtual table (rows in an external data source).</summary>
        public EntitySchemaBuilder VirtualTable(bool isVirtual = true)
        {
            Schema.IsVirtual = isVirtual;
            return this;
        }

        /// <summary>Sets the display name of an attribute already added (null by default, like a label-less attribute).</summary>
        public EntitySchemaBuilder Label(string attribute, string displayName)
        {
            _attributes[attribute].DisplayName = displayName;
            return this;
        }

        /// <summary>Sets the entity's display name (the logical name by default) and, optionally, its plural.</summary>
        public EntitySchemaBuilder EntityDisplayName(string displayName, string collectionName = null)
        {
            Schema.DisplayName = displayName;
            Schema.DisplayCollectionName = collectionName;
            return this;
        }

        /// <summary>The audit/system attributes every Dataverse entity carries (all in the default ignored list).</summary>
        public EntitySchemaBuilder SystemAttributes() =>
            DateTime("createdon", create: false, update: false)
            .DateTime("modifiedon", create: false, update: false)
            .DateTime("overriddencreatedon", create: true, update: false)
            .Attribute("createdby", AttributeTypeCode.Lookup, create: false, update: false, configure: a => a.LookupTargets = new[] { "systemuser" })
            .Attribute("modifiedby", AttributeTypeCode.Lookup, create: false, update: false, configure: a => a.LookupTargets = new[] { "systemuser" })
            .Attribute("createdonbehalfby", AttributeTypeCode.Lookup, create: false, update: false, configure: a => a.LookupTargets = new[] { "systemuser" })
            .Attribute("modifiedonbehalfby", AttributeTypeCode.Lookup, create: false, update: false, configure: a => a.LookupTargets = new[] { "systemuser" })
            .Attribute("versionnumber", AttributeTypeCode.BigInt, create: false, update: false)
            .Attribute("importsequencenumber", AttributeTypeCode.Integer, create: true, update: false)
            .Attribute("timezoneruleversionnumber", AttributeTypeCode.Integer)
            .Attribute("utcconversiontimezonecode", AttributeTypeCode.Integer);

        /// <summary>Continues with another entity of the same provider.</summary>
        public EntitySchemaBuilder Entity(string logicalName, string primaryName = "name", string primaryId = null) =>
            _owner.Entity(logicalName, primaryName, primaryId);

        public FakeSchemaProvider Done() => _owner;

        internal void Remove(string attribute) => _attributes.Remove(attribute);
    }
}
