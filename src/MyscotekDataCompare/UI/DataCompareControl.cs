using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.ServiceModel;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using McTools.Xrm.Connection;
using Microsoft.Xrm.Sdk;
using MyscotekDataCompare.Core;
using MyscotekDataCompare.Core.Schema;
using MyscotekDataCompare.Core.Services;
using XrmToolBox.Extensibility;
using XrmToolBox.Extensibility.Interfaces;
using CompareOptions = MyscotekDataCompare.Core.CompareOptions;

namespace MyscotekDataCompare.UI
{
    /// <summary>
    /// The Data Compare tool (SPEC section 6). The PRIMARY - the environment the data was migrated from -
    /// is the tool's normal XrmToolBox connection (<see cref="PluginControlBase.Service"/>); the SECONDARY -
    /// where it was migrated to - is a second connection requested with
    /// <see cref="PluginControlBase.RaiseRequestConnectionEvent"/> as an additional organisation
    /// (<see cref="SecondaryActionName"/>). Pick an entity and one of its views and press Compare:
    /// <see cref="CompareEngine"/> reads every record of the view in both environments and the grid shows
    /// each one coloured by status, the detail pane its columns side by side. Long operations run with
    /// Task.Run + async/await (not WorkAsync) so the log stays visible and live while they run; one
    /// operation at a time, and Cancel stops it. Every action that needs the primary asks XrmToolBox for it
    /// when there is none (<see cref="RequestPrimary"/>). Read-only: nothing is ever written. Control
    /// construction is in DataCompareControl.Layout.cs.
    /// </summary>
    public partial class DataCompareControl : PluginControlBase, IGitHubPlugin, IHelpPlugin
    {
        /// <summary>
        /// The action name of the secondary connection request: XrmToolBox's "additional organisation".
        /// For it the host leaves the tab's own connection (the primary) alone - its status bar connection,
        /// the tab title and highlight, and the connection it records for the tab - and hands the new
        /// connection to <see cref="UpdateConnection"/> with this action name. Any other action name would
        /// make the secondary the tab's connection in XrmToolBox.
        /// </summary>
        internal const string SecondaryActionName = "AdditionalOrganization";

        /// <summary>The Parameter of the secondary request; XrmToolBox passes it back unchanged.</summary>
        internal const string SecondaryParameter = "secondary";

        internal const string DialogTitle = "Data Compare";

        // The public repository: XrmToolBox links the tool to it (IGitHubPlugin) and opens the help page (IHelpPlugin).
        internal const string GitHubUserName = "scottm1985";
        internal const string GitHubRepositoryName = "DataCompare";
        internal const string HelpPageUrl = "https://github.com/scottm1985/DataCompare#readme";

        // Columns of the DataTable behind the results grid: the record id (hidden), the status text, then
        // c0..cN - one per view column. The synthetic names are unique and keep the row filter (and the
        // grid's DataPropertyName) free of escaping problems with alias.attribute names; the grid headers
        // show the attribute display names (the raw view column name is the header's tooltip).
        internal const string IdColumn = "__id";
        internal const string StatusColumn = "__status";
        private const string ValueColumnPrefix = "c";

        // A hidden column of the DataTable (never a grid column, never searched by the text filter): the row's
        // DifferingAttributes as "|name1|name2|" ("" when none), so the differing-column filter is one LIKE.
        internal const string DifferingColumn = "__differing";
        private const char DifferingSeparator = '|';

        /// <summary>The differing-column filter's first item: no filter.</summary>
        internal const string AnyColumn = "(any column)";

        /// <summary>Between a differing column's name and its row count in the filter: a middle dot.</summary>
        internal static readonly string CountSeparator = " " + (char)0x00B7 + " ";

        // The status texts (grid, status filter, detail caption) and the status filter's "everything".
        internal const string MissingText = "Missing";
        internal const string DifferentText = "Different";
        internal const string ExtraText = "Extra";
        internal const string MatchingText = "Matching";
        internal const string AllStatuses = "All";

        // The row colours (SPEC 1), dark text on all four; a selected row takes a darker shade of its colour.
        internal static readonly Color MissingColor = ColorTranslator.FromHtml("#F8D7DA");
        internal static readonly Color DifferentColor = ColorTranslator.FromHtml("#FFF3CD");
        internal static readonly Color MatchColor = ColorTranslator.FromHtml("#D4EDDA");
        internal static readonly Color ExtraColor = ColorTranslator.FromHtml("#CCE5FF");
        internal static readonly Color MissingSelectionColor = ColorTranslator.FromHtml("#F1AEB5");
        internal static readonly Color DifferentSelectionColor = ColorTranslator.FromHtml("#FFE69C");
        internal static readonly Color MatchSelectionColor = ColorTranslator.FromHtml("#A3CFBB");
        internal static readonly Color ExtraSelectionColor = ColorTranslator.FromHtml("#9EC5FE");
        internal static readonly Color RowTextColor = Color.Black;

        /// <summary>The selected line of the detail pane (neutral grey: the row colours mean statuses).</summary>
        internal static readonly Color DetailSelectionColor = ColorTranslator.FromHtml("#E2E3E5");

        internal const string NoPrimaryMessage =
            "Not connected: choose the PRIMARY environment (the one the data was migrated from) with the XrmToolBox connection bar (or press Refresh entities) and its entities are listed here.";

        internal const string NoSelectionCaption = "Select a row to see its columns in both environments.";

        private readonly IContainer _components = new Container();
        private readonly Action<DataCompareSettings> _saveSettings;
        private readonly bool _mirrorToXrmToolBoxLog;
        private readonly UiLogger _logger;
        private DataCompareSettings _settings;
        private bool _applyingSettings;

        /// <summary>Primary metadata, cached for the lifetime of the primary connection: grid headers and the comparison.</summary>
        private DataverseSchemaProvider _primarySchema;

        // ---- secondary connection (the primary is Service / ConnectionDetail) ----
        private IOrganizationService _secondaryService;
        private ConnectionDetail _secondaryDetail;

        /// <summary>Secondary metadata, cached for the lifetime of the secondary connection: mapped entities only (SPEC 5.9).</summary>
        private DataverseSchemaProvider _secondarySchema;

        /// <summary>The secondary's entities for the Entity mappings dialog, listed once per secondary connection; null until then.</summary>
        private IList<EntityInfo> _secondaryEntities;

        /// <summary>Bumped when the primary changes or is refreshed: results of older operations are dropped.</summary>
        private int _generation;

        // ---- entities and views ----
        private IList<EntityInfo> _entities = new List<EntityInfo>();
        private EntityInfo _currentEntity;
        private bool _suppressEntitySelection;

        // ---- the result shown ----
        private CompareResult _result;
        private ViewInfo _resultView;
        private IList<ViewColumn> _resultColumns = new List<ViewColumn>();
        private Dictionary<Guid, RowComparison> _rowsById = new Dictionary<Guid, RowComparison>();
        private DataTable _table;
        private RowComparison _detailRow;
        private IList<ColumnComparison> _detailLines = new List<ColumnComparison>();   // _detailRow's lines, before the filters

        // ---- the one long operation that may run at a time ----
        private CancellationTokenSource _operation;
        private bool _reloadEntitiesWhenIdle;
        private int _compareRun;

        /// <summary>Created by XrmToolBox (<see cref="MyscotekDataComparePlugin.GetControl"/>).</summary>
        public DataCompareControl()
            : this(DataCompareSettings.LoadFromXrmToolBox, DataCompareSettings.SaveToXrmToolBox, mirrorToXrmToolBoxLog: true)
        {
        }

        /// <summary>
        /// Test seam: where the settings come from and go to, and whether log lines are mirrored to
        /// XrmToolBox's own log file. The UI tests pass in-memory settings and no mirroring, so they touch
        /// nothing outside the test run. A settings store that fails to load is not fatal: the defaults are
        /// used and a warning is logged.
        /// </summary>
        internal DataCompareControl(Func<DataCompareSettings> loadSettings, Action<DataCompareSettings> saveSettings, bool mirrorToXrmToolBoxLog)
        {
            _saveSettings = saveSettings;
            _mirrorToXrmToolBoxLog = mirrorToXrmToolBoxLog;
            ShowMessage = ShowMessageBox;
            ShowCompareOptionsDialog = form => form.ShowDialog(this);
            ShowEntityMappingsDialog = form => form.ShowDialog(this);

            // A design size so the docked layout is computed before XrmToolBox docks the control.
            Size = new Size(1100, 750);
            Dock = DockStyle.Fill;
            Font = new Font("Segoe UI", 9f);
            BuildUi();

            _logger = new UiLogger(_log, MirrorEngineLine);
            _settings = LoadSettings(loadSettings);
            ApplySettingsToControls();
            ClearResult();
            UpdateControlStates();
        }

        /// <summary>
        /// Shows a message box owned by the tool: (text, buttons, icon, default button) => result. A seam
        /// for the UI tests, which replace it so no modal dialog can block them.
        /// </summary>
        internal Func<string, MessageBoxButtons, MessageBoxIcon, MessageBoxDefaultButton, DialogResult> ShowMessage { get; set; }

        /// <summary>
        /// Shows the Compare options dialog modally and returns its result (ShowDialog owned by the tool).
        /// A seam for the UI tests, which drive the dialog without a modal loop.
        /// </summary>
        internal Func<CompareOptionsForm, DialogResult> ShowCompareOptionsDialog { get; set; }

        /// <summary>Shows the Entity mappings dialog modally and returns its result: a seam for the UI tests, like the one above.</summary>
        internal Func<EntityMappingsForm, DialogResult> ShowEntityMappingsDialog { get; set; }

        // ---- read-only views of the state, for the UI tests ----
        internal UiLogger Logger => _logger;
        internal DataCompareSettings Settings => _settings;
        internal CompareResult Result => _result;
        internal DataTable ResultTable => _table;
        internal bool IsBusy => _operation != null;
        internal ToolTip ToolTips => _toolTip;

