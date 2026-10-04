using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using MyscotekDataCompare.Core;
using Label = System.Windows.Forms.Label;

namespace MyscotekDataCompare.UI
{
    /// <summary>
    /// The "Columns..." dialog of an entity mapping (SPEC 6.6), built in code: the column pairs whose names
    /// differ between the primary entity and its secondary table. Two combo boxes (the entities' columns when
    /// their metadata could be read; a typed logical name always works) and Add - which also updates the pair of
    /// a primary column already listed - and Remove. Every column not paired is compared with the secondary column
    /// of the same logical name. OK confirms (<see cref="Pairs"/>; a pair typed but not added yet is added first);
    /// Cancel discards.
    /// </summary>
    internal sealed class ColumnMappingsForm : Form
    {
        private readonly IList<NameChoice> _primaryColumns;
        private readonly IList<NameChoice> _secondaryColumns;
        private readonly ListView _list;
        private readonly ComboBox _primaryBox;
        private readonly ComboBox _secondaryBox;
        private readonly Button _removeButton;
        private readonly Label _statusLabel;
        private readonly Font _font = new Font("Segoe UI", 9f);

        /// <param name="primaryEntity">The mapping's primary entity (for the texts).</param>
        /// <param name="secondaryEntity">The mapping's secondary entity (for the texts).</param>
        /// <param name="pairs">The pairs now (copied: nothing changes until OK).</param>
        /// <param name="primaryColumns">The primary entity's columns; empty: typed names only.</param>
        /// <param name="secondaryColumns">The secondary entity's columns; empty: typed names only.</param>
        /// <param name="note">A note shown under the list (e.g. the metadata could not be read); may be empty.</param>
        public ColumnMappingsForm(string primaryEntity, string secondaryEntity, IEnumerable<ColumnMapping> pairs,
                                  IList<NameChoice> primaryColumns, IList<NameChoice> secondaryColumns, string note = null)
        {
            _primaryColumns = primaryColumns ?? new List<NameChoice>();
            _secondaryColumns = secondaryColumns ?? new List<NameChoice>();

            Text = $"Column pairs: {primaryEntity} {DataCompareControl.MappingArrow} {secondaryEntity}";
            Font = _font;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(600, 460);
            MinimumSize = new Size(500, 400);

            var intro = new Label
            {
                Name = "introLabel",
                Text = $"Columns of {primaryEntity} (primary) whose data is in a differently named column of {secondaryEntity} (secondary). " +
                       "Every other column is compared with the secondary column of the same logical name; a column without one is " +
                       "shown in the detail pane but not compared.",
                AutoSize = true,
                MaximumSize = new Size(570, 0),
                Dock = DockStyle.Fill,
                Margin = new Padding(3, 3, 3, 6)
            };

            _list = new ListView
            {
                Name = "columnList",
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                HideSelection = false,
                MultiSelect = false,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                Margin = new Padding(3)
            };
            _list.Columns.Add("Primary column", 270);
            _list.Columns.Add("Secondary column", 270);
            _list.SelectedIndexChanged += (sender, e) => ShowSelected();

            _primaryBox = NewCombo("primaryColumnBox", _primaryColumns);
            _secondaryBox = NewCombo("secondaryColumnBox", _secondaryColumns);
            var add = EntityMappingsForm.NewButton("addColumnButton", "Add");
            add.Click += (sender, e) => TryAdd();
            _removeButton = EntityMappingsForm.NewButton("removeColumnButton", "Remove");
            _removeButton.Click += (sender, e) => RemoveSelected();

            var editor = new TableLayoutPanel
            {
                Name = "columnEditor",
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2,
                RowCount = 3,
                Margin = new Padding(0, 6, 0, 0)
            };
            editor.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            for (int i = 0; i < 3; i++) editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            editor.Controls.Add(EntityMappingsForm.NewLabel("primaryColumnLabel", "Primary column:"), 0, 0);
            editor.Controls.Add(_primaryBox, 1, 0);
            editor.Controls.Add(EntityMappingsForm.NewLabel("secondaryColumnLabel", "Secondary column:"), 0, 1);
            editor.Controls.Add(_secondaryBox, 1, 1);
            FlowLayoutPanel editButtons = EntityMappingsForm.NewRow("columnButtons");
            editButtons.Controls.AddRange(new Control[] { add, _removeButton });
            editor.Controls.Add(editButtons, 1, 2);

            _statusLabel = new Label { Name = "columnStatusLabel", AutoSize = true, MaximumSize = new Size(570, 0), Margin = new Padding(3, 6, 3, 2) };

            var ok = EntityMappingsForm.NewButton("okButton", "OK");
            ok.Click += (sender, e) => Confirm();
            var cancel = EntityMappingsForm.NewButton("cancelButton", "Cancel");
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

            var layout = new TableLayoutPanel { Name = "layout", Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(8) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(intro, 0, 0);
            layout.Controls.Add(_list, 0, 1);
            layout.Controls.Add(editor, 0, 2);
            layout.Controls.Add(_statusLabel, 0, 3);
            layout.Controls.Add(buttons, 0, 4);
            Controls.Add(layout);

            foreach (ColumnMapping pair in new EntityMapping(primaryEntity, secondaryEntity, (pairs ?? Enumerable.Empty<ColumnMapping>()).ToArray()).UsableColumns())
                _list.Items.Add(NewItem(pair.PrimaryColumn, pair.SecondaryColumn));
            SetStatus(note, error: false);
            ShowSelected();
        }

        /// <summary>The pairs listed (normalised), in their order; what OK confirms.</summary>
        public IList<ColumnMapping> Pairs =>
            _list.Items.Cast<ListViewItem>().Select(i => (ColumnMapping)i.Tag).Select(p => p.Clone()).ToList();

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) _font.Dispose();
        }

