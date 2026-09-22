using System.Numerics;

namespace AcDream.UI.Abstractions.Panels.Chat;

public static class RetailChatColorTable
{
    /// <summary>The colours, indexed by log text type. Kept in the runtime so every host reads one table.</summary>
    public static IReadOnlyList<Vector4> Colors => AcDream.Runtime.Chat.RuntimeChatColors.Colors;

    public static bool TryGetColor(uint logTextType, out Vector4 color) =>
        AcDream.Runtime.Chat.RuntimeChatColors.TryGetColor(logTextType, out color);
}