        // ---- IGitHubPlugin, IHelpPlugin ----
        public string UserName => GitHubUserName;
        public string RepositoryName => GitHubRepositoryName;
        public string HelpUrl => HelpPageUrl;

        // =====================================================================================
        // Host integration: connections, closing, disposal
        // =====================================================================================

        /// <summary>
        /// Called by XrmToolBox when a connection is applied to the tool. The secondary comes back as an
        /// additional organisation (<see cref="SecondaryActionName"/>, with <see cref="SecondaryParameter"/>);
        /// anything else is the primary: a plain connection (actionName "") or one an action asked for
        /// through <see cref="RequestPrimary"/> (actionName = the name of that action's method).
        /// </summary>
        public override void UpdateConnection(IOrganizationService newService, ConnectionDetail detail, string actionName, object parameter)
        {
            if (InvokeRequired)
            {
                Invoke(new Action(() => UpdateConnection(newService, detail, actionName, parameter)));
                return;
            }

            if (string.Equals(actionName, SecondaryActionName, StringComparison.Ordinal))
            {
                // Base is deliberately NOT called: it would replace Service, which must stay the primary (and
                // then look for a method named AdditionalOrganization). The tool asks for no other additional
                // organisation, so the parameter is not checked.
                SetSecondary(newService, detail);
                return;
            }

            // Base comes first: it sets Service and ConnectionDetail and then, for a requested connection,
            // invokes the named method by reflection - so the action runs once, against the new primary.
            // ("" is a plain connection; null would make base look for a nameless method.)
            string action = actionName ?? string.Empty;
            // Refresh entities resets the tool and loads the new primary's entities itself (it does nothing
            // while an operation runs - never the case without a primary - so check that too).
            bool actionLoadsEntities = string.Equals(action, nameof(RefreshEntities), StringComparison.Ordinal) && !IsBusy;
            base.UpdateConnection(newService, detail, action, parameter);
            OnPrimaryChanged(resetAndLoad: !actionLoadsEntities);
        }

        public override void ClosingPlugin(PluginCloseInfo info)
        {
            SaveSettings();
            base.ClosingPlugin(info);
            if (!info.Cancel) _operation?.Cancel();   // a running comparison stops at the next page
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            FitLayout();
            if (Service == null) _logger.Write(LogLevel.Info, NoPrimaryMessage);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _operation?.Cancel();   // a running operation stops; its continuation sees IsDisposed
            base.Dispose(disposing);
            if (disposing)
            {
                // After base: the controls using these (grids, buttons, log box) are disposed by then.
                _components.Dispose();
                _table?.Dispose();
                _boldFont?.Dispose();
                _logFont?.Dispose();
            }
        }

        private void SetSecondary(IOrganizationService service, ConnectionDetail detail)
        {
            _secondaryService = service;
            _secondaryDetail = service != null ? detail : null;
            _secondarySchema = null;      // another environment: its metadata and entities are read again when needed
            _secondaryEntities = null;
            RefreshConnectionLabels();

            if (service == null)
            {
                _logger.Write(LogLevel.Warning, "Secondary environment cleared.");
            }
            else
            {
                _logger.Write(LogLevel.Info, "Secondary environment: " + DescribeConnection(detail, service));
                WarnIfSameOrganization();
            }
            UpdateControlStates();
        }

        /// <summary>
        /// A new primary: labels, log line and same-organisation check; with <paramref name="resetAndLoad"/>
        /// also forgets everything of the previous primary and loads the new primary's entities (without it
        /// the requested action - Refresh entities - has just done so).
        /// </summary>
        private void OnPrimaryChanged(bool resetAndLoad)
        {
            if (resetAndLoad)
            {
                _generation++;
                _operation?.Cancel();   // whatever is running belongs to the previous primary
                _primarySchema = Service != null ? new DataverseSchemaProvider(Service) : null;
                _entities = new List<EntityInfo>();
                _currentEntity = null;
                PopulateEntityList();
                ClearViews();
                ClearResult();
            }
            RefreshConnectionLabels();

            if (Service == null)
            {
                _logger.Write(LogLevel.Info, NoPrimaryMessage);
                UpdateControlStates();
                return;
            }

            _logger.Write(LogLevel.Info, "Primary environment: " + DescribeConnection(ConnectionDetail, Service));
            WarnIfSameOrganization();
            if (resetAndLoad) StartEntityLoad();
            UpdateControlStates();
        }

        private void RefreshConnectionLabels()
        {
            _primaryLabel.Text = "Primary: " + ConnectionName(ConnectionDetail, Service);
            _secondaryLabel.Text = "Secondary: " + ConnectionName(_secondaryDetail, _secondaryService);
        }

        private void WarnIfSameOrganization()
        {
            if (Service != null && _secondaryService != null && IsSameOrganization())
                _logger.Write(LogLevel.Warning, "The secondary looks like the same organisation as the primary: you will be asked to confirm before comparing.");
        }

        private bool IsSameOrganization()
        {
            if (Service != null && ReferenceEquals(Service, _secondaryService)) return true;
            return ConnectionDetail != null && _secondaryDetail != null && SameOrganization(ConnectionDetail, _secondaryDetail);
        }

