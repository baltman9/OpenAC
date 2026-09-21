using System.Text;
using AcDream.Launcher.Core.Orchestration;

namespace AcDream.Launcher.ViewModels;

/// <summary>
/// One windowless session's console: what the session has written, and a
/// line to send it. The session writes its console to its log file, so the
/// output is that file's tail, read again whenever the file has grown.
/// </summary>
public sealed class SessionConsoleViewModel : ObservableObject
{
    /// <summary>How much of the end of the log is shown.</summary>
    private const int TailBytes = 256 * 1024;

    private readonly ILauncherOrchestrator _orchestrator;
    private readonly string _sessionId;
    private string _output = string.Empty;
    private string _input = string.Empty;
    private string _notice = string.Empty;
    private long _shownLength = -1;
    private DateTime _shownWrittenAt;

    public SessionConsoleViewModel(
        ILauncherOrchestrator orchestrator,
        string sessionId,
        string title)
    {
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        _sessionId = sessionId;
        Title = title;
        SendCommand = new RelayCommand(Send, () => Input.Trim().Length > 0);
    }

    /// <summary>The window's title: whose console this is.</summary>
    public string Title { get; }

    /// <summary>The end of what the session has written.</summary>
    public string Output { get => _output; private set => SetProperty(ref _output, value); }

    /// <summary>The line being typed.</summary>
    public string Input
    {
        get => _input;
        set
        {
            if (SetProperty(ref _input, value ?? string.Empty))
                SendCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>Why the last line could not be sent, or empty.</summary>
    public string Notice { get => _notice; private set => SetProperty(ref _notice, value); }

    public RelayCommand SendCommand { get; }

    /// <summary>Raised after <see cref="Output"/> changes, so the view can scroll to the end.</summary>
    public event EventHandler? OutputChanged;

    /// <summary>Reads the log again if it has changed. Cheap when it has not.</summary>
    public void Refresh()
    {
        string? path = _orchestrator.GetSessionLogPath(_sessionId);
        if (path is null)
            return;
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists
                || (file.Length == _shownLength && file.LastWriteTimeUtc == _shownWrittenAt))
            {
                return;
            }

            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long start = Math.Max(0, stream.Length - TailBytes);
            stream.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            string text = reader.ReadToEnd();
            if (start > 0)
            {
                // The cut fell inside a line; show whole lines only.
                int firstBreak = text.IndexOf('\n');
                if (firstBreak >= 0)
                    text = text[(firstBreak + 1)..];
            }

            _shownLength = file.Length;
            _shownWrittenAt = file.LastWriteTimeUtc;
            Output = text;
            OutputChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (IOException)
        {
            // The session is writing or rotating the file; the next poll reads it.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void Send()
    {
        string line = Input.Trim();
        if (line.Length == 0)
            return;
        if (_orchestrator.TrySendConsoleLine(_sessionId, line))
        {
            Input = string.Empty;
            Notice = string.Empty;
        }
        else
        {
            Notice = "Not sent: the session is not running.";
        }
    }
}
