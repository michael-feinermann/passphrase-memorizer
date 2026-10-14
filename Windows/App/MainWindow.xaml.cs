using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using PassphraseMemorizer.Core;
using PassphraseMemorizer.Runtime;

namespace PassphraseMemorizer.App;

public partial class MainWindow : Window
{
    public const string DefaultModelPath = @"D:\KI-Modelle\Ollama\blobs\sha256-0374ce7b0124db9ba96fc649e835c531223ee224a497ce88a374baaea10932ec";
    private readonly PhraseLexicon _lexicon = new();
    private readonly Preferences _preferences;
    private readonly bool _testMode;
    private ValidatedPhrase? _phrase;
    private char[]? _story;
    private string? _modelPath;
    private CancellationTokenSource? _cancellation;
    private Task? _generation;
    private readonly DispatcherTimer _revealTimer = new() { Interval = TimeSpan.FromMinutes(10) };
    private bool _initialized, _inputVisible = true, _storyVisible, _running, _closing, _closeReady;
    private int _generationId;
    private HwndSource? _windowSource;
    private nint _handle;
    private PhraseFormat Format => Eff.IsChecked == true ? PhraseFormat.Eff : PhraseFormat.Bip39;
    private LiteraryStyle SelectedStyle => Rhyme.IsChecked == true ? LiteraryStyle.Rhyme : Ballad.IsChecked == true ? LiteraryStyle.Ballad : Poem.IsChecked == true ? LiteraryStyle.Poem : Rap.IsChecked == true ? LiteraryStyle.Rap : LiteraryStyle.ShortStory;
    private bool IsGerman => _preferences.Language == "de";
    private string Tr(string german, string english) => IsGerman ? german : english;

    public MainWindow() : this(Preferences.Load(), false) { }

