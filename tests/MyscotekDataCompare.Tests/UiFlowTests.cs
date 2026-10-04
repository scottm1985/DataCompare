using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using McTools.Xrm.Connection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using MyscotekDataCompare.Core;
using MyscotekDataCompare.Tests.Fakes;
using MyscotekDataCompare.UI;
using XrmToolBox.Extensibility;
using Xunit;
using Label = System.Windows.Forms.Label;

namespace MyscotekDataCompare.Tests
{
    /// <summary>
    /// Drives the real control end to end against fake primary and secondary services (see
    /// <see cref="UiScenario"/>): connecting both, the entity and view lists, a comparison, the row colours,
    /// the status and text filters, the summary strip, the detail pane with Differences only, the ignored
    /// attributes (re-evaluated without reading again), Cancel, failures and the connection requests. The
    /// message pump is run explicitly (<see cref="UiTestHost.PumpUntil"/>).
    /// </summary>
    [Collection(UiTestCollection.Name)]
    public class UiFlowTests
    {
        [Fact]
        public void Connect_both_compare_a_view_and_explore_the_result()
        {
            var scenario = new UiScenario();
            UiTestHost.Run(() =>
            {
                using (DataCompareControl control = UiTest.NewControl(scenario.Settings, scenario.Save, scenario.Dialogs))
                {
                    // 1. Primary connected: entities by display name, the last entity re-selected, its views loaded.
                    control.UpdateConnection(scenario.Primary, null, string.Empty, null);
                    ListBox views = UiTestHost.Find<ListBox>(control, "viewList");
                    UiTestHost.PumpUntil(() => !control.IsBusy && views.Items.Count > 0, "the entities and the account views");

                    ListView entities = UiTestHost.Find<ListView>(control, "entityList");
                    Assert.Equal(new[] { "Account", "Contact", "User" }, entities.Items.Cast<ListViewItem>().Select(i => i.Text));
                    Assert.Equal(new[] { "account", "contact", "systemuser" }, entities.Items.Cast<ListViewItem>().Select(i => i.SubItems[1].Text));
                    Assert.Equal("account", Assert.Single(entities.SelectedItems.Cast<ListViewItem>()).Name);
                    // The system views by name, then the personal views (whatever their names).
                    Assert.Equal(new[] { "Active Accounts", "Active Accounts and Primary Contacts", "Accounts I Follow (personal)" },
                        views.Items.Cast<object>().Select(views.GetItemText));
                    Assert.Equal("Active Accounts", views.Text);
                    Assert.Equal("Primary: (unnamed connection)", UiTestHost.FindToolItem<ToolStripLabel>(control, "primaryLabel").Text);
                    Assert.Contains("3 entities loaded from the primary.", UiTest.LogText(control));

                    // 2. Compare needs the secondary; it never replaces the primary.
                    Button compare = UiTestHost.Find<Button>(control, "compareButton");
                    Assert.False(compare.Enabled);
                    control.UpdateConnection(scenario.Secondary, null, DataCompareControl.SecondaryActionName, DataCompareControl.SecondaryParameter);
                    Assert.True(compare.Enabled);
                    Assert.Same(scenario.Primary, control.Service);
                    Assert.Equal("Secondary: (unnamed connection)", UiTestHost.FindToolItem<ToolStripLabel>(control, "secondaryLabel").Text);

                    // 3. Compare: the rows in the primary view's order, then the extras; headers, widths, summary.
                    compare.PerformClick();
                    UiTestHost.PumpUntil(() => !control.IsBusy && control.Result != null, "the comparison");

                    DataGridView grid = UiTestHost.Find<DataGridView>(control, "resultsGrid");
                    Assert.Equal(new[] { "Matching", "Different", "Missing", "Matching", "Different", "Extra" }, Column(grid, 0));
                    Assert.Equal(new[] { "Contoso Ltd", "Fabrikam", "Northwind", "O'Neil's Bakery", "Tailspin [UK] 50%", "Litware" }, Column(grid, 2));
                    Assert.Equal(new[] { "A-1", "A-2", "A-3", "A-5", "A-6", "A-4x" }, Column(grid, 3));   // an extra row shows the secondary record
                    List<DataGridViewColumn> visible = grid.Columns.Cast<DataGridViewColumn>().Where(c => c.Visible).ToList();
                    Assert.Equal(new[] { "Status", "Account Name", "Account Number" }, visible.Select(c => c.HeaderText));   // display names
                    Assert.Equal(new[] { "name", "accountnumber" }, visible.Skip(1).Select(c => c.ToolTipText));            // the raw names
                    Assert.Equal(new[] { 300, 120 }, visible.Skip(1).Select(c => c.Width));
                    Assert.False(grid.Columns[DataCompareControl.IdColumn].Visible);
                    Assert.Equal(new[] { 1, 2, 3, 5, 6, 4 }.Select(TestData.Id),
                        control.ResultTable.Rows.Cast<System.Data.DataRow>().Select(r => (Guid)r[DataCompareControl.IdColumn]));

                    Assert.Equal(new[] { "Primary: 5", "Secondary: 4", "|", "Missing: 1", "Different: 2", "Extra: 1", "Matching: 2" }, VisibleSummary(control));
                    Assert.Equal(DataCompareControl.MissingColor, UiTestHost.Find<Label>(control, "missingCountLabel").BackColor);
                    Assert.Equal(DataCompareControl.DifferentColor, UiTestHost.Find<Label>(control, "differentCountLabel").BackColor);
                    Assert.Equal(DataCompareControl.ExtraColor, UiTestHost.Find<Label>(control, "extraCountLabel").BackColor);
                    Assert.Equal(DataCompareControl.MatchColor, UiTestHost.Find<Label>(control, "matchingCountLabel").BackColor);
                    Assert.Equal("Done: 6 rows - missing 1, different 2, extra 1, matching 2", UiTestHost.Find<Label>(control, "progressLabel").Text);
                    Label rowCount = UiTestHost.Find<Label>(control, "rowCountLabel");
                    Assert.Equal("6 of 6 rows", rowCount.Text);
                    Assert.False(UiTestHost.Find<Button>(control, "cancelButton").Enabled);
                    Assert.True(compare.Enabled);

                    string log = UiTest.LogText(control);
                    Assert.Contains("] Comparing view \"Active Accounts\" of account between (unnamed connection) (primary) and (unnamed connection) (secondary)", log);
                    Assert.Contains("] Comparing Account (account) records matched on accountid: ", log);
                    Assert.Contains("] Result: 6 rows - matching 2, different 2, missing 1, extra 1.", log);
                    Assert.Empty(scenario.Primary.Writes);
                    Assert.Empty(scenario.Secondary.Writes);
                    Assert.Empty(scenario.Dialogs.Messages);
                    // The primary metadata was read once per entity, off the UI thread, for the headers and the run.
                    Assert.Equal(new[] { "account" }, scenario.MetadataRequests);
                    Assert.DoesNotContain("UI test", scenario.MetadataThreads);

                    // 4. Row colours by status, dark text; a selected row keeps a darker shade of its colour.
                    grid.ClearSelection();
                    Assert.Equal(new[] { "#D4EDDA", "#FFF3CD", "#F8D7DA", "#D4EDDA", "#FFF3CD", "#CCE5FF" }, RowColours(grid));
                    Select(grid, 1);
                    Assert.Equal("#FFE69C", RowColours(grid)[1]);
                    Select(grid, 5);
                    Assert.Equal("#9EC5FE", RowColours(grid)[5]);

                    // 5. The status filter, the text filter (any visible cell, the status too) and both together.
                    ComboBox status = UiTestHost.Find<ComboBox>(control, "statusFilter");
                    TextBox filter = UiTestHost.Find<TextBox>(control, "rowFilter");
                    status.SelectedItem = "Missing";
                    Assert.Equal(new[] { "Northwind" }, Column(grid, 2));
                    Assert.Equal("1 of 6 rows", rowCount.Text);
                    status.SelectedItem = "Different";
                    Assert.Equal(new[] { "Fabrikam", "Tailspin [UK] 50%" }, Column(grid, 2));
                    status.SelectedItem = "Extra";
                    Assert.Equal(new[] { "Litware" }, Column(grid, 2));
                    status.SelectedItem = "Matching";
                    Assert.Equal(new[] { "Contoso Ltd", "O'Neil's Bakery" }, Column(grid, 2));
                    status.SelectedItem = "All";
                    Assert.Equal(6, grid.Rows.Count);

                    SetFilter(control, filter, "o'neil");
                    Assert.Equal(new[] { "O'Neil's Bakery" }, Column(grid, 2));
                    SetFilter(control, filter, "[uk] 50%");   // wildcards and brackets match literally
                    Assert.Equal(new[] { "Tailspin [UK] 50%" }, Column(grid, 2));
                    SetFilter(control, filter, "a-4X");      // another column, case-insensitive
                    Assert.Equal(new[] { "Litware" }, Column(grid, 2));
                    SetFilter(control, filter, "missing");   // the status column is a visible cell too
                    Assert.Equal(new[] { "Northwind" }, Column(grid, 2));
                    SetFilter(control, filter, "a-");
                    status.SelectedItem = "Different";
                    Assert.Equal(new[] { "Fabrikam", "Tailspin [UK] 50%" }, Column(grid, 2));
                    Assert.Equal("2 of 6 rows", rowCount.Text);
                    SetFilter(control, filter, "nothing like this");
                    Assert.Empty(grid.Rows.Cast<DataGridViewRow>());
                    Assert.Equal("0 of 6 rows", rowCount.Text);
                    DataGridView detail = UiTestHost.Find<DataGridView>(control, "detailGrid");
                    Label caption = UiTestHost.Find<Label>(control, "detailCaption");
                    Assert.Empty(detail.Rows.Cast<DataGridViewRow>());   // no row selected: an empty detail pane
                    Assert.Equal(DataCompareControl.NoSelectionCaption, caption.Text);
                    status.SelectedItem = "All";
                    SetFilter(control, filter, string.Empty);
                    Assert.Equal(6, grid.Rows.Count);

                    // 6. The detail pane: the key first, then by display name; mismatches amber, columns not compared grey.
                    Select(grid, 1);   // Fabrikam: the account number differs
                    Guid fabrikam = scenario.Accounts[1].Id;
                    Assert.Equal($"Account {fabrikam:D} - Different", caption.Text);
                    List<DataGridViewRow> lines = detail.Rows.Cast<DataGridViewRow>().ToList();
                    Assert.Equal("accountid", lines[0].Cells[0].Value);
                    Assert.Equal("accountid - Primary key", lines[0].Cells[0].ToolTipText);
                    Assert.Equal(SystemColors.GrayText, lines[0].DefaultCellStyle.ForeColor);
                    Assert.Equal(new[] { "Account Name", "Account Number" }, lines.Skip(1).Take(2).Select(l => (string)l.Cells[0].Value));
                    DataGridViewRow number = lines.Single(l => (string)l.Cells[0].Value == "Account Number");
                    Assert.Equal(("A-2", "A-2x"), ((string)number.Cells[1].Value, (string)number.Cells[2].Value));
                    Assert.Equal(DataCompareControl.DifferentColor, number.DefaultCellStyle.BackColor);
                    Assert.Equal("accountnumber", number.Cells[0].ToolTipText);
                    DataGridViewRow name = lines.Single(l => (string)l.Cells[0].Value == "Account Name");
                    Assert.Equal(Color.Empty, name.DefaultCellStyle.BackColor);   // equal and compared: default colours
                    DataGridViewRow owner = lines.Single(l => (string)l.Cells[0].Value == "ownerid");
                    Assert.Equal(SystemColors.GrayText, owner.DefaultCellStyle.ForeColor);   // ignored
                    Assert.Equal("ownerid - Ignored", owner.Cells[0].ToolTipText);

                    // Differences only (saved at once): just the differing line.
                    CheckBox differencesOnly = UiTestHost.Find<CheckBox>(control, "differencesOnlyCheckBox");
                    differencesOnly.Checked = true;
                    Assert.True(scenario.LastSaved.DifferencesOnly);
                    Assert.Equal(new[] { "Account Number" }, detail.Rows.Cast<DataGridViewRow>().Select(l => (string)l.Cells[0].Value));
                    Select(grid, 3);   // O'Neil's Bakery: only the (ignored) owner differs - shown, greyed
                    Assert.Equal(new[] { "ownerid" }, detail.Rows.Cast<DataGridViewRow>().Select(l => (string)l.Cells[0].Value));
                    Assert.EndsWith(" - Matching", caption.Text);
                    Select(grid, 4);   // Tailspin: inactive in the secondary, so outside its view - found by id
                    Assert.Equal($"Account {scenario.Accounts[5].Id:D} - Different (found by id outside the secondary's view)", caption.Text);
                    Assert.Equal(new[] { "statecode", "statuscode" }, detail.Rows.Cast<DataGridViewRow>().Select(l => (string)l.Cells[0].Value));
                    differencesOnly.Checked = false;
                    Select(grid, 5);   // Litware, extra: nothing on the primary side
                    Assert.EndsWith(" - Extra", caption.Text);
                    Assert.All(detail.Rows.Cast<DataGridViewRow>(), l => Assert.Equal(string.Empty, l.Cells[1].Value));
                    Assert.Contains(detail.Rows.Cast<DataGridViewRow>(), l => (string)l.Cells[2].Value == "Litware");
                    Select(grid, 2);   // Northwind, missing: nothing on the secondary side
                    Assert.All(detail.Rows.Cast<DataGridViewRow>(), l => Assert.Equal(string.Empty, l.Cells[2].Value));
                    Assert.Empty(scenario.Dialogs.Messages);
                }
            });
        }

