using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using System.Xml.Serialization;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using MyscotekDataCompare.Core;
using MyscotekDataCompare.Core.Schema;
using MyscotekDataCompare.Core.Services;
using MyscotekDataCompare.Tests.Fakes;
using MyscotekDataCompare.UI;
using Xunit;
using Label = System.Windows.Forms.Label;

namespace MyscotekDataCompare.Tests
{
    /// <summary>
    /// The entity mappings and the prefix filter through the real control (SPEC 6.5, 6.6): the Entity mappings
    /// dialog (add, remove, column pairs, validation, cancel, persisted), the mapping indicator, a mapped
    /// comparison end to end, and the prefix filter of the Compare options dialog (re-evaluated at once).
    /// </summary>
    [Collection(UiTestCollection.Name)]
    public class UiMappingTests
    {
        private static readonly string Arrow = DataCompareControl.MappingArrow;

        [Fact]
        public void A_mapped_entity_is_compared_with_the_secondary_table_end_to_end()
        {
            var mapped = new MappedScenario(withMapping: true);
            UiScenario scenario = mapped.Scenario;
            UiTestHost.Run(() =>
            {
                using (DataCompareControl control = UiTest.NewControl(scenario.Settings, scenario.Save, scenario.Dialogs))
                {
                    UiFlowTests.ConnectBoth(control, scenario);
                    Label mapping = UiTestHost.Find<Label>(control, "mappingLabel");
                    Assert.True(mapping.Visible);   // the selected entity is mapped: shown before any comparison
                    Assert.Equal(Arrow + " new_account", mapping.Text);
                    Assert.Contains("compared with new_account", control.ToolTips.GetToolTip(mapping));

                    UiTestHost.Find<Button>(control, "compareButton").PerformClick();
                    UiTestHost.PumpUntil(() => !control.IsBusy && control.Result != null, "the mapped comparison");

                    DataGridView grid = UiTestHost.Find<DataGridView>(control, "resultsGrid");
                    Assert.Equal(new[] { "Matching", "Different", "Missing", "Matching", "Different", "Extra" }, UiFlowTests.Column(grid, 0));
                    Assert.Equal(new[] { "A-1", "A-2", "A-3", "A-5", "A-6", "A-4x" }, UiFlowTests.Column(grid, 3));   // the extra row: new_accountnumber
                    Assert.Equal(new[] { Arrow + " new_account", "Primary: 5", "Secondary: 4", "|", "Missing: 1", "Different: 2", "Extra: 1", "Matching: 2" },
                        UiFlowTests.VisibleSummary(control));
                    Assert.Equal("new_account", control.Result.SecondaryEntityLogicalName);
                    Assert.Equal(new[] { "new_account" }, FakeFetchXmlNames(scenario.Secondary));
                    Assert.Equal(1, mapped.SecondaryMetadataRequests);

                    // The detail pane names the secondary columns that differ from the primary's.
                    UiFlowTests.Select(grid, 1);
                    List<DataGridViewRow> lines = UiTestHost.Find<DataGridView>(control, "detailGrid").Rows.Cast<DataGridViewRow>().ToList();
                    Assert.Equal("accountid " + Arrow + " new_accountid", lines[0].Cells[0].Value);
                    Assert.Equal("accountid " + Arrow + " new_accountid - Primary key", lines[0].Cells[0].ToolTipText);
                    DataGridViewRow number = lines.Single(l => ((string)l.Cells[0].Value).StartsWith("Account Number", StringComparison.Ordinal));
                    Assert.Equal(("Account Number " + Arrow + " new_accountnumber", "A-2", "A-2x"),
                        ((string)number.Cells[0].Value, (string)number.Cells[1].Value, (string)number.Cells[2].Value));
                    Assert.Equal(DataCompareControl.DifferentColor, number.DefaultCellStyle.BackColor);
                    DataGridViewRow tags = lines.Single(l => (string)l.Cells[0].Value == "new_tags");
                    Assert.Equal("new_tags - " + DetailBuilder.NoCounterpartNote, tags.Cells[0].ToolTipText);
                    Assert.Equal(SystemColors.GrayText, tags.DefaultCellStyle.ForeColor);

                    UiTestHost.Pump();
                    string log = UiTest.LogText(control);
                    Assert.Contains("(secondary), mapped to new_account in the secondary", log);
                    Assert.Contains("] Entity mapping: account is compared with new_account in the secondary, matched on accountid = new_accountid; " +
                                    "columns matched by name except accountnumber -> new_accountnumber.", log);
                    Assert.Empty(scenario.Dialogs.Messages);
                    Assert.Empty(scenario.Secondary.Writes);
                }
            });
        }

