using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using System.Xml.Serialization;
using McTools.Xrm.Connection;
using Microsoft.Xrm.Sdk;
using MyscotekDataCompare.Core;
using MyscotekDataCompare.Core.Services;
using MyscotekDataCompare.Tests.Fakes;
using MyscotekDataCompare.UI;
using Xunit;
using Label = System.Windows.Forms.Label;
using LogLevel = MyscotekDataCompare.Core.LogLevel;

namespace MyscotekDataCompare.Tests
{
    /// <summary>
    /// Builds the real <see cref="DataCompareControl"/> outside XrmToolBox (STA thread, toast notifications
    /// stubbed - see <see cref="UiTestHost"/>) through its internal constructor with in-memory settings and no
    /// mirroring to XrmToolBox's log, so nothing outside the test run is touched. The public constructor
    /// differs only in reading/writing XrmToolBox's settings store.
    /// </summary>
    [Collection(UiTestCollection.Name)]
    public class UiSmokeTests
    {
        [Fact]
        public void Control_builds_without_the_XrmToolBox_host_and_starts_idle_with_nothing_loaded()
        {
            var dialogs = new DialogRecorder();
            UiTestHost.Run(() =>
            {
                using (DataCompareControl control = UiTest.NewControl(new DataCompareSettings(), dialogs: dialogs))
                {
                    Assert.True(control.IsHandleCreated);
                    Assert.Equal(new Font("Segoe UI", 9f), control.Font);

                    // toolbar: Select secondary | Refresh entities ... Primary | Secondary | Close
                    ToolStrip toolbar = UiTestHost.Find<ToolStrip>(control, "toolbar");
                    Assert.Equal(ToolStripGripStyle.Hidden, toolbar.GripStyle);
                    Assert.Equal("Select secondary environment...", UiTestHost.FindToolItem<ToolStripButton>(control, "selectSecondaryButton").Text);
                    Assert.True(UiTestHost.FindToolItem<ToolStripButton>(control, "selectSecondaryButton").Enabled);
                    Assert.Equal("Refresh entities", UiTestHost.FindToolItem<ToolStripButton>(control, "refreshEntitiesButton").Text);
                    Assert.True(UiTestHost.FindToolItem<ToolStripButton>(control, "refreshEntitiesButton").Enabled);
                    Assert.Equal(ToolStripItemAlignment.Right, UiTestHost.FindToolItem<ToolStripButton>(control, "closeButton").Alignment);
                    Assert.Equal("Primary: (none)", UiTestHost.FindToolItem<ToolStripLabel>(control, "primaryLabel").Text);
                    Assert.Equal("Secondary: (none)", UiTestHost.FindToolItem<ToolStripLabel>(control, "secondaryLabel").Text);
                    Assert.Equal(ToolStripItemAlignment.Right, UiTestHost.FindToolItem<ToolStripLabel>(control, "primaryLabel").Alignment);

                    // entities and views: empty
                    ListView entities = UiTestHost.Find<ListView>(control, "entityList");
                    Assert.Empty(entities.Items);
                    Assert.Equal(View.Details, entities.View);
                    Assert.True(entities.FullRowSelect);
                    Assert.False(entities.HideSelection);
                    Assert.Equal(new[] { "Display name", "Logical name" }, entities.Columns.Cast<ColumnHeader>().Select(c => c.Text));
                    Assert.Equal("Entities", UiTestHost.Find<Label>(control, "entitiesLabel").Text);
                    Assert.NotNull(UiTestHost.Find<TextBox>(control, "entityFilter"));
                    Assert.Equal("Views", UiTestHost.Find<Label>(control, "viewsLabel").Text);
                    ListBox views = UiTestHost.Find<ListBox>(control, "viewList");
                    Assert.Empty(views.Items);
                    Assert.False(views.Enabled);

                    // action row: Compare (bold) and Cancel disabled, Compare options... and Entity mappings... available, no progress
                    Button compare = UiTestHost.Find<Button>(control, "compareButton");
                    Assert.Equal("Compare", compare.Text);
                    Assert.True(compare.Font.Bold);
                    Assert.False(compare.Enabled);
                    Assert.False(UiTestHost.Find<Button>(control, "cancelButton").Enabled);
                    Assert.Equal("Compare options...", UiTestHost.Find<Button>(control, "compareOptionsButton").Text);
                    Assert.True(UiTestHost.Find<Button>(control, "compareOptionsButton").Enabled);
                    Assert.Equal("Entity mappings...", UiTestHost.Find<Button>(control, "entityMappingsButton").Text);
                    Assert.True(UiTestHost.Find<Button>(control, "entityMappingsButton").Enabled);
                    Assert.Equal(string.Empty, UiTestHost.Find<Label>(control, "progressLabel").Text);
                    Assert.Equal(new[] { "compareButton", "cancelButton", "compareOptionsButton", "entityMappingsButton", "progressLabel" },
                        UiTestHost.Find<FlowLayoutPanel>(control, "actionRow").Controls.Cast<Control>().Select(c => c.Name));

                    // summary strip: only the "nothing yet" text (no mapping, no prefix filter)
                    FlowLayoutPanel summary = UiTestHost.Find<FlowLayoutPanel>(control, "summaryRow");
                    Assert.Equal(new[] { "summaryEmptyLabel" }, summary.Controls.Cast<Control>().Where(c => c.Visible).Select(c => c.Name));
                    Assert.Equal(new[] { "mappingLabel", "prefixLabel", "summaryEmptyLabel" }, summary.Controls.Cast<Control>().Take(3).Select(c => c.Name));

                    // filters: All by default, the text filter empty, nothing to count
                    ComboBox status = UiTestHost.Find<ComboBox>(control, "statusFilter");
                    Assert.Equal(ComboBoxStyle.DropDownList, status.DropDownStyle);
                    Assert.Equal(new[] { "All", "Missing", "Different", "Extra", "Matching" }, status.Items.Cast<string>());
                    Assert.Equal("All", status.Text);
                    Assert.Equal(string.Empty, UiTestHost.Find<TextBox>(control, "rowFilter").Text);
                    Assert.Equal("0 of 0 rows", UiTestHost.Find<Label>(control, "rowCountLabel").Text);

                    // results grid: read-only, Status then a hidden id, no new-row line
                    DataGridView grid = UiTestHost.Find<DataGridView>(control, "resultsGrid");
                    Assert.IsType<BindingSource>(grid.DataSource);
                    Assert.Empty(grid.Rows.Cast<DataGridViewRow>());
                    Assert.True(grid.ReadOnly);
                    Assert.False(grid.AllowUserToAddRows);
                    Assert.False(grid.RowHeadersVisible);
                    Assert.Equal(DataGridViewSelectionMode.FullRowSelect, grid.SelectionMode);
                    Assert.False(grid.MultiSelect);
                    Assert.Equal(DataGridViewAutoSizeColumnsMode.None, grid.AutoSizeColumnsMode);
                    Assert.Equal(new[] { "Status", "Id" }, grid.Columns.Cast<DataGridViewColumn>().Select(c => c.HeaderText));
                    Assert.False(grid.Columns[DataCompareControl.IdColumn].Visible);

                    // detail pane: empty, Differences only off, Column / Primary / Secondary
                    DataGridView detail = UiTestHost.Find<DataGridView>(control, "detailGrid");
                    Assert.Empty(detail.Rows.Cast<DataGridViewRow>());
                    Assert.True(detail.ReadOnly);
                    Assert.Equal(new[] { "Column", "Primary", "Secondary" }, detail.Columns.Cast<DataGridViewColumn>().Select(c => c.HeaderText));
                    Assert.Equal(string.Empty, UiTestHost.Find<Label>(control, "detailCaption").Text);
                    CheckBox differencesOnly = UiTestHost.Find<CheckBox>(control, "differencesOnlyCheckBox");
                    Assert.Equal("Differences only", differencesOnly.Text);
                    Assert.False(differencesOnly.Checked);
                    Assert.True(differencesOnly.Enabled);

                    // log: read-only, no wrapping, no links, Consolas 9; tells the user to connect
                    RichTextBox log = UiTestHost.Find<RichTextBox>(control, "logBox");
                    Assert.True(log.ReadOnly);
                    Assert.False(log.WordWrap);
                    Assert.False(log.DetectUrls);
                    Assert.Equal(Color.White, log.BackColor);
                    Assert.Equal(new Font("Consolas", 9f), log.Font);
                    foreach (string button in new[] { "copyLogButton", "saveLogButton", "clearLogButton" })
                        Assert.True(UiTestHost.Find<Button>(control, button).Enabled, button);
                    UiTestHost.PumpUntil(() => log.Text.Contains(DataCompareControl.NoPrimaryMessage), "the no-connection message");
                    Assert.Matches(@"^\[\d\d:\d\d:\d\d\] Not connected", log.Text);

                    // the splits: entity list ~300 px once the splitter had a real size, views below it;
                    // results above the detail pane, which is above the log
                    SplitContainer main = UiTestHost.Find<SplitContainer>(control, "mainSplit");
                    Assert.Equal(Orientation.Vertical, main.Orientation);
                    Assert.Equal(300, main.SplitterDistance);
                    Assert.Equal(Orientation.Horizontal, UiTestHost.Find<SplitContainer>(control, "leftSplit").Orientation);
                    Assert.Equal(Orientation.Horizontal, UiTestHost.Find<SplitContainer>(control, "rightSplit").Orientation);
                    Assert.Equal(Orientation.Horizontal, UiTestHost.Find<SplitContainer>(control, "bottomSplit").Orientation);
                    Assert.False(control.IsBusy);
                    Assert.Null(control.Result);
                }
            });

            Assert.Empty(dialogs.Messages);
            Assert.True(ToastNotificationsStub.IsStubLoaded);   // the real toast assembly was never loaded
        }