        /// <summary>The same organisation: the same organisation name, or the same (web or service) URL.</summary>
        internal static bool SameOrganization(ConnectionDetail a, ConnectionDetail b)
        {
            if (a == null || b == null) return false;
            if (!string.IsNullOrEmpty(a.Organization) && !string.IsNullOrEmpty(b.Organization)
                && string.Equals(a.Organization, b.Organization, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string ua = NormalizeUrl(a.WebApplicationUrl) ?? NormalizeUrl(a.OrganizationServiceUrl);
            string ub = NormalizeUrl(b.WebApplicationUrl) ?? NormalizeUrl(b.OrganizationServiceUrl);
            return !string.IsNullOrEmpty(ua) && string.Equals(ua, ub, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            string trimmed = url.Trim().TrimEnd('/');
            return trimmed.Length == 0 ? null : trimmed;
        }

        private static string ConnectionName(ConnectionDetail detail, IOrganizationService service)
        {
            if (service == null) return "(none)";
            string name = detail?.ConnectionName;
            return string.IsNullOrWhiteSpace(name) ? "(unnamed connection)" : name;
        }

        /// <summary>Connection name plus its URL when known, for log lines.</summary>
        private static string DescribeConnection(ConnectionDetail detail, IOrganizationService service)
        {
            string name = ConnectionName(detail, service);
            string url = NormalizeUrl(detail?.WebApplicationUrl) ?? NormalizeUrl(detail?.OrganizationServiceUrl);
            return url == null ? name : name + " (" + url + ")";
        }

        /// <summary>How the primary organisation keys the last entity in the settings: its URL (or name).</summary>
        private string PrimaryOrganizationKey() =>
            DataCompareSettings.OrganizationKey(NormalizeUrl(ConnectionDetail?.WebApplicationUrl)
                                                ?? NormalizeUrl(ConnectionDetail?.OrganizationServiceUrl)
                                                ?? ConnectionDetail?.Organization);

        // =====================================================================================
        // Settings
        // =====================================================================================

        private DataCompareSettings LoadSettings(Func<DataCompareSettings> load)
        {
            try
            {
                return load?.Invoke() ?? new DataCompareSettings();
            }
            catch (Exception ex)
            {
                _logger.Write(LogLevel.Warning, "Settings could not be loaded, the defaults are used: " + ErrorText(ex));
                return new DataCompareSettings();
            }
        }

        private void SaveSettings()
        {
            try
            {
                _saveSettings?.Invoke(_settings);
            }
            catch (Exception ex)
            {
                _logger.Write(LogLevel.Warning, "Settings could not be saved: " + ErrorText(ex));
            }
        }

        private void ApplySettingsToControls()
        {
            _applyingSettings = true;
            try
            {
                _differencesOnly.Checked = _settings.DifferencesOnly;
            }
            finally
            {
                _applyingSettings = false;
            }
        }

        private void OnDifferencesOnlyChanged(object sender, EventArgs e)
        {
            if (_applyingSettings) return;
            _settings.DifferencesOnly = _differencesOnly.Checked;
            SaveSettings();
            Guard("Showing the row's columns", FillDetailGrid);
        }

        // =====================================================================================
        // Actions that need the primary connection
        // =====================================================================================

        /// <summary>
        /// Runs <paramref name="action"/> through <see cref="PluginControlBase.ExecuteMethod(Action)"/>: at once
        /// while the primary is connected; without one XrmToolBox shows its connection dialog (the tool
        /// library's rule for a control that needs a connection) and, once the user has connected, calls
        /// <see cref="UpdateConnection"/> with the action's method NAME, which the base class invokes by
        /// reflection (instance, public or not, no parameters). So every action passed here is a
        /// parameterless instance method with a unique name - never a lambda, which ExecuteMethod refuses -
        /// and guards itself: an exception in it would reach XrmToolBox from UpdateConnection.
        /// </summary>
        private void RequestPrimary(string what, Action action)
        {
            try
            {
                ExecuteMethod(action);
            }
            catch (Exception ex)
            {
                ReportError(what + " failed", ex);
            }
        }

        // =====================================================================================
        // Long operations
        // =====================================================================================

        /// <summary>
        /// Runs a long operation for an event handler: this async void never lets an exception escape.
        /// Failures are logged and shown in a message box; a cancellation is only logged.
        /// </summary>
        private async void RunGuarded(string action, Func<Task> work)
        {
            try
            {
                await work();
            }
            catch (OperationCanceledException)
            {
                if (!IsDisposed) _logger.Write(LogLevel.Warning, action + " cancelled.");
            }
            catch (Exception ex)
            {
                ReportError(action + " failed", ex);
            }
        }

        /// <summary>Runs a synchronous handler's work; a failure is logged and shown instead of reaching XrmToolBox.</summary>
        private void Guard(string action, Action work)
        {
            try
            {
                work();
            }
            catch (Exception ex)
            {
                ReportError(action + " failed", ex);
            }
        }

        private void ReportError(string what, Exception ex)
        {
            if (IsDisposed) return;
            _logger.Log(LogLevel.Error, what + ": " + ErrorText(ex));   // Log: mirrored to XrmToolBox's log
            try
            {
                ShowMessage(ex.Message, MessageBoxButtons.OK, MessageBoxIcon.Error, MessageBoxDefaultButton.Button1);
            }
            catch (Exception)
            {
                // The tab may be closing; the error is in the log.
            }
        }

        /// <summary>Marks the tool busy (inputs disabled, Cancel enabled) and returns the operation's token.</summary>
        private CancellationToken BeginOperation(string status)
        {
            if (_operation != null) throw new InvalidOperationException("Another operation is still running.");
            _operation = new CancellationTokenSource();
            SetProgress(status);
            UpdateControlStates();
            return _operation.Token;
        }

        private void EndOperation()
        {
            CancellationTokenSource operation = _operation;
            _operation = null;
            operation?.Dispose();
            if (IsDisposed) return;
            // Whatever the outcome (a failure too), the running-status text no longer applies; the
            // comparison then shows its final counts.
            SetProgress(string.Empty);
            UpdateControlStates();
            if (_reloadEntitiesWhenIdle)
            {
                _reloadEntitiesWhenIdle = false;
                StartEntityLoad();
            }
        }

        private void OnCancelClick(object sender, EventArgs e)
        {
            if (_operation == null) return;
            _operation.Cancel();
            SetProgress("Cancelling...");
        }

        private void SetProgress(string text) => _progressLabel.Text = text ?? string.Empty;

        /// <summary>Enables the inputs that make sense now (SPEC 6.3: busy disables them and enables Cancel).</summary>
        private void UpdateControlStates()
        {
            bool busy = IsBusy;
            bool connected = Service != null;

            _selectSecondaryButton.Enabled = !busy;
            _refreshEntitiesButton.Enabled = !busy;
            _compareOptionsButton.Enabled = !busy;
            _entityMappingsButton.Enabled = !busy;
            _entityList.Enabled = !busy;
            _viewList.Enabled = !busy && _viewList.Items.Count > 0;
            _compareButton.Enabled = !busy && connected && _secondaryService != null && _currentEntity != null && SelectedView != null;
            _cancelButton.Enabled = busy;
            Cursor = busy ? Cursors.AppStarting : Cursors.Default;
        }

        // =====================================================================================
        // Entities
        // =====================================================================================

        private void OnRefreshEntitiesClick(object sender, EventArgs e) => RequestPrimary("Refreshing entities", RefreshEntities);

        /// <summary>Refresh entities: runs through <see cref="RequestPrimary"/> (and <see cref="UpdateConnection"/> knows it loads the entities).</summary>
        private void RefreshEntities() => Guard("Refreshing entities", ReloadEntities);

        private void ReloadEntities()
        {
            if (IsBusy) return;
            if (Service == null)
            {
                _logger.Write(LogLevel.Warning, NoPrimaryMessage);
                return;
            }

            _generation++;
            // "Refresh" also forgets the cached primary metadata, so schema changes are picked up.
            _primarySchema = new DataverseSchemaProvider(Service);
            _entities = new List<EntityInfo>();
            _currentEntity = null;
            PopulateEntityList();
            ClearViews();
            ClearResult();
            UpdateControlStates();
            StartEntityLoad();
        }

        private void StartEntityLoad()
        {
            if (IsBusy)
            {
                _reloadEntitiesWhenIdle = true;   // after the (cancelled) running operation has ended
                return;
            }
            RunGuarded("Loading entities", LoadEntitiesAsync);
        }

        private async Task LoadEntitiesAsync()
        {
            IOrganizationService service = Service;
            if (IsBusy) return;
            if (service == null)
            {
                _logger.Write(LogLevel.Warning, NoPrimaryMessage);
                return;
            }

            int generation = _generation;
            CancellationToken token = BeginOperation("Loading entities...");
            IList<EntityInfo> entities;
            bool cancelled;
            try
            {
                entities = await Task.Run(() => EntityCatalog.GetEntities(service));
                cancelled = token.IsCancellationRequested;
            }
            finally
            {
                EndOperation();
            }

            if (generation != _generation || IsDisposed) return;   // the primary changed meanwhile
            SetProgress(string.Empty);
            if (cancelled) throw new OperationCanceledException("Loading entities was cancelled.");

            _entities = entities;
            PopulateEntityList();
            _logger.Write(LogLevel.Info, Plural(entities.Count, "entity", "entities") + " loaded from the primary.");
            UpdateControlStates();
            ReselectEntity(_settings.GetLastEntity(PrimaryOrganizationKey()));
        }

        private void ReselectEntity(string logicalName)
        {
            if (string.IsNullOrWhiteSpace(logicalName)) return;
            EntityInfo entity = _entities.FirstOrDefault(x => string.Equals(x.LogicalName, logicalName, StringComparison.OrdinalIgnoreCase));
            if (entity == null) return;

            _suppressEntitySelection = true;
            try
            {
                foreach (ListViewItem item in _entityList.Items)
                {
                    item.Selected = ReferenceEquals(item.Tag, entity);
                    if (item.Selected) item.EnsureVisible();
                }
            }
            finally
            {
                _suppressEntitySelection = false;
            }
            RunGuarded("Loading views", () => LoadViewsAsync(entity));
        }

        private void OnEntityFilterChanged(object sender, EventArgs e) => Guard("Filtering entities", PopulateEntityList);

        /// <summary>Fills the list with the entities matching the filter (display or logical name), keeping the selection.</summary>
        private void PopulateEntityList()
        {
            string filter = _entityFilter.Text.Trim();
            _suppressEntitySelection = true;
            _entityList.BeginUpdate();
            try
            {
                _entityList.Items.Clear();
                var items = new List<ListViewItem>();
                foreach (EntityInfo entity in _entities)
                {
                    if (filter.Length > 0 && !ContainsText(entity.DisplayName, filter) && !ContainsText(entity.LogicalName, filter)) continue;
                    items.Add(new ListViewItem(new[] { entity.DisplayName ?? entity.LogicalName, entity.LogicalName })
                    {
                        Name = entity.LogicalName,
                        Tag = entity
                    });
                }
                _entityList.Items.AddRange(items.ToArray());

                ListViewItem current = _currentEntity == null
                    ? null
                    : items.FirstOrDefault(i => string.Equals(((EntityInfo)i.Tag).LogicalName, _currentEntity.LogicalName, StringComparison.OrdinalIgnoreCase));
                if (current != null)
                {
                    current.Selected = true;
                    current.EnsureVisible();
                }
            }
            finally
            {
                _entityList.EndUpdate();
                _suppressEntitySelection = false;
            }
        }

        private void OnEntitySelectionChanged(object sender, EventArgs e)
        {
            if (_suppressEntitySelection || IsBusy || _entityList.SelectedItems.Count != 1) return;
            RequestPrimary("Loading views", LoadSelectedEntityViews);
        }

        /// <summary>Loads the views of the entity selected in the list. Runs through <see cref="RequestPrimary"/>.</summary>
        private void LoadSelectedEntityViews()
        {
            if (_suppressEntitySelection || IsBusy) return;
            if (_entityList.SelectedItems.Count != 1 || !(_entityList.SelectedItems[0].Tag is EntityInfo entity)) return;
            if (_currentEntity != null && string.Equals(_currentEntity.LogicalName, entity.LogicalName, StringComparison.OrdinalIgnoreCase)) return;
            RunGuarded("Loading views", () => LoadViewsAsync(entity));
        }

        /// <summary>Clears the entity selection so that clicking the entity again retries.</summary>
        private void ForgetEntity()
        {
            _currentEntity = null;
            _suppressEntitySelection = true;
            try
            {
                foreach (ListViewItem item in _entityList.SelectedItems.Cast<ListViewItem>().ToList()) item.Selected = false;
            }
            finally
            {
                _suppressEntitySelection = false;
            }
            UpdateSummary();   // no entity: no mapping indicator
            UpdateControlStates();
        }

        // =====================================================================================
        // Views
        // =====================================================================================

        /// <summary>
        /// Makes the entity current (the result is cleared) and loads its views: the system views, then the
        /// personal views - always both, each sorted by name; "(All records)" when it has none.
        /// </summary>
        private async Task LoadViewsAsync(EntityInfo entity)
        {
            IOrganizationService service = Service;
            if (IsBusy || service == null || entity == null) return;

            int generation = _generation;
            _currentEntity = entity;
            _settings.SetLastEntity(PrimaryOrganizationKey(), entity.LogicalName);
            SaveSettings();
            ClearViews();
            ClearResult();

            CancellationToken token = BeginOperation($"Loading the views of {entity.LogicalName}...");
            IList<ViewInfo> views;
            bool loaded = false;
            try
            {
                views = await Task.Run(() => ViewService.GetViews(service, entity.LogicalName, includePersonal: true, _logger));
                loaded = !token.IsCancellationRequested;
            }
            finally
            {
                EndOperation();
                if (!loaded && generation == _generation && !IsDisposed && ReferenceEquals(_currentEntity, entity))
                    ForgetEntity();
            }

            if (generation != _generation || IsDisposed || !ReferenceEquals(_currentEntity, entity)) return;
            SetProgress(string.Empty);
            if (!loaded) throw new OperationCanceledException("Loading views was cancelled.");

            if (views.Count == 0)
            {
                views = new List<ViewInfo> { ViewService.CreateAllRecordsView(entity.LogicalName, entity.PrimaryIdAttribute, entity.PrimaryNameAttribute) };
                _logger.Write(LogLevel.Info, $"{entity.LogicalName} has no usable views: {ViewService.AllRecordsViewName} is used.");
            }
            PopulateViews(views);
            UpdateControlStates();
        }

        private void PopulateViews(IList<ViewInfo> views)
        {
            _viewList.BeginUpdate();
            try
            {
                _viewList.Items.Clear();
                foreach (ViewInfo view in views) _viewList.Items.Add(view);
                _viewList.SelectedIndex = views.Count > 0 ? 0 : -1;
            }
            finally
            {
                _viewList.EndUpdate();
            }
        }

        private void ClearViews() => _viewList.Items.Clear();

        private ViewInfo SelectedView => _viewList.SelectedItem as ViewInfo;

        private void OnViewSelectionChanged(object sender, EventArgs e) => Guard("Selecting the view", () =>
        {
            // A result belongs to one view: another view clears it.
            if (_result != null && !SameView(SelectedView, _resultView)) ClearResult();
            UpdateControlStates();
        });

        private void OnViewDoubleClick(object sender, EventArgs e)
        {
            if (IsBusy || SelectedView == null) return;
            RequestPrimary("Compare", CompareSelectedView);
        }

        /// <summary>Same saved view: the same id and kind (and name, for the synthesised view), not necessarily the same instance.</summary>
        private static bool SameView(ViewInfo a, ViewInfo b)
        {
            if (a == null || b == null) return false;
            if (ReferenceEquals(a, b)) return true;
            return a.Id == b.Id && a.IsPersonal == b.IsPersonal
                   && (a.Id != Guid.Empty || string.Equals(a.Name, b.Name, StringComparison.Ordinal));
        }

        /// <summary>The grid columns of a view: its layout's cells; the primary name when the layout has none.</summary>
        internal static IList<ViewColumn> ViewColumns(ViewInfo view, string primaryNameAttribute)
        {
            IList<ViewColumn> columns = LayoutParser.Parse(view?.LayoutXml);
            if (columns.Count == 0 && !string.IsNullOrEmpty(primaryNameAttribute))
                columns = new List<ViewColumn> { new ViewColumn { Name = primaryNameAttribute, Width = 300 } };
            return columns;
        }

        // =====================================================================================
        // Compare
        // =====================================================================================

        private void OnSelectSecondaryClick(object sender, EventArgs e)
        {
            try
            {
                // XrmToolBox shows its connection selector and hands the choice to UpdateConnection as an
                // additional organisation: the tab keeps the primary as its connection (and its title).
                RaiseRequestConnectionEvent(new RequestConnectionEventArgs
                {
                    ActionName = SecondaryActionName,
                    Parameter = SecondaryParameter,
                    Control = this
                });
            }
            catch (Exception ex)
            {
                ReportError("Selecting the secondary failed", ex);
            }
        }

        private void OnCompareClick(object sender, EventArgs e) => RequestPrimary("Compare", CompareSelectedView);

        /// <summary>Compare: runs through <see cref="RequestPrimary"/>.</summary>
        private void CompareSelectedView() => RunGuarded("Compare", CompareAsync);

        /// <summary>
        /// Compares the selected view (SPEC 6.3): checks there is something to compare and asks before
        /// comparing an organisation with itself; then, on Task.Run, resolves the grid headers, runs
        /// <see cref="CompareEngine.Compare"/> with the progress marshalled to the progress label, and builds
        /// the grid's table. A cancelled run is shown like a complete one (its partial rows); a failure
        /// clears the result, is logged and shown.
        /// </summary>
        private async Task CompareAsync()
        {
            if (IsBusy) return;
            IOrganizationService primary = Service;
            IOrganizationService secondary = _secondaryService;
            EntityInfo entity = _currentEntity;
            ViewInfo view = SelectedView;

            string problem = primary == null ? "Connect to the primary environment first (XrmToolBox connection bar)."
                : secondary == null ? "Select the secondary environment first (toolbar: Select secondary environment...)."
                : entity == null ? "Select an entity first."
                : view == null ? "Select a view first."
                : null;
            if (problem != null)
            {
                ShowMessage(problem, MessageBoxButtons.OK, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button1);
                return;
            }

            string primaryName = DescribeConnection(ConnectionDetail, primary);
            string secondaryName = DescribeConnection(_secondaryDetail, secondary);
            CompareOptions options = _settings.BuildOptions();
            EntityMapping mapping = options.FindMapping(entity.LogicalName);
            bool sameTable = mapping == null
                             || string.Equals(EntityMapping.Normalize(mapping.SecondaryEntity), EntityMapping.Normalize(entity.LogicalName), StringComparison.Ordinal);
            if (IsSameOrganization() && sameTable)
            {
                DialogResult answer = ShowMessage(
                    "The secondary environment appears to be the SAME organisation as the primary:\n\n" +
                    "Primary: " + primaryName + "\nSecondary: " + secondaryName + "\n\n" +
                    "Comparing an environment with itself shows every record as matching. Continue anyway?",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                if (answer != DialogResult.Yes)
                {
                    _logger.Write(LogLevel.Info, "Compare not started: same organisation not confirmed.");
                    return;
                }
            }
            else if (IsSameOrganization())
            {
                // A table mapped to another table of the same environment is a real comparison: no question.
                _logger.Write(LogLevel.Info, $"Same organisation on both sides: {entity.LogicalName} is compared with its mapped table " +
                                             $"{EntityMapping.Normalize(mapping.SecondaryEntity)}.");
            }

            DataverseSchemaProvider schema = PrimarySchema(primary);
            // The secondary's metadata is read only for a mapped entity (its table's key and columns).
            var engine = new CompareEngine(primary, secondary, schema, SecondarySchema(secondary), options, _logger);
            IList<ViewColumn> columns = ViewColumns(view, entity.PrimaryNameAttribute);
            ClearResult();

            WriteRunLine(LogLevel.Info, new string('=', 60));
            WriteRunLine(LogLevel.Info, $"Comparing view \"{view.DisplayName}\" of {entity.LogicalName} between {primaryName} (primary) and {secondaryName} (secondary)" +
                                        (mapping != null ? $", mapped to {EntityMapping.Normalize(mapping.SecondaryEntity)} in the secondary" : string.Empty));

            int generation = _generation;
            int run = ++_compareRun;
            var progress = new Progress<CompareProgress>(p => OnCompareProgress(run, p));
            CancellationToken token = BeginOperation("Starting the comparison...");
            Shown shown = null;
            Exception failure = null;
            bool cancelledEarly = false;
            try
            {
                shown = await Task.Run(() =>
                {
                    // Headers first (metadata of the linked entities too): off the UI thread, like the run.
                    IList<string> headers = ResolveHeaders(view.FetchXml, entity.LogicalName, columns, schema);
                    CompareResult result = engine.Compare(entity.LogicalName, view.FetchXml, progress, token);
                    return Shown.Build(result, columns, headers);
                });
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                cancelledEarly = true;   // cancelled before the engine had anything to show
            }
            catch (Exception ex)
            {
                failure = ex;   // the engine has logged "Compare failed: ..."
            }
            finally
            {
                _compareRun++;   // late progress reports of this run are ignored from now on
                EndOperation();
            }

            if (IsDisposed || generation != _generation)
            {
                shown?.Table.Dispose();
                return;
            }
            if (cancelledEarly)
            {
                _logger.Write(LogLevel.Warning, "Compare cancelled before any record was compared.");
                SetProgress("Cancelled");
                return;
            }
            if (failure != null)
            {
                _logger.Log(LogLevel.Error, "Compare stopped: " + ErrorText(failure));
                SetProgress("Compare failed");
                ShowMessage(failure.Message, MessageBoxButtons.OK, MessageBoxIcon.Error, MessageBoxDefaultButton.Button1);
                return;
            }

            ShowResult(view, shown);
            SetProgress(FinalProgressText(shown.Result.Summary));
        }

        private void OnCompareProgress(int run, CompareProgress progress)
        {
            if (run != _compareRun || IsDisposed || progress == null) return;
            SetProgress(progress.Message ?? progress.Phase.ToString());
        }

        /// <summary>The progress label after a run: the outcome and the counts, e.g. "Done: 120 rows - missing 3, ...".</summary>
        internal static string FinalProgressText(CompareSummary summary)
        {
            if (summary == null) return string.Empty;
            var text = new StringBuilder(summary.Cancelled ? "Cancelled (partial result)" : "Done");
            text.Append(": ").Append(Plural(summary.Total, "row"))
                .Append(" - missing ").Append(Number(summary.Missing))
                .Append(", different ").Append(Number(summary.Different))
                .Append(", extra ").Append(Number(summary.Extra))
                .Append(", matching ").Append(Number(summary.Matching));
            if (summary.Unchecked > 0) text.Append(", not checked ").Append(Number(summary.Unchecked));
            if (summary.SecondaryQueryFailed) text.Append(" - the secondary view failed");
            return text.ToString();
        }

        /// <summary>The cached primary metadata (created for <paramref name="service"/> when there is none yet).</summary>
        private DataverseSchemaProvider PrimarySchema(IOrganizationService service) =>
            _primarySchema ?? (_primarySchema = new DataverseSchemaProvider(service));

        /// <summary>The cached secondary metadata (created for <paramref name="service"/> when there is none yet).</summary>
        private DataverseSchemaProvider SecondarySchema(IOrganizationService service) =>
            _secondarySchema ?? (_secondarySchema = new DataverseSchemaProvider(service));

        /// <summary>
        /// The display-name headers of the view's columns, or null when there is nothing to resolve. Runs on
        /// a worker thread (metadata requests); never throws - headers are cosmetic, the raw names stay.
        /// </summary>
        private static IList<string> ResolveHeaders(string fetchXml, string entity, IList<ViewColumn> columns, ISchemaProvider schema)
        {
            if (columns == null || columns.Count == 0 || schema == null) return null;
            try
            {
                return ColumnHeaderResolver.Resolve(fetchXml, entity, columns, schema);
            }
            catch (Exception)
            {
                return null;
            }
        }

        // =====================================================================================
        // The result: grid, summary strip, filters
        // =====================================================================================

        /// <summary>
        /// A result ready to be shown: the result, its view columns and headers, the grid's table and the
        /// rows by id - all built off the UI thread (<see cref="Build"/>), so binding is all the UI does.
        /// </summary>
        internal sealed class Shown
        {
            public CompareResult Result;
            public IList<ViewColumn> Columns;
            public IList<string> Headers;
            public DataTable Table;
            public Dictionary<Guid, RowComparison> RowsById;

            /// <summary>
            /// The grid's table for a result: hidden id, status text, then the text of each view column
            /// (<see cref="CompareResult.CellText"/>), one row per result row in the result's order.
            /// </summary>
            public static Shown Build(CompareResult result, IList<ViewColumn> columns, IList<string> headers)
            {
                columns = columns ?? new List<ViewColumn>();
                DataTable table = NewTable(columns.Count);
                var rowsById = new Dictionary<Guid, RowComparison>(result.Rows.Count);
                table.BeginLoadData();
                try
                {
                    foreach (RowComparison row in result.Rows)
                    {
                        var values = new object[3 + columns.Count];
                        values[0] = row.Id;
                        values[1] = StatusText(row.Status);
                        values[2] = DifferingText(row);
                        for (int i = 0; i < columns.Count; i++) values[3 + i] = result.CellText(row, columns[i].Name);
                        table.Rows.Add(values);
                        rowsById[row.Id] = row;
                    }
                }
                finally
                {
                    table.EndLoadData();
                }
                return new Shown { Result = result, Columns = columns, Headers = headers, Table = table, RowsById = rowsById };
            }
        }

        /// <summary>An empty table for <paramref name="valueColumns"/> view columns: __id (Guid), __status, __differing, c0..cN (strings).</summary>
        private static DataTable NewTable(int valueColumns)
        {
            var table = new DataTable("Rows") { Locale = CultureInfo.CurrentCulture };
            table.Columns.Add(IdColumn, typeof(Guid));
            table.Columns.Add(StatusColumn, typeof(string));
            table.Columns.Add(DifferingColumn, typeof(string));
            for (int i = 0; i < valueColumns; i++) table.Columns.Add(ValueColumnName(i), typeof(string));
            return table;
        }

        /// <summary>The hidden __differing cell of a row: "|name1|name2|" for its differing attributes, "" when there are none.</summary>
        internal static string DifferingText(RowComparison row)
        {
            IReadOnlyList<string> names = row?.DifferingAttributes;
            if (names == null || names.Count == 0) return string.Empty;
            var text = new StringBuilder();
            text.Append(DifferingSeparator);
            foreach (string name in names) text.Append(name).Append(DifferingSeparator);
            return text.ToString();
        }

        private static string ValueColumnName(int index) => ValueColumnPrefix + index.ToString(CultureInfo.InvariantCulture);

        /// <summary>The status text of the grid, the status filter and the detail caption.</summary>
        internal static string StatusText(RowStatus status)
        {
            switch (status)
            {
                case RowStatus.Missing: return MissingText;
                case RowStatus.Different: return DifferentText;
                case RowStatus.Extra: return ExtraText;
                default: return MatchingText;
            }
        }

        /// <summary>The row colour of a status text, and its darker shade for a selected row; false for any other text.</summary>
        internal static bool TryStatusColors(string status, out Color back, out Color selection)
        {
            switch (status)
            {
                case MissingText:
                    back = MissingColor;
                    selection = MissingSelectionColor;
                    return true;
                case DifferentText:
                    back = DifferentColor;
                    selection = DifferentSelectionColor;
                    return true;
                case ExtraText:
                    back = ExtraColor;
                    selection = ExtraSelectionColor;
                    return true;
                case MatchingText:
                    back = MatchColor;
                    selection = MatchSelectionColor;
                    return true;
                default:
                    back = Color.Empty;
                    selection = Color.Empty;
                    return false;
            }
        }

        /// <summary>
        /// Shows a result: binds its table to the grid (Status, hidden id, then the view's columns with
        /// their headers and layout widths), the summary strip, the filters and the detail pane. Internal:
        /// the performance test binds a large result through it.
        /// </summary>
        internal void ShowResult(ViewInfo view, Shown shown)
        {
            _result = shown.Result;
            _resultView = view;
            _resultColumns = shown.Columns;
            _rowsById = shown.RowsById;
            FillDifferingColumnFilter();   // before binding: BindTable applies the filters
            BindTable(shown.Table, shown.Columns, shown.Headers);
            UpdateSummary();
        }

        /// <summary>
        /// Forgets the result: an empty grid (no view columns), the summary strip's empty text, an empty detail pane,
        /// only "(any column)" in the differing-column filter.
        /// </summary>
        private void ClearResult()
        {
            _result = null;
            _resultView = null;
            _resultColumns = new List<ViewColumn>();
            _rowsById = new Dictionary<Guid, RowComparison>();
            FillDifferingColumnFilter();
            BindTable(NewTable(0), _resultColumns, null);
            UpdateSummary();
        }

        /// <summary>
        /// Rebinds the grid to <paramref name="table"/>: Status, hidden id, then one column per view column
        /// (header = the resolved display name, else the raw column name; the raw name is also the header
        /// tooltip; width from the layout). The grid's rows stay shared: colours come from CellFormatting.
        /// </summary>
        private void BindTable(DataTable table, IList<ViewColumn> columns, IList<string> headers)
        {
            _bindingSource.DataSource = null;
            // The BindingSource re-applies its Filter/Sort to a new data source, and they name columns the new
            // table may not have: drop them (ApplyRowFilter sets the filter again below).
            _bindingSource.Filter = null;
            _bindingSource.Sort = null;
            _filterApplied = string.Empty;
            _grid.Columns.Clear();
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = StatusColumn,
                DataPropertyName = StatusColumn,
                HeaderText = "Status",
                ToolTipText = "Missing: not in the secondary. Different: a compared column differs. Extra: not in the primary. Matching: identical.",
                Width = 80,
                ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.Automatic
            });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = IdColumn, DataPropertyName = IdColumn, HeaderText = "Id", Visible = false, ReadOnly = true });
            for (int i = 0; i < columns.Count; i++)
            {
                string header = headers != null && i < headers.Count && !string.IsNullOrWhiteSpace(headers[i]) ? headers[i] : columns[i].Name;
                _grid.Columns.Add(new DataGridViewTextBoxColumn
                {
                    Name = ValueColumnName(i),
                    DataPropertyName = ValueColumnName(i),
                    HeaderText = header,
                    ToolTipText = columns[i].Name,
                    Width = Math.Max(40, columns[i].Width),
                    ReadOnly = true,
                    SortMode = DataGridViewColumnSortMode.Automatic
                });
            }

            DataTable previous = _table;
            _table = table;
            _bindingSource.DataSource = table;
            if (previous != null && !ReferenceEquals(previous, table)) previous.Dispose();

            ApplyRowFilter();
        }

