using System.Drawing;
using System.Windows.Forms;

namespace CodexDesktopUsageSwitcher.Windows.UI;

// Minimal modal for pasting the Claude OAuth code returned by the browser. Returns the trimmed text
// on OK, or null if the user cancels. The pasted value is handed straight to the token exchange and
// is never logged.
internal static class CodeInputDialog
{
    public static string? Prompt(string title, string message)
    {
        using var form = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen,
            ClientSize = new Size(470, 180),
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            TopMost = true,
        };

        var label = new Label { Text = message, Left = 14, Top = 14, Width = 442, Height = 70 };
        var input = new TextBox { Left = 14, Top = 90, Width = 442 };
        var ok = new Button { Text = "확인", DialogResult = DialogResult.OK, Left = 296, Top = 130, Width = 74 };
        var cancel = new Button { Text = "취소", DialogResult = DialogResult.Cancel, Left = 382, Top = 130, Width = 74 };

        form.Controls.Add(label);
        form.Controls.Add(input);
        form.Controls.Add(ok);
        form.Controls.Add(cancel);
        form.AcceptButton = ok;
        form.CancelButton = cancel;

        return form.ShowDialog() == DialogResult.OK ? input.Text.Trim() : null;
    }
}
