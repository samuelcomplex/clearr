namespace Clearr.Api;

/// <summary>What HTTPS is actually doing right now (it only changes on restart).</summary>
public class HttpsState
{
    public int? ActivePort { get; set; }

    public string? Certificate { get; set; }

    public string? Error { get; set; }
}