    internal MainWindow(Preferences preferences, bool testMode)
    {
        _preferences = preferences;
        _testMode = testMode;
        InitializeComponent();
        English.IsChecked = !IsGerman;
        German.IsChecked = IsGerman;
        if (_preferences.WindowWidth is >= 820 and <= 4096) Width = _preferences.WindowWidth;
        if (_preferences.WindowHeight is >= 740 and <= 4096) Height = _preferences.WindowHeight;
        try { _modelPath = ModelFile.Validate(string.IsNullOrEmpty(_preferences.ModelPath) ? DefaultModelPath : _preferences.ModelPath); }
        catch { _modelPath = null; }
        _initialized = true;
        UpdateLanguage();
        if (!string.IsNullOrEmpty(_preferences.ModelPath) && _modelPath is null)
            ShowError(Tr("Das gespeicherte Modell ist nicht sicher lokal verfügbar. Wähle es erneut aus.", "The saved model is not safely available locally. Select it again."));
        _revealTimer.Tick += (_, _) => Conceal();
        Deactivated += (_, _) => Conceal();
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) Conceal(); };
        Closing += CloseRequested;
        SizeChanged += (_, _) =>
        {
            var scale = Math.Sqrt(Math.Max(1, ActualWidth / 1060)) * Math.Sqrt(Math.Max(1, ActualHeight / 840));
            Page.LayoutTransform = new ScaleTransform(scale, scale);
            if (WindowState == WindowState.Normal && !_closing)
            { _preferences.WindowWidth = Width; _preferences.WindowHeight = Height; }
        };
        SourceInitialized += (_, _) => InitializePrivacyWindow();
        PreviewKeyDown += (_, e) =>
        {
            if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.K) { ClearSession(); e.Handled = true; }
            if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.H) { Conceal(); e.Handled = true; }
        };
    }

    private void InitializePrivacyWindow()
    {
        _handle = new WindowInteropHelper(this).Handle;
        _windowSource = HwndSource.FromHwnd(_handle);
        _windowSource?.AddHook(WindowMessage);
        _ = WTSRegisterSessionNotification(_handle, 0);
        // Best effort: Windows excludes the surface from supported capture APIs.
        // The reveal warning remains explicit because this is no universal capture guarantee.
        _ = SetWindowDisplayAffinity(_handle, 0x11);
        var dark = 1;
        _ = DwmSetWindowAttribute(_handle, 20, ref dark, sizeof(int));
    }

    private nint WindowMessage(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x02B1 && ((int)wParam is 3 or 7) || message == 0x0218 && ((int)wParam is 4 or 10)) Conceal();
        if (message == 0x0011) ClearForShutdown();
        return 0;
    }

    private void LanguageChanged(object sender, RoutedEventArgs e)
    {
        if (!_initialized || _running) return;
        _preferences.Language = German.IsChecked == true ? "de" : "en";
        ClearStory(); UpdateLanguage(); Revalidate(); SavePreferences();
    }

    private void FormatChanged(object sender, RoutedEventArgs e)
    { if (_initialized && !_running) { ClearStory(); Revalidate(); UpdateLanguage(); } }

    private void StyleChanged(object sender, RoutedEventArgs e)
    { if (_initialized && !_running) ClearStory(); }

    private void InputChanged(object sender, TextChangedEventArgs e)
    { if (_initialized && !_closing) { ClearStory(); Revalidate(); } }

    private void Revalidate()
    {
        _phrase?.Dispose(); _phrase = null;
        if (PhraseInput.Text.Length > 0)
        {
            try { _phrase = _lexicon.Parse(PhraseInput.Text, Format); }
            catch { }
        }
        WordCountLabel.Text = Tr($"{_phrase?.WordCount ?? 0} Wörter", $"{_phrase?.WordCount ?? 0} words");
        bool invalid = PhraseInput.Text.Length > 0 && _phrase is null;
        ValidationLabel.Foreground = (Brush)FindResource(invalid ? "Amber" : "Secondary");
        ValidationLabel.Text = invalid
            ? Tr("Gib 1 bis 128 Wörter aus der gewählten englischen Wortliste ein. EFF erlaubt Leerzeichen und Bindestriche.", "Enter 1 to 128 words from the selected English word list. EFF accepts spaces and hyphens.")
            : _phrase is null ? Tr("Wörter durch Leerzeichen oder Zeilenumbrüche trennen.", "Separate words with spaces or line breaks.") : "";
        UpdateControls();
    }

    private void UpdateLanguage()
    {
        HeaderSubtitle.Text = Tr("Private Merkhilfen für BIP39 und EFF. Lokal auf deinem Windows-PC.", "Private memory aids for BIP39 and EFF. Locally on your Windows PC.");
        IsolationTitle.Text = Tr("Abgesicherte Offline-Ausführung", "Protected offline execution");
        IsolationText.Text = Tr("Lokale CUDA-Ausführung auf der NVIDIA-GPU. Modellgewichte und KI-Cache liegen im Grafikspeicher. Netzwerk- und Schreibsperren werden vor der Übertragung geheimer Wörter geprüft. Ohne Cloudkonto.", "Local CUDA execution on the NVIDIA GPU. Model weights and AI cache reside in GPU memory. Network and write restrictions are checked before secret words are transferred. No cloud account.");
        PhraseTitle.Text = Tr("Geheime Wörter eingeben", "Enter your secret words");
        PhraseSubtitle.Text = Tr("Die Wörter bleiben ausschließlich in dieser Sitzung.", "The words stay in this session only.");
        PhraseHelp.Text = Format == PhraseFormat.Bip39
            ? Tr("1 bis 128 Wörter aus der englischen BIP39-Wortliste. Keine Prüfung von Wallet-Gültigkeit oder Prüfsumme.", "1 to 128 words from the English BIP39 word list. Wallet validity and checksum are not checked.")
            : Tr("1 bis 128 Wörter aus der EFF Large Wordlist. Leerzeichen und Bindestriche sind als Trennzeichen erlaubt.", "1 to 128 words from the EFF Large Wordlist. Spaces and hyphens are accepted as separators.");
        InputHiddenLabel.Text = Tr("Eingabe verdeckt", "Input hidden");
        RevealInput.Content = Tr("Eingabe anzeigen", "Reveal input");
        ClearInputButton.Content = ClearStoryButton.Content = Tr("Sitzung leeren", "Clear session");
        ModelTitle.Text = Tr("Merkhilfe lokal erzeugen", "Create a memory aid locally");
        ModelSubtitle.Text = Tr("Gemma 4 31B oder ein kompatibles GGUF-Modell.", "Gemma 4 31B or a compatible GGUF model.");
        LocalModelLabel.Text = Tr("Lokales Modell", "Local model");
        ModelNameLabel.Text = _modelPath is null ? Tr("Noch kein Modell ausgewählt", "No model selected yet") : System.IO.Path.GetFileName(_modelPath);
        ModelPathHelp.Text = Tr("Der Modellpfad wird gespeichert und beim Start erneut geprüft.", "The model path is saved and checked again at startup.");
        ChooseModelButton.Content = _modelPath is null ? Tr("GGUF auswählen", "Choose GGUF") : Tr("Modell wechseln", "Change model");
        Rhyme.Content = Tr("Reim", "Rhyme"); Ballad.Content = Tr("Ballade", "Ballad"); Poem.Content = Tr("Gedicht", "Poem"); ShortStory.Content = Tr("Kurzgeschichte", "Short story"); Rap.Content = "Rap";
        OutputLanguageHelp.Text = Tr($"Ausgabe auf {(IsGerman ? "Deutsch" : "English")}. Die ursprünglichen englischen Wörter bleiben unverändert.", $"Output in {(IsGerman ? "Deutsch" : "English")}. The original English words remain unchanged.");
        CancelButton.Content = Tr("Abbrechen", "Cancel");
        StoryTitle.Text = Tr("Deine Merkhilfe", "Your memory aid");
        StorySubtitle.Text = Tr("Behandle die Merkhilfe wie das ursprüngliche Geheimnis.", "Treat the memory aid like the original secret.");
        StoryHiddenHelp.Text = Tr("Die Merkhilfe enthält geheime Wörter und wird erst nach deiner Bestätigung angezeigt.", "The memory aid contains secret words and appears only after you confirm reveal.");
        BackupHelp.Text = Tr("Die Merkhilfe ersetzt kein sicheres Backup. Prüfe Wörter und Reihenfolge an deiner ursprünglichen Eingabe.", "The memory aid does not replace a secure backup. Check the words and their order against your original input.");
        SecurityDetailsButton.Content = Tr("Sitzung und Löschung", "Session and clearing");
        SecuritySummary.Text = Tr("Kein gespeicherter Chatverlauf. Leeren und reguläres Beenden löschen die Eingabe und die KI-Sitzung. Die Anzeige wird bei Deaktivierung verdeckt.", "No saved chat history. Clearing and quitting normally erase the input and AI session. The display is concealed when the app deactivates.");
        SecurityDetails.Text = Tr("Die App überschreibt ihre eigenen veränderbaren geheimen Puffer und beendet den isolierten KI-Prozess. Bereits von .NET, Windows oder der Anzeige erzeugte Kopien lassen sich nicht garantiert unwiderruflich löschen. Erzwungenes Beenden kann die Bereinigung verhindern. Gespeichert werden ausschließlich die App-Sprache, die Fenstergröße und der lokale Modellpfad.", "The app overwrites its owned mutable secret buffers and terminates the isolated AI process. Copies already created by .NET, Windows, or rendering cannot be guaranteed to be erased irrevocably. Forced termination may prevent cleanup. Only the app language, window size and local model path are saved.");
        Revalidate(); UpdateControls();
    }

    private void UpdateControls()
    {
        if (!_initialized) return;
        English.IsEnabled = German.IsEnabled = Bip39.IsEnabled = Eff.IsEnabled = ChooseModelButton.IsEnabled = !_running && !_closing;
        Rhyme.IsEnabled = Ballad.IsEnabled = Poem.IsEnabled = ShortStory.IsEnabled = Rap.IsEnabled = !_running && !_closing;
        PhraseInput.IsReadOnly = _running || _closing;
        PhraseInput.Visibility = _inputVisible ? Visibility.Visible : Visibility.Collapsed;
        InputCover.Visibility = _inputVisible ? Visibility.Collapsed : Visibility.Visible;
        RevealInput.IsEnabled = InputVisibilityButton.IsEnabled = !_running && !_closing;
        InputVisibilityButton.Content = _inputVisible ? Tr("Verdecken", "Hide") : Tr("Anzeigen", "Reveal");
        GenerateButton.IsEnabled = _phrase is not null && _modelPath is not null && !_running && !_closing;
        GenerateButton.Visibility = _running ? Visibility.Collapsed : Visibility.Visible;
        GenerateButton.Content = _story is null ? Tr("Merkhilfe erzeugen", "Create memory aid") : Tr("Neu erzeugen", "Generate again");
        CancelButton.Visibility = BusyProgress.Visibility = _running ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.IsEnabled = !_closing;
        GenerationLabel.Text = _running ? Tr("Merkhilfe wird lokal erzeugt…", "Creating the memory aid locally…") : Tr("Jede Generierung beginnt mit einem leeren KI-Kontext.", "Each generation starts with an empty AI context.");
        StorySection.Visibility = _story is null ? Visibility.Collapsed : Visibility.Visible;
        StoryStateLabel.Text = _storyVisible ? Tr("Merkhilfe angezeigt", "Memory aid visible") : Tr("Merkhilfe verdeckt", "Memory aid hidden");
        StoryStateLabel.Foreground = (Brush)FindResource(_storyVisible ? "Amber" : "Secondary");
        StoryVisibilityButton.Content = _storyVisible ? Tr("Verdecken", "Hide") : Tr("Anzeigen", "Reveal");
        StoryScroll.Visibility = _storyVisible ? Visibility.Visible : Visibility.Collapsed;
        StoryHiddenHelp.Visibility = _storyVisible ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SavePreferences()
    {
        if (_testMode) return;
        try { _preferences.Save(); }
        catch { ShowError(Tr("Die Einstellungen konnten nicht gespeichert werden.", "Preferences could not be saved.")); }
    }

    private void ChooseModelClicked(object sender, RoutedEventArgs e)
    {
        if (_running || _closing) return;
        var dialog = new OpenFileDialog { Title = Tr("Lokales GGUF-Modell auswählen", "Choose local GGUF model"), Filter = "GGUF model (*.gguf)|*.gguf|All files (*.*)|*.*", CheckFileExists = true, Multiselect = false, AddToRecent = false };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            _modelPath = ModelFile.Validate(dialog.FileName);
            _preferences.ModelPath = _modelPath;
            ClearStory(); UpdateLanguage(); SavePreferences();
        }
        catch { ShowError(Tr("Wähle eine vollständig lokale, reguläre GGUF-Datei ohne Verknüpfung oder Cloud-Speicher.", "Choose a fully local, regular GGUF file without links or cloud storage.")); }
    }

    private void InputVisibilityClicked(object sender, RoutedEventArgs e)
    { if (!_running && !_closing) { _inputVisible = !_inputVisible; UpdateControls(); if (_inputVisible) PhraseInput.Focus(); } }

    private void ClearClicked(object sender, RoutedEventArgs e) => ClearSession();
    private void CancelClicked(object sender, RoutedEventArgs e) { CancelGeneration(); ClearStory(); UpdateControls(); }
    private void SecurityDetailsClicked(object sender, RoutedEventArgs e) => SecurityDetails.Visibility = SecurityDetails.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

    private void GenerateClicked(object sender, RoutedEventArgs e)
    {
        if (_running || _closing || _phrase is null || _modelPath is null) return;
        ClearStory();
        _running = true; _inputVisible = false;
        var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        _cancellation = cancellation;
        var id = ++_generationId;
        UpdateControls();
        _generation = GenerateAsync(_phrase, _modelPath, cancellation, id);
    }

    private async Task GenerateAsync(ValidatedPhrase phrase, string modelPath, CancellationTokenSource cancellation, int id)
    {
        var prompt = phrase.BuildPrompt(SelectedStyle, _preferences.Language);
        string result = "";
        try
        {
            result = await new RunnerClient().GenerateAsync(ModelFile.Validate(modelPath), prompt, phrase.Words, cancellation.Token);
            if (_closing || id != _generationId || cancellation.IsCancellationRequested) return;
            if (!phrase.VerifyStory(result))
            {
                ShowError(Tr("Die Ausgabe erfüllt die Merkhilfeprüfung nicht. Merkhilfetext und alle markierten Wörter in der richtigen Reihenfolge sind erforderlich. Versuche es erneut oder wähle eine andere Textform.", "The output does not pass the memory aid check. It must include memory aid text and every marked word in the correct order. Try again or choose another style."));
                return;
            }
            _story = result.ToCharArray(); _storyVisible = false;
        }
        catch (OperationCanceledException)
        { if (!_closing && id == _generationId) ShowError(Tr("Die Generierung wurde abgebrochen.", "Generation was cancelled.")); }
        catch
        { if (!_closing && id == _generationId) ShowError(Tr("Die GPU-Generierung wurde sicher abgebrochen. Prüfe Installation, NVIDIA-Treiber, Modell und freien Grafikspeicher. Es wird nicht auf CPU-Ausführung ausgewichen. Es wurde kein Verlauf gespeichert.", "GPU generation stopped safely. Check the installation, NVIDIA driver, model and free GPU memory. CPU execution is not used as a fallback. No history was saved.")); }
        finally
        {
            prompt = ""; result = "";
            if (_cancellation == cancellation) { _cancellation = null; _running = false; }
            cancellation.Dispose(); UpdateControls();
        }
    }

    private void StoryVisibilityClicked(object sender, RoutedEventArgs e)
    {
        if (_storyVisible) { Conceal(); return; }
        if (_story is null || _closing) return;
        var answer = MessageBox.Show(this,
            Tr("Die Merkhilfe enthält deine geheimen Wörter. Beende vorher Bildschirmfreigaben, Aufnahmen und Fernwartung. Windows garantiert keinen Schutz gegen Bildschirmaufnahmen.\n\nFür 10 Minuten anzeigen?", "The memory aid contains your secret words. Stop screen sharing, recording, and remote support first. Windows does not guarantee protection against screen capture.\n\nReveal for 10 minutes?"),
            Tr("Merkhilfe vorübergehend anzeigen?", "Reveal the memory aid temporarily?"), MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (answer != MessageBoxResult.OK || _story is null) return;
        RenderStory(); _storyVisible = true; _revealTimer.Stop(); _revealTimer.Start(); UpdateControls();
    }

    private void RenderStory()
    {
        StoryText.Inlines.Clear();
        if (_story is null) return;
        int cursor = 0;
        while (cursor < _story.Length)
        {
            var opening = Array.IndexOf(_story, '[', cursor);
            if (opening < 0) { StoryText.Inlines.Add(new Run(new string(_story, cursor, _story.Length - cursor))); break; }
            var closing = Array.IndexOf(_story, ']', opening);
            if (closing < 0) { StoryText.Inlines.Add(new Run(new string(_story, cursor, _story.Length - cursor))); break; }
            if (opening > cursor) StoryText.Inlines.Add(new Run(new string(_story, cursor, opening - cursor)));
            StoryText.Inlines.Add(new Run(new string(_story, opening, closing - opening + 1)) { FontWeight = FontWeights.Bold, Foreground = (Brush)FindResource("Blue") });
            cursor = closing + 1;
        }
    }

    private void Conceal()
    {
        if (!_initialized) return;
        _inputVisible = false; _storyVisible = false; _revealTimer.Stop(); StoryText.Inlines.Clear();
        Keyboard.ClearFocus(); UpdateControls();
    }

    private void CancelGeneration() { ++_generationId; _cancellation?.Cancel(); }

    private void ClearStory()
    {
        CancelGeneration(); _revealTimer.Stop(); StoryText.Inlines.Clear();
        if (_story is not null) { CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(_story.AsSpan())); _story = null; }
        _storyVisible = false; ErrorLabel.Text = ""; ErrorLabel.Visibility = Visibility.Collapsed;
        UpdateControls();
    }

    private void ClearSession()
    {
        ClearStory(); _phrase?.Dispose(); _phrase = null;
        _initialized = false; PhraseInput.Clear(); _initialized = true;
        _inputVisible = !_closing; Revalidate();
    }

    internal void ClearForShutdown() { _closing = true; ClearSession(); Conceal(); }

    private async void CloseRequested(object? sender, CancelEventArgs e)
    {
        if (_closeReady) return;
        e.Cancel = true;
        if (_closing) return;
        ClearForShutdown(); SavePreferences();
        if (_generation is not null) try { await _generation; } catch { }
        if (_handle != 0) _ = WTSUnRegisterSessionNotification(_handle);
        _windowSource?.RemoveHook(WindowMessage); _windowSource = null;
        _modelPath = null; _closeReady = true;
        Close(); Application.Current.Shutdown();
    }

    private void ShowError(string message) { ErrorLabel.Text = message; ErrorLabel.Visibility = Visibility.Visible; }

    internal int RunPublicSmokeTests()
    {
        var checks = 0;
        void Check(bool condition) { if (!condition) throw new InvalidOperationException("Public UI fixture failed."); checks++; }
        Check(_preferences.Language == "en" && English.IsChecked == true);
        PhraseInput.Text = "abandon ability able";
        Check(_phrase?.WordCount == 3);
        Check(!PhraseInput.IsUndoEnabled && !PhraseInput.AllowDrop && PhraseInput.ContextMenu is null);
        _story = "<script> **literal** A [abandon] bird found [ability], then became [able].".ToCharArray();
        RenderStory(); _storyVisible = true; UpdateControls();
        Check(StoryText.Inlines.OfType<Run>().Count(run => run.FontWeight == FontWeights.Bold && run.Text.StartsWith('[')) == 3);
        Check(StoryText.Inlines.OfType<Run>().Any(run => run.Text == "[abandon]"));
        Check(string.Concat(StoryText.Inlines.OfType<Run>().Select(run => run.Text)).StartsWith("<script> **literal**", StringComparison.Ordinal));
        Check(StoryText.TextWrapping == TextWrapping.Wrap && ((SolidColorBrush)StoryText.Foreground).Color == Colors.White);
        Conceal(); Check(StoryText.Inlines.Count == 0 && PhraseInput.Visibility == Visibility.Collapsed && !_storyVisible);
        German.IsChecked = true; Check(_preferences.Language == "de" && PhraseTitle.Text == "Geheime Wörter eingeben");
        Eff.IsChecked = true; PhraseInput.Text = "abacus-abdomen"; Check(_phrase?.WordCount == 2);
        Rap.IsChecked = true; Check(SelectedStyle == LiteraryStyle.Rap);
        ClearSession(); Check(PhraseInput.Text.Length == 0 && _phrase is null && _story is null && StoryText.Inlines.Count == 0);
        return checks;
    }

    internal void RenderPublicFixture(string path)
    {
        if (!_testMode) throw new InvalidOperationException("Only fixed public fixtures may be rendered to disk.");
        English.IsChecked = true; Bip39.IsChecked = true; ShortStory.IsChecked = true;
        PhraseInput.Text = "abandon ability able";
        _story = "A traveler chose to [abandon] an old road. With patient [ability], the traveler built a bridge and became [able] to lead the village safely home.".ToCharArray();
        _inputVisible = true; _storyVisible = true; RenderStory(); UpdateControls();
        // Render the complete card layout without opening a window, bypassing
        // capture restrictions, or involving any user-provided phrase.
        var scroll = (ScrollViewer)Content;
        scroll.Content = null; Content = null;
        var surface = new Grid { Width = 940, Background = (Brush)FindResource("Background") };
        surface.Children.Add(Page);
        surface.Measure(new Size(940, double.PositiveInfinity));
        var size = new Size(940, Math.Ceiling(surface.DesiredSize.Height));
        surface.Arrange(new Rect(size)); surface.UpdateLayout();
        var bitmap = new RenderTargetBitmap(940, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(surface);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        var fullPath = System.IO.Path.GetFullPath(path);
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath)!);
        using var output = new System.IO.FileStream(fullPath, System.IO.FileMode.Create, System.IO.FileAccess.Write, System.IO.FileShare.None);
        png.Save(output);
    }

    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowDisplayAffinity(nint hWnd, uint affinity);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hWnd, int attribute, ref int value, int size);
    [DllImport("wtsapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WTSRegisterSessionNotification(nint hWnd, int flags);
    [DllImport("wtsapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WTSUnRegisterSessionNotification(nint hWnd);
}