        [Fact]
        public void Editing_the_ignored_attributes_re_evaluates_the_result_without_reading_again()
        {
            var scenario = new UiScenario();
            UiTestHost.Run(() =>
            {
                using (DataCompareControl control = UiTest.NewControl(scenario.Settings, scenario.Save, scenario.Dialogs))
                {
                    ConnectAndCompare(control, scenario);
                    DataGridView grid = UiTestHost.Find<DataGridView>(control, "resultsGrid");
                    Select(grid, 3);   // O'Neil's Bakery: only the owner differs, which is ignored
                    Label caption = UiTestHost.Find<Label>(control, "detailCaption");
                    Assert.EndsWith(" - Matching", caption.Text);
                    int requests = scenario.Primary.Executed.Count + scenario.Secondary.Executed.Count;

                    // 1. Ignore only createdon: the owner now counts, and the row turns Different at once.
                    string shownList = null;
                    control.ShowCompareOptionsDialog = form => DriveDialog(form, box =>
                    {
                        shownList = box.Text;
                        box.Text = "CreatedOn";
                    }, "okButton");
                    UiTestHost.Find<Button>(control, "compareOptionsButton").PerformClick();

                    Assert.Equal(string.Join(Environment.NewLine, CompareOptions.DefaultIgnoredAttributes.OrderBy(n => n, StringComparer.Ordinal)), shownList);
                    Assert.Equal("createdon", control.Settings.IgnoredAttributes);
                    Assert.Equal("createdon", scenario.LastSaved.IgnoredAttributes);
                    Assert.Equal(new[] { "Matching", "Different", "Missing", "Different", "Different", "Extra" }, Column(grid, 0));
                    Assert.Equal(new[] { "Primary: 5", "Secondary: 4", "|", "Missing: 1", "Different: 3", "Extra: 1", "Matching: 1" }, VisibleSummary(control));
                    Assert.EndsWith(" - Different", caption.Text);   // the selected row's detail pane follows
                    DataGridViewRow owner = UiTestHost.Find<DataGridView>(control, "detailGrid").Rows.Cast<DataGridViewRow>()
                        .Single(l => (string)l.Cells[0].Value == "ownerid");
                    Assert.Equal(DataCompareControl.DifferentColor, owner.DefaultCellStyle.BackColor);
                    Assert.Equal(new[] { "createdon" }, control.Result.Options.IgnoredAttributes);
                    Assert.Equal(requests, scenario.Primary.Executed.Count + scenario.Secondary.Executed.Count);   // nothing read again
                    UiTestHost.Pump();
                    string log = UiTest.LogText(control);
                    Assert.Contains("] Ignored attributes: createdon.", log);
                    Assert.Contains("] Re-evaluated with the new compare options (nothing read again) - Result: 6 rows - matching 1, different 3, missing 1, extra 1.", log);
                    Assert.Equal("Done: 6 rows - missing 1, different 3, extra 1, matching 1", UiTestHost.Find<Label>(control, "progressLabel").Text);

                    // The status filter follows the new statuses.
                    UiTestHost.Find<ComboBox>(control, "statusFilter").SelectedItem = "Matching";
                    Assert.Equal(new[] { "Contoso Ltd" }, Column(grid, 2));
                    UiTestHost.Find<ComboBox>(control, "statusFilter").SelectedItem = "All";

                    // 2. Cancel changes nothing.
                    control.ShowCompareOptionsDialog = form => DriveDialog(form, box => box.Text = "", "cancelButton");
                    UiTestHost.Find<Button>(control, "compareOptionsButton").PerformClick();
                    Assert.Equal("createdon", control.Settings.IgnoredAttributes);
                    Assert.Equal("Different", Column(grid, 0)[3]);

                    // 3. An emptied list ignores nothing; Restore defaults brings the owner decision back.
                    control.ShowCompareOptionsDialog = form => DriveDialog(form, box => box.Text = "  ", "okButton");
                    UiTestHost.Find<Button>(control, "compareOptionsButton").PerformClick();
                    Assert.Equal(string.Empty, control.Settings.IgnoredAttributes);
                    Assert.Empty(control.Result.Options.IgnoredAttributes);
                    control.ShowCompareOptionsDialog = form => DriveDialog(form,
                        box => UiTestHost.Find<Button>(box.FindForm(), "restoreDefaultsButton").PerformClick(), "okButton");
                    UiTestHost.Find<Button>(control, "compareOptionsButton").PerformClick();
                    Assert.Equal(CompareOptions.FormatAttributeList(CompareOptions.DefaultIgnoredAttributes), control.Settings.IgnoredAttributes);
                    Assert.Equal(new[] { "Matching", "Different", "Missing", "Matching", "Different", "Extra" }, Column(grid, 0));
                    Assert.Equal(requests, scenario.Primary.Executed.Count + scenario.Secondary.Executed.Count);

                    // 4. The next comparison uses the saved list.
                    control.ShowCompareOptionsDialog = form => DriveDialog(form, box => box.Text = "createdon", "okButton");
                    UiTestHost.Find<Button>(control, "compareOptionsButton").PerformClick();
                    UiTestHost.Find<Button>(control, "compareButton").PerformClick();
                    UiTestHost.PumpUntil(() => !control.IsBusy && control.Result != null, "the second comparison");
                    Assert.Equal("Different", Column(grid, 0)[3]);
                    // createdon is not in this account metadata: nothing of the entity is ignored now.
                    Assert.Contains(" attributes compared, 0 ignored, ", UiTest.LogText(control).Split('\n').Last(l => l.Contains("records matched on")));
                    Assert.Empty(scenario.Dialogs.Messages);
                }
            });
        }

