using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OCStartup;

public partial class App : Application
{
    private static EventWaitHandle? _wake;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _wake = new EventWaitHandle(false, EventResetMode.AutoReset, "OCStartup.wake", out var first);
        if (!first)
        {
            _wake.Set();
            Shutdown();
            return;
        }
        ThreadPool.RegisterWaitForSingleObject(_wake,
            (_, _) => Dispatcher.Invoke(() => (MainWindow as MainWindow)?.Reappear()),
            null, Timeout.Infinite, false);

        // Язык нужен до разметки: подписи окна берутся из ресурсов приложения.
        var saved = Settings.Load().Lang;
        Loc.Use(saved.Length > 0 ? saved : Loc.Detect());

        DispatcherUnhandledException += (_, args) =>
        {
            var note = Path.Combine(Path.GetTempPath(), "oc-startup-crash.txt");
            try { File.WriteAllText(note, args.Exception.ToString()); } catch { }
            MessageBox.Show(args.Exception + Environment.NewLine + Environment.NewLine + note,
                            Loc.S("L_Crash"), MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        var window = new MainWindow();
        window.Show();

        var shot = Array.IndexOf(e.Args, "--shot");
        if (shot >= 0 && e.Args.Length > shot + 1)
        {
            var file = e.Args[shot + 1];
            var screen = e.Args.Length > shot + 2 ? e.Args[shot + 2] : "main";
            _ = ShootAsync(window, file, screen);
        }
    }

    private static async Task ShootAsync(MainWindow window, string file, string screen)
    {
        await Task.Delay(2500);
        window.ShowScreen(screen);
        await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        await Task.Delay(screen is "updates" ? 12000 : screen is "catalog" or "tags" or "config" ? 5000 : 400);

        try
        {
            var bitmap = new RenderTargetBitmap(
                (int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(bitmap));
            await using var stream = File.Create(file);
            png.Save(stream);
        }
        catch (Exception ex)
        {
            File.WriteAllText(file + ".error.txt", ex.ToString());
        }
        Current.Shutdown();
    }
}
