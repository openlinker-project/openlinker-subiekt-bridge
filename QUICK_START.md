# Quick Start — Subiekt POC

## What You Have

- ✅ Bridge source code (ready to configure)
- ✅ Cockpit source code (ready to install & test)
- ✅ Full documentation

## Step 1: Configure Bridge (Windows with Subiekt installed)

### 1a. Find Your Subiekt Binaries Path

On your Windows machine with Subiekt nexo PRO installed, open File Explorer and navigate to:

```
%LOCALAPPDATA%\InsERT\Deployments\Nexo\
```

Example full path:
```
C:\Users\YourName\AppData\Local\InsERT\Deployments\Nexo\Demo_10a1963f59ca\Binaries
```

Note the folder path up to `\Binaries` (but NOT including `\Binaries`):
```
C:\Users\YourName\AppData\Local\InsERT\Deployments\Nexo\Demo_10a1963f59ca
```

### 1b. Update SferaApi.csproj

Edit `C:\subiekt-poc\bridge\SferaApi\SferaApi.csproj` and change line 10:

**Before:**
```xml
<SferaBinaries>C:\Users\jakub\AppData\Local\InsERT\Deployments\Nexo\Demo_10a1963f59ca24dc1aed1e4c1d5\Binaries</SferaBinaries>
```

**After:**
```xml
<SferaBinaries>C:\Users\YourName\AppData\Local\InsERT\Deployments\Nexo\YourDeploymentName\Binaries</SferaBinaries>
```

### 1c. Configure appsettings.json

Create `C:\subiekt-poc\bridge\SferaApi\appsettings.json`:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft": "Warning"
    }
  },
  "AllowedHosts": "*",
  "Urls": "http://0.0.0.0:5005",
  "Sfera": {
    "BinariesDir": "C:\\Users\\YourName\\AppData\\Local\\InsERT\\Deployments\\Nexo\\YourDeploymentName\\Binaries",
    "ConfigDir": "C:\\Users\\YourName\\AppData\\Local\\InsERT\\Deployments\\Nexo\\YourDeploymentName\\Config",
    "TempDir": "C:\\Users\\YourName\\AppData\\Local\\InsERT\\Deployments\\Nexo\\YourDeploymentName\\Work",
    "DeploymentName": "Nexo",
    "SqlServer": "localhost\\INSERTNEXO",
    "SqlDatabase": "YourDatabaseName",
    "SqlUseWindowsAuth": true,
    "SqlEncrypt": false,
    "SqlUser": "",
    "SqlPassword": "",
    "NexoUser": "Szef",
    "NexoPassword": "YourPassword"
  }
}
```

Replace:
- `YourName` with your Windows username
- `YourDeploymentName` with your Nexo deployment folder name (e.g., `Demo_10a1963f59ca`)
- `YourDatabaseName` with your Subiekt database name
- `YourPassword` with your Subiekt login password

### 1d. Build & Run Bridge

```bash
cd C:\subiekt-poc\bridge\SferaApi
dotnet build --configuration Release
dotnet run --configuration Release
```

You should see:
```
info: Microsoft.Hosting.Lifetime[14]
      Now listening on: http://0.0.0.0:5005
```

Verify in another terminal:
```bash
curl http://localhost:5005/health
# Should return: {"status":"ok","time":"..."}

curl -X POST http://localhost:5005/api/session/connect
# Should return: {"connected":true}
```

---

## Step 2: Test Cockpit (Any Machine)

### 2a. Install Dependencies

```bash
cd C:\subiekt-poc\cockpit
npm install
```

### 2b. Verify Bridge is Running

Make sure bridge is still running on localhost:5005 (from Step 1d).

### 2c. Start Cockpit Dev Server

```bash
npm run dev
```

Browser will open to `http://localhost:5173`.

### 2d. Test Cockpit

1. Go to **🔌 Connect** tab
2. Click **Connect** button
3. You should see: `Status: Connected` (green badge)

If you see "Disconnected" (red), bridge is not running or not reachable.

### 2e. Test Data Tabs

1. **📦 Towary** — Should show list of your products from Subiekt
2. **👥 Kontrahenci** — Should show list of your contractors
3. **📄 Faktura** tab (form) — Should show dropdown of contractors from above

### 2f. Test E2E Flow

1. Go to **🔄 E2E Flow** tab
2. Fill form:
   - Nazwa Skrócona: "Test Company"
   - NIP: (leave empty or enter valid NIP)
   - Symbol Towaru: (any product symbol from Towary tab)
   - Ilość: 1
   - Cena: 99.99
3. Click **Execute Flow**
4. You should see all 3 steps complete:
   - ✓ Step 1: Upsert Kontrahent — shows new contractor ID
   - ✓ Step 2: Create Faktura — shows invoice ID
   - ✓ Step 3: Invoice Status — shows status object

---

## Troubleshooting

### Bridge won't start: "Nie można rozpoznać odwołania"
→ SferaBinaries path in `.csproj` is wrong. Double-check the path and rebuild.

### Bridge starts but cockpit says "Disconnected"
→ Check bridge is running: `curl http://localhost:5005/health`
→ Check CORS is enabled (should be by default in code)

### Cockpit won't install
→ Delete `cockpit/node_modules` and `package-lock.json`, then `npm install` again

### "Module not found" errors in cockpit
→ Ensure you're in `cockpit/` directory before `npm install`

---

## What's Next

After you verify:
1. Bridge connects to Sfera session (POST /api/session/connect returns `{connected:true}`)
2. Cockpit displays your real Subiekt data (Towary, Kontrahenci)
3. E2E Flow creates a real kontrahent in your database

You can proceed with:
- **Phase 2**: Replace SQL placeholders in write endpoints with real Sfera facades
- **Phase 3**: Add error handling, audit logging, KSeF integration
- **Phase 4**: Deploy to shared dev environment

See `docs/SETUP.md` for detailed instructions.

---

## Support

- **Bridge build fails?** → Check SferaBinaries path and appsettings.json
- **Cockpit won't load?** → Check `npm --version` (need 10+) and `node --version` (need 20+)
- **Data not showing?** → Verify bridge is running and appsettings.json has correct SQL credentials

See `docs/` folder for full documentation.