        [Fact]
        public void Differences_only_comes_from_the_settings_and_is_saved_when_it_changes()
        {
            UiTestHost.Run(() =>
            {
                var settings = new DataCompareSettings { DifferencesOnly = true };
                var saved = new List<bool>();
                using (DataCompareControl control = UiTest.NewControl(settings, s => saved.Add(s.DifferencesOnly)))
                {
                    CheckBox differencesOnly = UiTestHost.Find<CheckBox>(control, "differencesOnlyCheckBox");
                    Assert.True(differencesOnly.Checked);
                    Assert.Empty(saved);   // applying the settings does not write them back

                    differencesOnly.Checked = false;
                    differencesOnly.Checked = true;

                    Assert.Equal(new[] { false, true }, saved);
                    Assert.True(control.Settings.DifferencesOnly);
                }
            });
        }

        [Fact]
        public void A_settings_store_that_cannot_be_read_is_not_fatal()
        {
            UiTestHost.Run(() =>
            {
                using (var control = new DataCompareControl(() => throw new IOException("settings store unavailable"), _ => { }, mirrorToXrmToolBoxLog: false))
                {
                    var dialogs = new DialogRecorder();
                    control.ShowMessage = dialogs.Show;
                    control.CreateControl();

                    Assert.NotNull(control.Settings);
                    Assert.Equal(CompareOptions.DefaultIgnoredAttributeList, control.Settings.IgnoredAttributes);   // the defaults
                    RichTextBox log = UiTestHost.Find<RichTextBox>(control, "logBox");
                    UiTestHost.PumpUntil(() => log.Text.Contains("Settings could not be loaded, the defaults are used: settings store unavailable"), "the settings warning");
                    Assert.Empty(dialogs.Messages);
                }
            });
        }