        [Fact]
        public void The_mappings_dialog_adds_a_mapping_with_a_column_pair_and_saves_it()
        {
            var mapped = new MappedScenario(withMapping: false);
            UiScenario scenario = mapped.Scenario;
            UiTestHost.Run(() =>
            {
                using (DataCompareControl control = UiTest.NewControl(scenario.Settings, scenario.Save, scenario.Dialogs))
                {
                    UiFlowTests.ConnectBoth(control, scenario);
                    Assert.False(UiTestHost.Find<Label>(control, "mappingLabel").Visible);
                    List<string> secondaryChoices = null, primaryChoices = null, columnChoices = null;
                    bool columnsShown = false;

                    control.ShowEntityMappingsDialog = form =>
                    {
                        ShowOffScreen(form);
                        ListView list = UiTestHost.Find<ListView>(form, "mappingList");
                        Assert.Empty(list.Items);
                        Assert.False(UiTestHost.Find<Button>(form, "removeButton").Enabled);
                        primaryChoices = UiTestHost.Find<ComboBox>(form, "primaryEntityBox").Items.Cast<object>().Select(o => o.ToString()).ToList();
                        secondaryChoices = UiTestHost.Find<ComboBox>(form, "secondaryEntityBox").Items.Cast<object>().Select(o => o.ToString()).ToList();

                        UiTestHost.Find<Button>(form, "addButton").PerformClick();
                        Assert.Equal("Account (account)", UiTestHost.Find<ComboBox>(form, "primaryEntityBox").Text);   // the current entity
                        UiTestHost.Find<ComboBox>(form, "secondaryEntityBox").Text = "Migrated Account (new_account)";
                        Assert.Equal(new[] { "Account (account)", "Migrated Account (new_account)", "Matched by name" },
                            list.Items[0].SubItems.Cast<ListViewItem.ListViewSubItem>().Select(s => s.Text));

                        form.ShowColumnsDialog = columns =>
                        {
                            ShowOffScreen(columns);
                            columnsShown = true;
                            columnChoices = UiTestHost.Find<ComboBox>(columns, "secondaryColumnBox").Items.Cast<object>().Select(o => o.ToString()).ToList();
                            UiTestHost.Find<ComboBox>(columns, "primaryColumnBox").Text = "Account Number (accountnumber)";
                            UiTestHost.Find<ComboBox>(columns, "secondaryColumnBox").Text = "new_accountnumber";
                            UiTestHost.Find<Button>(columns, "addColumnButton").PerformClick();
                            UiTestHost.Find<Button>(columns, "okButton").PerformClick();
                            return columns.DialogResult;
                        };
                        UiTestHost.Find<Button>(form, "columnsButton").PerformClick();
                        UiTestHost.PumpUntil(() => columnsShown && list.Items[0].SubItems[2].Text == "1 column pair", "the column pairs");

                        UiTestHost.Find<Button>(form, "okButton").PerformClick();
                        return form.DialogResult;
                    };
                    UiTestHost.Find<Button>(control, "entityMappingsButton").PerformClick();
                    UiTestHost.PumpUntil(() => !control.IsBusy && (control.Settings.EntityMappings.Count == 1 || scenario.Dialogs.Messages.Count > 0), "the saved mapping");
                    Assert.Empty(scenario.Dialogs.Messages);

                    Assert.Equal(new[] { "Account (account)", "Contact (contact)", "User (systemuser)" }, primaryChoices);
                    Assert.Equal(new[] { "Contact (contact)", "Migrated Account (new_account)" }, secondaryChoices);   // listed from the secondary
                    Assert.Contains("Number (new_accountnumber)", columnChoices);                                    // the secondary's columns
                    EntityMapping saved = Assert.Single(scenario.LastSaved.EntityMappings);
                    Assert.Equal(("account", "new_account"), (saved.PrimaryEntity, saved.SecondaryEntity));
                    Assert.Equal(new[] { ("accountnumber", "new_accountnumber") }, saved.Columns.Select(c => (c.PrimaryColumn, c.SecondaryColumn)));
                    Label mapping = UiTestHost.Find<Label>(control, "mappingLabel");
                    Assert.True(mapping.Visible);
                    Assert.Equal(Arrow + " new_account", mapping.Text);
                    UiTestHost.Pump();
                    Assert.Contains("] Entity mappings: account -> new_account (accountnumber -> new_accountnumber).", UiTest.LogText(control));

                    // The next Compare applies it.
                    UiTestHost.Find<Button>(control, "compareButton").PerformClick();
                    UiTestHost.PumpUntil(() => !control.IsBusy && control.Result != null, "the comparison");
                    Assert.Equal("new_account", control.Result.SecondaryEntityLogicalName);
                    Assert.Equal("A-4x", UiFlowTests.Column(UiTestHost.Find<DataGridView>(control, "resultsGrid"), 3).Last());
                    Assert.Empty(scenario.Dialogs.Messages);
                }
            });
        }

        [Fact]
        public void Cancel_discards_the_edits_and_Remove_deletes_a_mapping_once_confirmed()
        {
            var mapped = new MappedScenario(withMapping: true);
            UiScenario scenario = mapped.Scenario;
            UiTestHost.Run(() =>
            {
                using (DataCompareControl control = UiTest.NewControl(scenario.Settings, scenario.Save, scenario.Dialogs))
                {
                    UiFlowTests.ConnectBoth(control, scenario);
                    int saves = scenario.SaveCount;

                    control.ShowEntityMappingsDialog = form =>
                    {
                        ShowOffScreen(form);
                        Assert.Equal("account", form.SelectedMapping.PrimaryEntity);   // the first mapping is selected
                        UiTestHost.Find<ComboBox>(form, "secondaryEntityBox").Text = "something_else";
                        UiTestHost.Find<Button>(form, "addButton").PerformClick();
                        UiTestHost.Find<Button>(form, "cancelButton").PerformClick();
                        return form.DialogResult;
                    };
                    UiTestHost.Find<Button>(control, "entityMappingsButton").PerformClick();
                    UiTestHost.PumpUntil(() => !control.IsBusy, "the dialog");
                    Assert.Equal("new_account", Assert.Single(control.Settings.EntityMappings).SecondaryEntity);
                    Assert.Equal(saves, scenario.SaveCount);

                    control.ShowEntityMappingsDialog = form =>
                    {
                        ShowOffScreen(form);
                        UiTestHost.Find<Button>(form, "removeButton").PerformClick();
                        Assert.Empty(UiTestHost.Find<ListView>(form, "mappingList").Items);
                        Assert.False(UiTestHost.Find<TableLayoutPanel>(form, "editor").Enabled);
                        UiTestHost.Find<Button>(form, "okButton").PerformClick();
                        return form.DialogResult;
                    };
                    UiTestHost.Find<Button>(control, "entityMappingsButton").PerformClick();
                    UiTestHost.PumpUntil(() => !control.IsBusy && control.Settings.EntityMappings.Count == 0, "the removal");
                    Assert.Empty(scenario.LastSaved.EntityMappings);
                    Assert.False(UiTestHost.Find<Label>(control, "mappingLabel").Visible);
                    UiTestHost.Pump();
                    Assert.Contains("] Entity mappings: none - every entity is compared with the same entity of the secondary.", UiTest.LogText(control));
                    Assert.Equal(1, mapped.SecondaryEntityListLoads);   // listed once for this secondary connection
                }
            });
        }

