using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using MyscotekDataCompare.Tests.Fakes;
using MyscotekDataCompare.UI;
using Xunit;

namespace MyscotekDataCompare.Tests
{
    /// <summary>
    /// The tool library checklist: the tool adapts its controls to the size XrmToolBox gives it. The real
    /// control, connected to both environments and with a result shown (a cancelled one, so every label of
    /// the summary strip is visible), is sized like a small and a large XrmToolBox tab: every container fills
    /// its space, the rows stack without gaps or overlaps, the grids and the log take what is left, and no
    /// button is clipped (the summary strip wraps; only a tool smaller than any real tab scrolls the results).
    /// </summary>
    [Collection(UiTestCollection.Name)]
    public class UiLayoutTests
    {
        [Fact]
        public void The_tool_fills_800x500_and_1600x900_without_clipping_or_overlapping_controls()
        {
            var scenario = new UiScenario();
            scenario.Secondary.FailFetchPages.Add(1);   // "Secondary view failed" in the summary strip too
            UiTestHost.Run(() =>
            {
                using (DataCompareControl control = UiTest.NewControl(scenario.Settings, scenario.Save, scenario.Dialogs))
                {
                    UiFlowTests.ConnectAndCompare(control, scenario);
                    UiFlowTests.Select(UiTestHost.Find<DataGridView>(control, "resultsGrid"), 1);
                    Assert.Contains("Secondary view failed", UiFlowTests.VisibleSummary(control));
                    SplitContainer main = UiTestHost.Find<SplitContainer>(control, "mainSplit");

                    SizeTo(control, new Size(800, 500));
                    AssertFills(control, scroll: false);
                    int narrowEntityWidth = main.SplitterDistance;
                    Assert.InRange(narrowEntityWidth, 200, 299);   // the entity list gives way on a small tool

                    SizeTo(control, new Size(1600, 900));
                    AssertFills(control, scroll: false);
                    Assert.Equal(300, main.SplitterDistance);   // and gets its width back on a large one
                    Assert.Equal(1, SummaryLines(control));     // the whole summary strip on one line

                    SizeTo(control, new Size(800, 500));
                    AssertFills(control, scroll: false);
                    Assert.Equal(narrowEntityWidth, main.SplitterDistance);

                    // Smaller than any XrmToolBox tab: the rows still do not overlap and the grid keeps its
                    // minimum; the results area scrolls instead.
                    SizeTo(control, new Size(640, 400));
                    AssertFills(control, scroll: true);
                    Assert.Equal(200, main.SplitterDistance);

                    // A width the user dragged the entity list to is kept whenever there is room for it.
                    SizeTo(control, new Size(1600, 900));
                    main.SplitterDistance = 260;
                    SizeTo(control, new Size(800, 500));
                    Assert.Equal(narrowEntityWidth, main.SplitterDistance);
                    SizeTo(control, new Size(1600, 900));
                    Assert.Equal(260, main.SplitterDistance);
                    AssertFills(control, scroll: false);

                    // A cancelled result (Not checked) is laid out the same way.
                    scenario.Secondary.FailFetchPages.Clear();
                    using (var release = new System.Threading.ManualResetEventSlim())
                    using (var requested = new System.Threading.ManualResetEventSlim())
                    {
                        scenario.Secondary.BeforeExecute = request =>
                        {
                            if (!(request is Microsoft.Xrm.Sdk.Messages.RetrieveMultipleRequest multiple)
                                || !(multiple.Query is Microsoft.Xrm.Sdk.Query.FetchExpression)) return;
                            requested.Set();
                            release.Wait(System.TimeSpan.FromSeconds(20));
                        };
                        UiTestHost.Find<Button>(control, "compareButton").PerformClick();
                        UiTestHost.PumpUntil(() => requested.IsSet, "the secondary view");
                        UiTestHost.Find<Button>(control, "cancelButton").PerformClick();
                        release.Set();
                        UiTestHost.PumpUntil(() => !control.IsBusy && control.Result != null, "the cancelled comparison");
                    }
                    Assert.Contains("Not checked: 3 (cancelled)", UiFlowTests.VisibleSummary(control));
                    SizeTo(control, new Size(800, 500));
                    AssertFills(control, scroll: false);
                    SizeTo(control, new Size(1600, 900));
                    AssertFills(control, scroll: false);
                    Assert.Empty(scenario.Dialogs.Messages);
                }
            });
        }