        [Fact]
        public void Settings_defaults_match_the_spec()
        {
            var settings = new DataCompareSettings();

            Assert.Equal(CompareOptions.DefaultIgnoredAttributeList, settings.IgnoredAttributes);
            Assert.Contains("transactioncurrencyid", settings.IgnoredAttributes.Split(','));   // owner decision, 2026-10-02
            Assert.False(settings.DifferencesOnly);
            Assert.Empty(settings.LastEntities);
            Assert.Equal(5000, settings.PageSize);
            Assert.Equal(5000, new DataCompareSettings { PageSize = 0 }.EffectivePageSize);
            Assert.Equal(5000, new DataCompareSettings { PageSize = 90000 }.EffectivePageSize);
            Assert.Equal(250, new DataCompareSettings { PageSize = 250 }.EffectivePageSize);

            CompareOptions options = new DataCompareSettings { PageSize = 250 }.BuildOptions();
            Assert.Equal(250, options.PageSize);
            Assert.Equal(CompareOptions.DefaultIgnoredAttributes.OrderBy(n => n), options.IgnoredAttributes.OrderBy(n => n));
            Assert.Empty(new DataCompareSettings { IgnoredAttributes = "" }.BuildOptions().IgnoredAttributes);   // blank: nothing ignored
            Assert.Equal(CompareOptions.DefaultIgnoredAttributes.Count, new DataCompareSettings { IgnoredAttributes = null }.BuildOptions().IgnoredAttributes.Count);
            Assert.Equal(new[] { "name", "telephone1" }, new DataCompareSettings { IgnoredAttributes = "Name; telephone1" }.BuildOptions().IgnoredAttributes.OrderBy(n => n));
        }

