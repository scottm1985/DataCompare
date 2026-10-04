using System;
using System.Collections.Generic;
using Microsoft.Xrm.Sdk;

namespace MyscotekDataCompare.Core
{
    /// <summary>What a row of the result is (SPEC 1: the row colours).</summary>
    public enum RowStatus
    {
        /// <summary>In both environments and every compared column is equal (GREEN).</summary>
        Match,

        /// <summary>In both environments and at least one compared column differs (AMBER).</summary>
        Different,

        /// <summary>In the primary, not in the secondary at all (RED).</summary>
        Missing,

        /// <summary>In the secondary's view, not in the primary at all (BLUE).</summary>
        Extra
    }

    /// <summary>One record of the result: its id, status and the two records (SPEC 5.7).</summary>
    public sealed class RowComparison
    {
        public RowComparison(Guid id, RowStatus status, Entity primary, Entity secondary, IReadOnlyList<string> differingAttributes,
                             bool inPrimaryView, bool inSecondaryView)
        {
            Id = id;
            Status = status;
            Primary = primary;
            Secondary = secondary;
            DifferingAttributes = differingAttributes ?? Array.Empty<string>();
            InPrimaryView = inPrimaryView;
            InSecondaryView = inSecondaryView;
        }

        /// <summary>The record id (the same GUID in both environments).</summary>
        public Guid Id { get; }

        public RowStatus Status { get; }

        /// <summary>The primary record with all its attributes; null for <see cref="RowStatus.Extra"/>.</summary>
        public Entity Primary { get; }

        /// <summary>The secondary record with all its attributes; null for <see cref="RowStatus.Missing"/>.</summary>
        public Entity Secondary { get; }

        /// <summary>
        /// The compared attributes whose values differ (logical names, ordinal order); empty unless
        /// <see cref="Status"/> is <see cref="RowStatus.Different"/>.
        /// </summary>
        public IReadOnlyList<string> DifferingAttributes { get; }

        /// <summary>True when the primary's view query returned the record (false: found in the primary by id only).</summary>
        public bool InPrimaryView { get; }

        /// <summary>True when the secondary's view query returned the record (false: found in the secondary by id, or missing).</summary>
        public bool InSecondaryView { get; }

        /// <summary>
        /// The record the grid shows the view's columns from: the one a VIEW query returned (it carries the
        /// view's linked columns) - the primary when the primary's view returned it, else the secondary.
        /// </summary>
        public Entity DisplayRecord => InPrimaryView ? Primary ?? Secondary : InSecondaryView ? Secondary ?? Primary : Primary ?? Secondary;

        public override string ToString() => $"{Status} {Id}";
    }
}
