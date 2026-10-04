using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Xrm.Sdk;
using MyscotekDataCompare.Core;
using MyscotekDataCompare.Core.Services;
using MyscotekDataCompare.Tests.Fakes;
using MyscotekDataCompare.UI;
using Xunit;
using Xunit.Abstractions;
using Label = System.Windows.Forms.Label;

namespace MyscotekDataCompare.Tests
{
    /// <summary>
    /// A result of 20 000 rows (six view columns, all four statuses) through the real control: building its
    /// table, binding it, painting, the status, text and differing-column filters, selecting a row and
    /// re-evaluating with other ignored attributes each stay well under a few seconds, and the grid's rows stay shared (the colours come from
    /// CellFormatting, never from per-row styles). The times are written to the test output.
    /// </summary>
    [Collection(UiTestCollection.Name)]
    public class UiPerformanceTests
    {
        private const int RowCount = 20000;

        /// <summary>Generous: the measured times are a fraction of this on a developer machine; a CI agent may be slow.</summary>
        private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

        /// <summary>Choosing a differing column filters at once (measured about 0.1 s): a tighter bound for its steps.</summary>
        private static readonly TimeSpan DifferingFilterBound = TimeSpan.FromSeconds(2);

        private readonly ITestOutputHelper _output;

        public UiPerformanceTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void A_20000_row_result_is_built_bound_filtered_and_re_evaluated_quickly()
        {
            CompareResult result = LargeResult();
            IList<ViewColumn> columns = new[] { "name", "accountnumber", "revenue", "numberofemployees", "industrycode", "pc.emailaddress1" }
                .Select(n => new ViewColumn { Name = n, Width = 120 }).ToList();
            var view = new ViewInfo { Id = Guid.NewGuid(), Name = "Big view", FetchXml = TestData.AccountView };
            var times = new List<(string Step, TimeSpan Time)>();

            UiTestHost.Run(() =>
            {
                using (DataCompareControl control = UiTest.NewControl(new DataCompareSettings()))
                {
                    control.Size = new Size(1600, 900);
                    UiTestHost.Pump();
                    DataGridView grid = UiTestHost.Find<DataGridView>(control, "resultsGrid");

                    DataCompareControl.Shown shown = Measure(times, "build the table (worker thread in the tool)",
                        () => DataCompareControl.Shown.Build(result, columns, null));
                    Measure(times, "bind and paint", () =>
                    {
                        control.ShowResult(view, shown);
                        grid.Refresh();
                        return 0;
                    });
                    Assert.Equal(RowCount, grid.Rows.Count);
                    Assert.Equal("20000 of 20000 rows", UiTestHost.Find<Label>(control, "rowCountLabel").Text);
                    Assert.Equal("Account 7", grid.Rows[7].Cells[2].Value);

                    Measure(times, "status filter (Missing)", () =>
                    {
                        UiTestHost.Find<ComboBox>(control, "statusFilter").SelectedItem = "Missing";
                        grid.Refresh();
                        return 0;
                    });
                    Assert.Equal(RowCount / 4, grid.Rows.Count);

                    Measure(times, "text filter (\"account 1999\")", () =>
                    {
                        UiTestHost.Find<TextBox>(control, "rowFilter").Text = "account 1999";
                        control.ApplyRowFilter();
                        grid.Refresh();
                        return 0;
                    });
                    Assert.Equal(new[] { "Account 1999", "Account 19991", "Account 19995", "Account 19999" }, UiFlowTests.Column(grid, 2));   // Missing: index % 4 == 3

                    UiTestHost.Find<ComboBox>(control, "statusFilter").SelectedItem = "All";
                    UiTestHost.Find<TextBox>(control, "rowFilter").Text = string.Empty;
                    Measure(times, "clear the filters", () =>
                    {
                        control.ApplyRowFilter();
                        grid.Refresh();
                        return 0;
                    });
                    Assert.Equal(RowCount, grid.Rows.Count);

                    // The differing-column filter: filled from the 20 000 rows when the result was shown.
                    ComboBox differing = UiTestHost.Find<ComboBox>(control, "differingColumnFilter");
                    Assert.Equal(new[] { DataCompareControl.AnyColumn, "Account Number (accountnumber)" + DataCompareControl.CountSeparator + (RowCount / 4) },
                        UiFlowTests.Items(differing));
                    Measure(times, "differing-column filter (accountnumber)", () =>
                    {
                        UiFlowTests.ChooseDiffering(differing, "accountnumber");
                        grid.Refresh();
                        return 0;
                    });
                    Assert.Equal(RowCount / 4, grid.Rows.Count);
                    Assert.Equal("5000 of 20000 rows", UiTestHost.Find<Label>(control, "rowCountLabel").Text);
                    Measure(times, "differing-column filter with the status filter (Missing)", () =>
                    {
                        UiTestHost.Find<ComboBox>(control, "statusFilter").SelectedItem = "Missing";
                        grid.Refresh();
                        return 0;
                    });
                    Assert.Equal(0, grid.Rows.Count);
                    UiTestHost.Find<ComboBox>(control, "statusFilter").SelectedItem = "All";
                    Measure(times, "differing-column filter back to (any column)", () =>
                    {
                        differing.SelectedIndex = 0;
                        grid.Refresh();
                        return 0;
                    });
                    Assert.Equal(RowCount, grid.Rows.Count);

                    Measure(times, "select a row deep down (detail pane)", () =>
                    {
                        UiFlowTests.Select(grid, 15001);   // Different
                        return 0;
                    });
                    Assert.EndsWith(" - Different", UiTestHost.Find<Label>(control, "detailCaption").Text);

                    Measure(times, "re-evaluate with nothing ignored", () =>
                    {
                        control.ShowCompareOptionsDialog = form => UiFlowTests.DriveDialog(form, box => box.Text = string.Empty, "okButton");
                        UiTestHost.Find<Button>(control, "compareOptionsButton").PerformClick();
                        grid.Refresh();
                        return 0;
                    });
                    // Every Matching row differs in its (no longer ignored) modifiedon: they all turn Different.
                    Assert.Equal(0, control.Result.Summary.Matching);
                    Assert.Equal(RowCount / 2, control.Result.Summary.Different);
                    // ... and the differing-column filter lists modifiedon too.
                    Assert.Equal(new[]
                    {
                        DataCompareControl.AnyColumn, "Account Number (accountnumber)" + DataCompareControl.CountSeparator + (RowCount / 4),
                        "Modified On (modifiedon)" + DataCompareControl.CountSeparator + (RowCount / 4)
                    }, UiFlowTests.Items(differing));

                    // The rows the grid did not paint are still shared (index -1): no per-row state was created.
                    Assert.Equal(-1, grid.Rows.SharedRow(10000).Index);
                }
            });

            foreach ((string step, TimeSpan time) in times) _output.WriteLine($"{step}: {time.TotalMilliseconds:0} ms");
            Assert.All(times, t => Assert.True(t.Time < Bound, $"{t.Step} took {t.Time.TotalMilliseconds:0} ms"));
            Assert.All(times.Where(t => t.Step.StartsWith("differing-column filter", StringComparison.Ordinal)),
                t => Assert.True(t.Time < DifferingFilterBound, $"{t.Step} took {t.Time.TotalMilliseconds:0} ms"));
        }

