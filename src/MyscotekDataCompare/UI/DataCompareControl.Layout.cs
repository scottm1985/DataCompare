using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Label = System.Windows.Forms.Label;

namespace MyscotekDataCompare.UI
{
    /// <summary>
    /// Control construction for <see cref="DataCompareControl"/> (SPEC 6.2), built in code: there is no
    /// designer or .resx file. Layout, top to bottom: toolbar; a vertical split with, on the left, the entity
    /// list above the view list and, on the right, a horizontal split with the results area on top (action
    /// row, summary strip, filter row, results grid) and, below, a second horizontal split with the detail
    /// pane above the log. Every control the logic or the UI tests look up has a Name. The layout follows the
    /// tool's size (<see cref="FitLayout"/>): the entity list gives way on a narrow tool, the results area
    /// keeps the height its rows need plus a minimum grid, and the detail pane and the log keep theirs.
    /// </summary>
    public partial class DataCompareControl
    {
        // ---- layout sizes in pixels (the rows themselves size to their content) ----

        /// <summary>The entity list's width while there is room for it (dragging the splitter changes it).</summary>
        private const int EntityPanelWidth = 300;
        private const int EntityPanelMinWidth = 200;

        /// <summary>The results side keeps at least this width: below it, the entity list gives way down to its minimum.</summary>
        private const int ResultsComfortWidth = 560;
        private const int ResultsMinWidth = 360;

        /// <summary>The share of the left side's height the entity list gets at first; the view list has the rest.</summary>
        private const double EntityListHeightShare = 0.6;
        internal const int EntityPanelMinHeight = 120;
        internal const int ViewPanelMinHeight = 90;

        /// <summary>The share of the height the results area gets at first; the detail pane and the log share the rest.</summary>
        private const double ResultsHeightShare = 0.5;

        /// <summary>The share of the lower part the detail pane gets at first; the log has the rest.</summary>
        private const double DetailHeightShare = 0.55;

        /// <summary>
        /// The progress text keeps the rest of the action row's line while it gets at least this width; below it
        /// (a narrow tool) it takes a line of its own under the buttons.
        /// </summary>
        internal const int ProgressMinWidth = 160;

        /// <summary>The results grid is never squeezed below this; a tool too small for it scrolls the results area.</summary>
        internal const int MinimumGridHeight = 90;
        internal const int DetailPanelMinHeight = 90;
        internal const int LogPanelMinHeight = 90;

        // ---- toolbar ----
        private ToolStrip _toolbar;
        private ToolStripButton _selectSecondaryButton;
        private ToolStripButton _refreshEntitiesButton;
        private ToolStripButton _closeButton;
        private ToolStripLabel _primaryLabel;
        private ToolStripLabel _secondaryLabel;

        // ---- splitters (placed once they have a real size, then kept fitting: see FitLayout) ----
        private SplitContainer _mainSplit;
        private SplitContainer _leftSplit;
        private SplitContainer _rightSplit;
        private SplitContainer _bottomSplit;
        private bool _mainSplitPlaced;
        private bool _leftSplitPlaced;
        private bool _rightSplitPlaced;
        private bool _bottomSplitPlaced;
        private int _entityPanelWidth = EntityPanelWidth;
        private bool _settingSplitter;   // our own SplitterDistance changes are not the user's
        private bool _fitQueued;

        /// <summary>A layout fit is waiting for the message loop (the UI tests pump until it has run).</summary>
        internal bool IsLayoutFitQueued => _fitQueued;

        // ---- entities and views (left) ----
        private TextBox _entityFilter;
        private ListView _entityList;
        private ListBox _viewList;

        // ---- results (right, top: rows of a table in a scrollable area) ----
        private Panel _resultsArea;
        private TableLayoutPanel _resultsPanel;
        private FlowLayoutPanel _actionRow;
        private Button _compareButton;
        private Button _cancelButton;
        private Button _compareOptionsButton;
        private Button _entityMappingsButton;
        private Label _progressLabel;
        private FlowLayoutPanel _summaryRow;
        private Label _mappingLabel;
        private Label _prefixLabel;
        private Label _summaryEmptyLabel;
        private Label _primaryCountLabel;
        private Label _secondaryCountLabel;
        private Label _summarySeparator;
        private Label _missingCountLabel;
        private Label _differentCountLabel;
        private Label _extraCountLabel;
        private Label _matchingCountLabel;
        private Label _uncheckedCountLabel;
        private Label _secondaryFailedLabel;
        private TableLayoutPanel _filterRow;
        private ComboBox _statusFilter;
        private TextBox _rowFilter;
        private Label _rowCountLabel;
        private DataGridView _grid;
        private BindingSource _bindingSource;

