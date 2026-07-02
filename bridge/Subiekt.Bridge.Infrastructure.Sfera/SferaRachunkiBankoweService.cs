using System.Linq.Expressions;
using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Subiekt.Bridge.Infrastructure.Sfera;

/// <summary>
/// Sets the seller's default ("Podstawowy") bank account (issue #1 stretch goal).
/// The flag lives on the PODMIOT side — <c>Podmiot.RachunekPodstawowy</c> — and is
/// sponsored only by the Podmiot business object; a direct write of
/// <c>RachunekBankowy.WlascicielPodstawowego</c> throws
/// <c>UnsponsoredModificationException</c>. Mechanism verified live, see
/// <c>docs/spikes/bank-account-probe-findings.md</c> s.6.
/// </summary>
public sealed class SferaRachunkiBankoweService
{
    private readonly ILogger<SferaRachunkiBankoweService> _log;
    private readonly SferaObjectAccessor _acc;

    public SferaRachunkiBankoweService(ILogger<SferaRachunkiBankoweService> log)
    {
        _log = log;
        _acc = new SferaObjectAccessor(log);
    }

    /// <summary>
    /// Make <paramref name="accountId"/> the owner's default account. Idempotent:
    /// when the account already is the default, no write happens.
    /// </summary>
    public void UstawRachunekPodstawowy(SferaSession sfera, int accountId)
    {
        var uchwyt = sfera.Uchwyt;
        var conn = uchwyt.PodajPolaczenie();
        if (conn.State != System.Data.ConnectionState.Open)
            conn.Open();

        // Pre-check: the account must exist, be active, and belong to the seller.
        int ownerId;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT rb.Wlasciciel_Id, rb.Aktywny
                FROM ModelDanychContainer.CentraGromadzeniaFinansow_RachunekBankowy rb
                WHERE rb.Id = @id
                  AND rb.Wlasciciel_Id = (SELECT TOP 1 Id FROM ModelDanychContainer.Podmioty WHERE Typ = 2 AND Podtyp = 11)";
            cmd.Parameters.AddWithValue("@id", accountId);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                throw new ArgumentException($"Rachunek bankowy {accountId} nie istnieje lub nie należy do sprzedawcy.");
            ownerId = reader.GetInt32(0);
            if (!reader.GetBoolean(1))
                throw new ArgumentException($"Rachunek bankowy {accountId} jest nieaktywny.");
        }

        // Load the owner's Podmiot business object via the IPodmioty facade.
        var podmiotType = SferaReflection.RequireType("InsERT.Moria.ModelDanych.Podmiot, InsERT.Moria.ModelDanych");
        var facadeType = SferaReflection.RequireType("InsERT.Moria.Klienci.IPodmioty, InsERT.Moria.Klienci");
        var facade = uchwyt.PodajObiektTypu(facadeType);

        var bo = FindByIdViaZnajdz(facade, podmiotType, ownerId)
            ?? throw new InvalidOperationException($"IPodmioty.Znajdz did not resolve podmiot {ownerId}.");
        var dane = bo.GetType().GetProperty("Dane", SferaObjectAccessor.Flags)?.GetValue(bo)
            ?? throw new InvalidOperationException("Podmiot BO.Dane is null");
        var daneType = dane.GetType();

        // Idempotency: already the default -> no-op.
        var current = _acc.GetProperty(dane, daneType, "RachunekPodstawowy");
        if (current is not null && Equals(_acc.GetProperty(current, current.GetType(), "Id"), accountId))
        {
            _log.LogInformation("Rachunek {id} już jest podstawowy — bez zmian", accountId);
            return;
        }

        // The target entity must come from the podmiot's OWN unit of work — its
        // Rachunki collection (cross-context assignment is rejected by Sfera).
        object? target = null;
        if (_acc.GetProperty(dane, daneType, "Rachunki") is System.Collections.IEnumerable accounts)
        {
            foreach (var account in accounts)
            {
                if (account is not null && Equals(_acc.GetProperty(account, account.GetType(), "Id"), accountId))
                {
                    target = account;
                    break;
                }
            }
        }
        if (target is null)
            throw new InvalidOperationException($"Rachunek {accountId} nie występuje w Podmiot.Rachunki właściciela {ownerId}.");

        _acc.SetProperty(dane, daneType, "RachunekPodstawowy", target);

        var zapisz = bo.GetType().GetMethod("Zapisz", SferaObjectAccessor.Flags, Type.EmptyTypes)
            ?? throw new InvalidOperationException("Podmiot BO.Zapisz() not found");
        var saved = zapisz.Invoke(bo, null);
        if (saved is not true)
            throw new InvalidOperationException(
                $"Sfera odrzuciła zmianę rachunku podstawowego (Zapisz={saved}). "
                + _acc.CollectValidationErrors(bo, bo.GetType(), includeDocumentLevel: true, includeStateHint: true));

        _log.LogInformation("Rachunek podstawowy zmieniony na {id} (podmiot {owner})", accountId, ownerId);
    }

    // facade.Znajdz(Expression<Func<TEntity,bool>>) built reflectively — same
    // pattern the live probe validated for IPodmioty.
    private static object? FindByIdViaZnajdz(object facade, Type entityType, int id)
    {
        var param = Expression.Parameter(entityType, "x");
        var body = Expression.Equal(Expression.Property(param, "Id"), Expression.Constant(id));
        var funcType = typeof(Func<,>).MakeGenericType(entityType, typeof(bool));
        var lambda = Expression.Lambda(funcType, body, param);
        var exprType = typeof(Expression<>).MakeGenericType(funcType);

        var znajdz = facade.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "Znajdz"
                && m.GetParameters().Length == 1
                && m.GetParameters()[0].ParameterType == exprType)
            ?? throw new InvalidOperationException($"Znajdz(Expression<Func<{entityType.Name},bool>>) not found on {facade.GetType().Name}");

        return znajdz.Invoke(facade, new object[] { lambda });
    }
}