        [Fact]
        public void Settings_survive_an_XmlSerializer_round_trip_and_old_or_partial_files_keep_the_defaults()
        {
            var settings = new DataCompareSettings { IgnoredAttributes = "", DifferencesOnly = true, PageSize = 2000 };
            settings.SetLastEntity("https://dev.crm4.dynamics.com/", "Account");
            settings.SetLastEntity("https://test.crm4.dynamics.com", "contact");
            settings.SetLastEntity("HTTPS://DEV.crm4.dynamics.com", "lead");   // replaces the first

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

            Assert.Contains("<DifferencesOnly>true</DifferencesOnly>", xml);
            Assert.Equal(string.Empty, loaded.IgnoredAttributes);   // an emptied list stays empty (ignore nothing), not the defaults
            Assert.True(loaded.DifferencesOnly);
            Assert.Equal(2000, loaded.PageSize);
            Assert.Equal(2, loaded.LastEntities.Count);
            Assert.Equal("lead", loaded.GetLastEntity("https://dev.crm4.dynamics.com"));
            Assert.Equal("contact", loaded.GetLastEntity("https://TEST.crm4.dynamics.com/"));
            Assert.Null(loaded.GetLastEntity("https://prod.crm4.dynamics.com"));

            // A file without the newer elements, with an element this version does not know, keeps the
            // defaults; a null list (no element) is the default list too.
            DataCompareSettings old;
            using (var reader = new StringReader("<?xml version=\"1.0\"?><DataCompareSettings><SomethingRemoved>x</SomethingRemoved>" +
                                                 "<DifferencesOnly>true</DifferencesOnly></DataCompareSettings>"))
            {
                old = (DataCompareSettings)serializer.Deserialize(reader);
            }
            Assert.True(old.DifferencesOnly);   // read after the unknown element
            Assert.Equal(CompareOptions.DefaultIgnoredAttributeList, old.IgnoredAttributes);
            Assert.Equal(5000, old.PageSize);
            Assert.Empty(old.LastEntities);

            DataCompareSettings empty;
            using (var reader = new StringReader("<?xml version=\"1.0\"?><DataCompareSettings />"))
            {
                empty = (DataCompareSettings)serializer.Deserialize(reader);
            }
            Assert.Equal(CompareOptions.DefaultIgnoredAttributeList, empty.IgnoredAttributes);

            var withNulls = new DataCompareSettings { LastEntities = null, IgnoredAttributes = null };
            Assert.Null(withNulls.GetLastEntity("x"));
            withNulls.SetLastEntity("x", "account");
            Assert.Equal("account", withNulls.GetLastEntity("X/"));
            Assert.Equal(CompareOptions.DefaultIgnoredAttributes.Count, withNulls.BuildOptions().IgnoredAttributes.Count);
            Assert.Equal(string.Empty, DataCompareSettings.OrganizationKey(null));
            Assert.Equal("https://other", DataCompareSettings.OrganizationKey(" HTTPS://Other/ "));
        }
    }

