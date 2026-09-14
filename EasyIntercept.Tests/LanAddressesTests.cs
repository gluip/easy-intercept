using System.Net;
using System.Net.NetworkInformation;
using EasyIntercept.Hosting;
using Xunit;

namespace EasyIntercept.Tests;

public class LanAddressesTests
{
    private static LanAddresses.Candidate Up(string ip, NetworkInterfaceType type = NetworkInterfaceType.Wireless80211) =>
        new(IPAddress.Parse(ip), type, OperationalStatus.Up);

    [Fact]
    public void Skips_addresses_a_phone_cannot_reach()
    {
        var result = LanAddresses.Select([
            Up("127.0.0.1", NetworkInterfaceType.Loopback),
            Up("169.254.10.20"),
            Up("fe80::1"),
            Up("100.101.102.103", NetworkInterfaceType.Tunnel),
            new(IPAddress.Parse("192.168.1.50"), NetworkInterfaceType.Ethernet, OperationalStatus.Down),
            Up("192.168.68.59"),
        ]);

        Assert.Equal(["192.168.68.59"], result);
    }

    [Fact]
    public void Puts_the_likely_home_network_before_virtual_bridges()
    {
        var result = LanAddresses.Select([
            Up("203.0.113.7"),
            Up("172.17.0.1", NetworkInterfaceType.Ethernet), // Docker / WSL bridge
            Up("10.0.0.12"),
            Up("192.168.68.59"),
        ]);

        Assert.Equal(["192.168.68.59", "10.0.0.12", "172.17.0.1", "203.0.113.7"], result);
    }

    [Fact]
    public void Lists_an_address_once_even_when_several_interfaces_report_it()
    {
        var result = LanAddresses.Select([Up("192.168.68.59"), Up("192.168.68.59", NetworkInterfaceType.Ethernet)]);

        Assert.Equal(["192.168.68.59"], result);
    }
}
