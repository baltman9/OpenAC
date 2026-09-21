using AcDream.Launcher.ViewModels;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace AcDream.Launcher;

/// <summary>
/// A windowless session's console in a window of its own: the end of what the
/// session has written, and a line to send it.
/// </summary>
public sealed partial class SessionConsoleWindow : Window
{
    private readonly DispatcherTimer _poll;
    private SessionConsoleViewModel? _observed;

    public SessionConsoleWindow()
    {
        InitializeComponent();
        _poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _poll.Tick += OnPoll;
        DataContextChanged += OnDataContextChanged;
        Opened += OnOpened;
        Closed += OnClosed;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        (DataContext as SessionConsoleViewModel)?.Refresh();
        _poll.Start();
        this.FindControl<TextBox>("InputBox")?.Focus();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _poll.Stop();
        _poll.Tick -= OnPoll;
        DataContextChanged -= OnDataContextChanged;
        Observe(null);
        Opened -= OnOpened;
        Closed -= OnClosed;
    }

    private void OnPoll(object? sender, EventArgs e) =>
        (DataContext as SessionConsoleViewModel)?.Refresh();

    private void OnDataContextChanged(object? sender, EventArgs e) =>
        Observe(DataContext as SessionConsoleViewModel);

    private void Observe(SessionConsoleViewModel? viewModel)
    {
        if (ReferenceEquals(_observed, viewModel))
            return;
        if (_observed is not null)
            _observed.OutputChanged -= OnOutputChanged;
        _observed = viewModel;
        if (_observed is not null)
            _observed.OutputChanged += OnOutputChanged;
    }

    private void OnOutputChanged(object? sender, EventArgs e)
    {
        // Keep the newest line in view, as a console does.
        if (this.FindControl<TextBox>("OutputBox") is { } output)
            output.CaretIndex = output.Text?.Length ?? 0;
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not SessionConsoleViewModel viewModel)
            return;
        if (viewModel.SendCommand.CanExecute(null))
            viewModel.SendCommand.Execute(null);
        e.Handled = true;
    }
}