        [Fact]
        public void The_mappings_dialog_refuses_an_incomplete_invalid_or_duplicate_mapping()
        {
            UiTestHost.Run(() =>
            {
                var entities = new[] { new EntityInfo { LogicalName = "account", DisplayName = "Account" } };
                using (var form = new EntityMappingsForm(new[] { new EntityMapping("contact", "new_contact") }, entities, null, null, null, "account"))
                {
                    ShowOffScreen(form);
                    Label status = UiTestHost.Find<Label>(form, "statusLabel");
                    Button ok = UiTestHost.Find<Button>(form, "okButton");
                    ComboBox primary = UiTestHost.Find<ComboBox>(form, "primaryEntityBox");
                    ComboBox secondary = UiTestHost.Find<ComboBox>(form, "secondaryEntityBox");

                    UiTestHost.Find<Button>(form, "addButton").PerformClick();
                    ok.PerformClick();
                    Assert.Equal("Mapping 2: choose both the primary and the secondary entity.", status.Text);
                    Assert.Equal(Color.Firebrick, status.ForeColor);
                    Assert.NotEqual(DialogResult.OK, form.DialogResult);

                    secondary.Text = "New Account";
                    ok.PerformClick();
                    Assert.Equal("Mapping 2: \"new account\" is not a logical name.", status.Text);

                    secondary.Text = "new_account";
                    primary.Text = "Contact";
                    ok.PerformClick();
                    Assert.Equal("Mapping 2: contact is mapped more than once.", status.Text);

                    primary.Text = "Account (account)";
                    UiTestHost.Find<Button>(form, "addButton").PerformClick();   // a blank mapping is dropped
                    ok.PerformClick();
                    Assert.Equal(DialogResult.OK, form.DialogResult);
                    Assert.Equal(new[] { ("contact", "new_contact"), ("account", "new_account") }, form.Mappings.Select(m => (m.PrimaryEntity, m.SecondaryEntity)));

                    // Columns... without both entities, or without connections: told so, typed names only.
                    UiTestHost.Find<Button>(form, "addButton").PerformClick();
                    UiTestHost.Find<Button>(form, "columnsButton").PerformClick();
                    Assert.Equal("Choose the primary and the secondary entity first.", status.Text);
                }
            });
        }

        [Fact]
        public void Without_connections_Columns_offers_typed_names_and_says_why()
        {
            UiTestHost.Run(() =>
            {
                using (var form = new EntityMappingsForm(new[] { new EntityMapping("account", "new_account", new ColumnMapping("name", "new_name")) },
                                                         null, null, null, name => throw new InvalidOperationException("boom")))
                {
                    ShowOffScreen(form);
                    ColumnMappingsForm shown = null;
                    string columnNote = null;
                    form.ShowColumnsDialog = columns =>
                    {
                        shown = columns;
                        ShowOffScreen(columns);
                        columnNote = UiTestHost.Find<Label>(columns, "columnStatusLabel").Text;
                        Assert.Empty(UiTestHost.Find<ComboBox>(columns, "primaryColumnBox").Items);
                        Assert.Equal(new[] { ("name", "new_name") }, columns.Pairs.Select(p => (p.PrimaryColumn, p.SecondaryColumn)));
                        UiTestHost.Find<Button>(columns, "cancelButton").PerformClick();
                        return DialogResult.Cancel;
                    };
                    UiTestHost.Find<Button>(form, "columnsButton").PerformClick();
                    UiTestHost.PumpUntil(() => shown != null, "the column pairs");
                    Label status = UiTestHost.Find<Label>(form, "statusLabel");
                    Assert.Contains("Not connected to the primary: type its column names.", status.Text);
                    Assert.Contains("The secondary's columns of new_account could not be read (boom): type the names.", status.Text);
                    Assert.Equal(status.Text, columnNote);
                    Assert.Equal("new_name", Assert.Single(form.Working).Columns.Single().SecondaryColumn);   // Cancel kept them
                }
            });
        }