        /// <summary>
        /// The summary strip: the entity mapping and the prefix filter when there are any, the counts on their
        /// row colours, Not checked and the secondary's failure when they apply.
        /// </summary>
        private void UpdateSummary()
        {
            CompareSummary summary = _result?.Summary;
            _summaryRow.SuspendLayout();
            try
            {
                UpdateMappingIndicator();
                IList<string> prefixes = CompareOptions.ParseAttributeList(_settings.ComparedPrefixes);
                _prefixLabel.Visible = prefixes.Count > 0;
                _prefixLabel.Text = PrefixCaption(prefixes);
                _toolTip.SetToolTip(_prefixLabel, prefixes.Count > 0
                    ? "Only the columns whose logical names start with these prefixes are compared; the others are shown greyed (Compare options...)."
                    : null);

                bool shown = summary != null;
                _summaryEmptyLabel.Visible = !shown;
                foreach (Control label in new Control[]
                         {
                             _primaryCountLabel, _secondaryCountLabel, _summarySeparator, _missingCountLabel,
                             _differentCountLabel, _extraCountLabel, _matchingCountLabel
                         })
                {
                    label.Visible = shown;
                }
                _uncheckedCountLabel.Visible = shown && (summary.Cancelled || summary.Unchecked > 0);
                _secondaryFailedLabel.Visible = shown && summary.SecondaryQueryFailed;
                _toolTip.SetToolTip(_secondaryFailedLabel, shown && summary.SecondaryQueryFailed
                    ? "The view query failed in the secondary - every unmatched primary record was looked up by id instead, " +
                      "and extra records the secondary's view would have returned may be missed: " + summary.SecondaryQueryError
                    : null);
                if (!shown) return;

                _primaryCountLabel.Text = "Primary: " + Number(summary.PrimaryCount);
                _secondaryCountLabel.Text = "Secondary: " + Number(summary.SecondaryCount);
                _missingCountLabel.Text = MissingText + ": " + Number(summary.Missing);
                _differentCountLabel.Text = DifferentText + ": " + Number(summary.Different);
                _extraCountLabel.Text = ExtraText + ": " + Number(summary.Extra);
                _matchingCountLabel.Text = MatchingText + ": " + Number(summary.Matching);
                _uncheckedCountLabel.Text = "Not checked: " + Number(summary.Unchecked) + (summary.Cancelled ? " (cancelled)" : string.Empty);
            }
            finally
            {
                _summaryRow.ResumeLayout(true);
            }
        }

