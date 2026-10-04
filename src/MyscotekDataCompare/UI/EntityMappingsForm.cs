using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using MyscotekDataCompare.Core;
using MyscotekDataCompare.Core.Schema;
using MyscotekDataCompare.Core.Services;
using Label = System.Windows.Forms.Label;

namespace MyscotekDataCompare.UI
{
    /// <summary>
    /// An entity or a column offered by the mapping dialogs' combo boxes: shown "Display name (logical name)",
    /// standing for its logical name. A combo box also takes a typed logical name (<see cref="Parse"/>).
    /// </summary>
    internal sealed class NameChoice
    {
        private static readonly Regex LogicalNamePattern = new Regex("^[a-z0-9_]+$", RegexOptions.CultureInvariant);

        public NameChoice(string logicalName, string displayName)
        {
            LogicalName = EntityMapping.Normalize(logicalName);
            DisplayName = displayName;
        }

        public string LogicalName { get; }

        public string DisplayName { get; }

        public override string ToString() =>
            string.IsNullOrWhiteSpace(DisplayName) || string.Equals(DisplayName.Trim(), LogicalName, StringComparison.Ordinal)
                ? LogicalName
                : $"{DisplayName.Trim()} ({LogicalName})";

        /// <summary>
        /// The logical name a combo box's text stands for: an offered item's logical name; the part in the last
        /// brackets of "Display name (logical name)"; else the text itself, trimmed and lower-cased ("" for blank).
        /// </summary>
        public static string Parse(string text, IEnumerable<NameChoice> choices)
        {
            string value = (text ?? string.Empty).Trim();
            if (value.Length == 0) return string.Empty;
            NameChoice offered = (choices ?? Enumerable.Empty<NameChoice>())
                .FirstOrDefault(c => string.Equals(c.ToString(), value, StringComparison.OrdinalIgnoreCase));
            if (offered != null) return offered.LogicalName;
            int open = value.LastIndexOf('('), close = value.LastIndexOf(')');
            if (close == value.Length - 1 && open >= 0 && open < close)
            {
                string inner = value.Substring(open + 1, close - open - 1).Trim();
                if (inner.Length > 0) return inner.ToLowerInvariant();
            }
            return value.ToLowerInvariant();
        }

        /// <summary>The combo box text of a logical name: its offered item's text, else the name itself.</summary>
        public static string Show(string logicalName, IEnumerable<NameChoice> choices)
        {
            string name = EntityMapping.Normalize(logicalName);
            if (name.Length == 0) return string.Empty;
            NameChoice offered = (choices ?? Enumerable.Empty<NameChoice>()).FirstOrDefault(c => c.LogicalName == name);
            return offered?.ToString() ?? name;
        }

        /// <summary>True for a plausible Dataverse logical name: lower-case letters, digits and underscores.</summary>
        public static bool IsLogicalName(string name) => !string.IsNullOrEmpty(name) && LogicalNamePattern.IsMatch(name);

        public static IList<NameChoice> FromEntities(IEnumerable<EntityInfo> entities) =>
            (entities ?? Enumerable.Empty<EntityInfo>())
                .Where(e => e != null && !string.IsNullOrWhiteSpace(e.LogicalName))
                .Select(e => new NameChoice(e.LogicalName, e.DisplayName))
                .ToList();

        /// <summary>The columns a mapping can pair: readable, not derived, by display name then logical name.</summary>
        public static IList<NameChoice> FromSchema(EntitySchema schema) =>
            (schema?.Attributes?.Values ?? Enumerable.Empty<AttributeSchema>())
                .Where(a => a != null && !string.IsNullOrWhiteSpace(a.LogicalName) && a.IsValidForRead && a.AttributeOf == null)
                .Select(a => new NameChoice(a.LogicalName, a.DisplayName))
                .OrderBy(c => c.ToString(), StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(c => c.LogicalName, StringComparer.Ordinal)
                .ToList();
    }