        [Fact]
        public void The_new_buttons_and_the_mapping_and_prefix_labels_fit_800x600_and_1920x1080()
        {
            var mapped = new MappedScenario(withMapping: true);
            UiScenario scenario = mapped.Scenario;
            scenario.Settings.ComparedPrefixes = "new_,contoso_";
            scenario.Secondary.FailFetchPages.Add(1);   // "Secondary view failed" in the summary strip too
            UiTestHost.Run(() =>
            {
                using (DataCompareControl control = UiTest.NewControl(scenario.Settings, scenario.Save, scenario.Dialogs))
                {
                    UiFlowTests.ConnectAndCompare(control, scenario);
                    UiFlowTests.Select(UiTestHost.Find<DataGridView>(control, "resultsGrid"), 1);
                    List<string> summary = UiFlowTests.VisibleSummary(control);
                    Assert.Equal(new[] { DataCompareControl.MappingArrow + " new_account", "Prefixes: new_, contoso_" }, summary.Take(2));
                    Assert.Contains("Secondary view failed", summary);
                    Button compare = UiTestHost.Find<Button>(control, "compareButton");
                    Button mappings = UiTestHost.Find<Button>(control, "entityMappingsButton");
                    Label progress = UiTestHost.Find<Label>(control, "progressLabel");
                    Assert.False(string.IsNullOrEmpty(progress.Text));

                    // 800 px: every button on the action row's first line; the progress text where it fits.
                    SizeTo(control, new Size(800, 600));
                    AssertFills(control, scroll: false);
                    Assert.Equal(compare.Top, mappings.Top);
                    Assert.True(progress.Width >= DataCompareControl.ProgressMinWidth, $"progress {progress.Width} px wide at 800x600");

                    // 1920 px: one line for the action row and one for the whole summary strip.
                    SizeTo(control, new Size(1920, 1080));
                    AssertFills(control, scroll: false);
                    Assert.Equal((compare.Top, compare.Top), (mappings.Top, progress.Top));
                    Assert.Equal(mappings.Right + mappings.Margin.Right + progress.Margin.Left, progress.Left);
                    Assert.Equal(1, SummaryLines(control));

                    // And back.
                    SizeTo(control, new Size(800, 600));
                    AssertFills(control, scroll: false);
                    SizeTo(control, new Size(640, 400));   // smaller than any tab: the buttons may wrap, nothing is clipped
                    AssertFills(control, scroll: true);
                    Assert.Empty(scenario.Dialogs.Messages);
                }
            });
        }

        [Fact]
        public void The_mapping_and_options_dialogs_fit_their_default_and_minimum_sizes()
        {
            UiTestHost.Run(() =>
            {
                var entities = new[] { new Core.Services.EntityInfo { LogicalName = "account", DisplayName = "Account" } };
                var forms = new Form[]
                {
                    new EntityMappingsForm(new[] { MappingEngineTests.AccountMapping() }, entities, entities, null, null),
                    new ColumnMappingsForm("account", "new_account", MappingEngineTests.AccountMapping().Columns, null, null, "A note."),
                    new CompareOptionsForm(new[] { "createdon" }, new[] { "contoso_" })
                };
                foreach (Form form in forms)
                {
                    using (form)
                    {
                        UiMappingTests.ShowOffScreen(form);
                        foreach (Size size in new[] { form.ClientSize, form.MinimumSize })
                        {
                            form.Size = size == form.MinimumSize ? size : form.Size;
                            UiTestHost.Pump(20);
                            var client = new Rectangle(Point.Empty, form.ClientSize);
                            foreach (Control named in UiTestHost.Descendants(form).Where(c => c.Visible && !string.IsNullOrEmpty(c.Name)))
                            {
                                Rectangle inForm = form.RectangleToClient(named.Parent.RectangleToScreen(named.Bounds));
                                Assert.True(client.Contains(inForm), $"{form.Text}: {named.Name} {inForm} lies outside {client}");
                            }
                            foreach (Control row in UiTestHost.Descendants(form).Where(c => c is FlowLayoutPanel || c.Name == "buttonRow" || c.Name == "editor" || c.Name == "columnEditor"))
                                AssertChildrenInside(row, " in " + form.Text);
                        }
                    }
                }
            });
        }

        private static void SizeTo(DataCompareControl control, Size size)
        {
            control.Size = size;
            // A fit can resize a split, which queues one more: pump until a pause leaves none queued.
            for (int i = 0; ; i++)
            {
                UiTestHost.PumpUntil(() => !control.IsLayoutFitQueued, "the layout to fit");
                UiTestHost.Pump(20);
                if (!control.IsLayoutFitQueued) break;
                Assert.True(i < 20, "the layout keeps fitting itself again");
            }
            Assert.Equal(size, control.ClientSize);
        }

