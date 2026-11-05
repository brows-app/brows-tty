using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Brows.Windows.Controls;

internal static class Program {
    [STAThread]
    private static int Main() {
        Application app = new() { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        int result = 1;
        app.Startup += async (sender, args) => {
            Terminal terminal = new() {
                Width = 800, Height = 400, AutoStart = false,
                CommandLine = "\"" + Environment.GetEnvironmentVariable("ComSpec")
                    + "\" /d /q /c \"echo package-consumer-render-ok & exit /b 17\""
            };
            using HwndSource source = new(new HwndSourceParameters("Package verification") {
                WindowStyle = unchecked((int)0x80000000), Width = 800, Height = 400
            }) { RootVisual = terminal };
            TaskCompletionSource<uint> exit = new();
            terminal.ProcessExited += (control, data) => exit.TrySetResult(data.ExitCode);
            terminal.Failed += (control, data) => exit.TrySetException(data.Exception);
            DispatcherTimer timeout = new() { Interval = TimeSpan.FromSeconds(20) };
            timeout.Tick += (control, data) =>
                exit.TrySetException(new TimeoutException("Package consumer timed out."));
            timeout.Start();
            try {
                terminal.Measure(new Size(800, 400));
                terminal.Arrange(new Rect(0, 0, 800, 400));
                await terminal.StartAsync();
                uint code = await exit.Task;
                if (code != 17) { throw new InvalidOperationException("Unexpected child exit code: " + code); }
                terminal.UpdateLayout();
                RenderTargetBitmap bitmap = new(800, 400, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(terminal);
                byte[] pixels = new byte[800 * 400 * 4];
                bitmap.CopyPixels(pixels, 800 * 4, 0);
                int lit = Enumerable.Range(0, 800 * 40).Count(index => pixels[index * 4] > 100);
                if (lit < 100) { throw new InvalidOperationException("Packaged control rendered no terminal text."); }
                Console.WriteLine("Package consumer passed: process exit 17, terminal glyphs rendered, " +
                    typeof(Terminal).Assembly.GetName().Name + ", " + Environment.Version);
                result = 0;
            }
            catch (Exception exception) { Console.Error.WriteLine(exception); }
            finally {
                timeout.Stop();
                await terminal.StopAsync();
                app.Shutdown(result);
            }
        };
        app.Run();
        return result;
    }
}
