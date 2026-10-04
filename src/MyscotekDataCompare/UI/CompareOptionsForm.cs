using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using CompareOptions = MyscotekDataCompare.Core.CompareOptions;
using Label = System.Windows.Forms.Label;

namespace MyscotekDataCompare.UI
{
    /// <summary>
    /// The "Compare options..." dialog (SPEC 6.2 item 3, 6.5), built in code. Two sections: the ignored
    /// attributes - a multi-line text box with their logical names (one a line; commas, semicolons and spaces
    /// separate names too) and Restore defaults - and the prefix filter - "only compare columns with these
    /// prefixes", blank = every column. A difference in an ignored attribute or a column outside the prefix
    /// filter never makes a row Different; the detail pane still shows it, greyed. OK and Cancel. The caller
    /// reads <see cref="AttributeNames"/> and <see cref="Prefixes"/> after OK.
    /// </summary>
    internal sealed class CompareOptionsForm : Form
    {
        private readonly TextBox _names;
        private readonly TextBox _prefixes;
        private readonly Label _countLabel;
        private readonly Label _prefixCountLabel;
        private readonly Font _font = new Font("Segoe UI", 9f);
        private readonly Font _listFont = new Font("Consolas", 9f);

        /// <param name="ignored">The attributes ignored now (shown sorted, one a line).</param>
        /// <param name="prefixes">The prefix filter now (one a line, in its order); empty = every column.</param>
        public CompareOptionsForm(IEnumerable<string> ignored, IEnumerable<string> prefixes)
        {
            Text = "Compare options";
            Font = _font;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(460, 560);
            MinimumSize = new Size(380, 460);

            var intro = new Label
            {
                Name = "introLabel",
                Text = "Ignored attributes: the logical names of the attributes that never make a row Different, one a line. " +
                       "They are still shown in the detail pane, greyed. An empty list ignores nothing.",
                AutoSize = true,
                MaximumSize = new Size(430, 0),
                Dock = DockStyle.Fill,
                Margin = new Padding(3, 3, 3, 6)
            };

            _names = NewListBox("ignoredAttributesBox");
            _names.TextChanged += (sender, e) => UpdateCounts();

            _countLabel = new Label { Name = "countLabel", AutoSize = true, Margin = new Padding(3, 8, 3, 2) };
            var restore = NewButton("restoreDefaultsButton", "Restore defaults");
            restore.Click += (sender, e) => Names = CompareOptions.DefaultIgnoredAttributes;
            FlowLayoutPanel ignoredFooter = NewRow("ignoredFooter");
            ignoredFooter.Controls.AddRange(new Control[] { restore, _countLabel });

            var prefixIntro = new Label
            {
                Name = "prefixIntroLabel",
                Text = "Only compare columns with these prefixes (one per line or comma-separated, blank = all), e.g. contoso_ or new_. " +
                       "The other columns are shown greyed; the ignored attributes still apply.",
                AutoSize = true,
                MaximumSize = new Size(430, 0),
                Dock = DockStyle.Fill,
                Margin = new Padding(3, 12, 3, 6)
            };

            _prefixes = NewListBox("prefixesBox");
            _prefixes.TextChanged += (sender, e) => UpdateCounts();
            _prefixCountLabel = new Label { Name = "prefixCountLabel", AutoSize = true, Margin = new Padding(3, 4, 3, 2) };

            var ok = NewButton("okButton", "OK");
            ok.DialogResult = DialogResult.OK;
            var cancel = NewButton("cancelButton", "Cancel");
            cancel.DialogResult = DialogResult.Cancel;
            // No AcceptButton: Enter makes a new line in the lists.
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

            var layout = new TableLayoutPanel
            {
                Name = "layout",
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 7,
                Padding = new Padding(8)
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 65f));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 35f));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(intro, 0, 0);
            layout.Controls.Add(_names, 0, 1);
            layout.Controls.Add(ignoredFooter, 0, 2);
            layout.Controls.Add(prefixIntro, 0, 3);
            layout.Controls.Add(_prefixes, 0, 4);
            layout.Controls.Add(_prefixCountLabel, 0, 5);
            layout.Controls.Add(buttons, 0, 6);
            Controls.Add(layout);

            Names = ignored ?? Enumerable.Empty<string>();
            PrefixList = prefixes ?? Enumerable.Empty<string>();
        }

        /// <summary>
        /// The names in the ignored box: trimmed, lower case, without duplicates, sorted (what OK confirms).
        /// </summary>
        public IList<string> AttributeNames =>
            CompareOptions.ParseAttributeList(_names.Text).OrderBy(n => n, StringComparer.Ordinal).ToList();

        /// <summary>The prefixes in the prefix box: trimmed, lower case, without duplicates, in their order; empty = every column.</summary>
        public IList<string> Prefixes => CompareOptions.ParseAttributeList(_prefixes.Text);

        /// <summary>The ignored box's content as names (set: sorted, one a line).</summary>
        public IEnumerable<string> Names
        {
            set
            {
                IEnumerable<string> sorted = CompareOptions.ParseAttributeList(string.Join(",", value ?? Enumerable.Empty<string>()))
                    .OrderBy(n => n, StringComparer.Ordinal);
                _names.Text = string.Join(Environment.NewLine, sorted);
                _names.SelectionStart = 0;
                _names.SelectionLength = 0;
            }
        }

        /// <summary>The prefix box's content (set: one a line, in the order given).</summary>
        public IEnumerable<string> PrefixList
        {
            set
            {
                _prefixes.Text = string.Join(Environment.NewLine, CompareOptions.ParseAttributeList(string.Join(",", value ?? Enumerable.Empty<string>())));
                _prefixes.SelectionStart = 0;
                _prefixes.SelectionLength = 0;
            }
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                // After base: the controls using the fonts are disposed by then.
                _font.Dispose();
                _listFont.Dispose();
            }
        }

        private void UpdateCounts()
        {
            int count = CompareOptions.ParseAttributeList(_names.Text).Count;
            _countLabel.Text = count == 0 ? "Nothing ignored" : count == 1 ? "1 attribute" : count + " attributes";
            IList<string> prefixes = Prefixes;
            _prefixCountLabel.Text = prefixes.Count == 0
                ? "Every column is compared"
                : "Only columns starting with " + string.Join(", ", prefixes);
        }

        private TextBox NewListBox(string name) => new TextBox
        {
            Name = name,
            Multiline = true,
            AcceptsReturn = true,
            ScrollBars = ScrollBars.Vertical,
            WordWrap = false,
            Dock = DockStyle.Fill,
            Font = _listFont,
            Margin = new Padding(3)
        };

        private static FlowLayoutPanel NewRow(string name) => new FlowLayoutPanel
        {
            Name = name,
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };

        private static Button NewButton(string name, string text) => new Button
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
