# Subiekt POC — Setup Guide

## Prerequisites

### Windows (for bridge)
- .NET 8.0 SDK or later (`dotnet --version`)
- Visual Studio 2022 or VS Code with C# extension
- Subiekt nexo PRO installed with Sfera SDK enabled
- SQL Server (local or remote)

### All Platforms (for cockpit)
- Node.js 20+ (`node --version`)
- npm 10+ (`npm --version`)

## Bridge Setup (C:\subiekt-poc\bridge\)

### 1. Locate Your Subiekt Binaries

The reference `sfera-api-main` uses a hardcoded path. You need to find your actual Subiekt installation:

```
C:\Users\<YourUsername>\AppData\Local\InsERT\Deployments\Nexo\<DeploymentName>\Binaries
```

Example:
```
C:\Users\john\AppData\Local\InsERT\Deployments\Nexo\Demo_10a1963f59ca\Binaries
```

### 2. Configure SferaApi.csproj

Open `C:\subiekt-poc\bridge\SferaApi\SferaApi.csproj` and update the `<SferaBinaries>` path:

```xml
<PropertyGroup>
  ...
  <SferaBinaries>C:\Users\<YourUsername>\AppData\Local\InsERT\Deployments\Nexo\<DeploymentName>\Binaries</SferaBinaries>
</PropertyGroup>
```

### 3. Configure appsettings.json

Copy `appsettings.example.json` to `appsettings.json`:

```bash
cd C:\subiekt-poc\bridge\SferaApi
copy appsettings.example.json appsettings.json
```

Edit `appsettings.json` with your Subiekt details:

```json
{
  "Sfera": {
    "BinariesDir": "C:\\Users\\john\\AppData\\Local\\InsERT\\Deployments\\Nexo\\Demo_10a1963f59ca\\Binaries",
    "ConfigDir": "C:\\Users\\john\\AppData\\Local\\InsERT\\Deployments\\Nexo\\Demo_10a1963f59ca\\Config",
    "TempDir": "C:\\Users\\john\\AppData\\Local\\InsERT\\Deployments\\Nexo\\Demo_10a1963f59ca\\Work",
    "DeploymentName": "Nexo",
    "SqlServer": "localhost\\INSERTNEXO",
    "SqlDatabase": "Subiekt_Demo",
    "SqlUseWindowsAuth": true,
    "SqlEncrypt": false,
    "SqlUser": "",
    "SqlPassword": "",
    "NexoUser": "Szef",
    "NexoPassword": "your_password_here"
  }
}
```

**Key mappings:**
- `BinariesDir` → location of InsERT.Moria.*.dll files
- `ConfigDir` → Nexo configuration directory
- `TempDir` → temporary/work directory (can be same as ConfigDir)
- `SqlServer` → your SQL Server instance (e.g., `.\INSERTNEXO` or `192.168.1.100`)
- `SqlDatabase` → your Subiekt database name
- `NexoUser` / `NexoPassword` → Subiekt login credentials

> **Bezpieczeństwo — sekrety i konto operatora:**
> - **Nie commituj sekretów.** `Auth:ApiKey`, `Sfera:SqlPassword` i `Sfera:NexoPassword`
>   trzymaj poza `appsettings.json` — w zmiennych środowiskowych
>   (`Auth__ApiKey`, `Sfera__SqlPassword`, `Sfera__NexoPassword`) lub w
>   `dotnet user-secrets`. ASP.NET Core nakłada te źródła na konfigurację z pliku.
> - **Konto operatora z minimalnymi uprawnieniami.** `NexoUser` powinien być
>   dedykowanym kontem operatora z prawami tylko do operacji, których most używa
>   (wystawianie dokumentów / odczyt kartotek), **NIE** kontem administratora `Szef`.
> - **Szyfrowanie SQL.** `SqlEncrypt` jest domyślnie `true`. Jeśli certyfikat
>   serwera SQL jest self-signed i połączenie się nie udaje, właściwym
>   rozwiązaniem jest ważny/zaufany certyfikat (albo — w zaufanej sieci —
>   `Sfera:SqlEncrypt=false`, akceptując ruch jawnym tekstem). Sfera nie udostępnia
>   przełącznika „trust server certificate".
> - **Nasłuch.** Domyślnie most słucha tylko na `127.0.0.1`. Wystawienie na LAN
>   (ustawienie `Host`/`Urls` na adres inny niż loopback) wymaga `Auth:Enabled=true`
>   i niepustego `ApiKey` — w przeciwnym razie aplikacja nie wystartuje.