        private static T Measure<T>(List<(string, TimeSpan)> times, string step, Func<T> action)
        {
            Stopwatch watch = Stopwatch.StartNew();
            T value = action();
            times.Add((step, watch.Elapsed));
            return value;
        }

        /// <summary>
        /// 20 000 rows in turn Matching, Different, Extra-or-Missing...: index % 4 = 0 Matching (only the ignored
        /// modifiedon differs), 1 Different (account number), 2 Extra, 3 Missing.
        /// </summary>
        private static CompareResult LargeResult()
        {
            var schema = TestData.StandardSchema().GetEntity("account");
            var rows = new List<RowComparison>(RowCount);
            var summary = new CompareSummary();
            DateTime then = new DateTime(2024, 3, 1, 12, 0, 0, DateTimeKind.Utc);
            for (int i = 0; i < RowCount; i++)
            {
                Guid id = TestData.Id(i + 1);
                Entity primary = TestData.Account(id, "Account " + i, ("accountnumber", "N-" + i), ("revenue", new Money(1000m + i)),
                    ("numberofemployees", i), ("industrycode", TestData.Opt(i % 7)), ("modifiedon", then),
                    ("pc.emailaddress1", new AliasedValue("contact", "emailaddress1", "person" + i + "@example.com")));
                switch (i % 4)
                {
                    case 0:
                        rows.Add(new RowComparison(id, RowStatus.Match, primary, primary.Changed(("modifiedon", then.AddDays(1))), null, true, true));
                        summary.Matching++;
                        break;
                    case 1:
                        rows.Add(new RowComparison(id, RowStatus.Different, primary, primary.Changed(("accountnumber", "X-" + i)), new[] { "accountnumber" }, true, true));
                        summary.Different++;
                        break;
                    case 2:
                        rows.Add(new RowComparison(id, RowStatus.Extra, null, primary, null, false, true));
                        summary.Extra++;
                        break;
                    default:
                        rows.Add(new RowComparison(id, RowStatus.Missing, primary, null, null, true, false));
                        summary.Missing++;
                        break;
                }
            }
            summary.PrimaryCount = summary.Matching + summary.Different + summary.Missing;
            summary.SecondaryCount = summary.Matching + summary.Different + summary.Extra;
            return new CompareResult(schema, new CompareOptions(), rows, summary);
        }
    }
}