        [Fact]
        public void Without_a_result_the_ignored_attributes_are_only_saved()
        {
            var scenario = new UiScenario();
            UiTestHost.Run(() =>
            {
                using (DataCompareControl control = UiTest.NewControl(scenario.Settings, scenario.Save, scenario.Dialogs))
                {
                    var requests = new List<string>();
                    control.OnRequestConnection += (sender, e) => requests.Add(((RequestConnectionEventArgs)e).ActionName);
                    control.ShowCompareOptionsDialog = form => DriveDialog(form, box => box.Text = "name\ntelephone1", "okButton");

                    UiTestHost.Find<Button>(control, "compareOptionsButton").PerformClick();

                    Assert.Equal("name,telephone1", control.Settings.IgnoredAttributes);
                    Assert.Null(control.Result);
                    Assert.Empty(requests);   // needs no connection
                    UiTestHost.Pump();
                    Assert.Contains("] Ignored attributes: name, telephone1.", UiTest.LogText(control));
                }
            });
        }

        [Fact]
        public void While_comparing_the_inputs_are_disabled_and_Cancel_shows_the_partial_result()
        {
            var scenario = new UiScenario();
            using (var secondaryViewRequested = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            {
                scenario.Secondary.BeforeExecute = request =>
                {
                    if (!(request is RetrieveMultipleRequest multiple) || !(multiple.Query is FetchExpression)) return;
                    secondaryViewRequested.Set();
                    release.Wait(TimeSpan.FromSeconds(20));
                };
                UiTestHost.Run(() =>
                {
                    using (DataCompareControl control = UiTest.NewControl(scenario.Settings, scenario.Save, scenario.Dialogs))
                    {
                        ConnectBoth(control, scenario);
                        UiTestHost.Find<Button>(control, "compareButton").PerformClick();
                        UiTestHost.PumpUntil(() => secondaryViewRequested.IsSet, "the secondary view to be requested");
                        UiTestHost.Pump();

                        Assert.True(control.IsBusy);
                        Button cancel = UiTestHost.Find<Button>(control, "cancelButton");
                        Assert.True(cancel.Enabled);
                        foreach (string input in new[] { "entityList", "viewList", "compareButton", "compareOptionsButton", "entityMappingsButton" })
                            Assert.False(UiTestHost.Find<Control>(control, input).Enabled, input + " should be disabled while comparing");
                        Assert.False(UiTestHost.FindToolItem<ToolStripButton>(control, "refreshEntitiesButton").Enabled);
                        Assert.False(UiTestHost.FindToolItem<ToolStripButton>(control, "selectSecondaryButton").Enabled);
                        Assert.StartsWith("Secondary:", UiTestHost.Find<Label>(control, "progressLabel").Text);   // the engine's progress message
                        Assert.True(UiTestHost.Find<CheckBox>(control, "differencesOnlyCheckBox").Enabled);

                        cancel.PerformClick();
                        Assert.Equal("Cancelling...", UiTestHost.Find<Label>(control, "progressLabel").Text);
                        release.Set();
                        UiTestHost.PumpUntil(() => !control.IsBusy && control.Result != null, "the comparison to stop");

                        // The pairs both views returned were still compared; the others were not checked.
                        Assert.True(control.Result.Summary.Cancelled);
                        DataGridView grid = UiTestHost.Find<DataGridView>(control, "resultsGrid");
                        Assert.Equal(new[] { "Contoso Ltd", "Fabrikam", "O'Neil's Bakery" }, Column(grid, 2));
                        Assert.Equal(new[] { "Primary: 5", "Secondary: 4", "|", "Missing: 0", "Different: 1", "Extra: 0", "Matching: 2", "Not checked: 3 (cancelled)" },
                            VisibleSummary(control));
                        Assert.Equal("Cancelled (partial result): 3 rows - missing 0, different 1, extra 0, matching 2, not checked 3",
                            UiTestHost.Find<Label>(control, "progressLabel").Text);
                        Assert.Contains("Cancelled by user: partial result, 3 record(s) not checked.", UiTest.LogText(control));
                        Assert.False(cancel.Enabled);
                        foreach (string input in new[] { "entityList", "viewList", "compareButton", "compareOptionsButton", "entityMappingsButton" })
                            Assert.True(UiTestHost.Find<Control>(control, input).Enabled, input + " should be enabled again");
                        Assert.Empty(scenario.Dialogs.Messages);
                    }
                });
            }
        }

        [Fact]
        public void A_failing_secondary_view_is_flagged_in_the_summary_and_the_rows_are_looked_up_by_id()
        {
            var scenario = new UiScenario();
            scenario.Secondary.FailFetchPages.Add(1);
            UiTestHost.Run(() =>
            {
                using (DataCompareControl control = UiTest.NewControl(scenario.Settings, scenario.Save, scenario.Dialogs))
                {
                    ConnectAndCompare(control, scenario);

                    Label failed = UiTestHost.Find<Label>(control, "secondaryFailedLabel");
                    Assert.True(failed.Visible);
                    Assert.Equal("Secondary view failed", failed.Text);
                    Assert.Equal(Color.Firebrick, failed.ForeColor);
                    Assert.Contains("Simulated FetchXML failure", control.ToolTips.GetToolTip(failed));
                    Assert.False(UiTestHost.Find<Label>(control, "uncheckedCountLabel").Visible);
                    // Every primary row was looked up by id; the secondary-only record could not be found.
                    Assert.Equal(new[] { "Matching", "Different", "Missing", "Matching", "Different" }, Column(UiTestHost.Find<DataGridView>(control, "resultsGrid"), 0));
                    Assert.EndsWith(" - the secondary view failed", UiTestHost.Find<Label>(control, "progressLabel").Text);
                    Assert.Empty(scenario.Dialogs.Messages);
                }
            });
        }

        [Fact]
        public void A_failing_comparison_is_logged_and_shown_and_clears_the_previous_result()
        {
            var scenario = new UiScenario();
            UiTestHost.Run(() =>
            {
                using (DataCompareControl control = UiTest.NewControl(scenario.Settings, scenario.Save, scenario.Dialogs))
                {
                    ConnectAndCompare(control, scenario);
                    Assert.Equal(6, UiTestHost.Find<DataGridView>(control, "resultsGrid").Rows.Count);

                    // The secondary lacks the entity: its view fails (a warning), then the lookup by id fails (fatal).
                    scenario.Secondary.FailRetrieveMultiple["account"] = FakeOrganizationService.Fault(FakeOrganizationService.GenericFailure, "Simulated: no account table");
                    UiTestHost.Find<Button>(control, "compareButton").PerformClick();
                    UiTestHost.PumpUntil(() => !control.IsBusy && scenario.Dialogs.Messages.Count == 1, "the error");

                    Assert.Equal("Simulated: no account table", scenario.Dialogs.Messages[0]);
                    string log = UiTest.LogText(control);
                    Assert.Contains("] Compare failed: Simulated: no account table", log);
                    Assert.Contains("] Compare stopped: Simulated: no account table", log);
                    Assert.Null(control.Result);
                    Assert.Empty(UiTestHost.Find<DataGridView>(control, "resultsGrid").Rows.Cast<DataGridViewRow>());
                    Assert.True(UiTestHost.Find<Label>(control, "summaryEmptyLabel").Visible);
                    Assert.Equal("Compare failed", UiTestHost.Find<Label>(control, "progressLabel").Text);
                    Assert.True(UiTestHost.Find<Button>(control, "compareButton").Enabled);

                    // The next run works again.
                    scenario.Secondary.FailRetrieveMultiple.Clear();
                    UiTestHost.Find<Button>(control, "compareButton").PerformClick();
                    UiTestHost.PumpUntil(() => !control.IsBusy && control.Result != null, "the retry");
                    Assert.Equal(6, UiTestHost.Find<DataGridView>(control, "resultsGrid").Rows.Count);
                }
            });
        }

        [Fact]
        public void Another_view_or_entity_clears_the_result_and_a_view_with_a_linked_column_shows_its_display_name()
        {
            var scenario = new UiScenario();
            UiTestHost.Run(() =>
            {
                using (DataCompareControl control = UiTest.NewControl(scenario.Settings, scenario.Save, scenario.Dialogs))
                {
                    ConnectAndCompare(control, scenario);
                    DataGridView grid = UiTestHost.Find<DataGridView>(control, "resultsGrid");
                    ListBox views = UiTestHost.Find<ListBox>(control, "viewList");
                    Select(grid, 1);

                    // Another view: the result, the summary and the detail pane are cleared.
                    views.SelectedIndex = 1;
                    Assert.Null(control.Result);
                    Assert.Empty(grid.Rows.Cast<DataGridViewRow>());
                    Assert.Equal(new[] { "Status", "Id" }, grid.Columns.Cast<DataGridViewColumn>().Select(c => c.HeaderText));
                    Assert.True(UiTestHost.Find<Label>(control, "summaryEmptyLabel").Visible);
                    Assert.Empty(UiTestHost.Find<DataGridView>(control, "detailGrid").Rows.Cast<DataGridViewRow>());
                    Assert.Equal(string.Empty, UiTestHost.Find<Label>(control, "detailCaption").Text);

                    // Double-clicking a view compares it: the linked column's header is "Email (Primary Contact)".
                    MethodInfo doubleClick = typeof(ListBox).GetMethod("OnDoubleClick", BindingFlags.Instance | BindingFlags.NonPublic);
                    doubleClick.Invoke(views, new object[] { EventArgs.Empty });
                    UiTestHost.PumpUntil(() => !control.IsBusy && control.Result != null, "the linked view's comparison");
                    Assert.Equal(new[] { "Status", "Account Name", "Email (Primary Contact)" },
                        grid.Columns.Cast<DataGridViewColumn>().Where(c => c.Visible).Select(c => c.HeaderText));
                    Assert.Equal("pc.emailaddress1", grid.Columns.Cast<DataGridViewColumn>().Last().ToolTipText);
                    Assert.Equal("info@contoso.example", Column(grid, 3)[0]);
                    Assert.Contains("] Comparing view \"Active Accounts and Primary Contacts\" of account", UiTest.LogText(control));
                    Assert.Equal(new[] { "account", "contact" }, scenario.MetadataRequests);   // the linked entity's metadata for the header

                    // Another entity: cleared too, and remembered for this primary organisation.
                    ListView entities = UiTestHost.Find<ListView>(control, "entityList");
                    entities.Items["contact"].Selected = true;
                    UiTestHost.PumpUntil(() => !control.IsBusy && views.Text == "(All records)", "the contact views");
                    Assert.Null(control.Result);
                    Assert.Empty(grid.Rows.Cast<DataGridViewRow>());
                    Assert.Equal("contact", control.Settings.GetLastEntity(string.Empty));
                    Assert.Equal("contact", scenario.LastSaved.GetLastEntity(string.Empty));
                    Assert.Empty(scenario.Dialogs.Messages);
                }
            });
        }

        [Fact]
        public void Comparing_an_organisation_with_itself_asks_first_and_No_compares_nothing()
        {
            var scenario = new UiScenario();
            UiTestHost.Run(() =>
            {
                using (DataCompareControl control = UiTest.NewControl(scenario.Settings, scenario.Save, scenario.Dialogs))
                {
                    control.UpdateConnection(scenario.Primary, null, string.Empty, null);
                    UiTestHost.PumpUntil(() => !control.IsBusy && UiTestHost.Find<ListBox>(control, "viewList").Items.Count > 0, "the account views");

                    // The same connection picked as the secondary: only a log line, no dialog yet.
                    control.UpdateConnection(scenario.Primary, null, DataCompareControl.SecondaryActionName, DataCompareControl.SecondaryParameter);
                    UiTestHost.PumpUntil(() => UiTest.LogText(control).Contains("The secondary looks like the same organisation as the primary"), "the heads-up line");
                    Assert.Empty(scenario.Dialogs.Messages);
                    int fetches = scenario.Primary.Fetches.Count;

                    scenario.Dialogs.Answer = DialogResult.No;
                    UiTestHost.Find<Button>(control, "compareButton").PerformClick();
                    UiTestHost.Pump();

                    Assert.StartsWith("The secondary environment appears to be the SAME organisation as the primary", Assert.Single(scenario.Dialogs.Messages));
                    Assert.Contains("Compare not started: same organisation not confirmed.", UiTest.LogText(control));
                    Assert.DoesNotContain("] Comparing view", UiTest.LogText(control));
                    Assert.Equal(fetches, scenario.Primary.Fetches.Count);
                    Assert.False(control.IsBusy);

                    // Yes compares (every record matches itself).
                    scenario.Dialogs.Answer = DialogResult.Yes;
                    UiTestHost.Find<Button>(control, "compareButton").PerformClick();
                    UiTestHost.PumpUntil(() => !control.IsBusy && control.Result != null, "the comparison");
                    Assert.Equal(5, control.Result.Summary.Matching);
                }
            });
        }

        [Fact]
        public void A_failure_to_list_the_entities_is_shown_and_Refresh_entities_retries()
        {
            var scenario = new UiScenario { FailEntityList = true };
            UiTestHost.Run(() =>
            {
                using (DataCompareControl control = UiTest.NewControl(scenario.Settings, scenario.Save, scenario.Dialogs))
                {
                    control.UpdateConnection(scenario.Primary, null, string.Empty, null);
                    UiTestHost.PumpUntil(() => !control.IsBusy && scenario.Dialogs.Messages.Count == 1, "the error");

                    Assert.Equal("Simulated metadata failure", scenario.Dialogs.Messages[0]);
                    Assert.Contains("Loading entities failed: Simulated metadata failure", UiTest.LogText(control));
                    Assert.Equal(string.Empty, UiTestHost.Find<Label>(control, "progressLabel").Text);   // no stale "Loading entities..."
                    ListView entities = UiTestHost.Find<ListView>(control, "entityList");
                    Assert.Empty(entities.Items);

                    scenario.FailEntityList = false;
                    ToolStripButton refresh = UiTestHost.FindToolItem<ToolStripButton>(control, "refreshEntitiesButton");
                    Assert.True(refresh.Enabled);
                    refresh.PerformClick();
                    UiTestHost.PumpUntil(() => !control.IsBusy && entities.Items.Count == 3 && UiTestHost.Find<ListBox>(control, "viewList").Items.Count > 0,
                        "the entities after the retry");
                }
            });
        }

        [Fact]
        public void Without_a_primary_Refresh_entities_asks_XrmToolBox_for_one_and_the_callback_loads_the_entities_once()
        {
            var scenario = new UiScenario();
            UiTestHost.Run(() =>
            {
                using (DataCompareControl control = UiTest.NewControl(scenario.Settings, scenario.Save, scenario.Dialogs))
                {
                    var requests = new List<RequestConnectionEventArgs>();
                    control.OnRequestConnection += (sender, e) => requests.Add((RequestConnectionEventArgs)e);   // what XrmToolBox listens to

                    ToolStripButton refresh = UiTestHost.FindToolItem<ToolStripButton>(control, "refreshEntitiesButton");
                    Assert.True(refresh.Enabled);
                    refresh.PerformClick();

                    RequestConnectionEventArgs request = Assert.Single(requests);
                    Assert.Equal("RefreshEntities", request.ActionName);   // the method base.UpdateConnection invokes once connected
                    Assert.Same(control, request.Control);
                    Assert.Null(request.Parameter);
                    Assert.Null(control.Service);
                    Assert.Equal(0, scenario.EntityListLoads);
                    Assert.Empty(scenario.Dialogs.Messages);

                    // XrmToolBox, once the user has chosen the connection: the action runs once, against the new primary.
                    control.UpdateConnection(scenario.Primary, null, request.ActionName, request.Parameter);
                    ListBox views = UiTestHost.Find<ListBox>(control, "viewList");
                    UiTestHost.PumpUntil(() => !control.IsBusy && views.Items.Count > 0, "the entities and the account views");

                    Assert.Same(scenario.Primary, control.Service);
                    Assert.Equal(1, scenario.EntityListLoads);   // not a second time by the connection change
                    string log = UiTest.LogText(control);
                    Assert.Equal(1, Occurrences(log, "] Primary environment: (unnamed connection)"));
                    Assert.Equal(1, Occurrences(log, "] 3 entities loaded from the primary."));

                    // Connected, the button reloads at once without asking again.
                    refresh.PerformClick();
                    UiTestHost.PumpUntil(() => !control.IsBusy && views.Items.Count > 0 && scenario.EntityListLoads == 2, "the reload");
                    Assert.Single(requests);
                    Assert.Equal(1, Occurrences(UiTest.LogText(control), "] Primary environment: "));
                }
            });
        }

        [Fact]
        public void With_a_secondary_but_no_primary_Compare_asks_for_the_primary_and_the_secondary_never_becomes_the_primary()
        {
            var scenario = new UiScenario();
            UiTestHost.Run(() =>
            {
                using (DataCompareControl control = UiTest.NewControl(scenario.Settings, scenario.Save, scenario.Dialogs))
                {
                    var requests = new List<RequestConnectionEventArgs>();
                    control.OnRequestConnection += (sender, e) => requests.Add((RequestConnectionEventArgs)e);
                    ToolStripLabel primaryLabel = UiTestHost.FindToolItem<ToolStripLabel>(control, "primaryLabel");
                    ToolStripLabel secondaryLabel = UiTestHost.FindToolItem<ToolStripLabel>(control, "secondaryLabel");

                    // The secondary first: its own request - an additional organisation, which XrmToolBox never
                    // makes the tab's connection - answered without touching Service.
                    UiTestHost.FindToolItem<ToolStripButton>(control, "selectSecondaryButton").PerformClick();
                    RequestConnectionEventArgs secondary = Assert.Single(requests);
                    Assert.Equal("AdditionalOrganization", secondary.ActionName);
                    Assert.Equal("secondary", secondary.Parameter);
                    Assert.Same(control, secondary.Control);
                    var secondaryDetail = new ConnectionDetail { ConnectionName = "Target org" };
                    control.UpdateConnection(scenario.Secondary, secondaryDetail, secondary.ActionName, secondary.Parameter);
                    UiTestHost.Pump();
                    Assert.Null(control.Service);
                    Assert.Null(control.ConnectionDetail);
                    Assert.Equal("Primary: (none)", primaryLabel.Text);
                    Assert.Equal("Secondary: Target org", secondaryLabel.Text);
                    Assert.Contains("] Secondary environment: Target org", UiTest.LogText(control));
                    Assert.Equal(0, scenario.EntityListLoads);

                    // Compare is disabled with nothing to compare; forced, its handler asks for the primary instead of failing.
                    Button compare = UiTestHost.Find<Button>(control, "compareButton");
                    Assert.False(compare.Enabled);
                    compare.Enabled = true;
                    compare.PerformClick();
                    UiTestHost.Pump();

                    Assert.Equal(2, requests.Count);
                    Assert.Equal("CompareSelectedView", requests[1].ActionName);
                    Assert.Same(control, requests[1].Control);
                    Assert.Empty(scenario.Dialogs.Messages);
                    Assert.False(control.IsBusy);

                    // The callback: the comparison runs once against the new primary - with no entity yet it says
                    // what is missing - and the primary's entities load once; the secondary is kept.
                    var primaryDetail = new ConnectionDetail { ConnectionName = "Source org" };
                    control.UpdateConnection(scenario.Primary, primaryDetail, requests[1].ActionName, requests[1].Parameter);
                    UiTestHost.PumpUntil(() => !control.IsBusy && UiTestHost.Find<ListBox>(control, "viewList").Items.Count > 0, "the entities and the account views");

                    Assert.Same(scenario.Primary, control.Service);
                    Assert.Same(primaryDetail, control.ConnectionDetail);
                    Assert.Equal(new[] { "Select an entity first." }, scenario.Dialogs.Messages);
                    Assert.Equal(1, scenario.EntityListLoads);
                    Assert.Equal("Primary: Source org", primaryLabel.Text);
                    Assert.Equal("Secondary: Target org", secondaryLabel.Text);
                    Assert.DoesNotContain("] Comparing view", UiTest.LogText(control));

                    // Connected, a new secondary still leaves Service alone and resets nothing.
                    var other = new FakeOrganizationService();
                    control.UpdateConnection(other, null, DataCompareControl.SecondaryActionName, DataCompareControl.SecondaryParameter);
                    Assert.Same(scenario.Primary, control.Service);
                    Assert.Equal(1, scenario.EntityListLoads);
                    Assert.Equal("Active Accounts", UiTestHost.Find<ListBox>(control, "viewList").Text);
                    Assert.Equal(2, requests.Count);
                }
            });
        }

        [Fact]
        public void Without_a_connection_the_controls_work_and_those_that_need_the_primary_ask_for_it()
        {
            var scenario = new UiScenario();
            UiTestHost.Run(() =>
            {
                using (DataCompareControl control = UiTest.NewControl(scenario.Settings, scenario.Save, scenario.Dialogs))
                {
                    var requests = new List<string>();
                    control.OnRequestConnection += (sender, e) => requests.Add(((RequestConnectionEventArgs)e).ActionName);

                    // Controls that need no connection: no request, no error.
                    UiTestHost.Find<TextBox>(control, "entityFilter").Text = "acc";
                    UiTestHost.Find<TextBox>(control, "rowFilter").Text = "contoso";
                    control.ApplyRowFilter();
                    UiTestHost.Find<ComboBox>(control, "statusFilter").SelectedItem = "Missing";
                    CheckBox differencesOnly = UiTestHost.Find<CheckBox>(control, "differencesOnlyCheckBox");
                    differencesOnly.Checked = !differencesOnly.Checked;
                    differencesOnly.Checked = !differencesOnly.Checked;
                    UiTestHost.Find<Button>(control, "clearLogButton").PerformClick();
                    UiTestHost.Pump();
                    Assert.Empty(requests);

                    // Controls that need the primary: Refresh entities is enabled without one; Compare is disabled
                    // until there is something to compare, so it is forced on to show that it asks for it too.
                    UiTestHost.FindToolItem<ToolStripButton>(control, "refreshEntitiesButton").PerformClick();
                    Button compare = UiTestHost.Find<Button>(control, "compareButton");
                    Assert.False(compare.Enabled);
                    compare.Enabled = true;
                    compare.PerformClick();
                    UiTestHost.Pump();

                    Assert.Equal(new[] { "RefreshEntities", "CompareSelectedView" }, requests);
                    foreach (string action in requests.Concat(new[] { "LoadSelectedEntityViews" }))
                    {
                        // What base.UpdateConnection looks up once connected: a unique instance method without parameters.
                        MethodInfo method = typeof(DataCompareControl).GetMethod(action, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        Assert.NotNull(method);
                        Assert.Empty(method.GetParameters());
                    }
                    Assert.Null(control.Service);
                    Assert.False(control.IsBusy);
                    Assert.Null(control.Result);
                    Assert.Empty(scenario.Dialogs.Messages);
                    Assert.Equal(0, scenario.EntityListLoads);
                }
            });
        }

        // ---- helpers ----

        internal static void ConnectBoth(DataCompareControl control, UiScenario scenario)
        {
            control.UpdateConnection(scenario.Primary, null, string.Empty, null);
            UiTestHost.PumpUntil(() => !control.IsBusy && UiTestHost.Find<ListBox>(control, "viewList").Items.Count > 0, "the account views");
            control.UpdateConnection(scenario.Secondary, null, DataCompareControl.SecondaryActionName, DataCompareControl.SecondaryParameter);
        }

        internal static void ConnectAndCompare(DataCompareControl control, UiScenario scenario)
        {
            ConnectBoth(control, scenario);
            UiTestHost.Find<Button>(control, "compareButton").PerformClick();
            UiTestHost.PumpUntil(() => !control.IsBusy && control.Result != null, "the comparison");
        }

        /// <summary>The text of one column of the rows the grid shows, in display order.</summary>
        internal static List<string> Column(DataGridView grid, int index) =>
            grid.Rows.Cast<DataGridViewRow>().Select(r => r.Cells[index].Value as string).ToList();

        /// <summary>The texts of the summary strip's visible labels, in order.</summary>
        internal static List<string> VisibleSummary(DataCompareControl control) =>
            UiTestHost.Find<FlowLayoutPanel>(control, "summaryRow").Controls.Cast<Control>().Where(c => c.Visible).Select(c => c.Text).ToList();

        /// <summary>Selects a row of the grid as a click would (current cell, full row).</summary>
        internal static void Select(DataGridView grid, int row)
        {
            grid.CurrentCell = grid.Rows[row].Cells[0];
            grid.Rows[row].Selected = true;
            UiTestHost.Pump(10);
        }

        /// <summary>The colour each displayed row is painted with, sampled in its Status cell away from the text ("#RRGGBB").</summary>
        internal static List<string> RowColours(DataGridView grid)
        {
            grid.PerformLayout();
            var colours = new List<string>();
            using (var bitmap = new Bitmap(grid.Width, grid.Height))
            {
                grid.DrawToBitmap(bitmap, new Rectangle(Point.Empty, grid.Size));
                for (int row = 0; row < grid.Rows.Count; row++)
                {
                    Rectangle cell = grid.GetCellDisplayRectangle(0, row, cutOverflow: true);
                    if (cell.IsEmpty) break;
                    Color colour = bitmap.GetPixel(cell.Right - 6, cell.Top + cell.Height / 2);
                    colours.Add($"#{colour.R:X2}{colour.G:X2}{colour.B:X2}");
                }
            }
            return colours;
        }

        private static void SetFilter(DataCompareControl control, TextBox filter, string text)
        {
            filter.Text = text;
            control.ApplyRowFilter();   // what the typing-pause timer does
        }

        /// <summary>Shows the Compare options dialog modeless off-screen, edits its ignored list and presses a button.</summary>
        internal static DialogResult DriveDialog(CompareOptionsForm form, Action<TextBox> edit, string button)
        {
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-3000, -3000);
            form.Show();
            edit(UiTestHost.Find<TextBox>(form, "ignoredAttributesBox"));
            UiTestHost.Find<Button>(form, button).PerformClick();
            return form.DialogResult;
        }

        private static int Occurrences(string text, string value)
        {
            int count = 0;
            for (int at = text.IndexOf(value, StringComparison.Ordinal); at >= 0; at = text.IndexOf(value, at + value.Length, StringComparison.Ordinal)) count++;
            return count;
        }
    }