        [Fact]
        public void The_column_pairs_dialog_adds_updates_removes_and_validates_pairs()
        {
            UiTestHost.Run(() =>
            {
                var primary = new List<NameChoice> { new NameChoice("accountnumber", "Account Number"), new NameChoice("name", "Account Name") };
                var secondary = new List<NameChoice> { new NameChoice("new_number", "Number") };
                using (var form = new ColumnMappingsForm("account", "new_account",
                           new[] { new ColumnMapping("Name", "New_Name"), new ColumnMapping("name", "ignored_duplicate") }, primary, secondary))
                {
                    ShowOffScreen(form);
                    ComboBox primaryBox = UiTestHost.Find<ComboBox>(form, "primaryColumnBox");
                    ComboBox secondaryBox = UiTestHost.Find<ComboBox>(form, "secondaryColumnBox");
                    Label status = UiTestHost.Find<Label>(form, "columnStatusLabel");
                    ListView list = UiTestHost.Find<ListView>(form, "columnList");
                    Assert.Equal("Column pairs: account " + Arrow + " new_account", form.Text);
                    Assert.Equal(new[] { "Account Name (name)", "new_name" }, list.Items[0].SubItems.Cast<ListViewItem.ListViewSubItem>().Select(s => s.Text));

                    primaryBox.Text = "accountnumber";
                    Assert.False(form.TryAdd());
                    Assert.Equal("Choose a primary and a secondary column.", status.Text);
                    secondaryBox.Text = "bad name";
                    Assert.False(form.TryAdd());
                    Assert.Equal("\"bad name\" is not a logical name.", status.Text);
                    secondaryBox.Text = "Number (new_number)";
                    Assert.True(form.TryAdd());
                    Assert.Equal((string.Empty, string.Empty), (primaryBox.Text, secondaryBox.Text));

                    primaryBox.Text = "Account Name (name)";   // a primary column already paired: its pair is updated
                    secondaryBox.Text = "new_title";
                    Assert.True(form.TryAdd());
                    Assert.Equal("name now pairs with new_title.", status.Text);
                    Assert.Equal(new[] { ("name", "new_title"), ("accountnumber", "new_number") }, form.Pairs.Select(p => (p.PrimaryColumn, p.SecondaryColumn)));

                    list.Items[0].Selected = true;   // selecting shows the pair; Remove takes it out
                    Assert.Equal(("Account Name (name)", "new_title"), (primaryBox.Text, secondaryBox.Text));
                    UiTestHost.Find<Button>(form, "removeColumnButton").PerformClick();
                    Assert.Equal(new[] { "accountnumber" }, form.Pairs.Select(p => p.PrimaryColumn));

                    primaryBox.Text = "name";   // a pair still in the boxes is added by OK
                    secondaryBox.Text = "new_name";
                    UiTestHost.Find<Button>(form, "okButton").PerformClick();
                    Assert.Equal(DialogResult.OK, form.DialogResult);
                    Assert.Equal(new[] { "accountnumber", "name" }, form.Pairs.Select(p => p.PrimaryColumn));
                }
            });
        }

        [Fact]
        public void A_changed_mapping_of_the_result_shown_asks_for_a_new_Compare()
        {
            var mapped = new MappedScenario(withMapping: true);
            UiScenario scenario = mapped.Scenario;
            UiTestHost.Run(() =>
            {
                using (DataCompareControl control = UiTest.NewControl(scenario.Settings, scenario.Save, scenario.Dialogs))
                {
                    UiFlowTests.ConnectAndCompare(control, scenario);
                    Label mapping = UiTestHost.Find<Label>(control, "mappingLabel");
                    Assert.Equal(Arrow + " new_account", mapping.Text);

                    // A new column pair: the result shown keeps the old mapping, the strip and the log say so.
                    control.ShowEntityMappingsDialog = form =>
                    {
                        ShowOffScreen(form);
                        form.SelectedMapping.Columns.Add(new ColumnMapping("name", "new_name"));
                        UiTestHost.Find<Button>(form, "okButton").PerformClick();
                        return form.DialogResult;
                    };
                    UiTestHost.Find<Button>(control, "entityMappingsButton").PerformClick();
                    UiTestHost.PumpUntil(() => !control.IsBusy && control.Settings.EntityMappings[0].Columns.Count == 2, "the new pair");
                    Assert.Equal(Arrow + " new_account (press Compare to apply)", mapping.Text);
                    Assert.Equal(Color.DarkGoldenrod, mapping.ForeColor);
                    Assert.Single(control.Result.Mapping.ColumnPairs);
                    UiTestHost.Pump();
                    Assert.Contains("] The entity mapping of account changed: the result shown was read from new_account. Press Compare to apply the new one", UiTest.LogText(control));

                    // Removed: the strip says so too; Compare again reads the same entity of the secondary.
                    control.ShowEntityMappingsDialog = form =>
                    {
                        ShowOffScreen(form);
                        UiTestHost.Find<Button>(form, "removeButton").PerformClick();
                        UiTestHost.Find<Button>(form, "okButton").PerformClick();
                        return form.DialogResult;
                    };
                    UiTestHost.Find<Button>(control, "entityMappingsButton").PerformClick();
                    UiTestHost.PumpUntil(() => !control.IsBusy && control.Settings.EntityMappings.Count == 0, "the removal");
                    Assert.Equal("Mapping removed (press Compare to apply)", mapping.Text);
                    UiTestHost.Find<Button>(control, "compareButton").PerformClick();
                    UiTestHost.PumpUntil(() => !control.IsBusy && control.Result != null, "the unmapped comparison");
                    Assert.Null(control.Result.Mapping);
                    Assert.False(mapping.Visible);
                    Assert.Equal("A-4x", UiFlowTests.Column(UiTestHost.Find<DataGridView>(control, "resultsGrid"), 3).Last());   // the account table's Litware
                    Assert.Empty(scenario.Dialogs.Messages);
                }
            });
        }

