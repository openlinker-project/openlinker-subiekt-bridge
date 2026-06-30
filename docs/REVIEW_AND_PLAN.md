# Subiekt Bridge — Code Review, Security Review & Plan Naprawczy

> Zakres: backend `.NET` (`bridge/SferaApi`) + warstwa API. Front (`cockpit/`) pominięty zgodnie z poleceniem.
> Cel docelowy: bridge ma stać się pełnoprawnym **adapterem Subiekt nexo dla OpenLinkera**, w standardzie heksagonalnym (ports & adapters), analogicznym do `openlinker` (`libs/core` = domena bez frameworka, `apps/*` = aplikacja, adaptery jako pluginy).

---

## 1. Werdykt: czy to jest repo heksagonalne?

**Nie.** To jest klasyczny *Minimal API smart-UI / transaction-script* z grubymi serwisami. Nie ma żadnej z trzech cech architektury heksagonalnej:

| Cecha hex / openlinker | Stan w POC |
|---|---|
| Domena bez zależności od frameworka (`libs/core/**/domain` — zero importów Nest/TypeORM) | **Brak warstwy domeny.** Reguły biznesowe (rozkład rabatu, mapowanie VAT, daty fiskalne, idempotencja) leżą wewnątrz serwisów naszpikowanych refleksją do Sfery i SQL-em. |
| Porty (typowane capability ports) + adaptery | **Brak portów.** Interfejsy (`IPodmiotyService`, `IDokumentySprzedazyService`) nazwane od konceptów Sfery, nie od zdolności domenowych; każdy łączy transport + SQL + SDK + mapowanie. |
| Rdzeń niezależny od dostawcy, dostawca jako adapter | **Dostawca przeciekł wszędzie** — refleksja do Sfery i surowy SQL do `ModelDanychContainer.*` są wymieszane w tych samych metodach co logika biznesowa i w samym `Program.cs`. |

Innymi słowy: dziś warstwy są „spłaszczone" do `Program.cs` (~1100 linii) + `Services/` (interfejs + implementacja + DTO w jednym pliku). Żeby to było częścią openlinkera, bridge musi być **adapterem implementującym porty OL** (`ProductMasterPort`, `InventoryMasterPort`, wystawianie faktur), a kontrakt OL (`providerInvoiceId`, `regulatoryStatus`, …) musi być stabilną granicą — nie wyciekiem fizycznego schematu bazy.

---

## 2. Code review (architektura i jakość)

### A. Krytyczne / architektoniczne

- **A1. Brak warstw — God file.** [`Program.cs`](../bridge/SferaApi/Program.cs) miesza routing, middleware auth, SQL biznesowy, diagnostykę i helpery w jednym pliku. Endpointy odczytu robią surowy SQL inline (`/api/towary`, `/api/kontrahenci`, `/api/stany`, `/api/partie`…).
- **A2. Brak modelu domenowego.** Encje anemiczne (DTO) + refleksja. Logika typu „proporcjonalne wmontowanie rabatu w ceny pozycji" ([`DokumentySprzedazyService.cs:100-167`](../bridge/SferaApi/Services/IDokumentySprzedazyService.cs)) jest nietestowalna bez żywego Subiekta.
- **A3. Duplikacja refleksji.** `SetEntity` / `GetEntity` / `InvokeIfExists` / `ExtractEntityErrors` skopiowane w `PodmiotyService`, `DokumentySprzedazyService`, `AsortymentyService`, `KorektyService`. To powinien być **jeden** adapter dostępu do obiektów Sfery.
- **A4. Przeciek persystencji.** Surowe stringi SQL i nazwy tabel (`ModelDanychContainer.Asortymenty`, `…Podmioty`, `…Dokumenty`) rozsiane po endpointach i serwisach. Brak repozytoriów / read-modeli.
- **A5. Kontrakt API = fizyczny schemat bazy.** Endpointy odczytu zwracają surowe wiersze jako `Dictionary<string,object?>` (np. `/api/towary`, `/api/stany`). Każda zmiana kolumny w Subiekcie psuje kontrakt konsumenta (OL). Brak stabilnego DTO wyjściowego.
- **A6. „Fałszywe async".** Każdy serwis robi `Task.Run(() => { lock(SyncRoot) … })` — przeskok na threadpool, po czym natychmiastowy globalny lock. Brak realnej współbieżności, a koszt kontekstu jest. Model wątkowy: jeden proces-szeroki `SyncRoot` serializuje **wszystko** (~1–5 ops/s wg samego `ARCHITECTURE.md`).
- **A7. Nazewnictwo interfejsów od SDK, nie od domeny.** `IPodmiotyService` powinno być portem typu `ICustomerDirectory` / `IInvoiceIssuer` / `IProductCatalog`. Interfejs + impl + DTO w jednym pliku utrudniają podmianę adaptera.