    /// <summary>
    /// Fake primary and secondary for the UI tests. The primary lists three entities (Account, Contact,
    /// User) and answers RetrieveEntity for account and contact (recording each request and its thread);
    /// its store holds two system views of account ("Active Accounts": name + accountnumber, active only;
    /// "Active Accounts and Primary Contacts": name + pc.emailaddress1 through a link-entity) and one
    /// personal view ("Accounts I Follow"), and the accounts. Both environments hold the same contacts.
    /// The accounts, in the primary view's order (by name): Contoso Ltd (identical: Matching), Fabrikam
    /// (account number differs: Different), Northwind (primary only: Missing), O'Neil's Bakery (only the
    /// ignored owner differs: Matching), Tailspin [UK] 50% (inactive in the secondary, so outside its view:
    /// found by id, Different); then Litware (secondary only: Extra).
    /// </summary>
    internal sealed class UiScenario
    {
        private static readonly Guid ActiveAccountsViewId = new Guid("5a9e1b1c-0000-0000-0000-000000000001");
        private static readonly Guid PrimaryContactsViewId = new Guid("5a9e1b1c-0000-0000-0000-000000000002");
        private static readonly Guid FollowedAccountsViewId = new Guid("5a9e1b1c-0000-0000-0000-000000000003");

        public UiScenario()
        {
            Entity contact = TestData.Record("contact", TestData.Id(100), ("fullname", "Ann Contoso"), ("emailaddress1", "info@contoso.example"));
            Primary.Add(contact);
            Secondary.Add(contact);
            EntityReference owner1 = TestData.Ref("systemuser", TestData.Id(201)), owner2 = TestData.Ref("systemuser", TestData.Id(202));

            Entity contoso = TestData.Account(TestData.Id(1), "Contoso Ltd", ("accountnumber", "A-1"), ("primarycontactid", TestData.Ref("contact", contact.Id)));
            Entity fabrikam = TestData.Account(TestData.Id(2), "Fabrikam", ("accountnumber", "A-2"));
            Entity northwind = TestData.Account(TestData.Id(3), "Northwind", ("accountnumber", "A-3"));
            Entity litware = TestData.Account(TestData.Id(4), "Litware", ("accountnumber", "A-4x"));
            Entity bakery = TestData.Account(TestData.Id(5), "O'Neil's Bakery", ("accountnumber", "A-5"), ("ownerid", owner1));
            Entity tailspin = TestData.Account(TestData.Id(6), "Tailspin [UK] 50%", ("accountnumber", "A-6"));
            Accounts.AddRange(new[] { contoso, fabrikam, northwind, litware, bakery, tailspin });

            Primary.AddRange(new[] { contoso, fabrikam, northwind, bakery, tailspin });
            Secondary.AddRange(new[]
            {
                contoso,
                fabrikam.Changed(("accountnumber", "A-2x")),
                litware,
                bakery.Changed(("ownerid", owner2)),
                tailspin.Changed(("statecode", TestData.Opt(1)), ("statuscode", TestData.Opt(2)))
            });

            Primary.Add(View("savedquery", ActiveAccountsViewId, "Active Accounts",
                "<fetch><entity name=\"account\"><attribute name=\"name\" /><attribute name=\"accountnumber\" /><order attribute=\"name\" />" +
                "<filter><condition attribute=\"statecode\" operator=\"eq\" value=\"0\" /></filter></entity></fetch>",
                "<grid name=\"resultset\" jump=\"name\" select=\"1\" icon=\"1\" preview=\"1\"><row name=\"result\" id=\"accountid\">" +
                "<cell name=\"name\" width=\"300\" /><cell name=\"accountnumber\" width=\"120\" /></row></grid>"));
            Primary.Add(View("savedquery", PrimaryContactsViewId, "Active Accounts and Primary Contacts",
                "<fetch><entity name=\"account\"><attribute name=\"name\" /><order attribute=\"name\" />" +
                "<filter><condition attribute=\"statecode\" operator=\"eq\" value=\"0\" /></filter>" +
                "<link-entity name=\"contact\" from=\"contactid\" to=\"primarycontactid\" link-type=\"outer\" alias=\"pc\">" +
                "<attribute name=\"emailaddress1\" /></link-entity></entity></fetch>",
                "<grid name=\"resultset\" jump=\"name\" select=\"1\" icon=\"1\" preview=\"1\"><row name=\"result\" id=\"accountid\">" +
                "<cell name=\"name\" width=\"300\" /><cell name=\"pc.emailaddress1\" width=\"200\" /></row></grid>"));
            // A personal view whose name sorts before the system views, yet is listed after them.
            Primary.Add(View("userquery", FollowedAccountsViewId, "Accounts I Follow",
                "<fetch><entity name=\"account\"><attribute name=\"name\" /><order attribute=\"name\" /></entity></fetch>",
                "<grid name=\"resultset\" jump=\"name\" select=\"1\" icon=\"1\" preview=\"1\"><row name=\"result\" id=\"accountid\">" +
                "<cell name=\"name\" width=\"300\" /></row></grid>"));

            Primary.ExecuteHandler = OnPrimaryRequest;
        }

