using System;
using System.Drawing;
using System.Windows.Forms;

namespace Continuum.Tray;

/// <summary>
/// Иконка в области уведомлений. NotifyIcon - из Windows Forms
/// (UseWindowsForms), сторонних пакетов нет. Значок рисуется кодом:
/// бинарных ресурсов в репозитории не держим.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _notifyIcon;

    /// <summary>«Открыть» из меню или двойной клик по иконке.</summary>
    public event EventHandler? OpenRequested;

    /// <summary>«Выход» из меню - настоящее завершение приложения.</summary>
    public event EventHandler? ExitRequested;

    public TrayIcon()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Открыть", null, (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add("Выход", null, (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty));

        _notifyIcon = new NotifyIcon
        {
            Text = "Continuum",
            Icon = CreateIcon(),
            Visible = true,
            ContextMenuStrip = menu,
        };
        _notifyIcon.DoubleClick += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }

    private static Icon CreateIcon()
    {
        var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.FromArgb(32, 32, 32));
            using var brush = new SolidBrush(Color.FromArgb(96, 165, 250));
            using var font = new Font("Segoe UI", 18, FontStyle.Bold, GraphicsUnit.Pixel);
            graphics.DrawString("C", font, brush, 7f, 5f);
        }

        // GetHicon: HICON не освобождается вместе с Icon - для единственного
        // значка на всё время жизни процесса это приемлемо
        var handle = bitmap.GetHicon();
        bitmap.Dispose();
        return Icon.FromHandle(handle);
    }
}