        [Fact]
        public void The_secondary_entities_are_listed_once_per_connection_and_a_failure_leaves_typed_names()
        {
            var mapped = new MappedScenario(withMapping: false) { FailSecondaryEntityList = true };
            UiScenario scenario = mapped.Scenario;
            UiTestHost.Run(() =>
            {
                using (DataCompareControl control = UiTest.NewControl(scenario.Settings, scenario.Save, scenario.Dialogs))
                {
                    UiFlowTests.ConnectBoth(control, scenario);
                    int choices = -1, opened = 0;
                    control.ShowEntityMappingsDialog = form =>
                    {
                        opened++;
                        choices = UiTestHost.Find<ComboBox>(form, "secondaryEntityBox").Items.Count;
                        return DialogResult.Cancel;
                    };

                    UiTestHost.Find<Button>(control, "entityMappingsButton").PerformClick();
                    UiTestHost.PumpUntil(() => !control.IsBusy && opened == 1, "the dialog");
                    Assert.Equal(0, choices);
                    UiTestHost.Pump();
                    Assert.Contains("] The secondary's entities could not be listed (Simulated secondary metadata failure): type the secondary entity names.", UiTest.LogText(control));
                    Assert.Empty(scenario.Dialogs.Messages);

                    mapped.FailSecondaryEntityList = false;   // retried, then kept
                    UiTestHost.Find<Button>(control, "entityMappingsButton").PerformClick();
                    UiTestHost.PumpUntil(() => !control.IsBusy && opened == 2, "the dialog again");
                    UiTestHost.Find<Button>(control, "entityMappingsButton").PerformClick();
                    UiTestHost.PumpUntil(() => !control.IsBusy && opened == 3, "the dialog a third time");
                    Assert.Equal((2, 2), (choices, mapped.SecondaryEntityListLoads));   // the failed attempt, then one listing kept

                    // Another secondary: listed again.
                    control.UpdateConnection(scenario.Secondary, null, DataCompareControl.SecondaryActionName, DataCompareControl.SecondaryParameter);
                    UiTestHost.Find<Button>(control, "entityMappingsButton").PerformClick();
                    UiTestHost.PumpUntil(() => !control.IsBusy && opened == 4, "the dialog for the new secondary");
                    Assert.Equal(3, mapped.SecondaryEntityListLoads);
                }
            });
        }

        [Fact]
        public void The_prefix_filter_round_trips_through_Compare_options_and_re_evaluates_the_result()
        {
            var scenario = new UiScenario();
            UiTestHost.Run(() =>
            {
                using (DataCompareControl control = UiTest.NewControl(scenario.Settings, scenario.Save, scenario.Dialogs))
                {
                    UiFlowTests.ConnectAndCompare(control, scenario);
                    DataGridView grid = UiTestHost.Find<DataGridView>(control, "resultsGrid");
                    int requests = scenario.Primary.Executed.Count + scenario.Secondary.Executed.Count;
                    string shownPrefixes = null;

                    control.ShowCompareOptionsDialog = form => DriveOptions(form, f =>
                    {
                        shownPrefixes = UiTestHost.Find<TextBox>(f, "prefixesBox").Text;
                        UiTestHost.Find<TextBox>(f, "prefixesBox").Text = "NEW_\r\ncontoso_, new_";
                        Assert.Equal("Only columns starting with new_, contoso_", UiTestHost.Find<Label>(f, "prefixCountLabel").Text);
                    });
                    UiTestHost.Find<Button>(control, "compareOptionsButton").PerformClick();

                    Assert.Equal(string.Empty, shownPrefixes);
                    Assert.Equal("new_,contoso_", control.Settings.ComparedPrefixes);
                    Assert.Equal("new_,contoso_", scenario.LastSaved.ComparedPrefixes);
                    Assert.Equal(new[] { "new_", "contoso_" }, control.Result.Options.ComparedPrefixes);
                    // Only new_/contoso_ columns count now: the account number and the state no longer make rows Different.
                    Assert.Equal(new[] { "Matching", "Matching", "Missing", "Matching", "Matching", "Extra" }, UiFlowTests.Column(grid, 0));
                    Label prefixes = UiTestHost.Find<Label>(control, "prefixLabel");
                    Assert.True(prefixes.Visible);
                    Assert.Equal("Prefixes: new_, contoso_", prefixes.Text);
                    Assert.Equal(new[] { "Prefixes: new_, contoso_", "Primary: 5", "Secondary: 4", "|", "Missing: 1", "Different: 0", "Extra: 1", "Matching: 4" },
                        UiFlowTests.VisibleSummary(control));
                    UiFlowTests.Select(grid, 1);
                    DataGridViewRow number = UiTestHost.Find<DataGridView>(control, "detailGrid").Rows.Cast<DataGridViewRow>()
                        .Single(l => (string)l.Cells[0].Value == "Account Number");
                    Assert.Equal("accountnumber - " + DetailBuilder.OutsidePrefixFilterNote, number.Cells[0].ToolTipText);
                    Assert.Equal(SystemColors.GrayText, number.DefaultCellStyle.ForeColor);
                    Assert.Equal(requests, scenario.Primary.Executed.Count + scenario.Secondary.Executed.Count);   // nothing read again
                    UiTestHost.Pump();
                    string log = UiTest.LogText(control);
                    Assert.Contains("] Prefix filter: only the columns starting with new_, contoso_ are compared.", log);
                    Assert.Contains("] Re-evaluated with the new compare options (nothing read again) - Result: 6 rows - matching 4, different 0, missing 1, extra 1.", log);

                    // Reopened, the dialog shows them one a line; emptied, every column counts again.
                    control.ShowCompareOptionsDialog = form => DriveOptions(form, f =>
                    {
                        shownPrefixes = UiTestHost.Find<TextBox>(f, "prefixesBox").Text;
                        UiTestHost.Find<TextBox>(f, "prefixesBox").Text = " ";
                        Assert.Equal("Every column is compared", UiTestHost.Find<Label>(f, "prefixCountLabel").Text);
                    });
                    UiTestHost.Find<Button>(control, "compareOptionsButton").PerformClick();
                    Assert.Equal("new_" + Environment.NewLine + "contoso_", shownPrefixes);
                    Assert.Equal(string.Empty, control.Settings.ComparedPrefixes);
                    Assert.False(prefixes.Visible);
                    Assert.Equal(new[] { "Matching", "Different", "Missing", "Matching", "Different", "Extra" }, UiFlowTests.Column(grid, 0));
                    UiTestHost.Pump();
                    Assert.Contains("] Prefix filter: none - the columns of every prefix are compared.", UiTest.LogText(control));
                }
            });
        }