        // ---- detail pane and log (right, bottom) ----
        private Label _detailCaption;
        private CheckBox _differencesOnly;
        private DataGridView _detailGrid;
        private RichTextBox _log;
        private Button _copyLogButton;
        private Button _saveLogButton;
        private Button _clearLogButton;

        private ToolTip _toolTip;
        private System.Windows.Forms.Timer _rowFilterTimer;
        private Font _boldFont;
        private Font _logFont;

        private void BuildUi()
        {
            SuspendLayout();

            _toolTip = new ToolTip(_components);
            _boldFont = new Font(Font, FontStyle.Bold);
            _logFont = new Font("Consolas", 9f);
            // The row filter is applied a moment after the last keystroke, not on every one.
            _rowFilterTimer = new System.Windows.Forms.Timer(_components) { Interval = 300 };
            _rowFilterTimer.Tick += OnRowFilterTimerTick;

            _toolbar = BuildToolbar();

            // The results rows sit in a scrollable area: it scrolls only when the tool is too small to give
            // them their height and a minimum grid next to the detail pane's and the log's minimums.
            _resultsArea = new Panel { Name = "resultsArea", Dock = DockStyle.Fill, AutoScroll = true, Margin = Padding.Empty };
            _resultsArea.Controls.Add(BuildResultsPanel());

            _bottomSplit = new SplitContainer
            {
                Name = "bottomSplit",
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal
            };
            _bottomSplit.Panel1.Controls.Add(BuildDetailPanel());
            _bottomSplit.Panel2.Controls.Add(BuildLogPanel());

            _rightSplit = new SplitContainer
            {
                Name = "rightSplit",
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal
            };
            _rightSplit.Panel1.Controls.Add(_resultsArea);
            _rightSplit.Panel2.Controls.Add(_bottomSplit);

            _leftSplit = new SplitContainer
            {
                Name = "leftSplit",
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal
            };
            _leftSplit.Panel1.Controls.Add(BuildEntityPanel());
            _leftSplit.Panel2.Controls.Add(BuildViewPanel());

            _mainSplit = new SplitContainer
            {
                Name = "mainSplit",
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                FixedPanel = FixedPanel.Panel1   // the entity list keeps its width when the tool is resized (FitLayout narrows it if need be)
            };
            _mainSplit.Panel1.Controls.Add(_leftSplit);
            _mainSplit.Panel2.Controls.Add(_rightSplit);

            // Dock order: the control added last docks first, so the toolbar takes the top and the split
            // fills the rest.
            Controls.Add(_mainSplit);
            Controls.Add(_toolbar);

            // SplitterDistance can only be set once a SplitContainer has a real size, and the summary strip
            // wraps differently at every width: fit the layout whenever a size changes.
            foreach (SplitContainer split in new[] { _mainSplit, _leftSplit, _rightSplit, _bottomSplit })
            {
                split.SizeChanged += (sender, e) => QueueFitLayout();
            }
            _resultsArea.ClientSizeChanged += (sender, e) => QueueFitLayout();
            _summaryRow.SizeChanged += (sender, e) => QueueFitLayout();   // the strip wrapped onto more (or fewer) lines
            _mainSplit.SplitterMoved += OnMainSplitterMoved;

            ResumeLayout(false);
            PerformLayout();
        }