        private static void AssertFills(DataCompareControl control, bool scroll)
        {
            Size size = control.ClientSize;
            string at = $" at {size.Width}x{size.Height}";

            // The toolbar across the top, every item on it (on a tool narrower than any tab, items may go to
            // its overflow menu); the splits below.
            ToolStrip toolbar = UiTestHost.Find<ToolStrip>(control, "toolbar");
            Assert.Equal(new Rectangle(0, 0, size.Width, toolbar.Height), toolbar.Bounds);
            if (size.Width >= 800) Assert.All(toolbar.Items.Cast<ToolStripItem>(), item => Assert.Equal(ToolStripItemPlacement.Main, item.Placement));
            SplitContainer main = UiTestHost.Find<SplitContainer>(control, "mainSplit");
            Assert.Equal(new Rectangle(0, toolbar.Height, size.Width, size.Height - toolbar.Height), main.Bounds);
            SplitContainer left = UiTestHost.Find<SplitContainer>(control, "leftSplit");
            Assert.Equal(main.Panel1.ClientSize, left.Size);
            SplitContainer right = UiTestHost.Find<SplitContainer>(control, "rightSplit");
            Assert.Equal(main.Panel2.ClientSize, right.Size);
            SplitContainer bottom = UiTestHost.Find<SplitContainer>(control, "bottomSplit");
            Assert.Equal(right.Panel2.ClientSize, bottom.Size);

            // Left: the entity list fills its panel below the filter, the view list its panel below its label.
            var entityPanel = UiTestHost.Find<TableLayoutPanel>(control, "entityPanel");
            Assert.Equal(left.Panel1.ClientSize, entityPanel.Size);
            Assert.True(left.Panel1.Height >= DataCompareControl.EntityPanelMinHeight, $"entity panel {left.Panel1.Height} px high" + at);
            AssertStacked(entityPanel, new Control[]
            {
                UiTestHost.Find<Label>(control, "entitiesLabel"), UiTestHost.Find<TextBox>(control, "entityFilter"), UiTestHost.Find<ListView>(control, "entityList")
            }, fullWidthFrom: 1, at);
            var viewPanel = UiTestHost.Find<TableLayoutPanel>(control, "viewPanel");
            Assert.Equal(left.Panel2.ClientSize, viewPanel.Size);
            Assert.True(left.Panel2.Height >= DataCompareControl.ViewPanelMinHeight, $"view panel {left.Panel2.Height} px high" + at);
            AssertStacked(viewPanel, new Control[] { UiTestHost.Find<Label>(control, "viewsLabel"), UiTestHost.Find<ListBox>(control, "viewList") }, fullWidthFrom: 1, at);

            // Right, top: the rows of the results panel, the grid taking the rest.
            var area = UiTestHost.Find<Panel>(control, "resultsArea");
            Assert.Equal(right.Panel1.ClientSize, area.Size);
            Assert.False(area.HorizontalScroll.Visible, "the results area scrolls sideways" + at);
            Assert.Equal(scroll, area.VerticalScroll.Visible);
            var results = UiTestHost.Find<TableLayoutPanel>(control, "resultsPanel");
            Assert.Equal(area.ClientSize.Width, results.Width);
            Assert.Equal(scroll ? area.AutoScrollMinSize.Height : area.ClientSize.Height, results.Height);
            DataGridView grid = UiTestHost.Find<DataGridView>(control, "resultsGrid");
            var rows = new Control[]
            {
                UiTestHost.Find<FlowLayoutPanel>(control, "actionRow"), UiTestHost.Find<FlowLayoutPanel>(control, "summaryRow"),
                UiTestHost.Find<TableLayoutPanel>(control, "filterRow"), grid
            };
            AssertStacked(results, rows, fullWidthFrom: 0, at);
            Assert.True(grid.Height >= DataCompareControl.MinimumGridHeight, $"grid {grid.Height} px high" + at);
            foreach (Control row in rows.Where(r => r is FlowLayoutPanel || r is TableLayoutPanel)) AssertChildrenInside(row, at);
            var progress = UiTestHost.Find<Label>(control, "progressLabel");
            Assert.Equal(progress.Parent.ClientSize.Width - progress.Margin.Right, progress.Right);   // the progress text takes the rest of its line
            var rowFilter = UiTestHost.Find<TextBox>(control, "rowFilter");
            Assert.True(rowFilter.Width >= 100, $"row filter {rowFilter.Width} px wide" + at);

            // Right, bottom: the detail pane above the log, each filling its panel.
            var detailPanel = UiTestHost.Find<TableLayoutPanel>(control, "detailPanel");
            Assert.Equal(bottom.Panel1.ClientSize, detailPanel.Size);
            Assert.True(bottom.Panel1.Height >= DataCompareControl.DetailPanelMinHeight, $"detail panel {bottom.Panel1.Height} px high" + at);
            var detailHeader = UiTestHost.Find<TableLayoutPanel>(control, "detailHeader");
            DataGridView detail = UiTestHost.Find<DataGridView>(control, "detailGrid");
            AssertStacked(detailPanel, new Control[] { detailHeader, detail }, fullWidthFrom: 0, at);
            AssertChildrenInside(detailHeader, at);
            Assert.True(detail.Height >= 50, $"detail grid {detail.Height} px high" + at);
            var differencesOnly = UiTestHost.Find<CheckBox>(control, "differencesOnlyCheckBox");
            Assert.Equal(differencesOnly.Parent.ClientSize.Width - differencesOnly.Margin.Right, differencesOnly.Right);   // at the right of the caption

            var logPanel = UiTestHost.Find<TableLayoutPanel>(control, "logPanel");
            Assert.Equal(bottom.Panel2.ClientSize, logPanel.Size);
            Assert.True(bottom.Panel2.Height >= DataCompareControl.LogPanelMinHeight, $"log panel {bottom.Panel2.Height} px high" + at);
            var logHeader = UiTestHost.Find<FlowLayoutPanel>(control, "logHeader");
            RichTextBox log = UiTestHost.Find<RichTextBox>(control, "logBox");
            AssertStacked(logPanel, new Control[] { logHeader, log }, fullWidthFrom: 0, at);
            AssertChildrenInside(logHeader, at);
            Assert.True(log.Height >= 40, $"log {log.Height} px high" + at);

            // Nothing named lies outside the tool.
            var bounds = new Rectangle(Point.Empty, size);
            foreach (Control named in UiTestHost.Descendants(control).Where(c => c.Visible && !string.IsNullOrEmpty(c.Name) && !IsInScrolledArea(c, area, scroll)))
            {
                Rectangle inTool = control.RectangleToClient(named.Parent.RectangleToScreen(named.Bounds));
                Assert.True(bounds.Contains(inTool), $"{named.Name} {inTool} lies outside the tool" + at);
            }
        }