        [Fact]
        public void A_prefix_filter_is_shown_without_a_result_and_used_by_the_next_compare()
        {
            var scenario = new UiScenario();
            scenario.Settings.ComparedPrefixes = "zz_";
            UiTestHost.Run(() =>
            {
                using (DataCompareControl control = UiTest.NewControl(scenario.Settings, scenario.Save, scenario.Dialogs))
                {
                    Assert.Equal(new[] { "Prefixes: zz_", "No comparison yet: pick an entity and a view, then press Compare." }, UiFlowTests.VisibleSummary(control));
                    UiFlowTests.ConnectAndCompare(control, scenario);
                    Assert.Equal(4, control.Result.Summary.Matching);   // nothing starts with zz_: every row found on both sides matches
                    Assert.Contains(" outside the prefix filter (zz_), ", UiTest.LogText(control));
                }
            });
        }

        [Fact]
        public void A_table_mapped_within_the_same_organisation_is_compared_without_the_same_organisation_question()
        {
            var mapped = new MappedScenario(withMapping: true);
            UiScenario scenario = mapped.Scenario;
            UiTestHost.Run(() =>
            {
                using (DataCompareControl control = UiTest.NewControl(scenario.Settings, scenario.Save, scenario.Dialogs))
                {
                    // Two connections to the same organisation (by name).
                    control.UpdateConnection(scenario.Primary, new McTools.Xrm.Connection.ConnectionDetail { Organization = "contoso" }, string.Empty, null);
                    ListView entities = UiTestHost.Find<ListView>(control, "entityList");
                    UiTestHost.PumpUntil(() => !control.IsBusy && entities.Items.Count == 3, "the entities");
                    entities.Items["account"].Selected = true;
                    UiTestHost.PumpUntil(() => !control.IsBusy && UiTestHost.Find<ListBox>(control, "viewList").Items.Count > 0, "the account views");
                    control.UpdateConnection(scenario.Secondary, new McTools.Xrm.Connection.ConnectionDetail { Organization = "CONTOSO" },
                        DataCompareControl.SecondaryActionName, DataCompareControl.SecondaryParameter);

                    UiTestHost.Find<Button>(control, "compareButton").PerformClick();
                    UiTestHost.PumpUntil(() => !control.IsBusy && control.Result != null, "the mapped comparison");

                    Assert.Empty(scenario.Dialogs.Messages);   // no question: account is compared with new_account
                    Assert.Equal("new_account", control.Result.SecondaryEntityLogicalName);
                    Assert.Contains("] Same organisation on both sides: account is compared with its mapped table new_account.", UiTest.LogText(control));

                    // Unmapped, the question comes back.
                    control.Settings.EntityMappings.Clear();
                    UiTestHost.Find<Button>(control, "compareButton").PerformClick();
                    UiTestHost.Pump();
                    Assert.StartsWith("The secondary environment appears to be the SAME organisation", Assert.Single(scenario.Dialogs.Messages));
                }
            });
        }

        [Fact]
        public void The_mapping_caption_follows_the_setting_and_the_result()
        {
            EntitySchema primary = TestData.StandardSchema().GetEntity("account");
            EntityMapping mapping = MappingEngineTests.AccountMapping();
            var map = new EntityMap(mapping, primary, MappingEngineTests.MigratedSchema().GetEntity("new_account"));
            var mappedResult = new CompareResult(primary, null, new List<RowComparison>(), new CompareSummary(), null, map);
            var plainResult = new CompareResult(primary, null, new List<RowComparison>(), new CompareSummary());

            Assert.Null(DataCompareControl.MappingCaption(null, null, out bool pending, out string tip));
            Assert.False(pending);
            Assert.Null(tip);
            Assert.Equal(Arrow + " new_account", DataCompareControl.MappingCaption(mapping, null, out pending, out tip));
            Assert.Contains("1 column pair", tip);
            Assert.Equal(Arrow + " new_account", DataCompareControl.MappingCaption(mapping.Clone(), mappedResult, out pending, out _));
            Assert.False(pending);
            Assert.Null(DataCompareControl.MappingCaption(null, plainResult, out pending, out _));
            Assert.Equal(Arrow + " new_account (press Compare to apply)", DataCompareControl.MappingCaption(mapping, plainResult, out pending, out tip));
            Assert.True(pending);
            Assert.StartsWith("The result shown was read without a mapping", tip);
            Assert.Equal("Mapping removed (press Compare to apply)", DataCompareControl.MappingCaption(null, mappedResult, out pending, out tip));
            Assert.StartsWith("The result shown was read from new_account", tip);
            Assert.Equal("account -> new_account (accountnumber -> new_accountnumber)", DataCompareControl.DescribeMapping(mapping));
            Assert.Equal("Prefixes: contoso_, new_", DataCompareControl.PrefixCaption(new[] { "contoso_", "new_" }));
            Assert.Equal(string.Empty, DataCompareControl.PrefixCaption(new string[0]));
        }

