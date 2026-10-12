using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;

namespace PassphraseMemorizer.App;

/// <summary>Paste and editing are allowed. Clipboard export, undo and drag export are disabled.</summary>
public sealed class PrivateTextBox : TextBox
{
    public PrivateTextBox()
    {
        ContextMenu = null;
        IsUndoEnabled = false;
        AllowDrop = false;
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, Deny, CannotExecute));
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Cut, Deny, CannotExecute));
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Undo, Deny, CannotExecute));
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Redo, Deny, CannotExecute));
        DataObject.AddCopyingHandler(this, (_, e) => e.CancelCommand());
        QueryContinueDrag += (_, e) => { e.Action = DragAction.Cancel; e.Handled = true; };
        DataObject.AddPastingHandler(this, (_, e) =>
        {
            if (!e.DataObject.GetDataPresent(DataFormats.UnicodeText) ||
                e.DataObject.GetData(DataFormats.UnicodeText) is not string text ||
                System.Text.Encoding.UTF8.GetByteCount(text) > 8192)
                e.CancelCommand();
        });
    }

    // Avoid exposing phrase text through Windows UI Automation.
    protected override AutomationPeer? OnCreateAutomationPeer() => null;

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 &&
            e.Key is Key.C or Key.X or Key.Z or Key.Y ||
            (Keyboard.Modifiers & ModifierKeys.Control) != 0 && e.Key == Key.Insert ||
            (Keyboard.Modifiers & ModifierKeys.Shift) != 0 && e.Key == Key.Delete)
            e.Handled = true;
        base.OnPreviewKeyDown(e);
    }

    private static void Deny(object sender, ExecutedRoutedEventArgs e) => e.Handled = true;
    private static void CannotExecute(object sender, CanExecuteRoutedEventArgs e) { e.CanExecute = false; e.Handled = true; }
}

/// <summary>Literal, non-selectable presentation with no UI Automation text export.</summary>
public sealed class PrivateStoryText : TextBlock
{
    protected override AutomationPeer? OnCreateAutomationPeer() => null;
}
