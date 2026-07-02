using Subiekt.Bridge.Domain.Common;

namespace Subiekt.Bridge.Application.Ports;

/// <summary>
/// Sets the seller's default ("Podstawowy") bank account (issue #1 stretch goal).
/// Implemented by the Sfera adapter through the single write worker — the flip is
/// a Podmiot-side business-object write (<c>Podmiot.RachunekPodstawowy</c>); the
/// previous default clears automatically. Idempotent: selecting the current
/// default succeeds without a write.
/// </summary>
public interface IDefaultBankAccountWriter
{
    /// <summary>Make <paramref name="bankAccountId"/> the seller's default account.</summary>
    Task<Result> SetDefaultAsync(int bankAccountId, CancellationToken cancellationToken = default);
}
