namespace Subiekt.Bridge.Infrastructure.Sfera;

// Two error classes the OL contract distinguishes:
//   unreachable -> infra problem (bridge can't reach Subiekt/SQL/Sfera session)
//   rejected    -> Subiekt/Sfera looked at the request and said no (validation,
//                  missing product, bad NIP, Zapisz=false, ...)
public enum BridgeErrorCode
{
    Unreachable,
    Rejected
}

public sealed class BridgeException : Exception
{
    public BridgeErrorCode Code { get; }

    // Full, detailed message (SQL/Sfera text) — for the SERVER LOG only. Never
    // send this to the client; it leaks internals.
    public string Detail { get; }

    // Generic, safe message returned to the client. Distinguishes the two
    // contract classes by category without exposing SQL/Sfera internals.
    public string Reason => Code == BridgeErrorCode.Unreachable
        ? "Subiekt/Sfera jest chwilowo niedostępny. Spróbuj ponownie później."
        : "Żądanie zostało odrzucone przez Subiekt/Sfera.";

    public BridgeException(BridgeErrorCode code, string detail, Exception? inner = null)
        : base(detail, inner)
    {
        Code = code;
        Detail = detail;
    }

    public string CodeString => Code == BridgeErrorCode.Unreachable ? "unreachable" : "rejected";

    // 503 = infra/transient (OL should retry); 422 = business rejection (don't retry blindly).
    public int HttpStatus => Code == BridgeErrorCode.Unreachable ? 503 : 422;

    public static BridgeException Unreachable(string detail, Exception? inner = null)
        => new(BridgeErrorCode.Unreachable, detail, inner);

    public static BridgeException Rejected(string detail, Exception? inner = null)
        => new(BridgeErrorCode.Rejected, detail, inner);

    // Classify an arbitrary exception into one of the two contract classes.
    // SQL/connectivity/session failures -> unreachable; everything else -> rejected.
    public static BridgeException Classify(Exception ex)
    {
        if (ex is BridgeException be) return be;

        // Unwrap reflection's TargetInvocationException to the real cause.
        var e = ex;
        while (e is System.Reflection.TargetInvocationException && e.InnerException != null)
            e = e.InnerException;

        var msg = e.Message;

        if (e is Microsoft.Data.SqlClient.SqlException
            || e is System.Data.Common.DbException
            || e is TimeoutException
            || e is System.Net.Sockets.SocketException
            || msg.Contains("nie jest pod", StringComparison.OrdinalIgnoreCase)   // "nie jest podłączona"
            || msg.Contains("not connected", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("Logowanie Sfera", StringComparison.OrdinalIgnoreCase))
        {
            return Unreachable(msg, e);
        }

        return Rejected(msg, e);
    }
}
