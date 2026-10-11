using System.Windows;
using Forms = System.Windows.Forms;

namespace ReceptorNdi;

/// <summary>Icono en la bandeja del sistema (junto al reloj).</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Window _window;

    public TrayIcon(Window window, Action exit)
    {
        _window = window;
        _icon = new Forms.NotifyIcon
        {
            Text = "Receptor Cámara NDI",
            Visible = true,
            Icon = LoadIcon(),
        };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Abrir", null, (_, _) => ShowWindow());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Salir", null, (_, _) => exit());
        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) => ShowWindow();
    }

    private static System.Drawing.Icon LoadIcon()
    {
        try
        {
            var s = Application.GetResourceStream(new Uri("pack://application:,,,/app.ico"))?.Stream;
            if (s != null) return new System.Drawing.Icon(s);
        }
        catch { }
        return System.Drawing.SystemIcons.Application;
    }

    public void ShowWindow()
    {
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    public void Balloon(string text)
    {
        _icon.BalloonTipTitle = "Receptor Cámara NDI";
        _icon.BalloonTipText = text;
        _icon.ShowBalloonTip(3000);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