        private ToolStrip BuildToolbar()
        {
            _selectSecondaryButton = new ToolStripButton("Select secondary environment...")
            {
                Name = "selectSecondaryButton",
                DisplayStyle = ToolStripItemDisplayStyle.Text,
                ToolTipText = "Choose the environment the data was migrated TO. The primary - where it came from - is the tool's normal XrmToolBox connection."
            };
            _selectSecondaryButton.Click += OnSelectSecondaryClick;

            _refreshEntitiesButton = new ToolStripButton("Refresh entities")
            {
                Name = "refreshEntitiesButton",
                DisplayStyle = ToolStripItemDisplayStyle.Text,
                ToolTipText = "Reload the primary's entity list and forget its cached metadata (asks for the primary connection when there is none)."
            };
            _refreshEntitiesButton.Click += OnRefreshEntitiesClick;

            _closeButton = new ToolStripButton("Close")
            {
                Name = "closeButton",
                DisplayStyle = ToolStripItemDisplayStyle.Text,
                Alignment = ToolStripItemAlignment.Right
            };
            _closeButton.Click += OnCloseClick;

            _primaryLabel = new ToolStripLabel("Primary: (none)") { Name = "primaryLabel", Alignment = ToolStripItemAlignment.Right };
            _secondaryLabel = new ToolStripLabel("Secondary: (none)") { Name = "secondaryLabel", Alignment = ToolStripItemAlignment.Right };

            var toolbar = new ToolStrip { Name = "toolbar", GripStyle = ToolStripGripStyle.Hidden, Dock = DockStyle.Top };
            // Right-aligned items are laid out from the right edge in the order they are added, so they
            // read "Primary: x | Secondary: y | Close". Items that do not fit go to the overflow menu.
            toolbar.Items.AddRange(new ToolStripItem[]
            {
                _selectSecondaryButton,
                new ToolStripSeparator(),
                _refreshEntitiesButton,
                _closeButton,
                new ToolStripSeparator { Alignment = ToolStripItemAlignment.Right },
                _secondaryLabel,
                new ToolStripSeparator { Alignment = ToolStripItemAlignment.Right },
                _primaryLabel
            });
            return toolbar;
        }

        private Control BuildEntityPanel()
        {
            TableLayoutPanel panel = NewTable(new Padding(6, 6, 2, 2), SizeType.AutoSize, SizeType.AutoSize, SizeType.Percent);
            panel.Name = "entityPanel";

            var title = new Label { Name = "entitiesLabel", Text = "Entities", AutoSize = true, Margin = new Padding(0, 0, 0, 4) };

            _entityFilter = new TextBox { Name = "entityFilter", Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 4) };
            SetCueBanner(_entityFilter, "Filter entities...");
            _entityFilter.TextChanged += OnEntityFilterChanged;
            _toolTip.SetToolTip(_entityFilter, "Filters the entities on display name or logical name as you type.");

            _entityList = new ListView
            {
                Name = "entityList",
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                HideSelection = false,
                MultiSelect = false,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                Margin = Padding.Empty
            };
            _entityList.Columns.Add("Display name", 160);
            _entityList.Columns.Add("Logical name", 110);
            _entityList.SelectedIndexChanged += OnEntitySelectionChanged;

            panel.Controls.Add(title, 0, 0);
            panel.Controls.Add(_entityFilter, 0, 1);
            panel.Controls.Add(_entityList, 0, 2);
            return panel;
        }

        private Control BuildViewPanel()
        {
            TableLayoutPanel panel = NewTable(new Padding(6, 2, 2, 6), SizeType.AutoSize, SizeType.Percent);
            panel.Name = "viewPanel";

            var title = new Label { Name = "viewsLabel", Text = "Views", AutoSize = true, Margin = new Padding(0, 0, 0, 4) };

            _viewList = new ListBox
            {
                Name = "viewList",
                Dock = DockStyle.Fill,
                IntegralHeight = false,
                HorizontalScrollbar = true,
                Margin = Padding.Empty
            };
            _viewList.SelectedIndexChanged += OnViewSelectionChanged;
            _viewList.DoubleClick += OnViewDoubleClick;
            _toolTip.SetToolTip(_viewList,
                "System views first, then your personal views (suffixed \"(personal)\"), each by name. Double-click a view to compare it.");

            panel.Controls.Add(title, 0, 0);
            panel.Controls.Add(_viewList, 0, 1);
            return panel;
        }