### 4. Build and Run Bridge

```bash
cd C:\subiekt-poc\bridge
dotnet build --configuration Release
dotnet run --configuration Release
```

The bridge will start on `http://localhost:5005`.

**Verify connection:**
```bash
# In another terminal
curl -X POST http://localhost:5005/api/session/connect
# Expected response: {"connected":true}

curl http://localhost:5005/api/session/status
# Expected response: {"connected":true}

curl http://localhost:5005/api/products?limit=5
# Expected response: list of products
```

### 5. Add appsettings.json to .gitignore

`appsettings.json` contains credentials. Never commit it:

```bash
cd C:\subiekt-poc\bridge
echo appsettings.json >> .gitignore
```

---

## Cockpit Setup (C:\subiekt-poc\cockpit\)

### 1. Install Dependencies

```bash
cd C:\subiekt-poc\cockpit
npm install
```

### 2. Configure Environment

The `.env.local` file is already configured for local development:

```env
VITE_API_URL=http://localhost:5005
```

### 3. Start Development Server

```bash
npm run dev
```

The cockpit will open on `http://localhost:5173`.

---

## Testing the POC

### 1. Connect to Sfera

1. Open cockpit at http://localhost:5173
2. Go to **🔌 Connect** tab
3. Click **Connect** button
4. You should see "Status: Connected"

### 2. Browse Data

- **📦 Towary** → List of products from your Subiekt database
- **👥 Kontrahenci** → List of contractors (customers/vendors)

### 3. Test Write Operations

**Upsert Kontrahent (👤 Upsert Kontrahent tab):**
- Fill form: Nazwa, NIP, Telefon
- Click **Upsert Kontrahent**
- Check response — should contain ID of new/updated contractor

**Create Invoice (📄 Faktura tab):**
- Select contractor from dropdown
- Add line items (symbol, qty, price)
- Click **Utwórz Fakturę** or **Utwórz Paragon**
- Check response — should contain invoice ID

**Check Status (📋 KSeF Status tab):**
- Enter invoice ID from previous step
- Click **Get Status**
- See status and KSeF submission state

### 4. Run E2E Flow

Go to **🔄 E2E Flow** tab:
1. Fill form: Contractor name, NIP, product symbol, qty, price
2. Click **Execute Flow**
3. Watch all 3 steps execute in sequence:
   - Upsert kontrahent
   - Create invoice
   - Get invoice status

---

## Troubleshooting

### Bridge won't start: "Missing Sfera config"
- Check that `appsettings.json` exists in `SferaApi/` directory
- Verify all paths exist (BinariesDir, ConfigDir, TempDir)

### Bridge won't start: "Assembly not found"
- Verify `SferaBinaries` path in `SferaApi.csproj` is correct
- Check that `InsERT.Moria.Sfera.dll` exists in that path
- Run `dotnet clean` and `dotnet build` again

### Bridge connects but cockpit can't reach it
- Verify bridge is running: `curl http://localhost:5005/health`
- Check CORS is enabled in `Program.cs` (look for `UseCors("AllowLocalhost")`)
- Verify cockpit is on localhost:5173 (not another port)

### Cockpit won't start
- Delete `node_modules/` and `package-lock.json`, then `npm install` again
- Check Node.js version: `node --version` (should be 20+)
- Clear npm cache: `npm cache clean --force`

### "Kontrahent not found" in invoice creation
- Make sure you upserted a kontrahent first
- Use the returned ID from upsert form

---

## Next Steps

After successful POC:
1. Implement full Sfera facades (currently using SQL placeholders)
2. Add error handling for specific Sfera exceptions
3. Add audit logging
4. Deploy to shared development environment
5. Integrate with KSeF submission flow
