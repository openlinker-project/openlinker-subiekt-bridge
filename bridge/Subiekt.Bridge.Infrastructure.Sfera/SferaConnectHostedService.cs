using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Subiekt.Bridge.Infrastructure.Sfera;

// Connects to Sfera on application startup so the bridge behaves like the OL
// contract expects ("most już połączony"). Runs in the background: a slow or
// failing Sfera login must not block the web host from coming up (so /health
// and the manual POST /api/session/connect fallback stay reachable).
public sealed class SferaConnectHostedService : IHostedService
{
    private readonly SferaSession _sfera;
    private readonly SferaOptions _opt;
    private readonly ILogger<SferaConnectHostedService> _log;

    public SferaConnectHostedService(SferaSession sfera, IOptions<SferaOptions> opt, ILogger<SferaConnectHostedService> log)
    {
        _sfera = sfera;
        _opt = opt.Value;
        _log = log;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_opt.AutoConnect)
        {
            _log.LogInformation("AutoConnect disabled — waiting for manual POST /api/session/connect");
            return Task.CompletedTask;
        }

        _ = Task.Run(() =>
        {
            try
            {
                lock (_sfera.SyncRoot) _sfera.Connect();
                _log.LogInformation("Auto-connect to Sfera succeeded");
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Auto-connect to Sfera failed: {msg}. /health will report it; retry via POST /api/session/connect.", ex.Message);
            }
        }, cancellationToken);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
