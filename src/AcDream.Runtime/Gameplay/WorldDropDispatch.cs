using AcDream.Core.Items;

namespace AcDream.Runtime.Gameplay;

/// <summary>
/// One successfully dispatched inventory-to-world request together with the
/// quantity selected by the retained UI.
/// </summary>
public readonly record struct WorldDropDispatch(
    PendingInventoryRequest Request,
    uint Amount);
