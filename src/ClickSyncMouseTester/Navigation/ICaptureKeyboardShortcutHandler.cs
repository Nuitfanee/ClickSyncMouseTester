namespace ClickSyncMouseTester.Navigation;

public enum CaptureKeyboardShortcut
{
    StartOrPause
}

public interface ICaptureKeyboardShortcutHandler
{
    bool TryHandleCaptureKeyboardShortcut(CaptureKeyboardShortcut shortcut);
}