        /// <summary>
        /// Adds the pair of the two combo boxes - or updates the secondary column of a primary column already
        /// listed. False (with a message) when a name is missing or is not a logical name.
        /// </summary>
        internal bool TryAdd()
        {
            string primary = NameChoice.Parse(_primaryBox.Text, _primaryColumns);
            string secondary = NameChoice.Parse(_secondaryBox.Text, _secondaryColumns);
            string problem = primary.Length == 0 || secondary.Length == 0 ? "Choose a primary and a secondary column."
                : !NameChoice.IsLogicalName(primary) ? $"\"{primary}\" is not a logical name."
                : !NameChoice.IsLogicalName(secondary) ? $"\"{secondary}\" is not a logical name."
                : null;
            if (problem != null)
            {
                SetStatus(problem, error: true);
                return false;
            }

            ListViewItem existing = _list.Items.Cast<ListViewItem>().FirstOrDefault(i => ((ColumnMapping)i.Tag).PrimaryColumn == primary);
            ListViewItem item = existing ?? _list.Items.Add(NewItem(primary, secondary));
            if (existing != null)
            {
                existing.Tag = new ColumnMapping(primary, secondary);
                existing.SubItems[1].Text = NameChoice.Show(secondary, _secondaryColumns);
            }
            foreach (ListViewItem other in _list.SelectedItems.Cast<ListViewItem>().ToList()) other.Selected = false;
            item.EnsureVisible();
            _primaryBox.Text = string.Empty;
            _secondaryBox.Text = string.Empty;
            SetStatus(existing != null ? $"{primary} now pairs with {secondary}." : string.Empty, error: false);
            return true;
        }

        private void RemoveSelected()
        {
            if (_list.SelectedItems.Count != 1) return;
            _list.Items.Remove(_list.SelectedItems[0]);
            // The removed pair must not come back as a pending pair on OK.
            _primaryBox.Text = string.Empty;
            _secondaryBox.Text = string.Empty;
            ShowSelected();
        }

        private void ShowSelected()
        {
            ColumnMapping pair = _list.SelectedItems.Count == 1 ? _list.SelectedItems[0].Tag as ColumnMapping : null;
            _removeButton.Enabled = pair != null;
            if (pair == null) return;
            _primaryBox.Text = NameChoice.Show(pair.PrimaryColumn, _primaryColumns);
            _secondaryBox.Text = NameChoice.Show(pair.SecondaryColumn, _secondaryColumns);
        }

        /// <summary>OK: a pair still in the combo boxes is added first (it must be valid); then the dialog closes with OK.</summary>
        private void Confirm()
        {
            bool pending = _primaryBox.Text.Trim().Length > 0 || _secondaryBox.Text.Trim().Length > 0;
            if (pending && !TryAdd())
            {
                DialogResult = DialogResult.None;
                return;
            }
            DialogResult = DialogResult.OK;
        }

        private void SetStatus(string text, bool error)
        {
            _statusLabel.Text = text ?? string.Empty;
            _statusLabel.ForeColor = error ? Color.Firebrick : SystemColors.ControlText;
        }

        private ListViewItem NewItem(string primary, string secondary) =>
            new ListViewItem(new[] { NameChoice.Show(primary, _primaryColumns), NameChoice.Show(secondary, _secondaryColumns) })
            {
                Tag = new ColumnMapping(primary, secondary)
            };

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
    }
}
