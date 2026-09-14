using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace EasyIntercept.Hosting;

/// <summary>IPv4 addresses on which other devices on the local network (a phone on the same Wi-Fi) can reach this machine.</summary>
public static class LanAddresses
{
    public readonly record struct Candidate(IPAddress Address, NetworkInterfaceType Type, OperationalStatus Status);

    public static IReadOnlyList<string> Current()
    {
        try
        {
            var candidates = NetworkInterface.GetAllNetworkInterfaces()
                .SelectMany(nic => nic.GetIPProperties().UnicastAddresses
                    .Select(ua => new Candidate(ua.Address, nic.NetworkInterfaceType, nic.OperationalStatus)));
            return Select(candidates);
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }

    /// <summary>
    /// Usable addresses, most likely home/office network first: 192.168/16, then 10/8, then 172.16/12
    /// (where Docker, WSL and Hyper-V bridges usually live), then anything else.
    /// </summary>
    public static IReadOnlyList<string> Select(IEnumerable<Candidate> candidates) =>
        candidates
            .Where(c => c.Status == OperationalStatus.Up
                && c.Type is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                && c.Address.AddressFamily == AddressFamily.InterNetwork
                && !IPAddress.IsLoopback(c.Address)
                && !IsLinkLocal(c.Address))
            .OrderBy(c => Rank(c.Address)) // stable: keeps interface order within a rank
            .Select(c => c.Address.ToString())
            .Distinct()
            .ToList();

    private static bool IsLinkLocal(IPAddress ip) => ip.GetAddressBytes() is [169, 254, ..];

    private static int Rank(IPAddress ip) => ip.GetAddressBytes() switch
    {
        [192, 168, ..] => 0,
        [10, ..] => 1,
        [172, >= 16 and <= 31, ..] => 2,
        _ => 3,
    };
}