        public FakeOrganizationService Primary { get; } = new FakeOrganizationService();
        public FakeOrganizationService Secondary { get; } = new FakeOrganizationService();

        /// <summary>The six accounts: Contoso Ltd, Fabrikam, Northwind, Litware, O'Neil's Bakery, Tailspin [UK] 50% (ids 1..6).</summary>
        public List<Entity> Accounts { get; } = new List<Entity>();

        public DataCompareSettings Settings { get; } = new DataCompareSettings
        {
            LastEntities = { new LastEntity { Organization = string.Empty, Entity = "account" } }
        };

        public DialogRecorder Dialogs { get; } = new DialogRecorder();

        /// <summary>RetrieveAllEntities fails while this is set.</summary>
        public bool FailEntityList { get; set; }

        /// <summary>How many times the primary was asked for its entity list (RetrieveAllEntities; counted on the worker thread).</summary>
        public int EntityListLoads => Volatile.Read(ref _entityListLoads);

        private int _entityListLoads;

        /// <summary>Entities whose metadata the primary was asked for (RetrieveEntity), in order.</summary>
        public List<string> MetadataRequests { get; } = new List<string>();

        /// <summary>The name of the thread each of those requests ran on (null for a thread-pool thread).</summary>
        public List<string> MetadataThreads { get; } = new List<string>();

