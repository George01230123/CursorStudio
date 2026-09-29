using System.Drawing;

namespace CursorStudio.UI;

/// <summary>输入方案名的小对话框。WinForms 没有内置的输入框，自己搭一个。</summary>
public sealed class NameDialog : Form
{
    private readonly TextBox _box;
    public string EnteredName { get; private set; } = "";

    public NameDialog(string title, string initial)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(360, 118);

        var caption = new Label
        {
            Text = "方案名",
            AutoSize = true,
            Location = new Point(16, 16),
        };

        _box = new TextBox
        {
            Text = initial,
            Location = new Point(16, 40),
            Width = 328,
            MaxLength = 60,
        };
        _box.SelectAll();

        var ok = new Button
        {
            Text = "确定",
            DialogResult = DialogResult.OK,
            Location = new Point(184, 76),
            Size = new Size(76, 28),
        };
        var cancel = new Button
        {
            Text = "取消",
            DialogResult = DialogResult.Cancel,
            Location = new Point(268, 76),
            Size = new Size(76, 28),
        };

        AcceptButton = ok;
        CancelButton = cancel;

        ok.Click += (_, _) =>
        {
            EnteredName = _box.Text.Trim();
            if (EnteredName.Length == 0) DialogResult = DialogResult.None;
        };

        Controls.AddRange(new Control[] { caption, _box, ok, cancel });
    }
}