        [Fact]
        public void Name_choices_show_display_and_logical_names_and_parse_what_is_typed()
        {
            var choices = new[] { new NameChoice("Account", "Account"), new NameChoice("new_account", "Migrated Account"), new NameChoice("x", null) };

            Assert.Equal(new[] { "Account (account)", "Migrated Account (new_account)", "x" }, choices.Select(c => c.ToString()));
            Assert.Equal("name", new NameChoice("name", "name").ToString());
            Assert.Equal("new_account", NameChoice.Parse("migrated account (new_account)", choices));
            Assert.Equal("contoso_case", NameChoice.Parse(" Case (CONTOSO_Case) ", choices));
            Assert.Equal("contoso_case", NameChoice.Parse("CONTOSO_Case", choices));
            Assert.Equal(string.Empty, NameChoice.Parse("  ", choices));
            Assert.Equal("Migrated Account (new_account)", NameChoice.Show("NEW_ACCOUNT", choices));
            Assert.Equal("contoso_case", NameChoice.Show("contoso_case", choices));
            Assert.Equal(string.Empty, NameChoice.Show(null, choices));
            Assert.True(NameChoice.IsLogicalName("new_account2"));
            Assert.False(NameChoice.IsLogicalName("New_Account"));
            Assert.False(NameChoice.IsLogicalName("my table"));
            Assert.Equal(new[] { "Account Name (name)", "Account Number (accountnumber)" },
                NameChoice.FromSchema(TestData.StandardSchema().GetEntity("account")).Select(c => c.ToString()).Where(t => t.StartsWith("Account N", StringComparison.Ordinal)));
            Assert.DoesNotContain(NameChoice.FromSchema(TestData.StandardSchema().GetEntity("account")), c => c.LogicalName == "revenue_base" || c.LogicalName == "new_secret");
            Assert.Empty(NameChoice.FromSchema(null));
        }

        [Fact]
        public void The_compare_options_dialog_shows_the_prefixes_one_a_line_and_parses_them()
        {
            UiTestHost.Run(() =>
            {
                using (var form = new CompareOptionsForm(new[] { "createdon" }, new[] { "CONTOSO_", "new_", "contoso_" }))
                {
                    ShowOffScreen(form);
                    TextBox box = UiTestHost.Find<TextBox>(form, "prefixesBox");
                    Assert.Equal("contoso_" + Environment.NewLine + "new_", box.Text);
                    Assert.Equal(new[] { "contoso_", "new_" }, form.Prefixes);
                    Assert.Equal("Only columns starting with contoso_, new_", UiTestHost.Find<Label>(form, "prefixCountLabel").Text);
                    Assert.True(box.Multiline && box.AcceptsReturn);
                    box.Text = "a_;b_ c_";
                    Assert.Equal(new[] { "a_", "b_", "c_" }, form.Prefixes);
                    UiTestHost.Find<Button>(form, "restoreDefaultsButton").PerformClick();   // restores the ignored list only
                    Assert.Equal(new[] { "a_", "b_", "c_" }, form.Prefixes);
                    Assert.Equal("Compare options", form.Text);
                }
            });
        }

        [Fact]
        public void Settings_keep_the_mappings_and_prefixes_and_old_files_load_without_them()
        {
            var settings = new DataCompareSettings { ComparedPrefixes = "contoso_,new_" };
            settings.EntityMappings.Add(MappingEngineTests.AccountMapping());
            settings.EntityMappings.Add(new EntityMapping("contact", " "));   // incomplete: kept in the file, never applied

            var serializer = new XmlSerializer(typeof(DataCompareSettings));
            string xml;
            using (var writer = new StringWriter())
            {
                serializer.Serialize(writer, settings);
                xml = writer.ToString();
            }
            DataCompareSettings loaded;
            using (var reader = new StringReader(xml))
            {
                loaded = (DataCompareSettings)serializer.Deserialize(reader);
            }

            Assert.Contains("<ComparedPrefixes>contoso_,new_</ComparedPrefixes>", xml);
            Assert.Contains("<PrimaryEntity>account</PrimaryEntity>", xml);
            Assert.Equal(2, loaded.EntityMappings.Count);
            Assert.Equal(("account", "new_account"), (loaded.EntityMappings[0].PrimaryEntity, loaded.EntityMappings[0].SecondaryEntity));
            Assert.Equal(("accountnumber", "new_accountnumber"), (loaded.EntityMappings[0].Columns[0].PrimaryColumn, loaded.EntityMappings[0].Columns[0].SecondaryColumn));
            CompareOptions options = loaded.BuildOptions();
            Assert.Equal(new[] { "contoso_", "new_" }, options.ComparedPrefixes);
            Assert.Equal("new_account", Assert.Single(options.EntityMappings).SecondaryEntity);   // only the complete one
            Assert.NotSame(loaded.EntityMappings[0], options.EntityMappings[0]);
            Assert.Equal("new_account", loaded.FindMapping("ACCOUNT").SecondaryEntity);
            Assert.Null(loaded.FindMapping("contact"));

            DataCompareSettings old;
            using (var reader = new StringReader("<?xml version=\"1.0\"?><DataCompareSettings><DifferencesOnly>true</DifferencesOnly></DataCompareSettings>"))
            {
                old = (DataCompareSettings)serializer.Deserialize(reader);
            }
            Assert.Equal(string.Empty, old.ComparedPrefixes);
            Assert.Empty(old.EntityMappings);
            Assert.Empty(old.BuildOptions().ComparedPrefixes);
            Assert.Empty(old.BuildOptions().EntityMappings);
            Assert.Empty(new DataCompareSettings { ComparedPrefixes = null, EntityMappings = null }.BuildOptions().ComparedPrefixes);
            Assert.Null(new DataCompareSettings { EntityMappings = null }.FindMapping("account"));
        }