        private Control BuildResultsPanel()
        {
            _resultsPanel = NewTable(new Padding(2, 4, 6, 2),
                SizeType.AutoSize,   // compare, cancel, compare options, entity mappings, progress
                SizeType.AutoSize,   // summary strip (wraps on a narrow tool)
                SizeType.AutoSize,   // status filter, text filter, row count
                SizeType.Percent);   // results grid
            _resultsPanel.Name = "resultsPanel";

            // -- action row: Compare, Cancel, Compare options..., Entity mappings..., then the progress text,
            //    which takes the rest of the line ("..." when it does not fit) or, on a narrow tool, a line of its
            //    own (FitActionRow); the buttons wrap on a tool narrower than any XrmToolBox tab --
            _compareButton = NewButton("compareButton", "Compare", OnCompareClick);
            _compareButton.Font = _boldFont;
            _toolTip.SetToolTip(_compareButton,
                "Read every record of the selected view in both environments, match them on their GUID and compare every attribute.");
            _cancelButton = NewButton("cancelButton", "Cancel", OnCancelClick);
            _toolTip.SetToolTip(_cancelButton, "Stop the comparison: the rows already decided are shown.");
            _compareOptionsButton = NewButton("compareOptionsButton", "Compare options...", OnCompareOptionsClick);
            _toolTip.SetToolTip(_compareOptionsButton,
                "Edit the attributes that never make a row Different (audit and owner columns by default) and the prefix filter " +
                "(compare only the columns starting with given prefixes). A result already shown is re-evaluated at once.");
            _entityMappingsButton = NewButton("entityMappingsButton", "Entity mappings...", OnEntityMappingsClick);
            _toolTip.SetToolTip(_entityMappingsButton,
                "Map an entity whose data was migrated into a differently named table of the secondary (and its renamed columns). " +
                "A mapping applies from the next Compare.");
            _progressLabel = new Label
            {
                Name = "progressLabel",
                AutoSize = false,
                AutoEllipsis = true,
                Size = new Size(200, 27),   // the buttons' height, so the text sits level with theirs; FitActionRow sets the width
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(6, 2, 3, 2)
            };

            _actionRow = NewRow("actionRow");
            _actionRow.Controls.AddRange(new Control[] { _compareButton, _cancelButton, _compareOptionsButton, _entityMappingsButton, _progressLabel });

            // -- summary strip: the entity mapping and the prefix filter when there are any, then the counts,
            //    each status on its row colour --
            _mappingLabel = NewSummaryLabel("mappingLabel", string.Empty, Color.Empty);
            _mappingLabel.Font = _boldFont;
            _mappingLabel.Visible = false;
            _prefixLabel = NewSummaryLabel("prefixLabel", string.Empty, Color.Empty);
            _prefixLabel.Visible = false;
            _summaryEmptyLabel = NewSummaryLabel("summaryEmptyLabel", "No comparison yet: pick an entity and a view, then press Compare.", Color.Empty);
            _primaryCountLabel = NewSummaryLabel("primaryCountLabel", "Primary: 0", Color.Empty);
            _toolTip.SetToolTip(_primaryCountLabel, "Records the primary's view returned.");
            _secondaryCountLabel = NewSummaryLabel("secondaryCountLabel", "Secondary: 0", Color.Empty);
            _toolTip.SetToolTip(_secondaryCountLabel, "Records the secondary's view returned.");
            _summarySeparator = NewSummaryLabel("summarySeparator", "|", Color.Empty);
            _missingCountLabel = NewSummaryLabel("missingCountLabel", "Missing: 0", MissingColor);
            _toolTip.SetToolTip(_missingCountLabel, "In the primary, not in the secondary at all.");
            _differentCountLabel = NewSummaryLabel("differentCountLabel", "Different: 0", DifferentColor);
            _toolTip.SetToolTip(_differentCountLabel, "In both, at least one compared column differs.");
            _extraCountLabel = NewSummaryLabel("extraCountLabel", "Extra: 0", ExtraColor);
            _toolTip.SetToolTip(_extraCountLabel, "In the secondary's view, not in the primary at all.");
            _matchingCountLabel = NewSummaryLabel("matchingCountLabel", "Matching: 0", MatchColor);
            _toolTip.SetToolTip(_matchingCountLabel, "In both, every compared column equal.");
            _uncheckedCountLabel = NewSummaryLabel("uncheckedCountLabel", "Not checked: 0", Color.Empty);
            _uncheckedCountLabel.ForeColor = Color.DarkGoldenrod;
            _toolTip.SetToolTip(_uncheckedCountLabel, "The comparison was cancelled: these records were not checked and are not in the grid.");
            _secondaryFailedLabel = NewSummaryLabel("secondaryFailedLabel", "Secondary view failed", Color.Empty);
            _secondaryFailedLabel.ForeColor = Color.Firebrick;

            _summaryRow = NewRow("summaryRow");
            _summaryRow.Controls.AddRange(new Control[]
            {
                _mappingLabel, _prefixLabel, _summaryEmptyLabel, _primaryCountLabel, _secondaryCountLabel, _summarySeparator,
                _missingCountLabel, _differentCountLabel, _extraCountLabel, _matchingCountLabel,
                _uncheckedCountLabel, _secondaryFailedLabel
            });

            // -- filter row: status, text, and at its right how many rows the filters show --
            var statusLabel = NewLabel("statusFilterLabel", "Status:");
            statusLabel.Margin = new Padding(3, 7, 0, 2);
            _statusFilter = new ComboBox
            {
                Name = "statusFilter",
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 110,
                Margin = new Padding(3, 3, 3, 3)
            };
            _statusFilter.Items.AddRange(new object[] { AllStatuses, MissingText, DifferentText, ExtraText, MatchingText });
            _statusFilter.SelectedIndex = 0;
            _statusFilter.SelectedIndexChanged += OnStatusFilterChanged;
            _toolTip.SetToolTip(_statusFilter, "Show only the rows of one status.");
            _rowFilter = new TextBox { Name = "rowFilter", Dock = DockStyle.Fill, Margin = new Padding(3, 3, 3, 3) };
            SetCueBanner(_rowFilter, "Filter rows...");
            _rowFilter.TextChanged += OnRowFilterTextChanged;
            _toolTip.SetToolTip(_rowFilter, "Shows only the rows with this text in one of their visible cells (not case-sensitive).");
            _rowCountLabel = NewLabel("rowCountLabel", "0 of 0 rows");
            _rowCountLabel.Margin = new Padding(6, 7, 3, 2);   // level with the filter's text

            _filterRow = new TableLayoutPanel
            {
                Name = "filterRow",
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 4,
                RowCount = 1,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };
            _filterRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _filterRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _filterRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            _filterRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _filterRow.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _filterRow.Controls.Add(statusLabel, 0, 0);
            _filterRow.Controls.Add(_statusFilter, 1, 0);
            _filterRow.Controls.Add(_rowFilter, 2, 0);
            _filterRow.Controls.Add(_rowCountLabel, 3, 0);

            // -- results grid: bound to a DataTable through a BindingSource whose Filter is the status and
            //    text filter; the rows are coloured by status in CellFormatting (rows stay shared) --
            _bindingSource = new BindingSource(_components);
            _grid = new DataGridView
            {
                Name = "resultsGrid",
                Dock = DockStyle.Fill,
                AutoGenerateColumns = false,
                ReadOnly = true,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
                AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None,
                BackgroundColor = SystemColors.Window,
                Margin = new Padding(3),
                DataSource = _bindingSource
            };
            _grid.DefaultCellStyle.WrapMode = DataGridViewTriState.False;
            _grid.CellFormatting += OnGridCellFormatting;
            _grid.SelectionChanged += OnGridSelectionChanged;
            _grid.DataError += OnGridDataError;

            _resultsPanel.Controls.Add(_actionRow, 0, 0);
            _resultsPanel.Controls.Add(_summaryRow, 0, 1);
            _resultsPanel.Controls.Add(_filterRow, 0, 2);
            _resultsPanel.Controls.Add(_grid, 0, 3);
            return _resultsPanel;
        }