        private void OnGridCellFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        {
            // The row's status from the bound row (never through _grid.Rows[i], which would unshare it).
            if (e.RowIndex < 0 || e.RowIndex >= _bindingSource.Count || !(_bindingSource[e.RowIndex] is DataRowView row)) return;
            if (!TryStatusColors(row.Row[StatusColumn] as string, out Color back, out Color selection)) return;
            e.CellStyle.BackColor = back;
            e.CellStyle.ForeColor = RowTextColor;
            e.CellStyle.SelectionBackColor = selection;
            e.CellStyle.SelectionForeColor = RowTextColor;
        }

        private void OnGridDataError(object sender, DataGridViewDataErrorEventArgs e)
        {
            e.ThrowException = false;
            _logger.Write(LogLevel.Warning, "Grid: " + (e.Exception?.Message ?? "data error"));
        }

        // ---- status and text filters (client-side, the BindingSource's Filter) ----

        /// <summary>The filter the BindingSource has now ("" = none): an unchanged filter is not applied again (a 20 000-row reset).</summary>
        private string _filterApplied = string.Empty;

        private void OnStatusFilterChanged(object sender, EventArgs e) => Guard("Filtering rows", ApplyRowFilter);

        private void OnRowFilterTextChanged(object sender, EventArgs e)
        {
            _rowFilterTimer.Stop();
            _rowFilterTimer.Start();
        }

