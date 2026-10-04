using System;
using System.Collections.Generic;
using System.Linq;
using MyscotekDataCompare.Core;
using XrmToolBox.Extensibility;

namespace MyscotekDataCompare.UI
{
    /// <summary>
    /// The tool's persisted settings (SPEC section 7). XrmToolBox's SettingsManager stores them as XML in
    /// %AppData%\MscrmTools\XrmToolBox\Settings\MyscotekDataCompare.xml, so the class is public with public
    /// read/write properties (XmlSerializer). Saved when the tool closes, when Differences only changes,
    /// when the compare options or the entity mappings are confirmed and when an entity is selected. <see cref="PageSize"/> is
    /// only edited in that file (close XrmToolBox first: the tool rewrites the file when it closes).
    /// Elements the class does not know are skipped when the file is read, and a missing element keeps
    /// its default.
    /// </summary>
    public class DataCompareSettings
    {
        /// <summary>
        /// The ignored attributes, comma-separated (the format of <see cref="CompareOptions.FormatAttributeList"/>).
        /// Default: <see cref="CompareOptions.DefaultIgnoredAttributeList"/>. Blank: nothing is ignored (the
        /// user cleared the list on purpose). Null - an element the file does not have - is the default too.
        /// </summary>
        public string IgnoredAttributes { get; set; } = CompareOptions.DefaultIgnoredAttributeList;

        /// <summary>
        /// Only the columns whose logical names start with one of these prefixes are compared (comma-separated,
        /// the format of <see cref="CompareOptions.FormatPrefixList"/>). Blank or missing (the default): every column.
        /// </summary>
        public string ComparedPrefixes { get; set; } = string.Empty;

        /// <summary>
        /// The global entity mappings (SPEC 5.9): an entity listed here is compared with the mapped table of the
        /// secondary, column pairs for the columns whose names differ. A file without the element: none.
        /// </summary>
        public List<EntityMapping> EntityMappings { get; set; } = new List<EntityMapping>();

        /// <summary>The detail pane's "Differences only" check box.</summary>
        public bool DifferencesOnly { get; set; }

        /// <summary>The entity selected last, per PRIMARY organisation; re-selected when the entity list loads.</summary>
        public List<LastEntity> LastEntities { get; set; } = new List<LastEntity>();

        /// <summary>Records per page of the view query (only in the file). Limited to 1..5000 by <see cref="EffectivePageSize"/>.</summary>
        public int PageSize { get; set; } = CompareOptions.DefaultPageSize;

        /// <summary><see cref="PageSize"/> limited to 1..5000; the default when unset or invalid.</summary>
        internal int EffectivePageSize => PageSize <= 0 ? CompareOptions.DefaultPageSize : Math.Min(PageSize, CompareOptions.MaxPageSize);

        /// <summary>
        /// The options a run uses: the ignored attributes (null = the defaults), the prefix filter, the complete
        /// entity mappings (copies) and the page size.
        /// </summary>
        internal CompareOptions BuildOptions()
        {
            var options = new CompareOptions { PageSize = EffectivePageSize };
            options.SetIgnoredAttributes(IgnoredAttributes);
            options.SetComparedPrefixes(ComparedPrefixes);
            foreach (EntityMapping mapping in EntityMappings ?? new List<EntityMapping>())
            {
                if (mapping != null && mapping.IsComplete()) options.EntityMappings.Add(mapping.Clone());
            }
            return options;
        }

        /// <summary>The first complete mapping of <paramref name="entity"/>, or null when it is not mapped.</summary>
        internal EntityMapping FindMapping(string entity) =>
            string.IsNullOrWhiteSpace(entity) ? null : (EntityMappings ?? new List<EntityMapping>()).FirstOrDefault(m => m != null && m.AppliesTo(entity));

        /// <summary>The entity selected last in <paramref name="organization"/>, or null.</summary>
        internal string GetLastEntity(string organization)
        {
            string org = OrganizationKey(organization);
            foreach (LastEntity entry in LastEntities ?? new List<LastEntity>())
            {
                if (entry != null && !string.IsNullOrWhiteSpace(entry.Entity)
                    && string.Equals(OrganizationKey(entry.Organization), org, StringComparison.Ordinal))
                {
                    return entry.Entity.Trim();
                }
            }
            return null;
        }

        /// <summary>Remembers <paramref name="entity"/> as the entity selected last in <paramref name="organization"/>.</summary>
        internal void SetLastEntity(string organization, string entity)
        {
            string org = OrganizationKey(organization);
            if (LastEntities == null) LastEntities = new List<LastEntity>();
            LastEntities.RemoveAll(e => e == null || string.Equals(OrganizationKey(e.Organization), org, StringComparison.Ordinal));
            if (!string.IsNullOrWhiteSpace(entity))
                LastEntities.Add(new LastEntity { Organization = org, Entity = entity.Trim().ToLowerInvariant() });
        }

        /// <summary>How an organisation is keyed: trimmed, without a trailing slash, lower case ("" when unknown).</summary>
        internal static string OrganizationKey(string organization) =>
            (organization ?? string.Empty).Trim().TrimEnd('/').ToLowerInvariant();

        /// <summary>Reads the settings file; defaults when there is none. May throw if the store is unusable.</summary>
        internal static DataCompareSettings LoadFromXrmToolBox()
        {
            // TryLoad<T> needs the concrete type: with object it would hand back something else and the
            // settings would silently reset to their defaults.
            return SettingsManager.Instance.TryLoad(typeof(DataCompareSettings), out DataCompareSettings settings) && settings != null
                ? settings
                : new DataCompareSettings();
        }

        /// <summary>Writes the settings file. May throw if the store is unusable.</summary>
        internal static void SaveToXrmToolBox(DataCompareSettings settings) =>
            SettingsManager.Instance.Save(typeof(DataCompareSettings), settings);
    }

    /// <summary>The entity selected last in one PRIMARY organisation, as stored in the settings file.</summary>
    public class LastEntity
    {
        /// <summary>The primary organisation: its URL (or name), lower case, without a trailing slash.</summary>
        public string Organization { get; set; }

        /// <summary>The entity logical name.</summary>
        public string Entity { get; set; }
    }
}
