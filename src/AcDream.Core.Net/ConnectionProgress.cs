namespace AcDream.Core.Net;

public enum ConnectionPhase
{
    Inactive,
    Connecting,
    CheckingData,
    Ready,
    Unsupported,
    Failed,
}

public readonly record struct ConnectionProgress(ConnectionPhase Phase, string? Error = null);

/// <summary>The server went silent and the session was given up.</summary>
public sealed class ServerConnectionLostException : Exception
{
    public ServerConnectionLostException()
        : base("The connection to the server was lost.")
    {
    }
}

public sealed class UnsupportedDataUpdateException : NotSupportedException
{
    public UnsupportedDataUpdateException()
        : base("This server requires a game-data update. OpenAC does not support downloading DAT updates yet. Install the server's required data files before connecting.")
    {
    }
}