        private Control BuildDetailPanel()
        {
            TableLayoutPanel panel = NewTable(new Padding(2, 2, 6, 2), SizeType.AutoSize, SizeType.Percent);
            panel.Name = "detailPanel";

            // -- header: the selected row (status, id) and Differences only at its right --
            _detailCaption = new Label
            {
                Name = "detailCaption",
                AutoSize = false,
                AutoEllipsis = true,
                Dock = DockStyle.Fill,
                Height = 21,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(3, 3, 3, 2)
            };
            _differencesOnly = NewCheckBox("differencesOnlyCheckBox", "Differences only");
            _differencesOnly.Margin = new Padding(9, 4, 3, 2);
            _differencesOnly.CheckedChanged += OnDifferencesOnlyChanged;
            _toolTip.SetToolTip(_differencesOnly, "Show only the columns whose values differ (ignored and derived columns too, greyed).");

            var header = new TableLayoutPanel
            {
                Name = "detailHeader",
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2,
                RowCount = 1,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            header.Controls.Add(_detailCaption, 0, 0);
            header.Controls.Add(_differencesOnly, 1, 0);

            // -- the selected row's columns: Column / Primary / Secondary --
            _detailGrid = new DataGridView
            {
                Name = "detailGrid",
                Dock = DockStyle.Fill,
                AutoGenerateColumns = false,
                ReadOnly = true,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                BackgroundColor = SystemColors.Window,
                Margin = new Padding(3)
            };
            _detailGrid.DefaultCellStyle.WrapMode = DataGridViewTriState.False;
            _detailGrid.DefaultCellStyle.SelectionBackColor = DetailSelectionColor;
            _detailGrid.DefaultCellStyle.SelectionForeColor = SystemColors.WindowText;
            _detailGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "detailColumn", HeaderText = "Column", FillWeight = 30, SortMode = DataGridViewColumnSortMode.NotSortable });
            _detailGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "detailPrimary", HeaderText = "Primary", FillWeight = 35, SortMode = DataGridViewColumnSortMode.NotSortable });
            _detailGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "detailSecondary", HeaderText = "Secondary", FillWeight = 35, SortMode = DataGridViewColumnSortMode.NotSortable });

            panel.Controls.Add(header, 0, 0);
            panel.Controls.Add(_detailGrid, 0, 1);
            return panel;
        }

        private Control BuildLogPanel()
        {
            TableLayoutPanel panel = NewTable(new Padding(2, 0, 6, 6), SizeType.AutoSize, SizeType.Percent);
            panel.Name = "logPanel";

            _copyLogButton = NewButton("copyLogButton", "Copy log", OnCopyLogClick);
            _saveLogButton = NewButton("saveLogButton", "Save log...", OnSaveLogClick);
            _clearLogButton = NewButton("clearLogButton", "Clear", OnClearLogClick);

            FlowLayoutPanel header = NewRow("logHeader");
            header.Controls.AddRange(new Control[] { NewLabel("logLabel", "Log"), _copyLogButton, _saveLogButton, _clearLogButton });

            _log = new RichTextBox
            {
                Name = "logBox",
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BackColor = Color.White,
                WordWrap = false,
                DetectUrls = false,
                ScrollBars = RichTextBoxScrollBars.Both,
                Font = _logFont,
                Margin = new Padding(3)
            };

            panel.Controls.Add(header, 0, 0);
            panel.Controls.Add(_log, 0, 1);
            return panel;
        }

        // =====================================================================================
        // Fitting the layout to the tool's size
        // =====================================================================================

        /// <summary>
        /// Fits the layout once the current layout pass is over. A size event comes in the middle of a
        /// SplitContainer's own resize, while its panels' layout is suspended: a SplitterDistance set there
        /// resizes the panel but not the controls docked in it. Several events make one fit.
        /// </summary>
        private void QueueFitLayout()
        {
            if (_fitQueued || IsDisposed || !IsHandleCreated) return;   // without a handle: OnLoad fits the layout
            _fitQueued = true;
            BeginInvoke(new Action(() =>
            {
                _fitQueued = false;
                if (!IsDisposed) FitLayout();
            }));
        }

        /// <summary>
        /// Fits the layout to the tool's size (in OnLoad, then whenever a split, the results area or the
        /// summary strip changes size). The entity list keeps its width (300 px, or what the user dragged it
        /// to) while the results side keeps <see cref="ResultsComfortWidth"/>, and gives way down to
        /// <see cref="EntityPanelMinWidth"/> on a narrow tool. The results area gets the height its rows
        /// need at their current width plus <see cref="MinimumGridHeight"/> (the splitter cannot be dragged
        /// above that) while the detail pane and the log keep their minimums; only a tool too small for all
        /// of them scrolls the results area. A SplitContainer refuses a SplitterDistance or panel minimum it
        /// cannot honour (also while it still has its default size), so each step is guarded and simply
        /// retried on the next size change.
        /// </summary>
        private void FitLayout()
        {
            FitEntityPanel();
            FitLeftSplit();
            FitResultsArea();
            FitBottomSplit();
        }

        private void FitEntityPanel()
        {
            int room = _mainSplit.Width - _mainSplit.SplitterWidth;
            if (room < EntityPanelMinWidth + ResultsMinWidth) return;   // not laid out at a real size yet, or far too narrow
            int width = Math.Max(EntityPanelMinWidth, Math.Min(_entityPanelWidth, room - ResultsComfortWidth));
            if (_mainSplitPlaced && _mainSplit.SplitterDistance == width) return;
            if (TrySetSplitter(_mainSplit, width, EntityPanelMinWidth, ResultsMinWidth)) _mainSplitPlaced = true;
        }

        /// <summary>A splitter the user dragged sets the entity list's width from then on (when there is room for it).</summary>
        private void OnMainSplitterMoved(object sender, SplitterEventArgs e)
        {
            if (!_settingSplitter && _mainSplitPlaced) _entityPanelWidth = Math.Max(EntityPanelMinWidth, _mainSplit.SplitterDistance);
        }

        /// <summary>The entity list above the view list: <see cref="EntityListHeightShare"/> at first, each keeping its minimum.</summary>
        private void FitLeftSplit()
        {
            int room = _leftSplit.Height - _leftSplit.SplitterWidth;
            if (room < 50) return;
            int maxDistance = room - ViewPanelMinHeight;
            int entityMin = Math.Min(EntityPanelMinHeight, Math.Max(25, maxDistance));
            int distance = _leftSplitPlaced ? _leftSplit.SplitterDistance : (int)(room * EntityListHeightShare);
            int target = Math.Max(entityMin, Math.Min(distance, maxDistance));
            if (_leftSplitPlaced && target == _leftSplit.SplitterDistance
                && _leftSplit.Panel1MinSize == entityMin && _leftSplit.Panel2MinSize == ViewPanelMinHeight)
            {
                return;
            }
            if (TrySetSplitter(_leftSplit, target, entityMin, ViewPanelMinHeight)) _leftSplitPlaced = true;
        }

        private void FitResultsArea()
        {
            for (int pass = 0; pass < 3; pass++)
            {
                int width = _resultsArea.ClientSize.Width;
                if (width <= 0) return;
                FitActionRow(width - _resultsPanel.Padding.Horizontal - _actionRow.Margin.Horizontal);
                int needed = ResultsHeightNeeded(width);
                if (_resultsArea.AutoScrollMinSize.Height != needed) _resultsArea.AutoScrollMinSize = new Size(0, needed);
                FitRightSplit(needed);
                if (_resultsArea.ClientSize.Width == width) return;   // else a scroll bar came or went: fit the new width
            }
        }

        /// <summary>
        /// Sizes the progress text to the rest of the action row's line at <paramref name="rowWidth"/> (the way the
        /// row's flow places the buttons, wrapping them when they do not fit), or to a line of its own when less
        /// than <see cref="ProgressMinWidth"/> would be left.
        /// </summary>
        private void FitActionRow(int rowWidth)
        {
            if (rowWidth <= 0) return;
            int x = 0;
            foreach (Control button in new Control[] { _compareButton, _cancelButton, _compareOptionsButton, _entityMappingsButton })
            {
                int width = button.Width + button.Margin.Horizontal;
                if (x > 0 && x + width > rowWidth) x = 0;   // the flow starts a new line
                x += width;
            }
            int rest = rowWidth - x - _progressLabel.Margin.Horizontal;
            int target = Math.Max(1, rest >= ProgressMinWidth ? rest : rowWidth - _progressLabel.Margin.Horizontal);
            if (_progressLabel.Width != target) _progressLabel.Width = target;
        }

        /// <summary>
        /// The height the results area needs at <paramref name="areaWidth"/>: each row at its preferred height
        /// for that width (the summary strip wraps) plus <see cref="MinimumGridHeight"/>.
        /// </summary>
        private int ResultsHeightNeeded(int areaWidth)
        {
            int inner = areaWidth - _resultsPanel.Padding.Horizontal;
            int height = _resultsPanel.Padding.Vertical + _grid.Margin.Vertical + MinimumGridHeight;
            foreach (Control row in new Control[] { _actionRow, _summaryRow, _filterRow })
            {
                height += row.GetPreferredSize(new Size(Math.Max(1, inner - row.Margin.Horizontal), 0)).Height + row.Margin.Vertical;
            }
            return height;
        }

        /// <summary>The lower part (detail pane and log) never gets less than both their minimums and the splitter.</summary>
        private int BottomMinHeight => DetailPanelMinHeight + LogPanelMinHeight + _bottomSplit.SplitterWidth;

        /// <summary>
        /// Keeps the results area at least <paramref name="needed"/> high (its panel minimum, so the splitter
        /// cannot be dragged above it either) while the lower part keeps <see cref="BottomMinHeight"/>; the
        /// first time the results area gets <see cref="ResultsHeightShare"/> of the height, or more.
        /// </summary>
        private void FitRightSplit(int needed)
        {
            int room = _rightSplit.Height - _rightSplit.SplitterWidth;
            int bottomMin = BottomMinHeight;
            int maxDistance = room - bottomMin;
            if (maxDistance < 50) return;   // not laid out at a real size yet, or far too short
            int resultsMin = Math.Min(needed, maxDistance);
            int distance = _rightSplitPlaced ? _rightSplit.SplitterDistance : (int)(room * ResultsHeightShare);
            int target = Math.Max(resultsMin, Math.Min(distance, maxDistance));
            if (_rightSplitPlaced && target == _rightSplit.SplitterDistance
                && _rightSplit.Panel1MinSize == resultsMin && _rightSplit.Panel2MinSize == bottomMin)
            {
                return;
            }
            if (TrySetSplitter(_rightSplit, target, resultsMin, bottomMin)) _rightSplitPlaced = true;
        }

        /// <summary>The detail pane above the log: <see cref="DetailHeightShare"/> at first, each keeping its minimum.</summary>
        private void FitBottomSplit()
        {
            int room = _bottomSplit.Height - _bottomSplit.SplitterWidth;
            int maxDistance = room - LogPanelMinHeight;
            if (maxDistance < DetailPanelMinHeight) return;   // not laid out at a real size yet
            int distance = _bottomSplitPlaced ? _bottomSplit.SplitterDistance : (int)(room * DetailHeightShare);
            int target = Math.Max(DetailPanelMinHeight, Math.Min(distance, maxDistance));
            if (_bottomSplitPlaced && target == _bottomSplit.SplitterDistance
                && _bottomSplit.Panel1MinSize == DetailPanelMinHeight && _bottomSplit.Panel2MinSize == LogPanelMinHeight)
            {
                return;
            }
            if (TrySetSplitter(_bottomSplit, target, DetailPanelMinHeight, LogPanelMinHeight)) _bottomSplitPlaced = true;
        }

        private bool TrySetSplitter(SplitContainer split, int distance, int panel1Min, int panel2Min)
        {
            _settingSplitter = true;
            try
            {
                // Relax the minimums first so the new distance is never out of range, then apply them.
                split.Panel1MinSize = 25;
                split.Panel2MinSize = 25;
                split.SplitterDistance = distance;
                split.Panel1MinSize = panel1Min;
                split.Panel2MinSize = panel2Min;
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
            catch (ArgumentException)
            {
                return false;
            }
            finally
            {
                _settingSplitter = false;
            }
        }

        // ---- small factories so every row lines up the same way ----

        private static TableLayoutPanel NewTable(Padding padding, params SizeType[] rows)
        {
            var table = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = rows.Length,
                Padding = padding,
                Margin = Padding.Empty
            };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            foreach (SizeType row in rows)
            {
                table.RowStyles.Add(row == SizeType.Percent ? new RowStyle(SizeType.Percent, 100f) : new RowStyle(row));
            }
            return table;
        }

        private static FlowLayoutPanel NewRow(string name) => new FlowLayoutPanel
        {
            Name = name,
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = true,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };

        private static Button NewButton(string name, string text, EventHandler onClick)
        {
            var button = new Button
            {
                Name = name,
                Text = text,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowOnly,
                MinimumSize = new Size(80, 27),
                Margin = new Padding(3, 2, 3, 2),
                UseVisualStyleBackColor = true
            };
            button.Click += onClick;
            return button;
        }

        private static CheckBox NewCheckBox(string name, string text) => new CheckBox
        {
            Name = name,
            Text = text,
            AutoSize = true,
            Margin = new Padding(3, 7, 9, 2)
        };

        private static Label NewLabel(string name, string text) => new Label
        {
            Name = name,
            Text = text,
            AutoSize = true,
            Margin = new Padding(3, 8, 3, 2)
        };

        /// <summary>A label of the summary strip: on its status colour (none: the panel's), dark text, a little padding.</summary>
        private static Label NewSummaryLabel(string name, string text, Color backColor)
        {
            var label = new Label
            {
                Name = name,
                Text = text,
                AutoSize = true,
                Padding = new Padding(4, 2, 4, 2),
                Margin = new Padding(3, 3, 3, 3)
            };
            if (!backColor.IsEmpty)
            {
                label.BackColor = backColor;
                label.ForeColor = RowTextColor;
            }
            return label;
        }

        // ---- cue banner (grey placeholder text) for the filter boxes ----

        private const int EmSetCueBanner = 0x1501;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        private static void SetCueBanner(TextBox box, string cue)
        {
            // Needs visual styles (XrmToolBox has them); set again whenever the handle is recreated.
            box.HandleCreated += (sender, e) => SendMessage(box.Handle, EmSetCueBanner, (IntPtr)1, cue);
        }
    }
}
