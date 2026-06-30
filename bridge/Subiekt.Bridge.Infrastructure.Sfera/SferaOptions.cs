namespace Subiekt.Bridge.Infrastructure.Sfera;

// Connection + login settings for the Sfera session. Bound from the "Sfera"
// config section in the composition root (Api).
public class SferaOptions
{
    public string BinariesDir { get; set; } = "";
    public string ConfigDir { get; set; } = "";
    public string TempDir { get; set; } = "";
    public string DeploymentName { get; set; } = "Nexo";

    public string SqlServer { get; set; } = "";
    public string SqlDatabase { get; set; } = "";
    public bool SqlUseWindowsAuth { get; set; } = true;
    // Encryption ON by default — connections to SQL must be encrypted in transit.
    public bool SqlEncrypt { get; set; } = true;
    public string SqlUser { get; set; } = "";
    public string SqlPassword { get; set; } = "";

    public string NexoUser { get; set; } = "";
    public string NexoPassword { get; set; } = "";

    // Connect to Sfera automatically on startup (background, best-effort).
    // The OL business contract assumes the bridge is already connected.
    public bool AutoConnect { get; set; } = true;
}