    /// <summary>
    /// The public constructor - the one XrmToolBox calls - with XrmToolBox's settings and log folders
    /// redirected (Paths.OverrideRootPath) to a folder in the test output, which is deleted afterwards.
    /// </summary>
    [Collection(UiTestCollection.Name)]
    public class UiHostIntegrationTests
    {
        [Fact]
        public void Public_constructor_persists_settings_with_SettingsManager_and_mirrors_errors_to_the_XrmToolBox_log()
        {
            UiTest.WithRedirectedXrmToolBoxRoot(root =>
            {
                var dialogs = new DialogRecorder();
                UiTestHost.Run(() =>
                {
                    using (var control = new DataCompareControl())
                    {
                        control.ShowMessage = dialogs.Show;
                        control.Size = new Size(1200, 800);
                        control.CreateControl();
                        CheckBox differencesOnly = UiTestHost.Find<CheckBox>(control, "differencesOnlyCheckBox");
                        Assert.False(differencesOnly.Checked);   // no settings file yet: the defaults
                        differencesOnly.Checked = true;          // saved at once

                        var failing = new FakeOrganizationService
                        {
                            ExecuteHandler = request => throw FakeOrganizationService.Fault(FakeOrganizationService.GenericFailure, "Simulated {0} failure")
                        };
                        control.UpdateConnection(failing, null, string.Empty, null);
                        UiTestHost.PumpUntil(() => !control.IsBusy && dialogs.Messages.Count == 1, "the error");
                    }

                    using (var reopened = new DataCompareControl())
                    {
                        reopened.ShowMessage = dialogs.Show;
                        Assert.True(UiTestHost.Find<CheckBox>(reopened, "differencesOnlyCheckBox").Checked);   // read back by SettingsManager
                    }
                });

                string settings = File.ReadAllText(Path.Combine(root, "Settings", "MyscotekDataCompare.xml"));
                Assert.Contains("<DifferencesOnly>true</DifferencesOnly>", settings);
                Assert.Contains("<IgnoredAttributes>" + CompareOptions.DefaultIgnoredAttributeList + "</IgnoredAttributes>", settings);
                string log = File.ReadAllText(Path.Combine(root, "Logs", "MyscotekDataCompare.log"));
                Assert.Contains("Loading entities failed: Simulated {0} failure", log);   // braces kept literally
                Assert.Equal("Simulated {0} failure", Assert.Single(dialogs.Messages));
            });
        }
    }

    [Collection(UiTestCollection.Name)]
    public class UiLoggerTests
    {
        [Fact]
        public void Lines_are_prefixed_with_the_time_coloured_by_level_and_Log_lines_reach_the_sink()
        {
            UiTestHost.Run(() =>
            {
                using (var box = new RichTextBox { WordWrap = false, ReadOnly = true, Width = 800 })   // as in the tool
                {
                    var sunk = new List<(LogLevel, string)>();
                    var logger = new UiLogger(box, (level, message) => sunk.Add((level, message)));
                    box.CreateControl();

                    logger.Log(LogLevel.Info, "Primary: page 1: 3 rows (3 records so far).");
                    logger.Log(LogLevel.Success, "Result: 4 rows - matching 1, different 1, missing 1, extra 1.");
                    logger.Log(LogLevel.Warning, "  Secondary: the view query failed");
                    logger.Log(LogLevel.Error, "Compare failed: boom");
                    logger.Write(LogLevel.Info, "shown only");
                    UiTestHost.PumpUntil(() => logger.LineCount == 5, "five lines");

                    string[] lines = box.Lines.Take(5).ToArray();
                    Assert.All(lines, l => Assert.Matches(@"^\[\d\d:\d\d:\d\d\] ", l));
                    Assert.EndsWith("]   Secondary: the view query failed", lines[2]);   // indentation is kept
                    Assert.Equal(SystemColors.WindowText.ToArgb(), ColourOfLine(box, 0).ToArgb());
                    Assert.Equal(Color.ForestGreen.ToArgb(), ColourOfLine(box, 1).ToArgb());
                    Assert.Equal(Color.DarkGoldenrod.ToArgb(), ColourOfLine(box, 2).ToArgb());
                    Assert.Equal(Color.Firebrick.ToArgb(), ColourOfLine(box, 3).ToArgb());
                    Assert.Equal(4, sunk.Count);   // Log lines go to the sink, Write lines do not
                }
            });
        }

