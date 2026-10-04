using System;
using Microsoft.Xrm.Sdk;
using MyscotekDataCompare.Core;
using MyscotekDataCompare.Core.Schema;
using MyscotekDataCompare.Tests.Fakes;
using Xunit;
using static MyscotekDataCompare.Tests.Fakes.TestData;

namespace MyscotekDataCompare.Tests
{
    /// <summary>SPEC 5.6: when two values, one from each environment, are equal.</summary>
    public class ValueComparerTests
    {
        private static readonly Guid A = Id(1), B = Id(2);

        [Fact]
        public void Null_absent_and_empty_values_are_all_equal_to_each_other()
        {
            Assert.True(ValueComparer.AreEqual(null, null));
            Assert.True(ValueComparer.AreEqual(string.Empty, null));
            Assert.True(ValueComparer.AreEqual(null, new OptionSetValueCollection()));
            Assert.True(ValueComparer.AreEqual(Parties(), null));
            Assert.True(ValueComparer.AreEqual(new byte[0], null));
            Assert.False(ValueComparer.AreEqual("x", null));
            Assert.False(ValueComparer.AreEqual(null, 0));                 // zero is a value
            Assert.False(ValueComparer.AreEqual(false, null));             // so is false
        }

        [Fact]
        public void Lookups_are_equal_by_logical_name_and_id_whatever_their_names()
        {
            Assert.True(ValueComparer.AreEqual(Ref("contact", A, "Jane Doe"), Ref("contact", A, "Jane Smith")));
            Assert.True(ValueComparer.AreEqual(Ref("contact", A), Ref("Contact", A)));
            Assert.False(ValueComparer.AreEqual(Ref("contact", A), Ref("contact", B)));
            Assert.False(ValueComparer.AreEqual(Ref("contact", A), Ref("account", A)));
            Assert.False(ValueComparer.AreEqual(Ref("contact", A), A));   // a Guid is not a lookup
        }

        [Fact]
        public void Option_sets_are_equal_by_value()
        {
            Assert.True(ValueComparer.AreEqual(Opt(3), Opt(3)));
            Assert.False(ValueComparer.AreEqual(Opt(3), Opt(4)));
            Assert.False(ValueComparer.AreEqual(Opt(3), 3));
        }

        [Fact]
        public void Multi_select_option_sets_are_equal_as_sets_of_values()
        {
            var first = new OptionSetValueCollection { Opt(1), Opt(3) };

            Assert.True(ValueComparer.AreEqual(first, new OptionSetValueCollection { Opt(3), Opt(1) }));
            Assert.True(ValueComparer.AreEqual(first, new OptionSetValueCollection { Opt(3), Opt(1), Opt(1) }));
            Assert.False(ValueComparer.AreEqual(first, new OptionSetValueCollection { Opt(1) }));
            Assert.False(ValueComparer.AreEqual(first, new OptionSetValueCollection { Opt(1), Opt(2) }));
        }

        [Fact]
        public void Money_is_equal_by_its_decimal_value()
        {
            Assert.True(ValueComparer.AreEqual(new Money(1.5m), new Money(1.50m)));
            Assert.False(ValueComparer.AreEqual(new Money(1.5m), new Money(1.51m)));
            Assert.False(ValueComparer.AreEqual(new Money(1.5m), 1.5m));
        }

        [Fact]
        public void Dates_are_compared_as_utc()
        {
            var utc = new DateTime(2024, 3, 1, 14, 30, 5, DateTimeKind.Utc);

            Assert.True(ValueComparer.AreEqual(utc, utc.ToLocalTime()));                                   // the same instant
            Assert.True(ValueComparer.AreEqual(utc, DateTime.SpecifyKind(utc, DateTimeKind.Unspecified))); // unspecified is taken as UTC
            Assert.False(ValueComparer.AreEqual(utc, utc.AddSeconds(1)));
            Assert.False(ValueComparer.AreEqual(utc, utc.AddTicks(1)));
        }

        [Fact]
        public void Numbers_booleans_and_guids_use_equality_of_the_same_type()
        {
            Assert.True(ValueComparer.AreEqual(42, 42));
            Assert.True(ValueComparer.AreEqual(42L, 42L));
            Assert.True(ValueComparer.AreEqual(1.10m, 1.1m));
            Assert.True(ValueComparer.AreEqual(0.1d, 0.1d));
            Assert.True(ValueComparer.AreEqual(true, true));
            Assert.True(ValueComparer.AreEqual(A, new Guid(A.ToString())));
            Assert.False(ValueComparer.AreEqual(42, 43));
            Assert.False(ValueComparer.AreEqual(0.1d, 0.10000001d));
            Assert.False(ValueComparer.AreEqual(true, false));
            Assert.False(ValueComparer.AreEqual(A, B));
            Assert.False(ValueComparer.AreEqual(42, 42L));     // different types: the schema differs, so does the value
            Assert.False(ValueComparer.AreEqual(42, 42m));
        }

        [Fact]
        public void Strings_are_compared_ordinally_without_trimming()
        {
            Assert.True(ValueComparer.AreEqual("Contoso", "Contoso"));
            Assert.False(ValueComparer.AreEqual("Contoso", "contoso"));
            Assert.False(ValueComparer.AreEqual("Contoso", "Contoso "));
            Assert.False(ValueComparer.AreEqual("a\r\nb", "a\nb"));
        }

