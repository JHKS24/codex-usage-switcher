using CodexUsageSwitcher.Windows.Application;
using CodexUsageSwitcher.Windows.Infrastructure;
using CodexUsageSwitcher.Windows.UI;

namespace CodexUsageSwitcher.Windows;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        if (!SingleInstanceCoordinator.TryCreatePrimary(out var singleInstance))
        {
            SingleInstanceCoordinator.SignalPrimary();
            return;
        }

        // Source-generated: EnableVisualStyles, SetCompatibleTextRenderingDefault and the
        // csproj's PerMonitorV2 high-DPI mode, which a manual call pair never applied.
        ApplicationConfiguration.Initialize();
        RegisterExceptionBoundary();

        var primaryInstance = singleInstance ?? throw new InvalidOperationException("Primary instance coordinator was not created.");
        using (primaryInstance)
        {
            var client = NativeSwitcherClient.CreateDefault();
            var loginLauncher = NativeInteractiveLauncher.CreateDefault();
            var settingsStore = JsonSettingsStore.CreateDefault();
            ApplySavedLanguage(settingsStore);
            var service = new SwitcherService(client, loginLauncher, settingsStore);
            using var context = new TrayApplicationContext(service);
            primaryInstance.ActivationRequested += (_, _) => context.ShowPopupFromExternalActivation();
            primaryInstance.Start();
            System.Windows.Forms.Application.Run(context);
        }
    }

    // The single error boundary (guardrails I1). UI-thread exceptions — including ones
    // escaping async void handlers — land here instead of the default crash dialog;
    // non-UI fatals at least leave a log entry before the process dies.
    private static void RegisterExceptionBoundary()
    {
        System.Windows.Forms.Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        System.Windows.Forms.Application.ThreadException += (_, e) => HandleUiException(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogError(e.ExceptionObject as Exception);
    }

    private static void HandleUiException(Exception exception)
    {
        LogError(exception);
        MessageBox.Show(
            Localizer.F("error.unexpected.body", PathRedaction.Scrub(exception.Message)),
            Localizer.L("common.appName"),
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }

    private static void LogError(Exception? exception) => CrashLog.Write(exception);

    // Honor a previously chosen UI language before any window is built; with none saved, the
    // Localizer keeps its system-culture default.
    private static void ApplySavedLanguage(ISettingsStore settingsStore)
    {
        var saved = settingsStore.LoadLanguageAsync(CancellationToken.None).GetAwaiter().GetResult();
        if (saved is not null)
        {
            Localizer.SetLanguage(Localizer.Parse(saved));
        }
    }
}