        [Fact]
        public void The_oldest_lines_are_trimmed_beyond_the_cap_and_lines_logged_before_the_handle_exists_are_kept()
        {
            UiTestHost.Run(() =>
            {
                using (var box = new RichTextBox { WordWrap = false, ReadOnly = true })
                {
                    var logger = new UiLogger(box, maxLines: 20);
                    for (int i = 1; i <= 25; i++) logger.Write(LogLevel.Info, "line " + i);   // no handle yet: queued
                    box.CreateControl();
                    UiTestHost.PumpUntil(() => box.Text.Contains("line 25"), "the queued lines");

                    Assert.True(logger.LineCount <= 20);
                    Assert.Equal(logger.LineCount, box.Lines.Count(l => l.Length > 0));
                    Assert.DoesNotContain("] line 1\n", box.Text);
                    Assert.EndsWith("] line 25", box.Lines[logger.LineCount - 1]);

                    logger.Clear();
                    Assert.Equal(0, logger.LineCount);
                    Assert.Equal(string.Empty, box.Text);
                }
            });
        }

        /// <summary>The colour of the first message character of a line (after "[HH:mm:ss] ").</summary>
        private static Color ColourOfLine(RichTextBox box, int line)
        {
            int start = 0;
            for (int i = 0; i < line; i++) start = box.Text.IndexOf('\n', start) + 1;
            box.Select(start + 11, 1);
            return box.SelectionColor;
        }
    }

    [Collection(UiTestCollection.Name)]
    public class UiHelperTests
    {
        [Fact]
        public void Row_filter_matches_any_column_escapes_like_wildcards_and_quotes_and_combines_with_the_status()
        {
            Assert.Null(DataCompareControl.BuildRowFilter("  ", new[] { "c0" }));
            Assert.Null(DataCompareControl.BuildRowFilter("x", new string[0]));
            Assert.Equal("[__status] LIKE '%contoso%' OR [c0] LIKE '%contoso%'", DataCompareControl.BuildRowFilter(" contoso ", new[] { "__status", "c0" }));
            Assert.Equal("[c0] LIKE '%O''Neil[*] [[]UK[]] 5[%]%'", DataCompareControl.BuildRowFilter("O'Neil* [UK] 5%", new[] { "c0" }));

            Assert.Null(DataCompareControl.CombineFilters(null, ""));
            Assert.Equal("a", DataCompareControl.CombineFilters("a", null));
            Assert.Equal("b", DataCompareControl.CombineFilters(null, "b"));
            Assert.Equal("(a) AND (b OR c)", DataCompareControl.CombineFilters("a", "b OR c"));
        }

        [Fact]
        public void Each_status_has_its_text_and_row_colour()
        {
            Assert.Equal(new[] { "Matching", "Different", "Missing", "Extra" },
                new[] { RowStatus.Match, RowStatus.Different, RowStatus.Missing, RowStatus.Extra }.Select(DataCompareControl.StatusText));

            (string Status, string Back, string Selection)[] expected =
            {
                ("Missing", "#F8D7DA", "#F1AEB5"), ("Different", "#FFF3CD", "#FFE69C"), ("Matching", "#D4EDDA", "#A3CFBB"), ("Extra", "#CCE5FF", "#9EC5FE")
            };
            foreach ((string status, string back, string selection) in expected)
            {
                Assert.True(DataCompareControl.TryStatusColors(status, out Color b, out Color s), status);
                Assert.Equal(ColorTranslator.FromHtml(back).ToArgb(), b.ToArgb());
                Assert.Equal(ColorTranslator.FromHtml(selection).ToArgb(), s.ToArgb());
            }
            Assert.False(DataCompareControl.TryStatusColors("All", out _, out _));
            Assert.False(DataCompareControl.TryStatusColors(null, out _, out _));
        }

