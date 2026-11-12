using System.Drawing;
using System.Windows;
using System.Windows.Threading;
using ImageStudio.Localization;
using Application = System.Windows.Application;

namespace ImageStudio.Views;

internal sealed class TrayIconController : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Action _restoreWindow;
    private readonly Action _exitApplication;
    private readonly System.Windows.Forms.ContextMenuStrip _menu;
    private readonly Icon _iconImage;
    private readonly System.Windows.Forms.NotifyIcon _icon;
    private readonly System.Windows.Forms.ToolStripMenuItem _restoreItem;
    private readonly System.Windows.Forms.ToolStripMenuItem _exitItem;
    private bool _disposed;

    public TrayIconController(Dispatcher dispatcher, Action restoreWindow, Action exitApplication)
    {
        _dispatcher = dispatcher;
        _restoreWindow = restoreWindow;
        _exitApplication = exitApplication;

        _menu = new System.Windows.Forms.ContextMenuStrip();
        _restoreItem = new System.Windows.Forms.ToolStripMenuItem();
        _exitItem = new System.Windows.Forms.ToolStripMenuItem();
        _menu.Items.Add(_restoreItem);
        _menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        _menu.Items.Add(_exitItem);
        _restoreItem.Click += (_, _) => _restoreWindow();
        _exitItem.Click += (_, _) => _exitApplication();

        var iconResource = Application.GetResourceStream(new Uri("pack://application:,,,/ImageStudio;component/Assets/AppIcon.ico"))
                           ?? throw new InvalidOperationException("Brak ikony aplikacji Assets/AppIcon.ico.");
        using (iconResource.Stream)
        using (var icon = new Icon(iconResource.Stream))
        {
            _iconImage = (Icon)icon.Clone();
            _icon = new System.Windows.Forms.NotifyIcon
            {
                Icon = _iconImage,
                Text = "ImageStudio",
                ContextMenuStrip = _menu,
            };
        }
        _icon.MouseClick += OnMouseClick;
        Loc.LanguageChanged += OnLanguageChanged;
        UpdateTexts();
    }

    public bool Visible
    {
        get => _icon.Visible;
        set => _icon.Visible = value;
    }

    private void OnMouseClick(object? sender, System.Windows.Forms.MouseEventArgs e)
    {
        if (e.Button == System.Windows.Forms.MouseButtons.Left) _restoreWindow();
    }

    private void OnLanguageChanged()
    {
        if (!_dispatcher.HasShutdownStarted) _dispatcher.BeginInvoke(UpdateTexts);
    }

    private void UpdateTexts()
    {
        _restoreItem.Text = Loc.T("tray.restore");
        _exitItem.Text = Loc.T("menu.exit");
        _icon.Text = Loc.T("tray.tooltip");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Loc.LanguageChanged -= OnLanguageChanged;
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        _iconImage.Dispose();
    }
}
