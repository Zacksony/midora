using System.IO;
using System.Windows;
using Midora.Application;
using Midora.Audio.Bass;
using Midora.Common;

namespace Midora.Desktop;

public partial class App : System.Windows.Application
{
    private SingleApplicationInstanceCoordinator? _instance;
    private CancellationTokenSource? _instanceRequests;

    static App()
    {
        System.Windows.Controls.ToolTipService.InitialShowDelayProperty.OverrideMetadata(
            typeof(FrameworkElement),
            new FrameworkPropertyMetadata(250));
        System.Windows.Controls.ToolTipService.InitialShowDelayProperty.OverrideMetadata(
            typeof(FrameworkContentElement),
            new FrameworkPropertyMetadata(250));
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        _ = MidoraWindowsApplicationIdentity.TryApplyToCurrentProcess();
        base.OnStartup(e);
        try
        {
            MidoraProgramData.EnsureReadyAndProbe();
            ApplicationStartupRequest request = new(
                Path.GetFullPath(Environment.CurrentDirectory),
                e.Args);
            ApplicationInstanceStartResult result =
                await SingleApplicationInstanceCoordinator.StartOrForwardAsync(
                    "Midora.Portable." + MidoraProgramData.Current.SingleInstanceScope,
                    request);
            if (result.ShouldExit)
            {
                Shutdown();
                return;
            }

            _instance = result.PrimaryInstance
                ?? throw new InvalidOperationException("The primary Midora instance was not created.");
            _instanceRequests = new CancellationTokenSource();

            ApplicationPreferences startupPreferences =
                new ApplicationPreferencesStore().Load().Preferences;
            _ = ApplicationSessionStorageCleanup.ClearInactiveSessions(startupPreferences);

            MainWindow window = new();
            MainWindow = window;
            window.Show();
            await window.HandleStartupRequestAsync(request);
            _ = ReceiveStartupRequestsAsync(window, _instanceRequests.Token);
        }
        catch (Exception exception)
        {
            MessageDialog.Show(
                $"Midora could not start.\n\n{exception.Message}",
                "Midora Startup Failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        _instanceRequests?.Cancel();
        if (_instance is not null)
        {
            await _instance.DisposeAsync();
        }
        _instanceRequests?.Dispose();
        AudioWorkerProcessGroup.Shutdown();
        base.OnExit(e);
    }

    private static async Task ReceiveStartupRequestsAsync(
        MainWindow window,
        CancellationToken cancellationToken)
    {
        App application = (App)Current;
        try
        {
            await foreach (ApplicationStartupRequest request in
                application._instance!.ReadAllAsync(cancellationToken))
            {
                await window.Dispatcher.InvokeAsync(async () =>
                {
                    if (window.WindowState == WindowState.Minimized)
                    {
                        window.WindowState = WindowState.Normal;
                    }
                    window.Activate();
                    await window.HandleStartupRequestAsync(request);
                }).Task.Unwrap();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }
}