        [Fact]
        public void The_final_progress_text_gives_the_outcome_and_the_counts()
        {
            Assert.Equal("Done: 6 rows - missing 1, different 2, extra 1, matching 2",
                DataCompareControl.FinalProgressText(new CompareSummary { Missing = 1, Different = 2, Extra = 1, Matching = 2 }));
            Assert.Equal("Cancelled (partial result): 1 row - missing 0, different 0, extra 0, matching 1, not checked 4 - the secondary view failed",
                DataCompareControl.FinalProgressText(new CompareSummary { Matching = 1, Unchecked = 4, Cancelled = true, SecondaryQueryFailed = true }));
            Assert.Equal(string.Empty, DataCompareControl.FinalProgressText(null));
            Assert.Equal("6 rows - matching 2, different 2, missing 1, extra 1",
                DataCompareControl.ResultCounts(new CompareSummary { Missing = 1, Different = 2, Extra = 1, Matching = 2 }));
        }

        [Fact]
        public void The_detail_caption_names_the_entity_the_id_and_the_status_and_where_the_record_was_found()
        {
            var result = new CompareResult(TestData.StandardSchema().GetEntity("account"), null, new List<RowComparison>(), new CompareSummary());
            Guid id = TestData.Id(7);
            Entity record = TestData.Account(id, "A");

            Assert.Equal($"Account {id:D} - Different",
                DataCompareControl.DetailCaption(result, new RowComparison(id, RowStatus.Different, record, record, null, true, true)));
            Assert.Equal($"Account {id:D} - Different (found by id outside the secondary's view)",
                DataCompareControl.DetailCaption(result, new RowComparison(id, RowStatus.Different, record, record, null, true, false)));
            Assert.Equal($"Account {id:D} - Matching (found by id outside the primary's view)",
                DataCompareControl.DetailCaption(result, new RowComparison(id, RowStatus.Match, record, record, null, false, true)));
            Assert.Equal($"Account {id:D} - Missing",
                DataCompareControl.DetailCaption(result, new RowComparison(id, RowStatus.Missing, record, null, null, true, false)));
            Assert.Equal($"Account {id:D} - Extra",
                DataCompareControl.DetailCaption(result, new RowComparison(id, RowStatus.Extra, null, record, null, false, true)));
        }

        [Fact]
        public void The_grid_shows_the_layout_columns_or_the_primary_name()
        {
            var view = new ViewInfo
            {
                LayoutXml = "<grid><row name=\"result\" id=\"accountid\"><cell name=\"name\" width=\"300\" /><cell name=\"pc.emailaddress1\" width=\"150\" /></row></grid>"
            };
            Assert.Equal(new[] { ("name", 300), ("pc.emailaddress1", 150) }, DataCompareControl.ViewColumns(view, "name").Select(c => (c.Name, c.Width)));
            Assert.Equal(new[] { ("name", 300) }, DataCompareControl.ViewColumns(new ViewInfo { LayoutXml = "" }, "name").Select(c => (c.Name, c.Width)));
            Assert.Empty(DataCompareControl.ViewColumns(new ViewInfo(), null));
        }

        [Fact]
        public void Same_organisation_is_detected_by_name_or_url()
        {
            ConnectionDetail Detail(string organization, string webUrl) => new ConnectionDetail { Organization = organization, WebApplicationUrl = webUrl };

            Assert.True(DataCompareControl.SameOrganization(Detail("contoso", "https://a.crm4.dynamics.com/"), Detail("CONTOSO", "https://b.crm4.dynamics.com")));
            Assert.True(DataCompareControl.SameOrganization(Detail(null, "https://a.crm4.dynamics.com/"), Detail("", "https://A.crm4.dynamics.com")));
            Assert.False(DataCompareControl.SameOrganization(Detail("dev", "https://dev.crm4.dynamics.com"), Detail("test", "https://test.crm4.dynamics.com")));
            Assert.False(DataCompareControl.SameOrganization(Detail("dev", null), null));
        }

        [Fact]
        public void Errors_are_one_line_and_prefer_the_Dataverse_fault_message()
        {
            Assert.Equal("Simulated failure on two lines", DataCompareControl.ErrorText(
                FakeOrganizationService.Fault(FakeOrganizationService.GenericFailure, "Simulated failure\r\n  on two lines")));
            Assert.Equal("boom", DataCompareControl.ErrorText(new InvalidOperationException(" boom ")));
            Assert.Equal(string.Empty, DataCompareControl.ErrorText(null));
        }

