using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace FC.SDK.Viewer;

/// <summary>
/// Finds Canon bodies on the local network with an SSDP M-SEARCH (UPnP discovery, 239.255.255.250:1900).
/// A body joined to a home network has whatever address DHCP gave it, so a fixed list cannot find it.
/// Replies that do not mention Canon are ignored, so routers and TVs stay out of the list.
/// </summary>
internal static class WifiDiscovery
{
    private static readonly IPEndPoint SsdpGroup = new(IPAddress.Parse("239.255.255.250"), 1900);

    public static async Task<IReadOnlyList<(string Host, string Description)>> FindCanonAsync(TimeSpan listen, ILogger logger, CancellationToken ct)
    {
        var found = new Dictionary<string, string>();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(listen);

        // Replies to the M-SEARCH come back unicast to this socket's ephemeral port.
        using var socket = new UdpClient(AddressFamily.InterNetwork);
        socket.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

        var request = Encoding.ASCII.GetBytes(
            "M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\nST: ssdp:all\r\n\r\n");
        for (int i = 0; i < 3; i++)
            await socket.SendAsync(request, SsdpGroup, ct);

        try
        {
            while (true)
            {
                var result = await socket.ReceiveAsync(deadline.Token);
                var text = Encoding.UTF8.GetString(result.Buffer);
                if (!text.Contains("canon", StringComparison.OrdinalIgnoreCase)) continue;

                var host = result.RemoteEndPoint.Address.ToString();
                if (!found.TryAdd(host, Header(text, "SERVER") ?? Header(text, "USN") ?? "Canon")) continue;
                logger.LogInformation("SSDP: Canon device at {Host}: {Announcement}", host, text.Replace("\r\n", " | "));
            }
        }
        catch (OperationCanceledException) { }

        return found.Select(kv => (kv.Key, kv.Value)).ToList();
    }

    private static string? Header(string message, string name)
    {
        foreach (var line in message.Split("\r\n"))
        {
            int colon = line.IndexOf(':');
            if (colon > 0 && line.AsSpan(0, colon).Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                return line[(colon + 1)..].Trim();
        }
        return null;
    }
}
