# POC Architecture & Design Decisions

## Overview

```
React Cockpit (localhost:5173)
        ↓ HTTP/JSON (CORS enabled)
.NET Bridge (localhost:5005)
        ↓ Assembly/COM
Sfera SDK (InsERT.Moria.Sfera.dll)
        ↓ COM Interop
Subiekt nexo PRO + SQL Server
```

---

## Bridge (.NET 8.0)

### Why .NET 8.0?

Sfera SDK is a .NET library (InsERT.Moria.Sfera.dll). Node.js cannot load .NET assemblies directly. Therefore:
- Bridge must be .NET process
- Cannot be deployed alongside cockpit
- Requires Windows + .NET SDK to build/run

### Why Minimal API (ASP.NET Core)?

- Lightweight compared to full MVC
- No controller boilerplate
- Inline endpoint definitions match POC's simple scope
- Fast startup time

### Thread Safety: SyncRoot Locking

Sfera SDK is **not thread-safe** for concurrent mutations. All write operations lock `SferaConnection.SyncRoot`:

```csharp
lock (s.SyncRoot)
{
    if (!s.IsConnected) return 409;
    try { /* operation */ }
    catch (Exception ex) { return 500 with message; }
}
```

This ensures:
- Only one thread accesses Sfera facade at a time
- Session state stays consistent
- Concurrent REST calls serialize internally

### CORS Policy

Only `localhost:5173` (cockpit dev server) is allowed. Production should:
- Use environment-specific allowed origins
- Avoid wildcard `*`
- Require signed JWT/API keys

### Response Envelope

All write endpoints return `ResponseEnvelope<T>`:

```csharp
public class ResponseEnvelope<T>
{
    public bool Success { get; set; }
    public T? Data { get; set; }
    public string? Error { get; set; }
}
```

Benefits:
- Consistent response shape across all endpoints
- Error details always in JSON body (easier debugging than HTTP status alone)
- Frontend can easily check `success` flag
- No need for custom error handling per endpoint

### Placeholders vs. Real Sfera Implementation

**Current state:** Write endpoints use SQL MERGE (placeholders).

```csharp
// POST /api/customers/upsert uses SQL INSERT, not IPodmioty.UtworzFirme()
insertCmd.CommandText = @"INSERT INTO ModelDanychContainer.Podmioty ...";
```

**Why:** Sfera facades require:
- Proper UnitOfWork scope
- Session bootstrap validation
- Complex parameter handling (enums, nested objects)

**Next phase:** Replace SQL with real Sfera facade calls:
```csharp
var podmioty = s.Uchwyt.PodajObiektTypu<IPodmioty>();
podmioty.UtworzFirme(/* detailed params */);
```

This requires DLL inspection (tools/SferaInspect) to understand exact API signatures.

---

## Cockpit (React 19 + Vite)

### Why TanStack Router?

- File-based routing (`routes/` directory matches URL structure)
- Type-safe route definitions
- Built-in server function support (for future SSR)
- Replaces separate routing library (cleaner)

### State Management: TanStack Query

No Redux/Zustand. TanStack Query handles:
- Server state caching
- Automatic refetching
- Loading/error states
- Request deduplication

```typescript
const { data, isLoading, error } = useQuery({
  queryKey: ['kontrahenci'],
  queryFn: () => api.kontrahenci(100),
})
```

### Forms: TanStack Form

Minimal form library integrated with TanStack Query mutations:

```typescript
const mutation = useMutation({
  mutationFn: (data) => api.upsertKontrahent(data),
})
```

No separate form state management library — TanStack Form provides the structure.

### Styling: Tailwind CSS

Utility-first CSS for rapid UI development. No custom CSS files (all in Tailwind classes).

---

## Data Flow

### Read Operations (Products, Contractors)

```
Cockpit (TanStack Query)
  → GET /api/products
    → Bridge (SQL SELECT)
      → Subiekt SQL DB
        → JSON response
  → Cache in React Query
  → Display in table
```

No Sfera SDK involvement — straight SQL queries are fast and cached.

### Write Operations (Upsert, Invoice)

```
Cockpit (TanStack Form + Query mutation)
  → POST /api/customers/upsert
    → Bridge (lock, validate, SQL INSERT or Sfera call)
      → Subiekt
        → ResponseEnvelope { success, data, error }
  → Display response JSON + status badge
```

### E2E Flow (Multi-step)

```
Cockpit (/flow route)
  1. User fills: Contractor name, NIP, product symbol, qty, price
  2. Execute Flow button
     → Step 1: POST /api/customers/upsert → get kontrahent ID
     → Step 2: POST /api/invoices → use ID → get invoice ID
     → Step 3: GET /api/invoices/{id}/status → show status
  3. Display all 3 results in JSON panels
```

Sequential execution (step N waits for N-1 to complete).

---

## Security Considerations

### Current (POC)
- ✗ No authentication (localhost only)
- ✗ No authorization (all endpoints public)
- ✗ No input validation (trust client-side only)
- ✗ No audit logging
- ✗ Credentials in appsettings.json (gitignored)