        /// <summary>A copy of the settings as last saved.</summary>
        public DataCompareSettings LastSaved { get; private set; } = new DataCompareSettings();

        /// <summary>How many times the settings were saved.</summary>
        public int SaveCount { get; private set; }

        public void Save(DataCompareSettings settings)
        {
            var copy = new DataCompareSettings
            {
                IgnoredAttributes = settings.IgnoredAttributes,
                ComparedPrefixes = settings.ComparedPrefixes,
                DifferencesOnly = settings.DifferencesOnly,
                PageSize = settings.PageSize
            };
            copy.LastEntities.AddRange(settings.LastEntities.Select(e => new LastEntity { Organization = e.Organization, Entity = e.Entity }));
            copy.EntityMappings.AddRange(settings.EntityMappings.Select(m => m.Clone()));
            SaveCount++;
            LastSaved = copy;
        }

        private OrganizationResponse OnPrimaryRequest(OrganizationRequest request)
        {
            switch (request)
            {
                case RetrieveAllEntitiesRequest _:
                    Interlocked.Increment(ref _entityListLoads);
                    if (FailEntityList) throw FakeOrganizationService.Fault(FakeOrganizationService.GenericFailure, "Simulated metadata failure");
                    var response = new RetrieveAllEntitiesResponse();
                    response.Results["EntityMetadata"] = new[]
                    {
                        Meta("systemuser", "SystemUser", "User", "systemuserid", "fullname"),
                        Meta("contact", "Contact", "Contact", "contactid", "fullname"),
                        Meta("account", "Account", "Account", "accountid", "name")
                    };
                    return response;
                case RetrieveEntityRequest retrieve:
                    lock (MetadataRequests)
                    {
                        MetadataRequests.Add(retrieve.LogicalName);
                        MetadataThreads.Add(Thread.CurrentThread.Name);
                    }
                    if (retrieve.LogicalName == "account") return Metadata(DataverseSchemaProviderTests.AccountMetadata());
                    if (retrieve.LogicalName == "contact") return Metadata(ContactMetadata());
                    throw FakeOrganizationService.Fault(FakeOrganizationService.ObjectDoesNotExist, "Could not find an entity with specified entity name: " + retrieve.LogicalName);
                default:
                    return null;
            }
        }

