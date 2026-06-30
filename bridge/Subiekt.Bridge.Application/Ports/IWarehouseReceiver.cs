using Subiekt.Bridge.Domain.Common;

namespace Subiekt.Bridge.Application.Ports;

/// <summary>Reference to a created warehouse-receipt document (przyjęcie zewnętrzne / PZ).</summary>
public readonly record struct WarehouseReceiptRef(int Id, string Numer);

/// <summary>
/// Port for warehouse goods receipts (przyjęcie magazynowe / PZ). The adapter creates a
/// real warehouse document through the provider so stock moves through the provider's own
/// accounting; the result carries the created document id and full number.
/// </summary>
public interface IWarehouseReceiver
{
    Task<Result<WarehouseReceiptRef>> ReceiveAsync(
        string symbol,
        decimal quantity,
        string magazyn,
        string? batchNumber = null,
        string? note = null,
        CancellationToken cancellationToken = default);
}
