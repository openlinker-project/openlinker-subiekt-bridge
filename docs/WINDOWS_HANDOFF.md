# Windows Handoff — refaktor heksagonalny `SferaApi`

> Instrukcja dla **nowego czatu/agenta na maszynie Windows**. Refaktor (Fazy 0–4) został w całości
> **zautorowany i przejrzany (/tech-review) na WSL, ale NIE skompilowany ani nie uruchomiony** — na WSL
> nie ma `dotnet`, a projekt to `net8.0-windows` + `UseWPF` + DLL-e InsERT Sfera. Twoim zadaniem jest:
> **zbudować, przetestować na żywej Sferze, naprawić co nie działa, i dokończyć rzeczy odłożone.**

## ✅ Status — ukończone na Windows (2026-06-22)
Build zielony, testy jednostkowe + reguła zależności zielone, smoke + E2E przeszły na żywej bazie demo
`Nexo_Demo_1`. Wszystkie odłożone TODO zaimplementowane: przyjęcie magazynowe idzie teraz **przez Sferę
(dokument PW / przychód wewnętrzny)** i `POST /api/goods-receipts` zwraca **200** (raw-MERGE i flaga
`Features:RawWarehouseReceiptUnsafe` USUNIĘTE); daty fiskalne liczy `Domain.SalesDocument.ComputeFiscalDates(IClock)`;
inline rozkład rabatu w legacy serwisie usunięty (jedyne źródło to `Domain.FoldDiscounts`); `VatSplit` zrekonsyliowany
z `Przelicz()` Subiekta (`ForLine`, parytet per-jednostka). Naprawiono bug odczytu `/api/products` (`Numer` to `int`,
nie `string`). Jedyny otwarty punkt → podawanie KONKRETNego numeru partii przy przyjęciu (patrz §6).

## 0. Czego dotyczy projekt
Most fiskalny Subiekt nexo (Sfera) ↔ OpenLinker. Przed refaktorem: jeden `Program.cs` (~1450 linii) +
grube serwisy refleksyjne. Po refaktorze: czysty heksagon (Domain / Application / Infrastructure.* / Api).

## 1. Pobranie kodu
Repo dostarczone jako **git bundle** ze wszystkimi branchami. Lokalizacja docelowa: `C:\projekty\blocky\sfera-openlinker`.

```powershell
# z bundle (zalecane — zachowuje historię i podpisy):
git clone C:\sciezka\do\sfera-openlinker.bundle C:\projekty\blocky\sfera-openlinker
cd C:\projekty\blocky\sfera-openlinker
git fetch origin "+refs/heads/*:refs/heads/*"   # pobierz wszystkie branche z bundla
git checkout refactor/hexagon
```
Branche: `main` (POC sprzed refaktoru), `phase/0-security` (hardening), `refactor/hexagon` (**Fazy 1–4 — tu pracuj**).

## 2. Środowisko / konwencja commitów
- **Ten sam użytkownik GitHub i ten sam klucz GPG** co na WSL. `commit.gpgsign=true` już ustawione.
- **Każdy commit: `git commit -s -S`** (sign-off + podpis GPG). Passphrase klucza jest **rotacyjny** —
  podaj go do `gpg-agent` (pinentry) **interaktywnie w swoim terminalu** lub poproś użytkownika; **nie
  wpisuj go do żadnego pliku ani commita.**
- ⚠️ **Zrotuj klucz GPG** — passphrase został ujawniony w czacie podczas autorowania na WSL.

## 3. Build
```powershell
# Ustaw ścieżkę do binariów Sfery TEJ maszyny (nadpisuje default z bridge/Directory.Build.props):
$env:SferaBinaries = "C:\Users\<user>\AppData\Local\InsERT\Deployments\Nexo\<deployment>\Binaries"
dotnet restore bridge\bridge.sln
dotnet build   bridge\bridge.sln -c Debug
```
Wymaga **.NET 8 SDK + Windows Desktop runtime** (przez `UseWPF`). Solucja używa platformy **Any CPU**
(kodegen x64 wymuszony per-projekt `PlatformTarget=x64`, bo DLL-e Sfery są x64).