        [Fact]
        public void Byte_arrays_are_compared_byte_by_byte()
        {
            Assert.True(ValueComparer.AreEqual(new byte[] { 1, 2, 3 }, new byte[] { 1, 2, 3 }));
            Assert.False(ValueComparer.AreEqual(new byte[] { 1, 2, 3 }, new byte[] { 1, 2, 4 }));
            Assert.False(ValueComparer.AreEqual(new byte[] { 1, 2, 3 }, new byte[] { 1, 2 }));
        }

        [Fact]
        public void Party_lists_are_equal_as_multisets_of_parties_whatever_the_activityparty_rows()
        {
            EntityCollection first = Parties(Party(Ref("contact", A)), Party(Ref("account", B)), Party(null, addressUsed: "x@example.com"));
            EntityCollection reordered = Parties(Party(null, addressUsed: "x@example.com"), Party(Ref("account", B, "Renamed")), Party(Ref("Contact", A)));

            Assert.True(ValueComparer.AreEqual(first, reordered));   // new activityparty ids, other order, names and case
            Assert.False(ValueComparer.AreEqual(first, Parties(Party(Ref("contact", A)), Party(Ref("account", B)))));
            Assert.False(ValueComparer.AreEqual(first, Parties(Party(Ref("contact", A)), Party(Ref("account", B)), Party(null, addressUsed: "X@example.com"))));
            Assert.False(ValueComparer.AreEqual(
                Parties(Party(Ref("contact", A)), Party(Ref("contact", A))),
                Parties(Party(Ref("contact", A)))));   // a multiset: the count matters
        }

        [Fact]
        public void Aliased_values_are_never_compared()
        {
            Assert.True(ValueComparer.AreEqual(new AliasedValue("contact", "emailaddress1", "a@x.com"), new AliasedValue("contact", "emailaddress1", "b@x.com")));
            Assert.True(ValueComparer.AreEqual(new AliasedValue("contact", "emailaddress1", "a@x.com"), null));
        }

        [Fact]
        public void Values_of_different_types_are_different()
        {
            Assert.False(ValueComparer.AreEqual("1", 1));
            Assert.False(ValueComparer.AreEqual(Opt(1), new OptionSetValueCollection { Opt(1) }));
        }

        [Fact]
        public void A_file_column_is_compared_by_presence_only()
        {
            var file = new AttributeSchema { LogicalName = "new_document", IsFile = true };
            var plain = new AttributeSchema { LogicalName = "new_externalid" };

            Assert.True(ValueComparer.AreEqual(A, B, file));        // both have a file: their ids differ by design
            Assert.False(ValueComparer.AreEqual(A, null, file));    // one has none
            Assert.True(ValueComparer.AreEqual(null, null, file));
            Assert.False(ValueComparer.AreEqual(A, B, plain));
            Assert.False(ValueComparer.AreEqual(A, B, null));
        }

        [Fact]
        public void IsEmpty_covers_null_empty_strings_collections_and_byte_arrays()
        {
            Assert.True(ValueComparer.IsEmpty(null));
            Assert.True(ValueComparer.IsEmpty(string.Empty));
            Assert.True(ValueComparer.IsEmpty(new OptionSetValueCollection()));
            Assert.True(ValueComparer.IsEmpty(new EntityCollection()));
            Assert.True(ValueComparer.IsEmpty(new byte[0]));
            Assert.False(ValueComparer.IsEmpty(" "));
            Assert.False(ValueComparer.IsEmpty(0));
            Assert.False(ValueComparer.IsEmpty(Guid.Empty));
        }

        [Fact]
        public void RawText_shows_what_the_comparison_looks_at_culture_invariantly()
        {
            Assert.Equal("null", ValueComparer.RawText(null));
            Assert.Equal("contact 00000001-0000-0000-0000-000000000000", ValueComparer.RawText(Ref("contact", A, "Jane")));
            Assert.Equal("3", ValueComparer.RawText(Opt(3)));
            Assert.Equal("1, 3", ValueComparer.RawText(new OptionSetValueCollection { Opt(3), Opt(1) }));
            Assert.Equal("1234.5", ValueComparer.RawText(new Money(1234.5m)));
            Assert.Equal("2024-03-01 14:30:05.0000000 UTC", ValueComparer.RawText(new DateTime(2024, 3, 1, 14, 30, 5, DateTimeKind.Utc)));
            Assert.Equal("\"Contoso \" (8 chars)", ValueComparer.RawText("Contoso "));
            Assert.Equal("\"a\\r\\nb\" (4 chars)", ValueComparer.RawText("a\r\nb"));
            Assert.Equal("\"x\" (1 char)", ValueComparer.RawText("x"));
            Assert.Equal("3 bytes", ValueComparer.RawText(new byte[] { 1, 2, 3 }));
            Assert.Equal("0.1", ValueComparer.RawText(0.1d));
            Assert.Equal("1.5", ValueComparer.RawText(1.5m));
            Assert.Equal("ref:contact:00000001-0000-0000-0000-000000000000", ValueComparer.RawText(Parties(Party(Ref("contact", A)))));
            Assert.Equal("\"x\" (1 char)", ValueComparer.RawText(new AliasedValue("contact", "fullname", "x")));   // unwrapped
        }
    }
}
