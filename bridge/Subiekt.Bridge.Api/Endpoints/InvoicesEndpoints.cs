using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SferaApi;
using SferaApi.Models;
using SferaApi.Validation;
using Subiekt.Bridge.Application.Ports;
using Subiekt.Bridge.Infrastructure.Sfera;

namespace SferaApi.Endpoints;

// /api/invoices — sales documents. Issue + correction run through the hexagon ports
// (IIssueInvoiceWithBuyer, ICorrectionIssuer -> Sfera write queue). Status reads
// go through the 3A IDocumentStatusReader (separate, non-locking SQL connection).
// All responses use the unified ResponseEnvelope and the OL-contract field shapes.
public static class InvoicesEndpoints
{
    public static void MapInvoicesEndpoints(this IEndpointRouteBuilder app)
    {
        // ISSUE — buyer-upsert + invoice-issue as ONE queued unit of work.
        app.MapPost("/api/invoices", async (
            IIssueInvoiceWithBuyer issueInvoice,
            IAuditLog auditLog,
            IIdempotencyStore idem,
            PdfUrlSigner pdfSigner,
            HttpContext http,
            CreateInvoiceRequestDto req) =>
        {
            if (req.TryValidate(InvoiceValidators.Create, out var validationFail))
                return validationFail;

            var inputJson = System.Text.Json.JsonSerializer.Serialize(req);
            var isParagon = string.Equals(req.DocumentType, "PA", StringComparison.OrdinalIgnoreCase);
            var currency = string.IsNullOrWhiteSpace(req.Currency) ? "PLN" : req.Currency;
            // Compute the request base once; reused for every pdfUrl below (signed URL shape
            // lives in PdfUrlSigner.BuildUrl — endpoints just supply the fallback base).
            var requestBase = PdfUrlSigner.RequestBase(http.Request);

            try
            {
                // Idempotency gate: a retried issueInvoice (same idempotencyKey) returns
                // the SAME document, never a duplicate fiscal document.
                if (!string.IsNullOrWhiteSpace(req.IdempotencyKey))
                {
                    var hit = await idem.TryGetAsync(req.IdempotencyKey);
                    if (hit.IsSuccess && hit.Value is { } prior)
                    {
                        var existingNumer = prior.ProviderInvoiceNumber;
                        await auditLog.LogAsync("UtworzFaktura(idempotent-hit)", inputJson,
                            System.Text.Json.JsonSerializer.Serialize(new { id = prior.ProviderInvoiceId, numer = existingNumer }), 200);
                        return Results.Ok(new ResponseEnvelope<object>
                        {
                            Success = true,
                            Data = new
                            {
                                providerInvoiceId = prior.ProviderInvoiceId,
                                providerInvoiceNumber = existingNumer,
                                documentType = isParagon ? "PA" : "FV",
                                currency,
                                regulatoryStatus = isParagon ? "none" : "pending",
                                pdfUrl = pdfSigner.BuildUrl(prior.ProviderInvoiceId, requestBase),
                                state = "issued",
                                idempotent = true,
                                orderId = req.OrderId,
                                id = prior.ProviderInvoiceId,
                                numer = existingNumer,
                                kontrahentId = req.KontrahentId,
                                iloscPozycji = req.Lines.Count,
                                status = "zatwierdzony"
                            }
                        });
                    }
                }

                var built = SferaApi.Contracts.InvoiceContractMapper.Build(req, currency);
                if (built.IsFailure)
                {
                    await auditLog.LogAsync("UtworzFaktura", inputJson, null, 422, built.Error.ToString());
                    return Results.Json(new ResponseEnvelope<object>
                    {
                        Success = false,
                        Error = new BridgeError { Code = "validation", Reason = built.Error.Message }
                    }, statusCode: 422);
                }

                var (document, buyer) = built.Value;

                var result = await issueInvoice.IssueAsync(document, buyer);
                if (result.IsFailure)
                {
                    var code = result.Error.Code;
                    var bex = code == "unreachable"
                        ? BridgeException.Unreachable(result.Error.Message)
                        : BridgeException.Rejected(result.Error.Message);
                    await auditLog.LogAsync("UtworzFaktura", inputJson, null, bex.HttpStatus, result.Error.ToString());
                    return EndpointHelpers.BridgeFail(bex);
                }

                var id = result.Value.Id;
                var numer = result.Value.Numer;

                if (!string.IsNullOrWhiteSpace(req.IdempotencyKey))
                    await idem.StoreAsync(req.IdempotencyKey, new IdempotentInvoice(id, numer));

                var opName = isParagon ? "UtworzParagon" : "UtworzFaktura";
                var outputJson = System.Text.Json.JsonSerializer.Serialize(new { id, numer });
                await auditLog.LogAsync(opName, inputJson, outputJson, 200);

                return Results.Ok(new ResponseEnvelope<object>
                {
                    Success = true,
                    Data = new
                    {
                        providerInvoiceId = id,
                        providerInvoiceNumber = numer,
                        documentType = isParagon ? "PA" : "FV",
                        currency,
                        regulatoryStatus = isParagon ? "none" : "pending",
                        pdfUrl = pdfSigner.BuildUrl(id, requestBase),
                        state = "issued",
                        idempotent = false,
                        orderId = req.OrderId,
                        id,
                        numer,
                        kontrahentId = document.BuyerId,
                        iloscPozycji = req.Lines.Count,
                        status = "zatwierdzony"
                    }
                });
            }
            catch (Exception ex)
            {
                var bex = BridgeException.Classify(ex);
                await auditLog.LogAsync("UtworzFaktura", inputJson, null, bex.HttpStatus, bex.Detail);
                return EndpointHelpers.BridgeFail(bex);
            }
        });

        // STATUS — read through the 3A IDocumentStatusReader (separate SQL connection).
        app.MapGet("/api/invoices/{id:int}/status", async (int id, IDocumentStatusReader statusReader, IAuditLog auditLog, PdfUrlSigner pdfSigner, HttpContext http) =>
        {
            var statusResult = await statusReader.GetStatusAsync(id);
            if (statusResult.IsFailure)
            {
                var bex = statusResult.Error.Code == "unreachable"
                    ? BridgeException.Unreachable(statusResult.Error.Message)
                    : BridgeException.Rejected(statusResult.Error.Message);
                await auditLog.LogAsync("StatusDokumentu", id.ToString(), null, bex.HttpStatus, statusResult.Error.ToString());
                return EndpointHelpers.BridgeFail(bex);
            }

            var status = statusResult.Value;
            return Results.Ok(new ResponseEnvelope<object>
            {
                Success = true,
                Data = new
                {
                    dokumentId = status.Id,
                    status = status.Status,
                    numer = status.Numer,
                    netto = status.Netto,
                    vat = status.Vat,
                    brutto = status.Brutto,
                    regulatoryStatus = status.Ksef?.Status ?? "none",
                    clearanceReference = status.Ksef?.Reference,
                    // Only advertise a PDF link for a document that actually exists — a
                    // not_found status must not carry a (signable) pdfUrl that 404s.
                    pdfUrl = string.Equals(status.Status, "not_found", StringComparison.OrdinalIgnoreCase)
                        ? null
                        : pdfSigner.BuildUrl(status.Id, PdfUrlSigner.RequestBase(http.Request)),   // status GET: single call, computed inline
                    ksef = new
                    {
                        status = status.Ksef?.Status,
                        submitted = status.Ksef?.Submitted,
                        reference = status.Ksef?.Reference
                    },
                    createdAt = status.CreatedAt
                }
            });
        });

        // PDF DOWNLOAD — render the issued FV/PA to application/pdf bytes.
        // SELF-AUTHENTICATED by a signed token in the query string (?t=...): this route
        // is EXEMPT from the static Bearer (browser opens it as a plain <a href> with no
        // Authorization header — see the auth middleware in Program.cs). The render runs
        // headlessly through the Sfera write queue (serialized; never races issuance).
        app.MapGet("/api/invoices/{id:int}/pdf", async (
            int id,
            string? t,
            IInvoicePdfRenderer renderer,
            IAuditLog auditLog,
            PdfUrlSigner pdfSigner,
            HttpContext http) =>
        {
            // 1. Signed-token gate (constant-time; fails closed when no secret configured).
            //    The token itself is never logged.
            if (!pdfSigner.Validate(id, t))
            {
                await auditLog.LogAsync("PobierzPdf", id.ToString(), null, StatusCodes.Status403Forbidden, "invalid_or_missing_token");
                return Results.Json(new ResponseEnvelope<object>
                {
                    Success = false,
                    Error = new BridgeError { Code = "unauthorized", Reason = "Brakujący lub nieprawidłowy token." }
                }, statusCode: StatusCodes.Status403Forbidden);
            }

            // 2. Render via the Sfera write queue (serialized single-writer).
            var result = await renderer.RenderAsync(id, http.RequestAborted);
            if (result.IsFailure)
            {
                if (result.Error.Code == "not_found")
                {
                    await auditLog.LogAsync("PobierzPdf", id.ToString(), null, StatusCodes.Status404NotFound, result.Error.ToString());
                    return Results.Json(new ResponseEnvelope<object>
                    {
                        Success = false,
                        Error = new BridgeError { Code = "not_found", Reason = $"Dokument {id} nie istnieje." }
                    }, statusCode: StatusCodes.Status404NotFound);
                }

                var bex = result.Error.Code == "unreachable"
                    ? BridgeException.Unreachable(result.Error.Message)
                    : BridgeException.Rejected(result.Error.Message);
                await auditLog.LogAsync("PobierzPdf", id.ToString(), null, bex.HttpStatus, result.Error.ToString());
                return EndpointHelpers.BridgeFail(bex);
            }

            var pdf = result.Value;
            await auditLog.LogAsync("PobierzPdf", id.ToString(),
                System.Text.Json.JsonSerializer.Serialize(new { id, bytes = pdf.Length }), 200);
            return Results.File(pdf, "application/pdf", fileDownloadName: $"dokument-{id}.pdf");
        });

        // KOREKTA — correction (faktura korygująca) via the ICorrectionIssuer port.
        app.MapPost("/api/invoices/{id:int}/corrections", async (int id, ICorrectionIssuer correctionIssuer, IAuditLog auditLog, IIdempotencyStore idem, KorektaRequestDto req) =>
        {
            if (req.TryValidate(InvoiceValidators.Korekta, out var validationFail))
                return validationFail;

            var inputJson = System.Text.Json.JsonSerializer.Serialize(req);
            try
            {
                // Idempotency gate: a retried correction (same idempotencyKey) returns
                // the SAME korekta, never a duplicate faktura korygująca.
                if (!string.IsNullOrWhiteSpace(req.IdempotencyKey))
                {
                    var hit = await idem.TryGetAsync(req.IdempotencyKey);
                    if (hit.IsSuccess && hit.Value is { } prior)
                    {
                        await auditLog.LogAsync("UtworzKorekte(idempotent-hit)", inputJson,
                            System.Text.Json.JsonSerializer.Serialize(new { korId = prior.ProviderInvoiceId, korNumer = prior.ProviderInvoiceNumber }), 200);
                        return Results.Ok(new ResponseEnvelope<object>
                        {
                            Success = true,
                            Data = new
                            {
                                providerInvoiceId = prior.ProviderInvoiceId,
                                providerInvoiceNumber = prior.ProviderInvoiceNumber,
                                korygowanyId = id,
                                przyczyna = req.Przyczyna,
                                state = "issued",
                                idempotent = true
                            }
                        });
                    }
                }

                var lines = (req.Lines ?? new List<KorektaLineDto>())
                    .Select(l => new CorrectionLine(l.Lp, l.NowaIlosc, l.NowaCena))
                    .ToList();

                var result = await correctionIssuer.IssueCorrectionAsync(id, req.Przyczyna, lines);
                if (result.IsFailure)
                {
                    var code = result.Error.Code;
                    var bex = code == "unreachable"
                        ? BridgeException.Unreachable(result.Error.Message)
                        : BridgeException.Rejected(result.Error.Message);
                    await auditLog.LogAsync("UtworzKorekte", inputJson, null, bex.HttpStatus, result.Error.ToString());
                    return EndpointHelpers.BridgeFail(bex);
                }

                var (korId, korNumer) = (result.Value.Id, result.Value.Numer);

                if (!string.IsNullOrWhiteSpace(req.IdempotencyKey))
                    await idem.StoreAsync(req.IdempotencyKey, new IdempotentInvoice(korId, korNumer));

                await auditLog.LogAsync("UtworzKorekte", inputJson, System.Text.Json.JsonSerializer.Serialize(new { korId, korNumer }), 200);
                return Results.Ok(new ResponseEnvelope<object>
                {
                    Success = true,
                    Data = new { providerInvoiceId = korId, providerInvoiceNumber = korNumer, korygowanyId = id, przyczyna = req.Przyczyna, state = "issued", idempotent = false }
                });
            }
            catch (Exception ex)
            {
                var bex = BridgeException.Classify(ex);
                await auditLog.LogAsync("UtworzKorekte", System.Text.Json.JsonSerializer.Serialize(req), null, bex.HttpStatus, bex.Detail);
                return EndpointHelpers.BridgeFail(bex);
            }
        });
    }
}