    /// <summary>
    /// The "Entity mappings..." dialog (SPEC 6.6), built in code: the global list of entity mappings - a
    /// primary entity whose data was migrated into a differently named secondary table - with Add, Remove and,
    /// for the selected mapping, its primary and secondary entity (combo boxes of the loaded entities that also
    /// take a typed logical name) and Columns... for the column pairs whose names differ. OK validates and
    /// confirms (<see cref="Mappings"/>); Cancel discards every change.
    /// </summary>
    internal sealed class EntityMappingsForm : Form
    {
        private readonly List<EntityMapping> _mappings;
        private readonly IList<NameChoice> _primaryEntities;
        private readonly IList<NameChoice> _secondaryEntities;
        private readonly Func<string, IList<NameChoice>> _primaryColumns;
        private readonly Func<string, IList<NameChoice>> _secondaryColumns;
        private readonly string _defaultPrimaryEntity;
        private readonly ListView _list;
        private readonly ComboBox _primaryBox;
        private readonly ComboBox _secondaryBox;
        private readonly Button _removeButton;
        private readonly Button _columnsButton;
        private readonly Label _columnsLabel;
        private readonly Label _statusLabel;
        private readonly TableLayoutPanel _editor;
        private readonly Font _font = new Font("Segoe UI", 9f);
        private bool _updating;

