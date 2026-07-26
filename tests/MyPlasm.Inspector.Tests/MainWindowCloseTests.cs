using System.Windows.Threading;
using MyPlasm.Inspector.App;

namespace MyPlasm.Inspector.Tests;

public sealed class MainWindowCloseTests
{
    [Fact]
    public void StartupSafeWindowClosesWithoutReenteringWpfClosingState()
    {
        Exception? failure = null;
        bool closed = false;
        using ManualResetEventSlim finished = new();

        Thread thread = new(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.UnhandledException += (_, eventArgs) =>
            {
                failure = eventArgs.Exception;
                eventArgs.Handled = true;
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            };

            try
            {
                MainWindow window = new(StartupLog.CreateSafe(), softwareRenderingActive: true)
                {
                    ShowInTaskbar = false,
                    WindowState = System.Windows.WindowState.Minimized,
                };
                window.Closed += (_, _) =>
                {
                    closed = true;
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
                };

                window.Show();
                dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(window.Close));
                Dispatcher.Run();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                finished.Set();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(finished.Wait(TimeSpan.FromSeconds(15)), "WPF close lifecycle timed out.");
        thread.Join();
        Assert.Null(failure);
        Assert.True(closed);
    }
}