        private void OnRowFilterTimerTick(object sender, EventArgs e) => Guard("Filtering rows", ApplyRowFilter);

        /// <summary>
        /// Applies the status, differing-column and text filters to the grid now (the text normally a moment after
        /// the last keystroke); a row is shown when it passes all three.
        /// </summary>
        internal void ApplyRowFilter() => ApplyRowFilter(force: false);

        /// <param name="force">Set the filter again even when its text did not change (the filtered cells did: re-evaluation).</param>
        private void ApplyRowFilter(bool force)
        {
            _rowFilterTimer.Stop();
            if (_table == null) return;
            string status = _statusFilter.SelectedItem as string;
            string filter = CombineFilters(
                CombineFilters(
                    string.IsNullOrEmpty(status) || status == AllStatuses ? null : "[" + StatusColumn + "] = '" + EscapeLikeValue(status) + "'",
                    DifferingColumnFilter(SelectedDifferingColumn)),
                BuildRowFilter(_rowFilter.Text, _table.Columns.Cast<DataColumn>().Select(c => c.ColumnName)
                    .Where(n => n != IdColumn && n != DifferingColumn)));
            if (string.IsNullOrEmpty(filter) && _filterApplied.Length == 0) force = false;   // no filter before or after: nothing to redo
            if (force || !string.Equals(filter ?? string.Empty, _filterApplied, StringComparison.Ordinal))
            {
                try
                {
                    if (force && _filterApplied.Length > 0) _bindingSource.RemoveFilter();
                    _bindingSource.Filter = filter;
                    _filterApplied = filter ?? string.Empty;
                }
                catch (InvalidExpressionException ex)
                {
                    _bindingSource.RemoveFilter();
                    _filterApplied = string.Empty;
                    _logger.Write(LogLevel.Warning, "The row filter could not be applied: " + ex.Message);
                }
            }
            UpdateRowCountLabel();
            ShowSelectedDetails();
        }

        /// <summary>The row filter of the differing-column filter: rows whose __differing cell names the attribute; null for none.</summary>
        internal static string DifferingColumnFilter(string logicalName) =>
            string.IsNullOrEmpty(logicalName)
                ? null
                : "[" + DifferingColumn + "] LIKE '%" + DifferingSeparator + EscapeLikeValue(logicalName) + DifferingSeparator + "%'";

        // ---- the differing-column filter (SPEC 6.9) ----

        /// <summary>One item of the differing-column filter: an attribute that differs in at least one row, and in how many.</summary>
        internal sealed class DifferingColumnChoice
        {
            public DifferingColumnChoice(string logicalName, string displayName, int count)
            {
                LogicalName = logicalName;
                DisplayName = displayName;
                Count = count;
            }

            public string LogicalName { get; }

            /// <summary>The primary metadata's label; the logical name when there is none.</summary>
            public string DisplayName { get; }

            /// <summary>The rows where the attribute differs.</summary>
            public int Count { get; }

            /// <summary>"Display Name (logicalname) - N" with a middle dot for the dash ("logicalname - N" when the label is the logical name).</summary>
            public override string ToString() =>
                (string.IsNullOrWhiteSpace(DisplayName) || string.Equals(DisplayName.Trim(), LogicalName, StringComparison.Ordinal)
                    ? LogicalName
                    : DisplayName.Trim() + " (" + LogicalName + ")")
                + CountSeparator + Number(Count);
        }

        /// <summary>
        /// The union of the rows' <see cref="RowComparison.DifferingAttributes"/>, each with the number of rows where it
        /// differs and its display name from the result's (primary) metadata, sorted by display name then logical name.
        /// Empty without a result.
        /// </summary>
        internal static List<DifferingColumnChoice> DifferingColumnChoices(CompareResult result)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (result != null)
            {
                foreach (RowComparison row in result.Rows)
                {
                    foreach (string name in row.DifferingAttributes)
                    {
                        counts.TryGetValue(name, out int count);
                        counts[name] = count + 1;
                    }
                }
            }
            return counts
                .Select(pair =>
                {
                    string label = result.Schema.Attribute(pair.Key)?.DisplayName;
                    return new DifferingColumnChoice(pair.Key, string.IsNullOrWhiteSpace(label) ? pair.Key : label, pair.Value);
                })
                .OrderBy(c => c.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(c => c.LogicalName, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>The attribute the differing-column filter keeps the rows of; null for "(any column)".</summary>
        internal string SelectedDifferingColumn => (_differingFilter.SelectedItem as DifferingColumnChoice)?.LogicalName;

        /// <summary>Set while the differing-column filter's items are replaced: its selection events apply nothing.</summary>
        private bool _fillingDifferingFilter;

        private void OnDifferingFilterChanged(object sender, EventArgs e)
        {
            if (!_fillingDifferingFilter) Guard("Filtering rows", ApplyRowFilter);
        }

        /// <summary>
        /// Refills the differing-column filter from the result shown: "(any column)", then <see cref="DifferingColumnChoices"/>.
        /// The attribute selected stays selected while it still differs somewhere, else "(any column)" is. Applies no
        /// filter itself (the caller does).
        /// </summary>
        private void FillDifferingColumnFilter()
        {
            string selected = SelectedDifferingColumn;
            List<DifferingColumnChoice> choices = DifferingColumnChoices(_result);
            _fillingDifferingFilter = true;
            _differingFilter.BeginUpdate();
            try
            {
                _differingFilter.Items.Clear();
                _differingFilter.Items.Add(AnyColumn);
                _differingFilter.Items.AddRange(choices.Cast<object>().ToArray());
                int index = selected == null ? -1 : choices.FindIndex(c => string.Equals(c.LogicalName, selected, StringComparison.OrdinalIgnoreCase));
                _differingFilter.SelectedIndex = index + 1;   // not found: (any column)
                // The list opens wide enough for the longest item; the box keeps its width.
                int widest = _differingFilter.Items.Cast<object>()
                    .Select(item => TextRenderer.MeasureText(item.ToString(), _differingFilter.Font).Width)
                    .DefaultIfEmpty(0).Max() + SystemInformation.VerticalScrollBarWidth + 8;
                _differingFilter.DropDownWidth = Math.Max(_differingFilter.Width, Math.Min(widest, 600));
            }
            finally
            {
                _differingFilter.EndUpdate();
                _fillingDifferingFilter = false;
            }
        }

        /// <summary>Two row filters AND-ed (either may be null); null when both are.</summary>
        internal static string CombineFilters(string first, string second)
        {
            if (string.IsNullOrEmpty(first)) return string.IsNullOrEmpty(second) ? null : second;
            if (string.IsNullOrEmpty(second)) return first;
            return "(" + first + ") AND (" + second + ")";
        }

        /// <summary>
        /// A DataView row filter matching rows where any of <paramref name="columns"/> contains
        /// <paramref name="text"/> (case-insensitive); null for no filter. The text is escaped for LIKE: '
        /// is doubled and *, %, [ and ] are bracketed, so they match literally.
        /// </summary>
        internal static string BuildRowFilter(string text, IEnumerable<string> columns)
        {
            string value = (text ?? string.Empty).Trim();
            if (value.Length == 0) return null;
            string pattern = "'%" + EscapeLikeValue(value) + "%'";
            List<string> conditions = (columns ?? Enumerable.Empty<string>())
                .Select(c => "[" + EscapeColumnName(c) + "] LIKE " + pattern)
                .ToList();
            return conditions.Count == 0 ? null : string.Join(" OR ", conditions);
        }

        internal static string EscapeLikeValue(string value)
        {
            var escaped = new StringBuilder(value.Length + 8);
            foreach (char c in value)
            {
                switch (c)
                {
                    case '\'':
                        escaped.Append("''");
                        break;
                    case '*':
                    case '%':
                    case '[':
                    case ']':
                        escaped.Append('[').Append(c).Append(']');
                        break;
                    default:
                        escaped.Append(c);
                        break;
                }
            }
            return escaped.ToString();
        }

        /// <summary>Inside [...] a column name escapes ] and \ with a backslash.</summary>
        private static string EscapeColumnName(string name) => (name ?? string.Empty).Replace("\\", "\\\\").Replace("]", "\\]");

        private void UpdateRowCountLabel()
        {
            int total = _table?.Rows.Count ?? 0;
            int visible = _bindingSource.Count;
            _rowCountLabel.Text = Number(visible) + " of " + Plural(total, "row");
        }

        // =====================================================================================
        // Detail pane
        // =====================================================================================

        private void OnGridSelectionChanged(object sender, EventArgs e) => Guard("Showing the row's columns", ShowSelectedDetails);

        /// <summary>The result row of the grid's selected row; null when no row is selected.</summary>
        internal RowComparison SelectedRow
        {
            get
            {
                if (_result == null || _grid.SelectedRows.Count != 1) return null;
                int index = _grid.SelectedRows[0].Index;
                if (index < 0 || index >= _bindingSource.Count || !(_bindingSource[index] is DataRowView view)) return null;
                return view.Row[IdColumn] is Guid id && _rowsById.TryGetValue(id, out RowComparison row) ? row : null;
            }
        }

        private void ShowSelectedDetails() => ShowDetails(SelectedRow);

        /// <summary>
        /// Fills the detail pane with a row's columns (SPEC 6.2): the primary key first, then by display name;
        /// only the differing lines with Differences only, only the lines matching the column search when it is
        /// set. A compared line that differs is amber; a line that is not compared (key, ignored, derived, not in
        /// the primary metadata) has grey text. Empty without a row.
        /// </summary>
        private void ShowDetails(RowComparison row)
        {
            _detailRow = row;
            if (row == null || _result == null)
            {
                _detailCaption.Text = _result == null ? string.Empty : NoSelectionCaption;
                _detailLines = new List<ColumnComparison>();
            }
            else
            {
                _detailCaption.Text = DetailCaption(_result, row);
                _detailLines = _result.GetDetails(row);
            }
            FillDetailGrid();
        }

        /// <summary>
        /// The column search (SPEC 6.2) changed: the same row's lines are filtered again (nothing recomputed). The
        /// text stays when another row is selected, so one column can be followed from row to row.
        /// </summary>
        private void OnDetailFilterChanged(object sender, EventArgs e) => Guard("Searching the row's columns", FillDetailGrid);

        /// <summary>Escape in the column search clears it (and is not passed on while there is text to clear).</summary>
        private void OnDetailFilterPreviewKeyDown(object sender, PreviewKeyDownEventArgs e)
        {
            if (e.KeyCode == Keys.Escape && _detailFilter.TextLength > 0) e.IsInputKey = true;
        }

        private void OnDetailFilterKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.Escape || _detailFilter.TextLength == 0) return;
            _detailFilter.Clear();
            e.Handled = true;
            e.SuppressKeyPress = true;   // no beep
        }