        /// <param name="mappings">The mappings now (copied: nothing changes until OK).</param>
        /// <param name="primaryEntities">The primary's entities (the tool's entity list); empty: typed names only.</param>
        /// <param name="secondaryEntities">The secondary's entities when they could be listed; empty: typed names only.</param>
        /// <param name="primaryColumns">Reads a primary entity's columns (on a worker thread); null or failing: typed names.</param>
        /// <param name="secondaryColumns">Reads a secondary entity's columns (on a worker thread); null or failing: typed names.</param>
        /// <param name="defaultPrimaryEntity">The entity Add starts with when it is not mapped yet (the tool's current entity).</param>
        public EntityMappingsForm(IEnumerable<EntityMapping> mappings, IEnumerable<EntityInfo> primaryEntities, IEnumerable<EntityInfo> secondaryEntities,
                                  Func<string, IList<NameChoice>> primaryColumns, Func<string, IList<NameChoice>> secondaryColumns,
                                  string defaultPrimaryEntity = null)
        {
            _mappings = (mappings ?? Enumerable.Empty<EntityMapping>()).Where(m => m != null).Select(m => m.Clone()).ToList();
            _primaryEntities = NameChoice.FromEntities(primaryEntities);
            _secondaryEntities = NameChoice.FromEntities(secondaryEntities);
            _primaryColumns = primaryColumns;
            _secondaryColumns = secondaryColumns;
            _defaultPrimaryEntity = EntityMapping.Normalize(defaultPrimaryEntity);
            ShowColumnsDialog = form => form.ShowDialog(this);

            Text = "Entity mappings";
            Font = _font;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(620, 480);
            MinimumSize = new Size(520, 420);

            var intro = new Label
            {
                Name = "introLabel",
                Text = "Map an entity whose data was migrated into a differently named table: its records are then compared with " +
                       "that table in the secondary, still matched on their GUID. Columns match by logical name; use Columns... " +
                       "for the ones that were renamed. A mapping applies from the next Compare.",
                AutoSize = true,
                MaximumSize = new Size(590, 0),
                Dock = DockStyle.Fill,
                Margin = new Padding(3, 3, 3, 6)
            };

            _list = new ListView
            {
                Name = "mappingList",
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                HideSelection = false,
                MultiSelect = false,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                Margin = new Padding(3)
            };
            _list.Columns.Add("Primary entity", 200);
            _list.Columns.Add("Secondary entity", 200);
            _list.Columns.Add("Columns", 170);
            _list.SelectedIndexChanged += (sender, e) => ShowSelected();

            var add = NewButton("addButton", "Add");
            add.Click += (sender, e) => AddMapping();
            _removeButton = NewButton("removeButton", "Remove");
            _removeButton.Click += (sender, e) => RemoveMapping();
            FlowLayoutPanel listButtons = NewRow("listButtons");
            listButtons.Controls.AddRange(new Control[] { add, _removeButton });

            _primaryBox = NewCombo("primaryEntityBox", _primaryEntities);
            _primaryBox.TextChanged += (sender, e) => OnEntityTextChanged();
            _secondaryBox = NewCombo("secondaryEntityBox", _secondaryEntities);
            _secondaryBox.TextChanged += (sender, e) => OnEntityTextChanged();
            _columnsButton = NewButton("columnsButton", "Columns...");
            _columnsButton.Click += OnColumnsClick;
            _columnsLabel = new Label { Name = "columnsLabel", AutoSize = true, Margin = new Padding(6, 8, 3, 2) };

            _editor = new TableLayoutPanel
            {
                Name = "editor",
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2,
                RowCount = 3,
                Margin = new Padding(0, 6, 0, 0)
            };
            _editor.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            for (int i = 0; i < 3; i++) _editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _editor.Controls.Add(NewLabel("primaryEntityLabel", "Primary entity:"), 0, 0);
            _editor.Controls.Add(_primaryBox, 1, 0);
            _editor.Controls.Add(NewLabel("secondaryEntityLabel", "Secondary entity:"), 0, 1);
            _editor.Controls.Add(_secondaryBox, 1, 1);
            FlowLayoutPanel columnsRow = NewRow("columnsRow");
            columnsRow.Controls.AddRange(new Control[] { _columnsButton, _columnsLabel });
            _editor.Controls.Add(columnsRow, 1, 2);

            _statusLabel = new Label { Name = "statusLabel", AutoSize = true, MaximumSize = new Size(590, 0), Margin = new Padding(3, 6, 3, 2) };

            var ok = NewButton("okButton", "OK");
            ok.Click += (sender, e) => Confirm();
            var cancel = NewButton("cancelButton", "Cancel");
            cancel.DialogResult = DialogResult.Cancel;
            CancelButton = cancel;

            var buttons = new TableLayoutPanel
            {
                Name = "buttonRow",
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 3,
                RowCount = 1,
                Margin = new Padding(0, 6, 0, 0)
            };
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            buttons.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            buttons.Controls.Add(ok, 1, 0);
            buttons.Controls.Add(cancel, 2, 0);

            var layout = new TableLayoutPanel { Name = "layout", Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(8) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(intro, 0, 0);
            layout.Controls.Add(_list, 0, 1);
            layout.Controls.Add(listButtons, 0, 2);
            layout.Controls.Add(_editor, 0, 3);
            layout.Controls.Add(_statusLabel, 0, 4);
            layout.Controls.Add(buttons, 0, 5);
            Controls.Add(layout);

            foreach (EntityMapping mapping in _mappings) _list.Items.Add(NewItem(mapping));
            if (_list.Items.Count > 0) _list.Items[0].Selected = true;
            ShowSelected();
        }

        /// <summary>Shows the column pairs dialog modally (ShowDialog owned by this form): a seam for the UI tests.</summary>
        internal Func<ColumnMappingsForm, DialogResult> ShowColumnsDialog { get; set; }

        /// <summary>The confirmed mappings (normalised, complete); set by OK.</summary>
        public IList<EntityMapping> Mappings { get; private set; } = new List<EntityMapping>();

        /// <summary>The mappings as edited so far (not validated), in the list's order.</summary>
        internal IList<EntityMapping> Working => _mappings;

        /// <summary>The mapping selected in the list, or null.</summary>
        internal EntityMapping SelectedMapping => _list.SelectedItems.Count == 1 ? _list.SelectedItems[0].Tag as EntityMapping : null;

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) _font.Dispose();
        }

        private void AddMapping()
        {
            bool defaultFree = _defaultPrimaryEntity.Length > 0 && !_mappings.Any(m => EntityMapping.Normalize(m.PrimaryEntity) == _defaultPrimaryEntity);
            var mapping = new EntityMapping { PrimaryEntity = defaultFree ? _defaultPrimaryEntity : string.Empty, SecondaryEntity = string.Empty };
            _mappings.Add(mapping);
            ListViewItem item = NewItem(mapping);
            _list.Items.Add(item);
            foreach (ListViewItem other in _list.SelectedItems.Cast<ListViewItem>().ToList()) other.Selected = false;
            item.Selected = true;
            item.EnsureVisible();
            ShowSelected();
            (defaultFree ? _secondaryBox : _primaryBox).Focus();
        }

