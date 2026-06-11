using ClickSyncMouseTester.Models;
using System;
using System.Runtime.Versioning;

namespace ClickSyncMouseTester.Services;

[SupportedOSPlatform("windows")]
public class RawInputKeyDetectionService : IDisposable
{
    private readonly IRawMouseControlInputSource _mouseControlInputSource;

    private readonly IRawKeyboardInputSource _keyboardInputSource;

    private bool _disposed;

    public event EventHandler<RawMouseButtonInputEventArgs> MouseButtonInput;

    public event EventHandler<RawMouseWheelInputEventArgs> MouseWheelInput;

    public event EventHandler<RawKeyboardInputEventArgs> KeyboardInput;

    public RawInputKeyDetectionService(IRawInputBroker broker)
        : this(broker, broker)
    {
    }

    public RawInputKeyDetectionService(IRawMouseControlInputSource mouseControlInputSource, IRawKeyboardInputSource keyboardInputSource)
    {
        if (mouseControlInputSource == null)
        {
            throw new ArgumentNullException(nameof(mouseControlInputSource));
        }
        if (keyboardInputSource == null)
        {
            throw new ArgumentNullException(nameof(keyboardInputSource));
        }
        _mouseControlInputSource = mouseControlInputSource;
        _keyboardInputSource = keyboardInputSource;
        _mouseControlInputSource.MouseButtonInput += OnHostMouseButtonInput;
        _mouseControlInputSource.MouseWheelInput += OnHostMouseWheelInput;
        _keyboardInputSource.KeyboardInput += OnHostKeyboardInput;
    }

    private void OnHostMouseButtonInput(object sender, RawMouseButtonInputEventArgs e)
    {
        MouseButtonInput?.Invoke(this, e);
    }

    private void OnHostMouseWheelInput(object sender, RawMouseWheelInputEventArgs e)
    {
        MouseWheelInput?.Invoke(this, e);
    }

    private void OnHostKeyboardInput(object sender, RawKeyboardInputEventArgs e)
    {
        KeyboardInput?.Invoke(this, e);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _mouseControlInputSource.MouseButtonInput -= OnHostMouseButtonInput;
            _mouseControlInputSource.MouseWheelInput -= OnHostMouseWheelInput;
            _keyboardInputSource.KeyboardInput -= OnHostKeyboardInput;
        }
    }

    void IDisposable.Dispose()
    {
        this.Dispose();
    }
}





