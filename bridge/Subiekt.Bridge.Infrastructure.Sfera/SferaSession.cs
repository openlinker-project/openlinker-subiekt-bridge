using System.Data;
using InsERT.Moria.Sfera;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Subiekt.Bridge.Infrastructure.Sfera;

// Holds a single open Sfera handle (Uchwyt) for the whole process.
// Sfera is not thread-safe for arbitrary parallel calls, so any mutation
// path must be serialized — see SferaWriteQueue, which runs all mutations on a
// single dedicated worker. SyncRoot is retained: EnsureConnected / TrySqlPing /
// the hosted connect service still take it, and the write worker calls happen
// under it so a /health probe can detect a busy session.
public sealed class SferaSession : IDisposable
{
    private readonly SferaOptions _opt;
    private readonly ILogger<SferaSession> _log;
    private MenedzerPolaczen? _manager;
    private Uchwyt? _uchwyt;

    public SferaSession(IOptions<SferaOptions> opt, ILogger<SferaSession> log)
    {
        _opt = opt.Value;
        _log = log;
    }

    public object SyncRoot { get; } = new();
    public Uchwyt Uchwyt => _uchwyt ?? throw new InvalidOperationException("Sfera not connected yet");
    public bool IsConnected => _uchwyt is not null;

    public void Connect()
    {
        if (_uchwyt is not null) return;

        _log.LogInformation("Sfera: łączenie z bazą {db} na {srv} (szyfrowanie={enc})",
            _opt.SqlDatabase, _opt.SqlServer, _opt.SqlEncrypt);

        var dane = DanePolaczenia.Jawne(
            serwer: _opt.SqlServer,
            baza: _opt.SqlDatabase,
            autentykacjaWindowsDoSerwera: _opt.SqlUseWindowsAuth,
            uzytkownikSerwera: _opt.SqlUser,
            hasloUzytkownikaSerwera: _opt.SqlPassword,
            szyfrowaniePolaczeniaZSerwerem: _opt.SqlEncrypt,
            nazwaUzytkownikaDoImpersonacjiWBazie: "",
            katalogBinariow: _opt.BinariesDir,
            katalogPlikowKonfiguracyjnych: _opt.ConfigDir,
            katalogTymczasowy: _opt.TempDir,
            plikStartowy: "",
            nazwaWdrozenia: _opt.DeploymentName);

        _manager = new MenedzerPolaczen { DostepDoUI = false, WlaczKontekst = true };

        // ProductId.Subiekt — the DLL that defines ProductId is loaded lazily
        // via the resolver. We pass it via string lookup to avoid a direct
        // compile-time reference to InsERT.Moria.API here (keeps this file
        // resilient if we later thin out the references).
        var productIdType = Type.GetType("InsERT.Mox.Product.ProductId, InsERT.Mox.Core", throwOnError: true)!;
        var subiektProduct = Enum.Parse(productIdType, "Subiekt");

        var polaczMethod = typeof(MenedzerPolaczen).GetMethods()
            .Single(m => m.Name == "Polacz"
                && m.GetParameters().Length == 4
                && m.GetParameters()[2].ParameterType == typeof(IPostepLadowaniaSfery));

        var emptyExtras = Array.CreateInstance(productIdType, 0);

        _uchwyt = (Uchwyt)polaczMethod.Invoke(_manager, new[] {
            dane,
            subiektProduct,
            (IPostepLadowaniaSfery?)null,
            emptyExtras
        })!;

        _log.LogInformation("Sfera: połączono, loguję użytkownika {user}", _opt.NexoUser);

        var wynik = _uchwyt.Zaloguj(_opt.NexoUser, _opt.NexoPassword);
        if (wynik != SferaWynikLogowania.Zalogowano)
        {
            _uchwyt.Dispose();
            _uchwyt = null;
            throw new InvalidOperationException($"Logowanie Sfera nie powiodło się: {wynik}");
        }

        _log.LogInformation("Sfera: zalogowano");
    }

    // Make sure we have a live session before a business operation. If the
    // process was never connected, connect now; if a previous session went
    // stale (SQL probe throws), drop it and reconnect. Reentrant-safe: callers
    // already holding SyncRoot can call this (C# lock is reentrant per thread).
    public void EnsureConnected()
    {
        lock (SyncRoot)
        {
            if (_uchwyt is null)
            {
                ConnectOrThrowUnreachable();
                return;
            }

            if (!PingInternal(out var error))
            {
                _log.LogWarning("Sfera session probe failed ({err}); reconnecting", error);
                Reconnect();
            }
        }
    }

    private void ConnectOrThrowUnreachable()
    {
        try { Connect(); }
        catch (BridgeException) { throw; }
        catch (Exception ex)
        {
            throw BridgeException.Unreachable("Sfera/Subiekt niedostępny: " + ex.Message, ex);
        }
    }

    private void Reconnect()
    {
        try { _uchwyt?.Dispose(); } catch { /* ignore */ }
        _uchwyt = null;
        _manager = null;
        ConnectOrThrowUnreachable();
    }

    // Non-mutating health probe used by /health. Returns whether SQL behind the
    // current session answers a trivial query. Does NOT reconnect.
    public bool TrySqlPing(out string? error)
    {
        if (_uchwyt is null) { error = "not connected"; return false; }
        // Don't hang /health if a write holds SyncRoot (e.g. a reconnect blocking
        // on dead SQL). If we can't probe within 2s, report it rather than block.
        if (!System.Threading.Monitor.TryEnter(SyncRoot, TimeSpan.FromSeconds(2)))
        {
            error = "busy (operation in progress)";
            return false;
        }
        try { return PingInternal(out error); }
        finally { System.Threading.Monitor.Exit(SyncRoot); }
    }

    private bool PingInternal(out string? error)
    {
        error = null;
        try
        {
            var conn = _uchwyt!.PodajPolaczenie();
            if (conn.State != ConnectionState.Open) conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1";
            cmd.CommandTimeout = 3;   // fail fast when SQL is unreachable (no ~30s hang)
            cmd.ExecuteScalar();
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public void Dispose()
    {
        _uchwyt?.Dispose();
        _uchwyt = null;
        _manager = null;
    }
}
