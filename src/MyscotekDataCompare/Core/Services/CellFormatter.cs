using System;
using System.Globalization;
using System.Linq;
using Microsoft.Xrm.Sdk;

namespace MyscotekDataCompare.Core.Services
{
    /// <summary>Turns a record's attribute value into the text shown in a grid cell.</summary>
    public static class CellFormatter
    {
        /// <summary>Shown for image (byte[]) values.</summary>
        public const string ImagePlaceholder = "(image)";

        /// <summary>
        /// FormattedValues[column] when present; otherwise the value formatted by type (see
        /// <see cref="FormatValue"/>). A missing column gives "".
        /// </summary>
        public static string Format(Entity e, string column)
        {
            if (e == null || string.IsNullOrEmpty(column)) return string.Empty;
            if (e.FormattedValues != null && e.FormattedValues.TryGetValue(column, out string formatted) && formatted != null) return formatted;
            return e.Attributes.TryGetValue(column, out object value) ? FormatValue(value) : string.Empty;
        }

        /// <summary>
        /// The text of a value in the detail pane (SPEC 5.8), which must show what the comparison looks at:
        /// a lookup is "Name (id)" (just the id without a name) - lookups are compared by id; an option set
        /// is "Label (value)" (just the value without a label) and a multi-select "Label; Label (1, 3)";
        /// a party list is its parties, each "Name (id)" or the unresolved address, joined with "; "; an
        /// image or other byte array "(image, N bytes)"; anything else as <see cref="Format"/> shows it
        /// (FormattedValues first, so money, dates and yes/no read as in Dynamics). Null or missing: "".
        /// </summary>
        public static string FormatDetail(Entity e, string column)
        {
            if (e == null || string.IsNullOrEmpty(column)) return string.Empty;
            if (!e.Attributes.TryGetValue(column, out object value) || value == null) return string.Empty;
            string formatted = e.FormattedValues != null && e.FormattedValues.TryGetValue(column, out string text) && !string.IsNullOrEmpty(text)
                ? text
                : null;

            switch (value)
            {
                case EntityReference reference:
                    return Reference(reference, formatted);
                case OptionSetValue option:
                    string number = option.Value.ToString(CultureInfo.CurrentCulture);
                    return formatted != null && formatted != number ? $"{formatted} ({number})" : number;
                case OptionSetValueCollection options:
                    string numbers = FormatValue(options);
                    return formatted != null && formatted != numbers ? $"{formatted} ({numbers})" : numbers;
                case EntityCollection parties:
                    return string.Join("; ", parties.Entities
                        .Select(p => p.Attributes.TryGetValue("partyid", out object party) && party is EntityReference partyReference
                            ? Reference(partyReference, null)
                            : p.GetAttributeValue<string>("addressused"))
                        .Where(s => !string.IsNullOrEmpty(s)));
                case byte[] bytes:
                    return $"({ImagePlaceholder.Trim('(', ')')}, {bytes.Length.ToString("N0", CultureInfo.CurrentCulture)} bytes)";
                case AliasedValue aliased:
                    return formatted ?? FormatValue(aliased.Value);
                default:
                    return formatted ?? FormatValue(value);
            }
        }

        private static string Reference(EntityReference reference, string formattedName)
        {
            string name = !string.IsNullOrEmpty(formattedName) ? formattedName : reference.Name;
            return string.IsNullOrEmpty(name) ? reference.Id.ToString() : $"{name} ({reference.Id})";
        }

        /// <summary>
        /// EntityReference: Name (fallback id); AliasedValue: unwrapped then formatted; Money: "N2";
        /// OptionSetValue: its value; OptionSetValueCollection: values joined; DateTime: local time "g";
        /// bool; byte[]: "(image)"; activity parties: party names joined; null: "".
        /// </summary>
        public static string FormatValue(object value)
        {
            switch (value)
            {
                case null:
                    return string.Empty;
                case AliasedValue aliased:
                    return FormatValue(aliased.Value);
                case EntityReference reference:
                    return !string.IsNullOrEmpty(reference.Name) ? reference.Name : reference.Id.ToString();
                case Money money:
                    return money.Value.ToString("N2", CultureInfo.CurrentCulture);
                case OptionSetValue option:
                    return option.Value.ToString(CultureInfo.CurrentCulture);
                case OptionSetValueCollection options:
                    return string.Join(", ", options.Where(o => o != null).Select(o => o.Value.ToString(CultureInfo.CurrentCulture)));
                case DateTime date:
                    return date.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
                case bool flag:
                    return flag.ToString(CultureInfo.CurrentCulture);
                case byte[] _:
                    return ImagePlaceholder;
                case EntityCollection parties:
                    return string.Join("; ", parties.Entities
                        .Select(p => p.Attributes.TryGetValue("partyid", out object party) && party != null
                            ? FormatValue(party)
                            : p.GetAttributeValue<string>("addressused"))
                        .Where(s => !string.IsNullOrEmpty(s)));
                case IFormattable formattable:
                    return formattable.ToString(null, CultureInfo.CurrentCulture);
                default:
                    return value.ToString();
            }
        }
    }
}