### Na co uważać przy pierwszym buildzie
- **`SferaBinaries`** — popraw `bridge/Directory.Build.props` lub ustaw env var na realną ścieżkę.
- **Restore pakietów** (wersje dobrane „na ślepo", potwierdź dostępność w feedzie): `Microsoft.Data.Sqlite 8.0.7`,
  `Dapper 2.1.35`, `Microsoft.Data.SqlClient 5.1.5`, `FluentValidation 11.9.2`, `NetArchTest.Rules 1.3.2`,
  `Microsoft.Extensions.{Hosting,Logging,Options}.Abstractions 8.0.0`, xUnit + `Microsoft.NET.Test.Sdk`.
- **`TreatWarningsAsErrors=true`** obowiązuje w **Domain / Application / Infrastructure.Sql / Infrastructure.Persistence**
  (czysty kod). Wyłączone w **Api** i **Infrastructure.Sfera** (grandfathered legacy refleksja) oraz `tools/SferaInspect`.
  Jeśli strict-projekt rzuci warning → napraw (kod był pisany warning-clean, ale nie zweryfikowany kompilatorem).
- **Pozycje do potwierdzenia kompilacji** (zgłoszone w /tech-review jako „verify on Windows"): inferencja
  `SferaWriteQueue.EnqueueAsync<T>`, boxed-tuple w `CancellationToken.Register(static …)`, rozszerzenia
  `IEndpointRouteBuilder`, typ zwracany `Uchwyt.PodajPolaczenie()` (oczekiwany `Microsoft.Data.SqlClient.SqlConnection`).

## 4. Testy jednostkowe (bez Sfery)
```powershell
dotnet test bridge\bridge.sln
```
- `Subiekt.Bridge.Domain.Tests` — NIP (suma kontrolna), rozkład rabatu (arytmetyka), parsowanie VAT, daty fiskalne.
- `Subiekt.Bridge.Application.Tests` — `UpsertCustomerHandler` na fejku portu.
- `Subiekt.Bridge.Architecture.Tests` — **reguła zależności (NetArchTest)**: Domain→nic, Application→tylko Domain,
  Infrastructure.*→nie-Api. **Musi przejść** — to gwarant, że dług architektoniczny nie wróci.

## 5. Smoke + live testy (żywa Sfera)
Uruchom Api (loopback, z `Auth__ApiKey` ustawionym) i sprawdź:
1. Autoconnect do Sfery + `GET /health` (sfera+sql OK).
2. `POST /api/customers/upsert` — poprawny NIP → 200; **zły NIP (suma kontrolna) → 422 z konkretnym komunikatem**.
3. `POST /api/invoices` happy-path → faktura; **parytet rozkładu rabatu** vs zachowanie sprzed refaktoru na
   dokumencie wielostawkowym z rabatem; **retry z tym samym `IdempotencyKey`** → ten sam dokument, bez duplikatu.
4. `POST /api/invoices/{id}/corrections`.
5. Reads: `/api/products`, `/api/customers`, `/api/warehouses`, `/api/stock`, `/api/batches/{symbol}`,
   `/api/invoices/{id}/status` (mapowanie KSeF accepted/sent/rejected/pending) — porównaj kształt JSON z cockpitem.
6. **Audyt** (SQLite) przeżywa restart procesu; `/api/audit/last` zwraca wpisy.
7. `POST /api/goods-receipts` → **200** — tworzy realny dokument **PW (przychód wewnętrzny)** przez Sferę i
   zwiększa stan właściwego magazynu (raw-MERGE usunięty). Zły magazyn/towar → 422 z komunikatem.
8. Auth: `GET /api/*` bez ważnego `Authorization: Bearer <token>` → 401 (jedyny akceptowany schemat; `X-Api-Key` odrzucony); start z bindingiem nie-loopback bez klucza/HTTPS → **odmowa startu**.
9. **Model wątkowy**: pod obciążeniem żaden deadlock; `SferaWriteQueue` serializuje mutacje; `/health` (TrySqlPing,
   timeout 2s) nie zakleszcza się z workerem; autoconnect w tle nie koliduje z pierwszym zapisem.

## 6. Rzeczy ODŁOŻONE / TODO — STATUS
Wszystkie markery `TODO(Faza-Windows)`/`TODO(Faza-4)` usunięte z kodu. Stan:
- ✅ **`SferaWarehouseReceiver`** — ZROBIONE. Przyjęcie idzie przez Sferę dokumentem **PW** (`SferaPrzyjeciaService`,
  fasada `IPrzychodyWewnetrzne` → `UtworzPrzychodWewnetrzny`/`Utworz`, wzorzec `Dodaj`+`Zapisz` przez `SferaWriteQueue`).
  Wybór magazynu UoW-safe przez `Kontekst().UstawMagazynWedlugSymbolu` z przywróceniem kontekstu w `finally`.
  Surowy `MERGE` USUNIĘTY (był footgunem). Zweryfikowane na żywo: stan rośnie we właściwym magazynie.
- ✅ **Daty fiskalne** — ZROBIONE. `SferaInvoiceIssuer`/`SferaIssueInvoiceWithBuyer` liczą daty przez
  `Domain.SalesDocument.ComputeFiscalDates(IClock)` (impl. `SystemClock`); serwis legacy ustawia je z inputu, bez inline.
- ✅ **Inline rozkład rabatu** — USUNIĘTY z `SferaDokumentySprzedazyService`; jedyne źródło to `Domain.FoldDiscounts`
  (parytet potwierdzony na żywym dokumencie wielostawkowym).
- ✅ **`VatSplit`** — ZREKONSYLIOWANY. Nowe `VatSplit.ForLine` liczy per-jednostka jak `Przelicz()` (round net/unit ×
  qty, VAT z net); `ComputeVatSplits` agreguje per-stawkę. Test parytetu na liczbach z żywego FS 147 (156.51/180.01).
- ✅ **Bug `/api/products`** — `ProductView.Numer`/`ProductItemResponse.Numer` zmienione `string?`→`int?` (kolumna DB jest
  `int`; poprzednio Dapper rzucał `DataException` → 422). JSON cockpitu = liczba (jak przed refaktorem).
- 🟦 **Read-endpointy surowe tablice** + **Cockpit** (`Asortyment_Id`/`Magazyn_Id` zachowane) — bez zmian; do
  potwierdzenia gdy front gotowy (świadoma decyzja, nie dług).
- 🟦 **`Microsoft.Data.SqlClient` w Api** — nadal używany przez read-modele Dapper i nową ścieżkę przyjęcia (walidacja
  istnienia towaru/magazynu); zostaje świadomie.
- ⚠️ **OTWARTE (decyzja produktowa): podawanie KONKRETNego `numer_partii` przy przyjęciu.** Przyjęcie towaru
  partiowanego BEZ podanego numeru działa (Sfera auto-przydziela partię). Podanie własnego numeru partii **odrzuca
  przyjęcie z czytelnym 422** zamiast cicho je gubić — bo mechanizm `PartiaPozycji` na PW wymaga głębszej integracji z
  podsystemem partii Sfery. Uzasadnienie odłożenia: legacy raw-MERGE też NIGDY nie zapisywał partii (zwracał ją tylko
  w odpowiedzi), a wymóg „własny numer partii z OpenLinkera" jest niepotwierdzony. Do decyzji: czy jest potrzebny.

## 7. Akcje bezpieczeństwa (Faza 0 — ops)
- **Sekrety przez env / `dotnet user-secrets`** (NIE w `appsettings.json`): `Auth__ApiKey`, `Sfera__NexoPassword`, `Sfera__SqlPassword`.
- **Konto operatora nexo z minimalnymi uprawnieniami** zamiast `Szef`.
- `Sfera:SqlEncrypt=true` (default). Self-signed cert: właściwy cert, albo `SqlEncrypt=false` tylko w zaufanej sieci.
- **Zrotuj klucz GPG** (patrz §2).

## 8. Co już zrobione — mapa commitów (`git log --oneline main..refactor/hexagon`)
- `Faza 0` (×2) — secure-by-default in-place: auth fail-closed/stałoczasowy, binding loopback + wymóg TLS poza nim,
  `/api/diag/*` za flagą+Dev+auth, ProblemDetails bez wycieku + correlationId, `/api/goods-receipts` MERGE→501,
  sekrety poza appsettings, SqlEncrypt, redakcja PII, naprawa UTF-8. (Poprawki review: multi-URL loopback bypass, TLS, swagger, martwa opcja cert.)
- `Faza 1` — szkielet solucji: Domain/Application/Infrastructure.{Sfera,Sql,Persistence}/Api + tests; Directory.Build.props;
  `.editorconfig`; **test reguły zależności (NetArchTest)**; Any CPU + per-projekt x64; relokacja `SferaBinaries`.
- `Faza 2` — domena (Nip+checksum, Money, VatRate, SalesDocument z rozkładem rabatu/VAT/datami) + porty Application
  + use-case + slice `UpsertCustomer` + testy. (Review: domenowe komunikaty walidacji do klienta; bez wycieku infra.)
- `Faza 3A` — Infrastructure.Persistence (atomowa idempotencja temp+rename, audyt SQLite) + Infrastructure.Sql
  (typowane read-modele Dapper, osobne połączenie odczytu poza lockiem zapisu).
- `Faza 3B` — adaptery portów Sfery + jeden `SferaObjectAccessor` (koniec duplikacji refleksji) + `SferaWriteQueue`
  (Channels, jeden konsument) + UoW faktury + przepięcie DI. (Review: cancellation w kolejce, drain na shutdown, klasyfikacja błędów SQL.)
- `Faza 4` — composition root + `Endpoints/*` + stabilne `Contracts/` + FluentValidation; reads na read-modelach
  z zachowanymi kluczami JSON.

## 9. Reguła na przyszłość
Reguła zależności jest **wymuszana testem** (`Architecture.Tests`): `Api → Application → Domain`,
`Infrastructure.* → Application+Domain`, **Domain → nic**. Nowy ERP = nowy projekt `Infrastructure.*`
implementujący te same porty. Każdy commit: `-s -S`.