        // ---- helpers ----

        internal static void ShowOffScreen(Form form)
        {
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-3000, -3000);
            form.Show();
        }

        private static DialogResult DriveOptions(CompareOptionsForm form, Action<CompareOptionsForm> edit)
        {
            ShowOffScreen(form);
            edit(form);
            UiTestHost.Find<Button>(form, "okButton").PerformClick();
            return form.DialogResult;
        }

        private static List<string> FakeFetchXmlNames(FakeOrganizationService service) => service.Fetches.Select(FakeFetchXml.EntityName).Distinct().ToList();
    }

    /// <summary>
    /// <see cref="UiScenario"/> with the accounts migrated into new_account in the secondary too (account number in
    /// new_accountnumber): the secondary lists its entities (new_account "Migrated Account", contact) and answers
    /// new_account's metadata. With <c>withMapping</c> the settings map account to new_account.
    /// </summary>
    internal sealed class MappedScenario
    {
        private int _secondaryEntityListLoads;
        private int _secondaryMetadataRequests;

        public MappedScenario(bool withMapping)
        {
            if (withMapping) Scenario.Settings.EntityMappings.Add(MappingEngineTests.AccountMapping());
            List<Entity> accounts = Scenario.Accounts;   // Contoso, Fabrikam, Northwind, Litware, O'Neil's Bakery, Tailspin
            Scenario.Secondary.Add(Migrate(accounts[0]));
            Scenario.Secondary.Add(Migrate(accounts[1], ("new_accountnumber", "A-2x")));
            Scenario.Secondary.Add(Migrate(accounts[3]));
            Scenario.Secondary.Add(Migrate(accounts[4], ("ownerid", TestData.Ref("systemuser", TestData.Id(202)))));
            Scenario.Secondary.Add(Migrate(accounts[5], ("statecode", TestData.Opt(1)), ("statuscode", TestData.Opt(2))));
            Scenario.Secondary.ExecuteHandler = OnSecondaryRequest;
        }

        public UiScenario Scenario { get; } = new UiScenario();

        public bool FailSecondaryEntityList { get; set; }

        public int SecondaryEntityListLoads => Volatile.Read(ref _secondaryEntityListLoads);

        public int SecondaryMetadataRequests => Volatile.Read(ref _secondaryMetadataRequests);

        private static Entity Migrate(Entity account, params (string Name, object Value)[] changes)
        {
            var record = new Entity("new_account") { Id = account.Id };
            foreach (KeyValuePair<string, object> pair in account.Attributes)
            {
                string name = pair.Key == "accountid" ? "new_accountid" : pair.Key == "accountnumber" ? "new_accountnumber" : pair.Key;
                record[name] = pair.Value;
            }
            foreach ((string name, object value) in changes) record[name] = value;
            return record;
        }

        private OrganizationResponse OnSecondaryRequest(OrganizationRequest request)
        {
            switch (request)
            {
                case RetrieveAllEntitiesRequest _:
                    Interlocked.Increment(ref _secondaryEntityListLoads);
                    if (FailSecondaryEntityList) throw FakeOrganizationService.Fault(FakeOrganizationService.GenericFailure, "Simulated secondary metadata failure");
                    var list = new RetrieveAllEntitiesResponse();
                    list.Results["EntityMetadata"] = new[] { Meta("new_account", "Migrated Account", "new_accountid"), Meta("contact", "Contact", "contactid") };
                    return list;
                case RetrieveEntityRequest retrieve when retrieve.LogicalName == "new_account":
                    Interlocked.Increment(ref _secondaryMetadataRequests);
                    var response = new RetrieveEntityResponse();
                    response.Results["EntityMetadata"] = Meta("new_account", "Migrated Account", "new_accountid").With("Attributes", new AttributeMetadata[]
                    {
                        new UniqueIdentifierAttributeMetadata { LogicalName = "new_accountid" },
                        new StringAttributeMetadata { LogicalName = "name", DisplayName = new Microsoft.Xrm.Sdk.Label("Name", 1033) },
                        new StringAttributeMetadata { LogicalName = "new_accountnumber", DisplayName = new Microsoft.Xrm.Sdk.Label("Number", 1033) },
                        new LookupAttributeMetadata { LogicalName = "primarycontactid", Targets = new[] { "contact" } },
                        new LookupAttributeMetadata { LogicalName = "ownerid", Targets = new[] { "systemuser", "team" } },
                        new StateAttributeMetadata { LogicalName = "statecode" },
                        new StatusAttributeMetadata { LogicalName = "statuscode" }
                    });
                    return response;
                default:
                    return null;
            }
        }

        private static EntityMetadata Meta(string logicalName, string label, string primaryId) =>
            new EntityMetadata { LogicalName = logicalName, SchemaName = logicalName, DisplayName = new Microsoft.Xrm.Sdk.Label(label, 1033) }
                .With("IsIntersect", false)
                .With("IsPrivate", false)
                .With("PrimaryIdAttribute", primaryId)
                .With("PrimaryNameAttribute", "name");
    }
}