        [Fact]
        public void The_compare_options_dialog_lists_the_ignored_names_sorted_and_restores_the_defaults()
        {
            UiTestHost.Run(() =>
            {
                using (var form = new CompareOptionsForm(new[] { "modifiedon", "CreatedOn", "modifiedon" }, null))
                {
                    form.StartPosition = FormStartPosition.Manual;
                    form.Location = new Point(-3000, -3000);
                    form.Show();
                    TextBox box = UiTestHost.Find<TextBox>(form, "ignoredAttributesBox");
                    Label count = UiTestHost.Find<Label>(form, "countLabel");

                    Assert.Equal("createdon" + Environment.NewLine + "modifiedon", box.Text);
                    Assert.Equal("2 attributes", count.Text);
                    Assert.True(box.Multiline && box.AcceptsReturn);
                    Assert.Null(form.AcceptButton);   // Enter is a new line in the list
                    Assert.Same(UiTestHost.Find<Button>(form, "cancelButton"), form.CancelButton);
                    Assert.Equal(DialogResult.OK, UiTestHost.Find<Button>(form, "okButton").DialogResult);

                    box.Text = "  Name, telephone1;name\r\n\r\nemailaddress1  ";
                    Assert.Equal(new[] { "emailaddress1", "name", "telephone1" }, form.AttributeNames);
                    box.Text = " ";
                    Assert.Empty(form.AttributeNames);
                    Assert.Equal("Nothing ignored", count.Text);

                    UiTestHost.Find<Button>(form, "restoreDefaultsButton").PerformClick();
                    Assert.Equal(CompareOptions.DefaultIgnoredAttributes.OrderBy(n => n, StringComparer.Ordinal), form.AttributeNames);
                    Assert.Equal(CompareOptions.DefaultIgnoredAttributes.Count, box.Lines.Length);
                    Assert.Equal("16 attributes", count.Text);
                }
            });
        }
    }

    /// <summary>Shared set-up for the UI tests.</summary>
    internal static class UiTest
    {
        /// <summary>A control with in-memory settings, no XrmToolBox log mirroring and recorded dialogs; handle created.</summary>
        public static DataCompareControl NewControl(DataCompareSettings settings, Action<DataCompareSettings> save = null, DialogRecorder dialogs = null)
        {
            var control = new DataCompareControl(() => settings, save ?? (_ => { }), mirrorToXrmToolBoxLog: false)
            {
                Size = new Size(1200, 800)
            };
            control.ShowMessage = (dialogs ?? new DialogRecorder()).Show;
            control.CreateControl();
            UiTestHost.Pump();
            return control;
        }

        public static string LogText(DataCompareControl control) => UiTestHost.Find<RichTextBox>(control, "logBox").Text;

        /// <summary>
        /// Runs <paramref name="body"/> with XrmToolBox's root folder (settings, logs) redirected to a new folder in the
        /// test output, deleted afterwards, so the public constructor never touches the real %AppData% XrmToolBox folders.
        /// </summary>
        public static void WithRedirectedXrmToolBoxRoot(Action<string> body)
        {
            string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "XrmToolBoxRoot-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            FieldInfo rootPath = typeof(XrmToolBox.Extensibility.Paths).GetField("rootPath", BindingFlags.NonPublic | BindingFlags.Static);
            try
            {
                XrmToolBox.Extensibility.Paths.OverrideRootPath(root);
                body(root);
            }
            finally
            {
                rootPath?.SetValue(null, null);   // back to the default root for anything else in this process
                try
                {
                    Directory.Delete(root, recursive: true);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    /// <summary>Stands in for the control's message boxes: records the text and answers Yes/No questions with <see cref="Answer"/>.</summary>
    internal sealed class DialogRecorder
    {
        public List<string> Messages { get; } = new List<string>();

        public DialogResult Answer { get; set; } = DialogResult.No;

        public DialogResult Show(string text, MessageBoxButtons buttons, MessageBoxIcon icon, MessageBoxDefaultButton defaultButton)
        {
            Messages.Add(text);
            return buttons == MessageBoxButtons.OK ? DialogResult.OK : Answer;
        }
    }
}
