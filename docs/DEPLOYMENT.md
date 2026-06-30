# Wdrożenie mostu Subiekt na produkcję (test → prod)

Most jest środowisko-agnostyczny — ten sam kod działa na produkcji. Różnice to **konfiguracja** i **licencja**, nie przepisywanie. Poniżej checklista przełączenia.

## 0. Warunek krytyczny: licencja Sfera
- Most działa wyłącznie przez **Sfera dla Subiekta nexo** (`InsERT.Moria.Sfera`).
- **Baza demo/testowa ma Sferę wbudowaną** — dlatego POC działa bez dokupowania.
- **Produkcyjny, kupiony Subiekt nexo NIE ma Sfery automatycznie** — to osobny, płatny dodatek. Bez aktywnej licencji `Polacz()`/`Zaloguj()` **nie przejdą**.
- ✅ Akcja: potwierdź/dokup licencję Sfera u InsERT/partnera; ustal liczbę sesji/limity.

## 1. Konfiguracja (`appsettings.Production.json`)
Skopiuj szablon i uzupełnij:
- `Sfera.BinariesDir/ConfigDir/TempDir` → ścieżki **produkcyjnego** wdrożenia nexo (`%LOCALAPPDATA%\InsERT\Deployments\Nexo\<WDROZENIE>\...`).
- `Sfera.SqlServer` + `SqlDatabase` → produkcyjny SQL/baza.
- `Sfera.NexoUser` → **realne konto operatora z minimalnymi uprawnieniami** (patrz §2) — **nie** `Szef`, nie konto demo.
- `Auth.Enabled=true` + `Auth.ApiKey` = mocny sekret (ten sam wpisuje OpenLinker; patrz §4).
- `Sfera.SqlEncrypt=true` (domyślnie). Jeśli certyfikat serwera SQL jest self-signed i połączenie się nie udaje, właściwym rozwiązaniem jest ważny/zaufany certyfikat (albo — w zaufanej sieci — `Sfera:SqlEncrypt=false`, akceptując ruch jawnym tekstem). Sfera nie udostępnia przełącznika „trust server certificate".
- `AutoConnect=true`.

> **Sekrety NIE trafiają do pliku.** `Auth.ApiKey`, `Sfera.SqlPassword`,
> `Sfera.NexoPassword`, `Tls.CertPassword` zostawiaj puste w `appsettings*.json`
> i podawaj w runtime przez zmienne środowiskowe (`Auth__ApiKey`,
> `Sfera__SqlPassword`, `Sfera__NexoPassword`, `Tls__CertPassword`) lub
> `dotnet user-secrets`. Nigdy nie commituj sekretów.

> **Nasłuch domyślnie loopback.** Bez `Host`/`Urls` most słucha tylko na
> `127.0.0.1`. Wystawienie na LAN wymaga ustawienia `Host`/`Urls` na adres inny
> niż loopback **oraz** `Auth.Enabled=true` z niepustym `ApiKey` — inaczej most
> przerwie start (fail-closed).
>
> **TLS przy nasłuchu nie-loopback.** Nasłuch nie-loopback **wymaga** co najmniej
> jednego URL `https://` — inaczej most przerwie start (klucz API leciałby jawnym
> tekstem, a `UseHttpsRedirection()` byłby bezczynny). Dwa scenariusze:
> - **Reverse-proxy kończący TLS** (zalecane): zepnij most na **loopback**
>   (`127.0.0.1`), a proxy (IIS/nginx) niech wystawia HTTPS na zewnątrz.
> - **Bezpośrednie wystawienie na LAN**: skonfiguruj w `Urls` adres `https://...`
>   (np. `https://0.0.0.0:5005`) z certyfikatem po stronie Kestrela — most włączy
>   wtedy HSTS i przekierowanie HTTPS. Certyfikat podajesz w sekcji `Tls` (patrz
>   §1a poniżej).

## 1a. HTTPS / certyfikat serwera (nasłuch bezpośredni na LAN)
Most czyta certyfikat serwera z sekcji `Tls`:
```jsonc
"Tls": {
  "CertPath": "C:\\certs\\bridge.pfx",   // ścieżka do .pfx (PKCS#12); względna => względem katalogu aplikacji
  "CertPassword": ""                      // SEKRET: podaj przez Tls__CertPassword / user-secrets, NIE w pliku
}
```
Kestrel ładuje ten certyfikat dla KAŻDEGO endpointu `https://` w `Urls`. Brak
`Tls:CertPath` przy nasłuchu `https://` => Kestrel zgłosi błąd „no certificate"
na starcie. Przy nasłuchu loopback-HTTP sekcji `Tls` nie ustawiasz (bez zmian).

- **PRODUKCJA**: użyj PRAWDZIWEGO certyfikatu z zaufanego CA (klient — OpenLinker —
  zweryfikuje łańcuch). `Tls:CertPath` = ścieżka do `.pfx`, hasło przez
  `Tls__CertPassword`. Certyfikatu ani hasła NIE commituj.
- **DEV/TEST (tylko!)**: certyfikat self-signed:
  ```
  dotnet dev-certs https -ep C:\projekty\...\bridge\Subiekt.Bridge.Api\dev-cert.pfx -p <haslo>
  ```
  Self-signed: klient musi mu zaufać albo pominąć weryfikację (`curl -k`). NIE
  używaj na produkcji.
- **ALTERNATYWA (zalecana na produkcji)**: terminacja TLS na reverse-proxy
  (IIS/nginx) + nasłuch mostu na `127.0.0.1` — wtedy sekcji `Tls` w ogóle nie
  potrzebujesz, proxy trzyma certyfikat.

