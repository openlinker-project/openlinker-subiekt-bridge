using Subiekt.Bridge.Domain.Common;

namespace Subiekt.Bridge.Domain.Invoices;

/// <summary>
/// An explicit payment selection for a sales document (issue #1). Strict
/// combination rules — the contract deliberately rejects ambiguous requests
/// instead of inferring intent:
/// <list type="bullet">
/// <item>both fields absent — no selection (today's provider defaults apply);</item>
/// <item><c>transfer</c> REQUIRES a positive <c>bankAccountId</c>;</item>
/// <item><c>cash</c> must NOT carry a <c>bankAccountId</c>;</item>
/// <item><c>bankAccountId</c> without an explicit method is rejected;</item>
/// <item>any other method string is rejected.</item>
/// </list>
/// </summary>
public sealed class PaymentSelection
{
    private PaymentSelection(PaymentMethod method, int? bankAccountId)
    {
        Method = method;
        BankAccountId = bankAccountId;
    }

    public PaymentMethod Method { get; }

    /// <summary>Provider-side bank account id (Subiekt <c>RachunekBankowy.Id</c>). Set only for <see cref="PaymentMethod.Transfer"/>.</summary>
    public int? BankAccountId { get; }

    /// <summary>
    /// Parse the wire pair into a selection. Success with <c>null</c> means "no
    /// selection" (both fields absent) — the caller keeps the default behavior.
    /// </summary>
    public static Result<PaymentSelection?> TryCreate(string? method, int? bankAccountId)
    {
        var hasMethod = !string.IsNullOrWhiteSpace(method);

        if (!hasMethod)
        {
            return bankAccountId is null
                ? Result.Success<PaymentSelection?>(null)
                : Result.Failure<PaymentSelection?>(new Error(
                    "payment.bankAccount",
                    "bankAccountId requires an explicit paymentMethod 'transfer'."));
        }

        switch (method!.Trim().ToLowerInvariant())
        {
            case "cash":
                return bankAccountId is null
                    ? Result.Success<PaymentSelection?>(new PaymentSelection(PaymentMethod.Cash, null))
                    : Result.Failure<PaymentSelection?>(new Error(
                        "payment.bankAccount",
                        "paymentMethod 'cash' must not carry a bankAccountId."));

            case "transfer":
                if (bankAccountId is not > 0)
                    return Result.Failure<PaymentSelection?>(new Error(
                        "payment.bankAccount",
                        "paymentMethod 'transfer' requires a positive bankAccountId."));
                return Result.Success<PaymentSelection?>(new PaymentSelection(PaymentMethod.Transfer, bankAccountId));

            default:
                return Result.Failure<PaymentSelection?>(new Error(
                    "payment.method",
                    $"Unrecognized paymentMethod '{method}'. Allowed: 'cash', 'transfer'."));
        }
    }
}
