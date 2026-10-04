using System;

namespace MyscotekDataCompare.Core
{
    /// <summary>
    /// The counts of a compare run (SPEC 5.7). On a complete run
    /// <c>Matching + Different + Missing == PrimaryCount + FoundByIdInPrimary</c> and
    /// <c>Matching + Different + Extra == SecondaryCount + FoundByIdInSecondary</c>; the rows of the result
    /// number <see cref="Total"/>. A cancelled run counts the rows it could not decide in <see cref="Unchecked"/>.
    /// </summary>
    public sealed class CompareSummary
    {
        /// <summary>Distinct records the primary's view query returned.</summary>
        public int PrimaryCount { get; set; }

        /// <summary>Distinct records the secondary's view query returned (before it failed, if it did).</summary>
        public int SecondaryCount { get; set; }

        /// <summary>Rows in both environments with every compared column equal (GREEN).</summary>
        public int Matching { get; set; }

        /// <summary>Rows in both environments with at least one compared column different (AMBER).</summary>
        public int Different { get; set; }

        /// <summary>Rows of the primary that do not exist in the secondary (RED).</summary>
        public int Missing { get; set; }

        /// <summary>Rows of the secondary's view that do not exist in the primary (BLUE).</summary>
        public int Extra { get; set; }

        /// <summary>Rows whose status a cancelled run could not decide (not in the result's rows). 0 on a complete run.</summary>
        public int Unchecked { get; set; }

        /// <summary>Primary view rows the secondary's view did not return but that exist in the secondary (looked up by id).</summary>
        public int FoundByIdInSecondary { get; set; }

        /// <summary>Secondary view rows the primary's view did not return but that exist in the primary (looked up by id).</summary>
        public int FoundByIdInPrimary { get; set; }

        /// <summary>Rows with an id already returned by the same query (e.g. a 1:N link-entity without distinct), ignored.</summary>
        public int DuplicateRowsIgnored { get; set; }

        /// <summary>True when the secondary's view query failed (e.g. a linked column the secondary lacks); every unmatched primary row was then looked up by id.</summary>
        public bool SecondaryQueryFailed { get; set; }

        /// <summary>The error of the failed secondary view query; null otherwise.</summary>
        public string SecondaryQueryError { get; set; }

        public bool Cancelled { get; set; }

        public TimeSpan Elapsed { get; set; }

        /// <summary>The rows of the result: Matching + Different + Missing + Extra.</summary>
        public int Total => Matching + Different + Missing + Extra;

        internal CompareSummary Clone() => (CompareSummary)MemberwiseClone();
    }
}
