using System;
using Microsoft.Xrm.Sdk.Metadata;

namespace MyscotekDataCompare.Core.Schema
{
    /// <summary>The slice of an attribute's metadata the compare engine and the UI need.</summary>
    public sealed class AttributeSchema
    {
        public string LogicalName;
        public string DisplayName;                  // the user-localised label (grid headers, detail pane); the logical name when there is none
        public AttributeTypeCode AttributeType;     // Microsoft.Xrm.Sdk.Metadata
        public bool IsValidForRead = true;          // false: never returned by a query, never compared (SPEC 5.5); null metadata counts as true
        public bool IsValidForCreate, IsValidForUpdate;
        public string AttributeOf;                  // non-null => derived attribute (e.g. *_base, *name, entityimage_url): shown, never compared (SPEC 5.5)
        public int SourceType;                      // 0 simple, 1 calculated, 2 rollup (compared like any other)
        public bool IsFile;                         // FileAttributeMetadata: compared by presence only (SPEC 5.6)
        public bool IsMultiSelect;                  // MultiSelectPicklistAttributeMetadata (Virtual type, OptionSetValueCollection values)
        public bool IsImage;                        // ImageAttributeMetadata (Virtual type, byte[] values)
        public string[] LookupTargets = Array.Empty<string>();   // for Lookup/Customer/Owner/PartyList
    }
}
