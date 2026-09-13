namespace AcDream.App.Input;

/// <summary>
/// The window's one focus edge, fanned out to the owners that react to it:
/// mouse look ends when the window goes to the background, and the runtime
/// settings fold focus into the effective display ("UI Only in Background").
/// The pointer owner is resolved per call because it is composed after the
/// window exists.
/// </summary>
internal sealed class WindowFocusRouter
{
    private readonly Func<CameraPointerInputController?> _cameraPointerInput;
    private readonly Action<bool> _windowFocused;

    public WindowFocusRouter(
        Func<CameraPointerInputController?> cameraPointerInput,
        Action<bool> windowFocused)
    {
        _cameraPointerInput = cameraPointerInput
            ?? throw new ArgumentNullException(nameof(cameraPointerInput));
        _windowFocused = windowFocused
            ?? throw new ArgumentNullException(nameof(windowFocused));
    }

    public void HandleFocusChanged(bool focused)
    {
        _cameraPointerInput()?.HandleFocusChanged(focused);
        _windowFocused(focused);
    }
}