        /// <summary>
        /// The column search: true when <paramref name="search"/> (trimmed) is blank or a case-insensitive part of the
        /// line's logical name or display name.
        /// </summary>
        internal static bool MatchesColumnSearch(ColumnComparison line, string search)
        {
            string value = (search ?? string.Empty).Trim();
            if (value.Length == 0) return true;
            return (line.LogicalName ?? string.Empty).IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0
                   || (line.DisplayName ?? string.Empty).IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Shows <see cref="_detailLines"/> through Differences only and the column search, and counts them.</summary>
        private void FillDetailGrid()
        {
            _detailGrid.SuspendLayout();
            try
            {
                _detailGrid.Rows.Clear();
                bool differencesOnly = _differencesOnly.Checked;
                string search = _detailFilter.Text;
                var lines = new List<DataGridViewRow>();
                foreach (ColumnComparison line in _detailLines)
                {
                    if (differencesOnly && !line.IsDifferent) continue;
                    if (!MatchesColumnSearch(line, search)) continue;
                    var gridRow = new DataGridViewRow();
                    bool renamed = line.SecondaryLogicalName != null
                                   && !string.Equals(line.SecondaryLogicalName, line.LogicalName, StringComparison.OrdinalIgnoreCase);
                    string arrow = renamed ? " " + MappingArrow + " " + line.SecondaryLogicalName : string.Empty;
                    gridRow.CreateCells(_detailGrid, line.DisplayName + arrow, line.PrimaryText, line.SecondaryText);
                    gridRow.Cells[0].ToolTipText = line.LogicalName + arrow + (line.Note == null ? string.Empty : " - " + line.Note);
                    gridRow.Tag = line;
                    if (line.IsMismatch)
                    {
                        gridRow.DefaultCellStyle.BackColor = DifferentColor;
                        gridRow.DefaultCellStyle.ForeColor = RowTextColor;
                        gridRow.DefaultCellStyle.SelectionBackColor = DifferentSelectionColor;
                        gridRow.DefaultCellStyle.SelectionForeColor = RowTextColor;
                    }
                    else if (!line.IsCompared)
                    {
                        gridRow.DefaultCellStyle.BackColor = SystemColors.Window;
                        gridRow.DefaultCellStyle.ForeColor = SystemColors.GrayText;
                        gridRow.DefaultCellStyle.SelectionForeColor = SystemColors.GrayText;
                    }
                    lines.Add(gridRow);
                }
                _detailGrid.Rows.AddRange(lines.ToArray());
                _detailGrid.ClearSelection();
                _detailCountLabel.Text = Number(lines.Count) + " of " + Plural(_detailLines.Count, "column");
            }
            finally
            {
                _detailGrid.ResumeLayout();
            }
        }

        /// <summary>"{entity} {id} - {status}", and where the record was found when it was outside a view.</summary>
        internal static string DetailCaption(CompareResult result, RowComparison row)
        {
            string entity = string.IsNullOrWhiteSpace(result.Schema.DisplayName) ? result.EntityLogicalName : result.Schema.DisplayName;
            string caption = $"{entity} {row.Id:D} - {StatusText(row.Status)}";
            if (row.Primary != null && row.Secondary != null)
            {
                if (!row.InSecondaryView) caption += " (found by id outside the secondary's view)";
                else if (!row.InPrimaryView) caption += " (found by id outside the primary's view)";
            }
            return caption;
        }

        // =====================================================================================
        // Compare options: the ignored attributes and the prefix filter
        // =====================================================================================

        /// <summary>The arrow of an entity or column mapping: primary -> secondary.</summary>
        internal static readonly string MappingArrow = ((char)0x2192).ToString();

        private void OnCompareOptionsClick(object sender, EventArgs e) => Guard("Editing the compare options", EditCompareOptions);

        /// <summary>
        /// The Compare options dialog (SPEC 6.5): OK saves the ignored attributes and the prefix filter and, when
        /// a result is shown, re-decides its Match/Different rows at once with <see cref="CompareResult.Reevaluate"/>
        /// (nothing is read again). Needs no connection.
        /// </summary>
        private void EditCompareOptions()
        {
            if (IsBusy) return;
            CompareOptions current = _settings.BuildOptions();
            using (var form = new CompareOptionsForm(current.IgnoredAttributes, current.ComparedPrefixes))
            {
                if (ShowCompareOptionsDialog(form) != DialogResult.OK) return;
                IList<string> names = form.AttributeNames;
                IList<string> prefixes = form.Prefixes;
                _settings.IgnoredAttributes = CompareOptions.FormatAttributeList(names);
                _settings.ComparedPrefixes = CompareOptions.FormatPrefixList(prefixes);
                SaveSettings();
                _logger.Write(LogLevel.Info, names.Count == 0
                    ? "Ignored attributes: none."
                    : "Ignored attributes: " + string.Join(", ", names) + ".");
                _logger.Write(LogLevel.Info, prefixes.Count == 0
                    ? "Prefix filter: none - the columns of every prefix are compared."
                    : "Prefix filter: only the columns starting with " + string.Join(", ", prefixes) + " are compared.");
            }
            UpdateSummary();
            if (_result != null) ReevaluateResult();
        }

        /// <summary>"Prefixes: contoso_, new_" (the summary strip's prefix filter label).</summary>
        internal static string PrefixCaption(IList<string> prefixes) =>
            prefixes == null || prefixes.Count == 0 ? string.Empty : "Prefixes: " + string.Join(", ", prefixes);

        /// <summary>Applies the compare options of the settings to the result shown: statuses, summary, filters and detail pane.</summary>
        private void ReevaluateResult()
        {
            CompareResult updated = _result.Reevaluate(_settings.BuildOptions());
            var rowsById = new Dictionary<Guid, RowComparison>(updated.Rows.Count);
            foreach (RowComparison row in updated.Rows) rowsById[row.Id] = row;
            _result = updated;
            _rowsById = rowsById;

            // Only the status texts and the differing attributes change: update them in place (the order and the
            // other cells stay).
            _bindingSource.RaiseListChangedEvents = false;
            _table.BeginLoadData();
            try
            {
                foreach (DataRow tableRow in _table.Rows)
                {
                    if (!(tableRow[IdColumn] is Guid id) || !rowsById.TryGetValue(id, out RowComparison row)) continue;
                    string status = StatusText(row.Status);
                    if (!string.Equals(tableRow[StatusColumn] as string, status, StringComparison.Ordinal)) tableRow[StatusColumn] = status;
                    string differing = DifferingText(row);
                    if (!string.Equals(tableRow[DifferingColumn] as string, differing, StringComparison.Ordinal)) tableRow[DifferingColumn] = differing;
                }
            }
            finally
            {
                _table.EndLoadData();
                _bindingSource.RaiseListChangedEvents = true;
                _bindingSource.ResetBindings(false);
            }

            UpdateSummary();
            // The differing columns follow the new decisions (a column that no longer differs anywhere drops out and
            // its filter falls back to "(any column)"), and every filter is applied again to the changed cells.
            FillDifferingColumnFilter();
            ApplyRowFilter(force: true);   // also the row count and the detail pane
            _grid.Invalidate();
            WriteRunLine(LogLevel.Info, "Re-evaluated with the new compare options (nothing read again) - Result: " + ResultCounts(updated.Summary) + ".");
            if (!IsBusy) SetProgress(FinalProgressText(updated.Summary));
        }

        // =====================================================================================
        // Entity mappings
        // =====================================================================================

        private void OnEntityMappingsClick(object sender, EventArgs e) => RunGuarded("Editing the entity mappings", EditEntityMappingsAsync);

        /// <summary>
        /// The Entity mappings dialog (SPEC 6.6). The secondary's entities are listed first - once per secondary
        /// connection, off the UI thread; when that fails the names are typed - then the dialog is shown (its
        /// Columns... reads the two entities' columns through the cached schema providers). OK saves the mappings.
        /// A changed mapping of the entity whose result is shown is not applied to that result (re-evaluation
        /// cannot change what was read): the log and the summary strip say to press Compare.
        /// </summary>
        private async Task EditEntityMappingsAsync()
        {
            if (IsBusy) return;
            IOrganizationService secondary = _secondaryService;
            if (secondary != null && _secondaryEntities == null)
            {
                CancellationToken token = BeginOperation("Listing the secondary's entities...");
                try
                {
                    IList<EntityInfo> listed = await Task.Run(() => EntityCatalog.GetEntities(secondary));
                    if (ReferenceEquals(secondary, _secondaryService) && !token.IsCancellationRequested) _secondaryEntities = listed;
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    if (!IsDisposed) _logger.Write(LogLevel.Warning, "The secondary's entities could not be listed (" + ErrorText(ex) + "): type the secondary entity names.");
                }
                finally
                {
                    EndOperation();
                }
                if (IsDisposed) return;
            }

            IOrganizationService primary = Service;
            DataverseSchemaProvider primarySchema = primary != null ? PrimarySchema(primary) : null;
            DataverseSchemaProvider secondarySchema = secondary != null ? SecondarySchema(secondary) : null;
            Func<string, IList<NameChoice>> primaryColumns = null, secondaryColumns = null;
            if (primarySchema != null) primaryColumns = name => NameChoice.FromSchema(primarySchema.GetEntity(name));
            if (secondarySchema != null) secondaryColumns = name => NameChoice.FromSchema(secondarySchema.GetEntity(name));

            using (var form = new EntityMappingsForm(_settings.EntityMappings, _entities, _secondaryEntities ?? new List<EntityInfo>(),
                                                     primaryColumns, secondaryColumns, _currentEntity?.LogicalName))
            {
                if (ShowEntityMappingsDialog(form) != DialogResult.OK) return;
                _settings.EntityMappings = form.Mappings.ToList();
            }
            SaveSettings();
            _logger.Write(LogLevel.Info, _settings.EntityMappings.Count == 0
                ? "Entity mappings: none - every entity is compared with the same entity of the secondary."
                : "Entity mappings: " + string.Join("; ", _settings.EntityMappings.Select(DescribeMapping)) + ".");
            if (_result != null && !EntityMapping.SameMapping(_settings.FindMapping(_result.EntityLogicalName), _result.Mapping?.Mapping))
            {
                _logger.Write(LogLevel.Warning, $"The entity mapping of {_result.EntityLogicalName} changed: the result shown was read " +
                                                (_result.Mapping != null ? "from " + _result.Mapping.SecondaryEntity : "without a mapping") +
                                                ". Press Compare to apply the new one (re-evaluation cannot change mappings).");
            }
            UpdateSummary();
        }

        /// <summary>"account -> new_account (contoso_name -> new_name)" for the log.</summary>
        internal static string DescribeMapping(EntityMapping mapping)
        {
            IList<ColumnMapping> pairs = mapping.UsableColumns();
            return EntityMapping.Normalize(mapping.PrimaryEntity) + " -> " + EntityMapping.Normalize(mapping.SecondaryEntity) +
                   (pairs.Count == 0 ? string.Empty : " (" + string.Join(", ", pairs.Select(p => p.PrimaryColumn + " -> " + p.SecondaryColumn)) + ")");
        }

        /// <summary>The summary strip's mapping label for the current entity (or the result's).</summary>
        private void UpdateMappingIndicator()
        {
            string entity = _result?.EntityLogicalName ?? _currentEntity?.LogicalName;
            string text = MappingCaption(entity == null ? null : _settings.FindMapping(entity), _result, out bool pending, out string tooltip);
            _mappingLabel.Visible = text != null;
            _mappingLabel.Text = text ?? string.Empty;
            _mappingLabel.ForeColor = pending ? Color.DarkGoldenrod : SystemColors.ControlText;
            _toolTip.SetToolTip(_mappingLabel, tooltip);
        }

        /// <summary>
        /// The mapping indicator (SPEC 6.6): "-> new_account" while the entity is mapped (and the result shown,
        /// if any, was read with that mapping); "-> new_account (press Compare to apply)" or "Mapping removed (press
        /// Compare to apply)" - <paramref name="pending"/> - when the result shown was read with another mapping;
        /// null when the entity is not mapped and nothing is pending.
        /// </summary>
        internal static string MappingCaption(EntityMapping current, CompareResult result, out bool pending, out string tooltip)
        {
            EntityMapping applied = result?.Mapping?.Mapping;
            pending = result != null && !EntityMapping.SameMapping(current, applied);
            string secondary = current == null ? null : EntityMapping.Normalize(current.SecondaryEntity);
            if (!pending)
            {
                tooltip = current == null
                    ? null
                    : $"{EntityMapping.Normalize(current.PrimaryEntity)} is mapped: its records are compared with {secondary} in the secondary, " +
                      $"matched on their GUID ({EntityMappingsForm.ColumnsText(current).ToLowerInvariant()}). Entity mappings... edits it.";
                return current == null ? null : MappingArrow + " " + secondary;
            }
            tooltip = "The result shown was read " + (applied != null ? "from " + applied.SecondaryEntity : "without a mapping") +
                      "; the entity mapping changed since. Press Compare to apply it.";
            return current != null ? MappingArrow + " " + secondary + " (press Compare to apply)" : "Mapping removed (press Compare to apply)";
        }

        /// <summary>"120 rows - matching a, different b, missing c, extra d" (the engine's result line).</summary>
        internal static string ResultCounts(CompareSummary s) =>
            $"{Plural(s.Total, "row")} - matching {Number(s.Matching)}, different {Number(s.Different)}, missing {Number(s.Missing)}, extra {Number(s.Extra)}" +
            (s.Unchecked > 0 ? $", not checked {Number(s.Unchecked)}" : string.Empty);

        // =====================================================================================
        // Log panel and XrmToolBox's own log
        // =====================================================================================

        /// <summary>A run header line: on screen, and in XrmToolBox's log as Info.</summary>
        private void WriteRunLine(LogLevel level, string text)
        {
            _logger.Write(level, text);
            MirrorToXrmToolBoxLog(LogLevel.Info, text);
        }

        /// <summary>
        /// The sink for every <see cref="UiLogger.Log"/> line (engine and UI errors, on the logging thread):
        /// errors go to XrmToolBox's log as errors, the engine's result line as Info.
        /// </summary>
        private void MirrorEngineLine(LogLevel level, string message)
        {
            if (level == LogLevel.Error) MirrorToXrmToolBoxLog(LogLevel.Error, message);
            else if (message != null && message.StartsWith("Result: ", StringComparison.Ordinal)) MirrorToXrmToolBoxLog(LogLevel.Info, message);
        }

        private void MirrorToXrmToolBoxLog(LogLevel level, string message)
        {
            if (!_mirrorToXrmToolBoxLog) return;
            try
            {
                // "{0}" + argument: the host formats the message, so braces in record names stay literal.
                string text = (message ?? string.Empty).Trim();
                if (level == LogLevel.Error) LogError("{0}", text);
                else LogInfo("{0}", text);
            }
            catch (Exception)
            {
                // XrmToolBox's log file is best-effort; it must never break a comparison.
            }
        }

        private void OnCopyLogClick(object sender, EventArgs e)
        {
            try
            {
                string text = LogText();
                if (text.Length > 0) Clipboard.SetText(text);
            }
            catch (Exception ex)
            {
                ReportError("Copying the log failed", ex);
            }
        }

        private void OnSaveLogClick(object sender, EventArgs e)
        {
            try
            {
                using (var dialog = new SaveFileDialog
                {
                    Title = "Save log",
                    Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
                    DefaultExt = "txt",
                    AddExtension = true,
                    FileName = "DataCompare-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".txt"
                })
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    File.WriteAllText(dialog.FileName, LogText(), Encoding.UTF8);
                    _logger.Write(LogLevel.Info, "Log saved to " + dialog.FileName);
                }
            }
            catch (Exception ex)
            {
                ReportError("Saving the log failed", ex);
            }
        }