        private void RemoveMapping()
        {
            if (_list.SelectedItems.Count != 1) return;
            ListViewItem item = _list.SelectedItems[0];
            int index = item.Index;
            _mappings.Remove((EntityMapping)item.Tag);
            _list.Items.Remove(item);
            if (_list.Items.Count > 0) _list.Items[Math.Min(index, _list.Items.Count - 1)].Selected = true;
            ShowSelected();
        }

        /// <summary>Loads the selected mapping into the editor (disabled without one).</summary>
        private void ShowSelected()
        {
            EntityMapping mapping = SelectedMapping;
            _updating = true;
            try
            {
                _primaryBox.Text = NameChoice.Show(mapping?.PrimaryEntity, _primaryEntities);
                _secondaryBox.Text = NameChoice.Show(mapping?.SecondaryEntity, _secondaryEntities);
            }
            finally
            {
                _updating = false;
            }
            _editor.Enabled = mapping != null;
            _removeButton.Enabled = mapping != null;
            _columnsLabel.Text = mapping == null ? string.Empty : ColumnsText(mapping);
        }

        private void OnEntityTextChanged()
        {
            if (_updating) return;
            EntityMapping mapping = SelectedMapping;
            if (mapping == null) return;
            mapping.PrimaryEntity = NameChoice.Parse(_primaryBox.Text, _primaryEntities);
            mapping.SecondaryEntity = NameChoice.Parse(_secondaryBox.Text, _secondaryEntities);
            UpdateItem(_list.SelectedItems[0]);
            _statusLabel.Text = string.Empty;
        }

        /// <summary>
        /// Columns...: reads both entities' columns off the UI thread (typed names when they cannot be read),
        /// then shows the column pairs of the selected mapping; OK there replaces them.
        /// </summary>
        private async void OnColumnsClick(object sender, EventArgs e)
        {
            try
            {
                EntityMapping mapping = SelectedMapping;
                if (mapping == null) return;
                string primary = EntityMapping.Normalize(mapping.PrimaryEntity), secondary = EntityMapping.Normalize(mapping.SecondaryEntity);
                if (primary.Length == 0 || secondary.Length == 0)
                {
                    SetStatus("Choose the primary and the secondary entity first.", error: true);
                    return;
                }

                _columnsButton.Enabled = false;
                SetStatus($"Reading the columns of {primary} and {secondary}...", error: false);
                var problems = new List<string>();
                IList<NameChoice> primaryColumns, secondaryColumns;
                try
                {
                    primaryColumns = await Task.Run(() => ReadColumns(_primaryColumns, primary, "primary", problems));
                    secondaryColumns = await Task.Run(() => ReadColumns(_secondaryColumns, secondary, "secondary", problems));
                }
                finally
                {
                    if (!IsDisposed) _columnsButton.Enabled = true;
                }
                if (IsDisposed || !ReferenceEquals(mapping, SelectedMapping)) return;
                SetStatus(string.Join(" ", problems), error: false);

                using (var form = new ColumnMappingsForm(primary, secondary, mapping.Columns, primaryColumns, secondaryColumns, string.Join(" ", problems)))
                {
                    if (ShowColumnsDialog(form) != DialogResult.OK) return;
                    mapping.Columns = form.Pairs.ToList();
                }
                if (!IsDisposed && _list.SelectedItems.Count == 1 && ReferenceEquals(_list.SelectedItems[0].Tag, mapping))
                {
                    UpdateItem(_list.SelectedItems[0]);
                    _columnsLabel.Text = ColumnsText(mapping);
                }
            }
            catch (Exception ex)
            {
                if (!IsDisposed) SetStatus("The columns could not be edited: " + ex.Message, error: true);
            }
        }

