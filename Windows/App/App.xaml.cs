using System.Windows;
using PassphraseMemorizer.Crypto;

namespace PassphraseMemorizer.App;

public partial class App : Application
{
    private VerifiedBundleLease? _bundle;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, error) =>
        {
            (MainWindow as MainWindow)?.ClearForShutdown();
            error.Handled = true;
            MessageBox.Show("Passphrase Memorizer stopped safely. The session has been cleared.", "Passphrase Memorizer", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        };
        var testModes = new[] { "--smoke-test", "--integration-test", "--sandbox-test", "--cancel-test" };
        if (e.Args.Any(argument => testModes.Contains(argument)))
        {
            int code;
            try
            {
                if (e.Args.Count(argument => testModes.Contains(argument)) != 1)
                    throw new ArgumentException("Select one public fixture mode.");
                // The sole unsigned path is a headless fixed public UI fixture.
                // It neither opens a user window nor loads or saves preferences.
                if (!e.Args.Contains("--smoke-test"))
                    _bundle = await Task.Run(() => BundleVerifier.OpenAndVerifyRunningExecutable(AppContext.BaseDirectory));
                code = await PublicFixtureTests.RunAsync(e.Args);
            }
            catch { Console.WriteLine("PUBLIC_FIXTURE_RESULT FAIL startup_or_runtime"); code = 1; }
            Shutdown(code);
            return;
        }
        if (e.Args.Length != 0) { Shutdown(2); return; }
        try
        {
            // Verify both signatures, the complete payload inventory and this
            // executable before accepting input. Hold read leases until exit.
            _bundle = await Task.Run(() => BundleVerifier.OpenAndVerifyRunningExecutable(AppContext.BaseDirectory));
            var window = new MainWindow();
            MainWindow = window;
            window.Show();
        }
        catch
        {
            MessageBox.Show("Passphrase Memorizer could not start. Check the signed installation and local word lists.", "Passphrase Memorizer", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _bundle?.Dispose(); _bundle = null;
        base.OnExit(e);
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        (MainWindow as MainWindow)?.ClearForShutdown();
        base.OnSessionEnding(e);
    }
}
