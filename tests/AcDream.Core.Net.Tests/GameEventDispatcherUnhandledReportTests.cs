using System;
using System.IO;
using AcDream.Core.Net.Messages;
using Xunit;

namespace AcDream.Core.Net.Tests;

public sealed class GameEventDispatcherUnhandledReportTests
{
    private static GameEventEnvelope Envelope(GameEventType type) =>
        new(0u, 0u, type, new byte[12]);

    private static string CaptureStdErr(Action action)
    {
        TextWriter previous = Console.Error;
        var buffer = new StringWriter();
        Console.SetError(buffer);
        try
        {
            action();
        }
        finally
        {
            Console.SetError(previous);
        }

        return buffer.ToString();
    }

    [Fact]
    public void AnUnhandledEventIsReportedOnceAndThenOnlyCounted()
    {
        var dispatcher = new GameEventDispatcher();

        string output = CaptureStdErr(() =>
        {
            dispatcher.Dispatch(Envelope(GameEventType.BookDataResponse));
            dispatcher.Dispatch(Envelope(GameEventType.BookDataResponse));
            dispatcher.Dispatch(Envelope(GameEventType.BookDataResponse));
        });

        Assert.Single(
            output.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("0x00B4", output, StringComparison.Ordinal);
        Assert.Equal(
            3, dispatcher.GetUnhandledCount(GameEventType.BookDataResponse));
    }

    [Fact]
    public void EachKindIsReportedSeparately()
    {
        var dispatcher = new GameEventDispatcher();

        string output = CaptureStdErr(() =>
        {
            dispatcher.Dispatch(Envelope(GameEventType.BookDataResponse));
            dispatcher.Dispatch(Envelope(GameEventType.BookPageDataResponse));
        });

        Assert.Equal(
            2, output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Contains("0x00B4", output, StringComparison.Ordinal);
        Assert.Contains("0x00B8", output, StringComparison.Ordinal);
    }

    [Fact]
    public void AHandledEventIsNotReported()
    {
        var dispatcher = new GameEventDispatcher();
        dispatcher.Register(GameEventType.BookDataResponse, static _ => { });

        string output = CaptureStdErr(
            () => dispatcher.Dispatch(Envelope(GameEventType.BookDataResponse)));

        Assert.Equal(string.Empty, output);
    }
}