Przykładowy start (HTTPS bezpośrednio na wszystkich interfejsach):
```
set ASPNETCORE_URLS=https://0.0.0.0:5005
set Tls__CertPath=C:\certs\bridge.pfx
set Tls__CertPassword=<haslo-pfx>
set Auth__ApiKey=<mocny-sekret>
dotnet run -c Release --no-launch-profile
```
> Uwaga: profil `launchSettings.json` (`SferaApi`) wymusza `http://0.0.0.0:5005` —
> dla nasłuchu HTTPS uruchamiaj z `--no-launch-profile` (lub przez `dotnet publish`
> jako usługę), inaczej `ASPNETCORE_URLS` zostanie nadpisany i start padnie na
> guardzie „non-localhost binding requires an https:// URL".

### Firewall
Bezpośrednie wystawienie wymaga reguły zezwalającej na ruch przychodzący na porcie
mostu (np. 5005) z hosta OpenLinkera (najlepiej ograniczonej do LAN/VPN):
```
New-NetFirewallRule -DisplayName "Subiekt Bridge 5005" -Direction Inbound `
  -Action Allow -Protocol TCP -LocalPort 5005 -Profile Domain,Private
```
Nie wyłączaj firewalla globalnie — dodaj wąską regułę dla portu mostu.

> Uwaga build: `SferaApi.csproj` ma `HintPath` do binariów Sfery na potrzeby **kompilacji** — na maszynie budującej muszą być dostępne DLL-e nexo (dowolne wdrożenie). W runtime liczy się `Sfera.BinariesDir` z appsettings.

## 2. Konto operatora i uprawnienia (least-privilege)
- **Dedykowane konto operatora o minimalnych uprawnieniach** — tylko prawa, których most faktycznie używa: wystawianie FS/PA, korekty, zakładanie kontrahentów/towarów. **NIE używaj konta administratora `Szef`.**
- Zalecane osobne konto „integracja/OpenLinker" (audyt, rozdział od ludzi).
- Hasło tego konta (`NexoPassword`) podawaj przez env/user-secrets, nie w pliku.

## 3. Re-test po przełączeniu (Demo Flows)
- Most używa refleksji po API Sfery — stabilne w obrębie **tej samej wersji nexo**. Przy innej wersji odpal cockpit → **🎬 Demo Flows** i przeklikaj; w razie rozjazdu nazw użyj `/api/diag/*` (faktura-bo, podmiot-bo, asortyment-facade, korekta-bo).
- Minimalny zestaw: B2B faktura, paragon, towar spoza Subiekta, mieszany VAT, magazyn, idempotencja, rollback, korekta/zwrot, /health, batch.

## 4. OpenLinker ↔ most
- OL connection-test woła `/health` (nie credentiale Subiekta).
- W OL (#759) ustaw: **`bridgeBaseUrl` = `https://<host>:5005`** (adres HTTPS mostu osiągalny z hosta OL przez sieć) + token uwierzytelniający = ten sam sekret co `Auth.ApiKey`. JEDYNY akceptowany schemat OL→most to **`Authorization: Bearer <token>`** (nagłówek `X-Api-Key` NIE jest już akceptowany).
- Przy certyfikacie self-signed (DEV) klient OL musi mu zaufać; na produkcji użyj certyfikatu z zaufanego CA, żeby weryfikacja TLS po stronie OL przeszła bez wyjątków.
- Most domyślnie nasłuchuje na `127.0.0.1:5005`. Dla dostępu z hosta OL ustaw `Host`/`Urls` na adres LAN i otwórz **regułę firewall** na 5005 (LAN/VPN). Wystawienie nie-loopback wymaga `Auth.Enabled=true` + niepustego `ApiKey` **oraz adresu `https://` (TLS)** — inaczej most nie wystartuje (token Bearer nie może lecieć po LAN otwartym tekstem). Nie wystawiaj publicznie bez VPN/reverse-proxy + TLS.

## 5. KSeF na produkcji
- Most **nie wysyła** do KSeF — robi to Subiekt. W nexo: *Konfiguracja → Parametry KSeF → Wysyłka → „Generuj e-Faktury: Automatycznie przy zapisie"*.
- Most czyta i mapuje status na 5 wartości (`none/pending/sent/accepted/rejected`) + `clearanceReference` = `KSEF_ID`.
- **Korekta faktury** wymaga, by oryginał był wcześniej wysłany do KSeF (reguła Subiekta). Zwrot do paragonu — bez KSeF.

## 6. Decyzje polityki (świadome)
- **Sprzedaż poniżej stanu**: most woła `IgnorujBlokadeRealizacjiPozycji()` (faktura zawsze wychodzi, stan może zejść <0). Jeśli prod ma tego NIE robić — uczynić warunkowym (flaga per-request).
- **Waluta**: v1 = PLN (nie-PLN jest echowane, ale dokument księguje się w PLN). Nie-PLN = wiring `ObslugaWaluty` (v2).
- **Walidacja NIP**: most nie waliduje formatu NIP — robi to OpenLinker (AC-3).

## 7. Uruchomienie
```
cd bridge\SferaApi
set ASPNETCORE_ENVIRONMENT=Production
dotnet run            # lub: dotnet publish -c Release i uruchom jako usługa Windows
```
- Zalecane: uruchom jako **usługa Windows** (autostart, restart po awarii) na maszynie z nexo.
- Plik `idempotency-store.json` (mapa idempotencyKey→faktura) powstaje obok aplikacji — zapewnij trwałość katalogu.
