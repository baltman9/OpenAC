// src/AcDream.Plugin.Abstractions/IEvents.cs
namespace AcDream.Plugin.Abstractions;

public interface IEvents
{
    event Action<WorldEntitySnapshot> EntitySpawned;

    event Action<double> Tick;

    /// <summary>
    /// Raised once each time the local player finishes entering the world,
    /// including after a reconnect. Plugins are not reloaded across a
    /// reconnect, so this fires again on the same instance.
    /// </summary>
    event Action LoginComplete
    {
        add { }
        remove { }
    }

    /// <summary>
    /// Raised when the in-world session ends, before the session is torn
    /// down, so a handler can still read gameplay state.
    /// </summary>
    event Action Logoff
    {
        add { }
        remove { }
    }

    /// <summary>
    /// Raised with the server's death message when the local player dies.
    /// </summary>
    event Action<string> LocalPlayerDied
    {
        add { }
        remove { }
    }
}
