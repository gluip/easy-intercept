#if WINDOWS
using System.Drawing;
using System.Windows.Forms;

namespace EasyIntercept.Desktop;

/// <summary>Small modal dialog shown when the configured UI port is already taken.</summary>
public static class PortPrompt
{
    public static int? Show(int busyPort, int suggestedPort)
    {
        int? result = null;
        var thread = new Thread(() =>
        {
            Application.EnableVisualStyles();
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            using var form = Build(busyPort, suggestedPort, out var input);
            if (form.ShowDialog() == DialogResult.OK)
                result = (int)input.Value;
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return result;
    }

    private static Form Build(int busyPort, int suggestedPort, out NumericUpDown input)
    {
        var form = new Form
        {
            Text = "EasyIntercept",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = true,
            TopMost = true,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(16),
            Font = SystemFonts.MessageBoxFont!,
        };
        try { form.Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { /* default icon */ }

        var label = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(380, 0),
            Text = $"Port {busyPort} is already in use by another application.\n\n" +
                   "Choose a different port for the EasyIntercept web UI. " +
                   "It will be saved so you don't have to do this again.",
        };

        input = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 65535,
            Value = suggestedPort,
            Width = 120,
            Margin = new Padding(0, 12, 0, 12),
        };

        var ok = new Button { Text = "Use this port", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Quit", DialogResult = DialogResult.Cancel, AutoSize = true };

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Bottom };
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);

        var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        layout.Controls.Add(label);
        layout.Controls.Add(input);
        layout.Controls.Add(buttons);

        form.Controls.Add(layout);
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        return form;
    }
}
#endif