        private static Entity View(string entity, Guid id, string name, string fetchXml, string layoutXml) =>
            TestData.Record(entity, id, ("name", name), ("returnedtypecode", "account"), ("querytype", 0), ("statecode", TestData.Opt(0)),
                ("fetchxml", fetchXml), ("layoutxml", layoutXml));

        private static EntityMetadata ContactMetadata() =>
            new EntityMetadata { LogicalName = "contact", SchemaName = "Contact", DisplayName = new Microsoft.Xrm.Sdk.Label("Contact", 1033) }
                .With("PrimaryIdAttribute", "contactid")
                .With("PrimaryNameAttribute", "fullname")
                .With("IsIntersect", false)
                .With("Attributes", new AttributeMetadata[]
                {
                    new UniqueIdentifierAttributeMetadata { LogicalName = "contactid" },
                    new StringAttributeMetadata { LogicalName = "fullname", DisplayName = new Microsoft.Xrm.Sdk.Label("Full Name", 1033) },
                    new StringAttributeMetadata { LogicalName = "emailaddress1", DisplayName = new Microsoft.Xrm.Sdk.Label("Email", 1033) }
                });

        private static OrganizationResponse Metadata(EntityMetadata metadata)
        {
            var response = new RetrieveEntityResponse();
            response.Results["EntityMetadata"] = metadata;
            return response;
        }

        private static EntityMetadata Meta(string logicalName, string schemaName, string label, string primaryId, string primaryName) =>
            new EntityMetadata { LogicalName = logicalName, SchemaName = schemaName, DisplayName = new Microsoft.Xrm.Sdk.Label(label, 1033) }
                .With("IsIntersect", false)
                .With("IsPrivate", false)
                .With("PrimaryIdAttribute", primaryId)
                .With("PrimaryNameAttribute", primaryName);
    }
}