        /// <summary>
        /// The controls of a one-column table, in order, each starting where the previous one ends (no gap,
        /// no overlap) and the last one ending at the bottom; from <paramref name="fullWidthFrom"/> on they
        /// span the column.
        /// </summary>
        private static void AssertStacked(TableLayoutPanel table, IList<Control> controls, int fullWidthFrom, string at)
        {
            int top = table.Padding.Top;
            for (int i = 0; i < controls.Count; i++)
            {
                Control c = controls[i];
                Assert.True(c.Top == top + c.Margin.Top, $"{c.Name} starts at {c.Top}, expected {top + c.Margin.Top}" + at);
                Assert.Equal(table.Padding.Left + c.Margin.Left, c.Left);
                if (i >= fullWidthFrom)
                    Assert.True(c.Right == table.ClientSize.Width - table.Padding.Right - c.Margin.Right, $"{c.Name} ends at {c.Right}" + at);
                top = c.Bottom + c.Margin.Bottom;
            }
            Assert.True(top == table.ClientSize.Height - table.Padding.Bottom,
                $"{table.Name}: the last row ends at {top} of {table.ClientSize.Height} ({string.Join(", ", controls.Select(c => c.Name + " " + c.Bounds))})" + at);
        }

        /// <summary>Every control of a row is wholly inside it (not clipped) and none overlaps another.</summary>
        private static void AssertChildrenInside(Control row, string at)
        {
            List<Control> children = row.Controls.Cast<Control>().Where(c => c.Visible).ToList();
            foreach (Control child in children)
            {
                Assert.True(row.ClientRectangle.Contains(child.Bounds), $"{child.Name} {child.Bounds} is clipped by {row.Name} {row.ClientRectangle}" + at);
                Assert.DoesNotContain(children, other => other != child && other.Bounds.IntersectsWith(child.Bounds));
            }
        }

        /// <summary>The number of lines the visible labels of the summary strip take.</summary>
        private static int SummaryLines(DataCompareControl control) =>
            UiTestHost.Find<FlowLayoutPanel>(control, "summaryRow").Controls.Cast<Control>().Where(c => c.Visible).Select(c => c.Top - c.Margin.Top).Distinct().Count();

        private static bool IsInScrolledArea(Control c, Control area, bool scroll)
        {
            if (!scroll) return false;
            for (Control parent = c.Parent; parent != null; parent = parent.Parent)
            {
                if (parent == area) return true;
            }
            return false;
        }
    }
}