### B. Poważne

- **B1. Mutacja stanów magazynowych surowym SQL.** [`/api/przyjecie`](../bridge/SferaApi/Program.cs) (`Program.cs:801-816`) robi `MERGE` bezpośrednio na `ModelDanychContainer.StanyMagazynowe` — **z pominięciem Sfery**, czyli bez przeliczeń, walidacji, partii i audytu. To realne ryzyko korupcji spójności księgowej. Komentarz „for demo" nie chroni produkcji.
- **B2. Endpointy diagnostyczne w API produkcyjnym.** ~7 `GET /api/diag/*` introspektuje typy Sfery, zrzuca kolumny i **wartości** dla zaszytych symboli (`'OPKSK'`,`'BANAW200'`), wykonuje `SELECT *` i zwraca surowe wiersze + pełne stack-trace’y (`Results.Problem(ex.ToString())`). Brak ich autoryzacji (auth chroni tylko POST). Info-disclosure + powierzchnia ataku.
- **B3. Audyt tylko w pamięci.** [`AuditService`](../bridge/SferaApi/Services/IAuditService.cs) trzyma logi w `List<>` w RAM — znikają po restarcie. Dla mostu fiskalnego audyt musi być trwały (komentarz sam mówi „in production would INSERT"). Feature opisany jako „done", realnie stub.
- **B4. Wyciek wewnętrznych błędów do klienta.** `BridgeException.Classify` ustawia `Reason = ex.Message` i zwraca to w body ([`BridgeException.cs:38-61`](../bridge/SferaApi/Services/BridgeException.cs)) — surowe komunikaty SQL/Sfery trafiają do konsumenta. Diag zwraca `ex.ToString()`.
- **B5. Brak walidacji wejścia.** DTO bindowane wprost; brak DataAnnotations/FluentValidation. `limit` nieograniczony od góry (`/api/towary?limit=999999`). NIP niewalidowany. `/api/przyjecie` parsuje JSON ręcznie — `GetProperty("symbol")` rzuca przy braku pola.
- **B6. Idempotencja niewytrzymała na crash.** [`IdempotencyStore.Persist`](../bridge/SferaApi/Services/IdempotencyStore.cs) robi `File.WriteAllText` (nieatomowo). Crash w trakcie zapisu → utrata mapy → duplikat dokumentu fiskalnego, czemu store miał właśnie zapobiegać. Trzeba temp-file + atomic rename.
- **B7. Wieloetap bez transakcji.** `/api/faktury` przy braku `KontrahentId` najpierw auto-upsertuje nabywcę, potem wystawia fakturę (`Program.cs:911-927`). Częściowa porażka zostawia osieroconego kontrahenta. Brak Unit of Work / kompensacji.

### C. Drobne / utrzymaniowe

- **C1.** `SferaApi.csproj` ma zaszytą ścieżkę maszynową (`<SferaBinaries>C:\Users\42zer\...`) — nieprzenośne na inne stacje/CI.
- **C2.** `UseWPF=true` w API headless — ciężka zależność; zweryfikować czy faktycznie wymagana przez Sferę.
- **C3.** Mieszanka PL/EN w identyfikatorach i komentarzach — brak konwencji.
- **C4.** Zero testów. Przy hex domena + aplikacja byłyby testowalne bez Subiekta; dziś niemożliwe (wszystko zależy od żywego połączenia).
- **C5.** Plik `idempotency-store.json` z realnymi numerami faktur leży w repo roboczym — powinien być w katalogu danych poza źródłami i w `.gitignore`.

---

## 3. Security review

| # | Waga | Problem | Lokalizacja |
|---|------|---------|-------------|
| S1 | **Wysoka** | Nasłuch na `0.0.0.0`, HTTP bez TLS, **auth wyłączony domyślnie** (`Auth:Enabled=false`). Stated „localhost only", faktycznie otwarte na LAN. | `Program.cs:45-47`, `appsettings.json`, `SferaOptions.cs` |
| S2 | **Wysoka** | Mutacja księgowości surowym SQL z pominięciem Sfery. | `Program.cs:801-816` |
| S3 | **Wysoka** | Endpointy `/api/diag/*` bez autoryzacji: introspekcja, zrzut danych, stack-trace. | `Program.cs:104-519` |
| S4 | **Wysoka** | Sekrety w jawnym `appsettings.json` (`NexoUser=Szef`, `NexoPassword=robocze`); brak secret-store/DPAPI/env. Domyślny `ApiKey="poc-secret"`. | `appsettings.json`, `appsettings.Production.json` |
| S5 | Średnia | Brak autoryzacji na GET — `/api/kontrahenci` (NIP, telefon), `/api/audit/last`, `/health` jawne. Auth chroni tylko POST `/api`. | `Program.cs:56-79` |
| S6 | Średnia | Porównanie klucza API `provided != authOpt.ApiKey` — niestałoczasowe (timing). | `Program.cs:66` |
| S7 | Średnia | Wyciek wewnętrznych komunikatów błędów do klienta (info-disclosure). | `BridgeException.cs`, diag |
| S8 | Średnia | CORS: wpisy CIDR `http://192.168.0.0/16`, `http://10.0.0.0/8` **nie działają** (origin to dokładny string, nie podsieć) — mylące; do tego `AllowAnyHeader/Method`. | `Program.cs:30-40` |
| S9 | Niska | Brak rate-limitingu na endpointach tworzących dokumenty fiskalne. | `Program.cs` |
| S10 | Niska | Nieatomowy zapis store idempotencji → ryzyko duplikatu po crashu. | `IdempotencyStore.cs:71-85` |

**Pozytyw:** zapytania SQL są parametryzowane (`AddWithValue`), a nazwy kolumn w diag pochodzą z `INFORMATION_SCHEMA`, nie od użytkownika — **ryzyko SQL injection jest niskie**. Główne zagrożenia to ekspozycja sieciowa + brak autoryzacji + info-disclosure + obejście Sfery, nie wstrzyknięcia.

---

## 4. Architektura docelowa (mapowanie na openlinker)

Bridge = **adapter Subiekta** dla portów OL. Proponowany podział na projekty (reguła zależności: strzałki tylko „do środka", Domain nie zależy od niczego):

```
src/
  Subiekt.Bridge.Domain/              # czysta domena — 0 zależności (odpowiednik libs/core/**/domain)
    Invoices/        Invoice, InvoiceLine, DocumentType, Discount  (reguła rozkładu rabatu, VAT split)
    Customers/       Customer, Nip (walidacja), Address
    Products/        Product
    Common/          Money, VatRate, Result<T>, błędy domenowe

  Subiekt.Bridge.Application/         # przypadki użycia + PORTY (zależy tylko od Domain)
    Ports/
      ICustomerDirectory.cs           (upsert/lookup nabywcy)
      IInvoiceIssuer.cs               (wystaw fakturę/paragon)
      ICorrectionIssuer.cs            (korekta/zwrot)
      IProductCatalog.cs              (read + upsert towaru)
      IStockReader.cs / IWarehouseReceiver.cs
      IDocumentStatusReader.cs        (status + KSeF)
      IIdempotencyStore.cs  IAuditLog.cs
    UseCases/  IssueInvoiceHandler, UpsertCustomerHandler, IssueCorrectionHandler, ...

  Subiekt.Bridge.Infrastructure.Sfera/   # ADAPTER zapisu (refleksja schowana TUTAJ)
    SferaSession.cs                   (dawne SferaConnection)
    SferaObjectAccessor.cs            (JEDYNY helper refleksji: Set/Get/Invoke/Errors)
    SferaCustomerDirectory : ICustomerDirectory
    SferaInvoiceIssuer     : IInvoiceIssuer  ...

  Subiekt.Bridge.Infrastructure.Sql/     # ADAPTER odczytu (read-modele, mapowanie na DTO OL)
    SqlProductCatalog, SqlStockReader, SqlCustomerQueries, SqlDocumentStatusReader

  Subiekt.Bridge.Infrastructure.Persistence/  # idempotencja (atomowo) + audyt (trwały: tabela/SQLite)

  Subiekt.Bridge.Api/                 # adapter sterujący (HTTP)
    Program.cs                        (TYLKO composition root + pipeline)
    Endpoints/  CustomersEndpoints, InvoicesEndpoints, ProductsEndpoints, ...
    Contracts/  stabilne DTO kontraktu OL (request/response)
    Middleware/ AuthMiddleware, ProblemDetails error-handler, correlation-id

tests/
  Domain.Tests          (czyste, bez Subiekta)
  Application.Tests      (porty zafejkowane)
  Api.Tests             (WebApplicationFactory)
```

Dzięki temu: domena testowalna bez Subiekta, Sfera/SQL podmienialne, a `Program.cs` staje się 30-liniowym bootstrapem.

---

## 5. Plan naprawczy (fazami)

### Faza 0 — Bezpieczeństwo (natychmiast, bez refaktoru)
1. **Wyłączyć/odgrodzić `/api/diag/*`** — `#if DEBUG` lub feature-flag + wymóg auth (S3, B2).
2. **Zablokować/usunąć surowy `MERGE` w `/api/przyjecie`** — albo przez Sferę, albo 501/za flagą (S2, B1).
3. **ProblemDetails zamiast surowych komunikatów** — generyczny komunikat do klienta + log z correlation-id po stronie serwera (S7, B4).
4. **Sekrety poza kodem** — env / `dotnet user-secrets` / DPAPI; rotacja domyślnych `Szef/robocze` i `poc-secret` (S4).
5. **Auth domyślnie włączony poza localhost; objąć też GET; porównanie stałoczasowe (`CryptographicOperations.FixedTimeEquals`); wymusić HTTPS** (S1, S5, S6).
6. **Naprawić CORS** — usunąć martwe wpisy CIDR, origin z konfiguracji, bez `AllowAnyHeader` (S8).

### Faza 1 — Szkielet heksagonalny
7. Rozbić solucję na projekty `Domain/Application/Infrastructure.*/Api` z wymuszoną regułą zależności (Domain bez referencji).
8. Wydzielić **porty** i przemianować interfejsy na zdolności domenowe (A7).
9. Przenieść logikę endpointów do modułów `Endpoints/` + handlerów aplikacyjnych; `Program.cs` → composition root (A1).
10. **Jeden** `SferaObjectAccessor`; usunąć zduplikowaną refleksję (A3).
11. Wprowadzić stabilne DTO wyjściowe (odciąć kontrakt od fizycznego schematu) (A5).

### Faza 2 — Domena i poprawność
12. Model domenowy + przeniesienie reguł (rozkład rabatu, VAT, daty fiskalne, polityka idempotencji) do Domain/Application; testy jednostkowe (A2, C4).
13. **Trwały audyt** (tabela SQL/SQLite) zamiast `List<>` (B3).
14. **Atomowy** zapis idempotencji (temp + rename) (B6).
15. Warstwa walidacji wejścia (DataAnnotations/FluentValidation; limity zakresów; walidacja NIP) (B5).
16. Unit of Work / kompensacja dla wieloetapowego wystawiania (B7).

### Faza 3 — Hardening i ops
17. Zamiast globalnego `SyncRoot`+`Task.Run`: dedykowana **jednowątkowa kolejka** operacji Sfery (Channel) — realny async na granicy HTTP, serializacja tylko mutacji (A6).
18. Health/readiness, structured logging z correlation-id, rate-limiting na endpointach fiskalnych (S9).
19. Testy + CI; usunąć ścieżkę maszynową z csproj; zweryfikować `UseWPF` (C1, C2, C4).

### Kolejność priorytetów
**Faza 0 = blokująca** przed jakąkolwiek ekspozycją poza localhost. Faza 1 odblokowuje resztę (bez warstw nie da się sensownie dołożyć testów/domeny). Fazy 2–3 podnoszą do standardu openlinkera.
