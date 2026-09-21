using AcDream.Launcher.Core.Launching;
using AcDream.Launcher.ViewModels;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

namespace AcDream.Launcher;

/// <summary>
/// A windowless session's console in a window of its own: the end of what the
/// session has written, and a line to send it.
/// </summary>
public sealed partial class SessionConsoleWindow : Window
{
    /// <summary>How many of the newest lines are drawn; the file itself keeps more.</summary>
    private const int MostLinesShown = 1500;

    private static readonly IBrush NoticeBrush = new SolidColorBrush(Color.FromRgb(0x7A, 0x88, 0x90));

    private readonly DispatcherTimer _poll;
    private readonly Dictionary<int, IBrush> _brushes = [];
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
        if (sender is not SessionConsoleViewModel viewModel
            || this.FindControl<SelectableTextBlock>("OutputText") is not { } output
            || this.FindControl<ScrollViewer>("OutputScroll") is not { } scroll)
        {
            return;
        }

        // Follow the newest line, as a console does -- unless the reader has
        // scrolled up to look at something, which a new line must not undo.
        bool atEnd = scroll.Offset.Y >= scroll.Extent.Height - scroll.Viewport.Height - 4d;

        string[] lines = viewModel.Output.Split('\n');
        int first = Math.Max(0, lines.Length - MostLinesShown);
        var inlines = new InlineCollection();
        for (int index = first; index < lines.Length; index++)
        {
            string line = lines[index].TrimEnd('\r');
            if (index == lines.Length - 1 && line.Length == 0)
                break;
            foreach (SessionConsoleRun run in SessionConsoleText.ParseLine(line))
            {
                var inline = new Run(run.Text);
                if (run.Dim)
                    inline.Foreground = NoticeBrush;
                else if (run.Color is { } rgb)
                    inline.Foreground = BrushFor(rgb);
                inlines.Add(inline);
            }
            inlines.Add(new LineBreak());
        }

        output.Inlines = inlines;
        if (atEnd)
            Dispatcher.UIThread.Post(scroll.ScrollToEnd, DispatcherPriority.Background);
    }

    private IBrush BrushFor(int rgb)
    {
        if (!_brushes.TryGetValue(rgb, out IBrush? brush))
        {
            brush = new SolidColorBrush(Color.FromRgb(
                (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
            _brushes[rgb] = brush;
        }
        return brush;
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
