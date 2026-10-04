namespace MyscotekDataCompare.Core
{
    /// <summary>The steps of a compare run, in the order they run (SPEC 5.4).</summary>
    public enum ComparePhase
    {
        /// <summary>Step 1: paging the view query on the primary environment.</summary>
        LoadingPrimary,

        /// <summary>Step 2: paging the same query on the secondary environment.</summary>
        LoadingSecondary,

        /// <summary>Step 3: looking up by id, in the secondary, the primary rows its view did not return.</summary>
        CheckingMissing,

        /// <summary>Step 4: looking up by id, in the primary, the secondary rows its view did not return.</summary>
        CheckingExtras,

        /// <summary>Step 5: comparing every pair, column by column.</summary>
        Comparing,

        /// <summary>The run is over (also after a cancellation).</summary>
        Done
    }

    /// <summary>
    /// Progress snapshot reported through <see cref="System.IProgress{T}"/>: at the start of every phase,
    /// after every page and every lookup batch, every 1000 compared rows, and once with
    /// <see cref="ComparePhase.Done"/> at the end. A new instance is reported each time (safe to keep).
    /// </summary>
    public sealed class CompareProgress
    {
        public ComparePhase Phase { get; set; }

        /// <summary>Loading: the page just read (1-based). Checking: the lookup batch just run (1-based). Otherwise 0.</summary>
        public int PageNumber { get; set; }

        /// <summary>Loading: records read so far on that side. Checking: ids looked up so far. Comparing / Done: rows classified so far.</summary>
        public int RecordsSoFar { get; set; }

        /// <summary>The number of records or ids the phase works through, when known (never while loading); null otherwise.</summary>
        public int? Total { get; set; }

        /// <summary>A one-line description for a progress label, e.g. "Primary: page 3, 15000 records".</summary>
        public string Message { get; set; }

        public override string ToString() => Message ?? Phase.ToString();
    }
}
