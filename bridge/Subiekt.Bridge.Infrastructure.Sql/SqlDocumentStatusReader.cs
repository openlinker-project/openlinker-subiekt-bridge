using Dapper;
using Subiekt.Bridge.Application.Ports;
using Subiekt.Bridge.Domain.Common;

namespace Subiekt.Bridge.Infrastructure.Sql;

/// <summary>
/// Dapper-backed <see cref="IDocumentStatusReader"/>. Ports the legacy
/// <c>DokumentyElektroniczneService.GetStatusAsync</c>: reads the document plus
/// its KSeF signals (Dokumenty + a correlated NumeryKSeFDokumentow lookup) and
/// maps them onto the OL contract's document and KSeF status values.
/// </summary>
public sealed class SqlDocumentStatusReader : IDocumentStatusReader
{
    // Same columns/tables as the legacy reader. The correlated subquery yields the
    // KSEF_ID (NumeryKSeFDokumentow.Numer) joined by Dokument_Id.
    private const string Sql = @"
        SELECT TOP 1
            d.Id                              AS Id,
            d.NumerWewnetrzny_PelnaSygnatura  AS Numer,
            d.DataSprzedazy                   AS DataSprzedazy,
            d.Wartosc_NettoPoRabacie          AS Netto,
            d.Wartosc_VatPoRabacie            AS Vat,
            d.Wartosc_BruttoPoRabacie         AS Brutto,
            d.DataWystawieniaNadanaPrzezKSEF  AS KsefDate,
            d.DokumentHandlowy_AwariaKSeFId   AS AwariaKsefId,
            (SELECT TOP 1 n.Numer
               FROM ModelDanychContainer.NumeryKSeFDokumentow n
              WHERE n.Dokument_Id = d.Id)     AS KsefId
        FROM ModelDanychContainer.Dokumenty d
        WHERE d.Id = @id;";

    private readonly ISqlConnectionFactory _factory;

    public SqlDocumentStatusReader(ISqlConnectionFactory factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    // Flat projection of the SELECT; mapped to the public DocumentStatus below.
    private sealed record Row(
        int Id,
        string? Numer,
        DateTime? DataSprzedazy,
        decimal? Netto,
        decimal? Vat,
        decimal? Brutto,
        DateTime? KsefDate,
        int? AwariaKsefId,
        string? KsefId);

    public async Task<Result<DocumentStatus>> GetStatusAsync(int documentId, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var conn = _factory.Create();
            var row = await conn.QueryFirstOrDefaultAsync<Row>(
                new CommandDefinition(Sql, new { id = documentId }, cancellationToken: cancellationToken))
                .ConfigureAwait(false);

            if (row is null)
                return Result.Success(new DocumentStatus(documentId, "not_found", null, null, null, null, null, null));

            // Map Subiekt's KSeF signals onto the OL contract's 5 values.
            string ksefStatus;
            if (!string.IsNullOrWhiteSpace(row.KsefId))
                ksefStatus = "accepted";        // KSeF accepted & returned a KSEF_ID
            else if (row.AwariaKsefId.HasValue)
                ksefStatus = "rejected";        // KSeF send failure (awaria) registered
            else if (row.KsefDate.HasValue)
                ksefStatus = "sent";            // sent, KSeF assigned a date, awaiting number
            else
                ksefStatus = "pending";         // issued, not yet sent/confirmed

            var numer = row.Numer ?? "";
            var status = !string.IsNullOrWhiteSpace(numer) ? "zatwierdzony" : "bufor";

            var ksef = new KsefStatus(
                Status: ksefStatus,
                Submitted: row.KsefDate.HasValue || !string.IsNullOrWhiteSpace(row.KsefId),
                Reference: row.KsefId);

            return Result.Success(new DocumentStatus(
                Id: row.Id,
                Status: status,
                Numer: numer,
                Netto: row.Netto,
                Vat: row.Vat,
                Brutto: row.Brutto,
                CreatedAt: row.DataSprzedazy,
                Ksef: ksef));
        }
        catch (Exception ex)
        {
            return Result.Failure<DocumentStatus>(
                new Error(SqlErrorClassifier.Classify(ex), "Nie udało się odczytać statusu dokumentu."));
        }
    }
}