### Production (Future)
- ✓ JWT or API key auth for bridge
- ✓ Role-based access control (read-only vs. write)
- ✓ Input validation on all endpoints
- ✓ SQL injection prevention (parameterized queries ✓, but real Sfera calls need review)
- ✓ Audit trail (who created/modified documents)
- ✓ Secrets in Azure Key Vault, not appseettings.json
- ✓ HTTPS only (not http://localhost)
- ✓ CORS restricted to production cockpit domain

---

## Error Handling Strategy

### Bridge

Catch **all** exceptions and return JSON:

```csharp
try { /* operation */ }
catch (Exception ex) {
    return new ResponseEnvelope<object> {
        Success = false,
        Error = ex.Message
    };
}
```

Benefits:
- No 500 error pages (always JSON)
- Frontend can display error to user
- Stack traces visible for debugging

**Trade-off:** Exposes internal errors to client. Production should:
- Log full exception server-side
- Return generic message to client
- Use error codes instead of raw messages

### Cockpit

TanStack Query + manual error states:

```typescript
const mutation = useMutation({
  mutationFn: (data) => api.upsertKontrahent(data),
  onError: (error) => {
    setError(error.message);
  }
});
```

Display errors in red banner with JSON response panel.

---

## Known Limitations & Trade-offs

| Limitation | Impact | Why | Fix (Future) |
|-----------|--------|-----|------|
| SQL placeholders for writes | Writes don't use real Sfera facades | Faster POC scaffolding | Call IPodmioty/IDokumentySprzedazy via reflection |
| No transaction rollback in E2E | If step 2 fails, step 1 committed | Sfera manages transactions internally; POC doesn't scope them | Add UnitOfWork pattern for multi-step operations |
| No pagination beyond LIMIT | Large datasets truncated | HTTP response size matters; UI doesn't need all rows | Implement cursor-based pagination in bridge |
| No audit logging | Can't trace who created/modified | Subiekt has audit tables; bridge ignores them | Query and display Subiekt audit trail in cockpit |
| Session per bridge process | Can't run multiple bridge instances | Single-tenant design | Use distributed cache (Redis) for session state |
| Reflection-based Sfera access | Fragile (breaks if InsERT API changes) | Avoids DLL reference coupling | Upgrade to compiled facade imports |
| CORS wildcard risk if deployed | Any origin can call bridge | Currently localhost only; acceptable for dev | Use environment-specific origin in production |

---

## Performance Characteristics

### Read Operations
- **Bottleneck:** SQL Server query + JSON serialization
- **Optimization:** Table indexing on Asortyment_Id, NIP, Symbol
- **Caching:** TanStack Query caches by queryKey; manual refetch via button

### Write Operations
- **Bottleneck:** Sfera facade calls + SQL commits
- **Timeout:** None configured (may hang on slow SQL); add 30s timeout in production
- **Concurrency:** SyncRoot lock serializes all mutations; max throughput ~1-5 ops/sec per bridge

### Memory
- **Bridge:** ~100MB resident (CLR + Sfera libs)
- **Cockpit:** ~50MB resident (React + dependencies)

---

## Deployment Assumptions

### Bridge
- Windows Server with .NET 8 Runtime
- Same network as Subiekt SQL Server
- Fixed port 5005
- Restart handling: systemd service or Windows service wrapper

### Cockpit
- Static SPA served from nginx/IIS
- Configured to call bridge at runtime (via .env)
- No server-side rendering (yet)

---

## Testing Strategy (Not Yet Implemented)

### Unit Tests
- Bridge: SferaConnection mock, SferaReflection reflection tests
- Cockpit: Component snapshot tests (JsonResponse, FormLayout)

### Integration Tests
- Bridge: Real SQL Server, test read/write endpoints
- Cockpit: Vite testing library + MSW (mock service worker)

### E2E Tests
- Playwright: full user flow (cockpit → bridge → Subiekt)
- Against real Subiekt instance in test environment

---

## Future Enhancements

1. **Real Sfera Facades** — Replace SQL with IPodmioty, IDokumentySprzedazy, IDokumentyElektroniczne
2. **Server-Side Rendering** — TanStack Start with server functions for better UX
3. **Authentication** — JWT or OAuth for production deployment
4. **Audit Trail** — Query Subiekt's audit tables, display in cockpit
5. **KSeF Integration** — Call Sfera's KSeF facade to submit invoices to tax authority
6. **Distributed Session** — Redis-backed session for multi-instance bridge
7. **Performance Monitoring** — APM (Application Insights) for bridge
8. **Mobile UI** — React Native or responsive Tailwind improvements

---

## Decision Log

| Date | Decision | Rationale |
|------|----------|-----------|
| 2026-06-18 | Use .NET Bridge, not Express.js | Sfera requires .NET CLR; Node cannot load DLLs |
| 2026-06-18 | ASP.NET Core Minimal API (not MVC) | Lightweight; matches POC simplicity |
| 2026-06-18 | TanStack Router (not React Router) | File-based routing + SSR ready |
| 2026-06-18 | TanStack Query (no Redux) | Simpler; server-state library is right tool |
| 2026-06-18 | SQL placeholders for writes | Faster iteration; real Sfera calls come next sprint |
| 2026-06-18 | ResponseEnvelope for all writes | Consistent API shape; easier debugging |
| 2026-06-18 | SyncRoot lock on all Sfera calls | Sfera thread-safety requirement |
| 2026-06-18 | CORS for localhost:5173 only | Production-safe; prevents accidental wildcard |
| 2026-06-18 | Tailwind CSS (no custom CSS) | Rapid UI development; matches TanStack ecosystem |