        private void OnClearLogClick(object sender, EventArgs e) => Guard("Clearing the log", _logger.Clear);

        /// <summary>The log text with Windows line breaks (the RichTextBox uses \n).</summary>
        private string LogText() => _log.Text.TrimEnd('\n').Replace("\n", Environment.NewLine);

        private void OnCloseClick(object sender, EventArgs e) => Guard("Closing the tool", CloseTool);

        // =====================================================================================
        // Small helpers
        // =====================================================================================

        /// <summary>An error as one line: the Dataverse fault's own message when there is one, else the exception's.</summary>
        internal static string ErrorText(Exception ex)
        {
            if (ex == null) return string.Empty;
            string message = (ex as FaultException<OrganizationServiceFault>)?.Detail?.Message;
            if (string.IsNullOrWhiteSpace(message)) message = ex.Message;
            return string.IsNullOrWhiteSpace(message) ? ex.GetType().Name : OneLine(message);
        }

        private static string OneLine(string text)
        {
            var builder = new StringBuilder(text.Length);
            bool pendingSpace = false;
            foreach (char c in text.Trim())
            {
                if (char.IsWhiteSpace(c))
                {
                    pendingSpace = true;
                    continue;
                }
                if (pendingSpace && builder.Length > 0) builder.Append(' ');
                pendingSpace = false;
                builder.Append(c);
            }
            return builder.ToString();
        }

        private static bool ContainsText(string text, string value) =>
            text != null && text.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;

        private static string Number(int value) => value.ToString(CultureInfo.CurrentCulture);

        internal static string Plural(int count, string singular, string plural = null) =>
            Number(count) + " " + (count == 1 ? singular : plural ?? singular + "s");

        private DialogResult ShowMessageBox(string text, MessageBoxButtons buttons, MessageBoxIcon icon, MessageBoxDefaultButton defaultButton) =>
            MessageBox.Show(this, text, DialogTitle, buttons, icon, defaultButton);
    }
}