        /// <summary>A side's columns, or an empty list (and a note) when they cannot be read.</summary>
        private static IList<NameChoice> ReadColumns(Func<string, IList<NameChoice>> read, string entity, string side, List<string> problems)
        {
            if (read == null)
            {
                lock (problems) problems.Add($"Not connected to the {side}: type its column names.");
                return new List<NameChoice>();
            }
            try
            {
                IList<NameChoice> columns = read(entity);
                if (columns == null || columns.Count == 0)
                {
                    lock (problems) problems.Add($"No columns of {entity} in the {side}: type the names.");
                    return new List<NameChoice>();
                }
                return columns;
            }
            catch (Exception ex)
            {
                lock (problems) problems.Add($"The {side}'s columns of {entity} could not be read ({ex.Message}): type the names.");
                return new List<NameChoice>();
            }
        }

        /// <summary>OK: every mapping complete, plausible logical names, one mapping per primary entity; blank rows are dropped.</summary>
        private void Confirm()
        {
            var confirmed = new List<EntityMapping>();
            var primaries = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < _mappings.Count; i++)
            {
                EntityMapping mapping = _mappings[i];
                string primary = EntityMapping.Normalize(mapping.PrimaryEntity), secondary = EntityMapping.Normalize(mapping.SecondaryEntity);
                if (primary.Length == 0 && secondary.Length == 0) continue;
                string problem = primary.Length == 0 || secondary.Length == 0 ? "choose both the primary and the secondary entity."
                    : !NameChoice.IsLogicalName(primary) ? $"\"{primary}\" is not a logical name."
                    : !NameChoice.IsLogicalName(secondary) ? $"\"{secondary}\" is not a logical name."
                    : !primaries.Add(primary) ? $"{primary} is mapped more than once."
                    : null;
                if (problem != null)
                {
                    foreach (ListViewItem item in _list.Items) item.Selected = item.Index == i;
                    ShowSelected();
                    SetStatus($"Mapping {i + 1}: {problem}", error: true);
                    DialogResult = DialogResult.None;
                    return;
                }
                confirmed.Add(mapping.Normalized());
            }
            Mappings = confirmed;
            DialogResult = DialogResult.OK;
        }

        private void SetStatus(string text, bool error)
        {
            _statusLabel.Text = text ?? string.Empty;
            _statusLabel.ForeColor = error ? Color.Firebrick : SystemColors.ControlText;
        }

        private ListViewItem NewItem(EntityMapping mapping)
        {
            var item = new ListViewItem(new[] { string.Empty, string.Empty, string.Empty }) { Tag = mapping };
            UpdateItem(item);
            return item;
        }

        private void UpdateItem(ListViewItem item)
        {
            var mapping = (EntityMapping)item.Tag;
            item.SubItems[0].Text = NameChoice.Show(mapping.PrimaryEntity, _primaryEntities);
            item.SubItems[1].Text = NameChoice.Show(mapping.SecondaryEntity, _secondaryEntities);
            item.SubItems[2].Text = ColumnsText(mapping);
        }

        /// <summary>"Matched by name" or "2 column pairs".</summary>
        internal static string ColumnsText(EntityMapping mapping)
        {
            int pairs = mapping?.UsableColumns().Count ?? 0;
            return pairs == 0 ? "Matched by name" : pairs == 1 ? "1 column pair" : pairs + " column pairs";
        }

        private static ComboBox NewCombo(string name, IList<NameChoice> choices)
        {
            var box = new ComboBox
            {
                Name = name,
                DropDownStyle = ComboBoxStyle.DropDown,
                Dock = DockStyle.Fill,
                AutoCompleteMode = AutoCompleteMode.SuggestAppend,
                AutoCompleteSource = AutoCompleteSource.ListItems,
                Margin = new Padding(3)
            };
            box.Items.AddRange(choices.Cast<object>().ToArray());
            return box;
        }

        internal static Label NewLabel(string name, string text) => new Label
        {
            Name = name,
            Text = text,
            AutoSize = true,
            Margin = new Padding(3, 7, 3, 2)
        };

        internal static FlowLayoutPanel NewRow(string name) => new FlowLayoutPanel
        {
            Name = name,
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };

        internal static Button NewButton(string name, string text) => new Button
        {
            Name = name,
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowOnly,
            MinimumSize = new Size(80, 27),
            Margin = new Padding(3, 2, 3, 2),
            UseVisualStyleBackColor = true
        };
    }
}
