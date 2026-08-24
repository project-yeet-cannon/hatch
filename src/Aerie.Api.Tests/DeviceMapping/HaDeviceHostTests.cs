using Aerie.Api.Services.DeviceMapping;

namespace Aerie.Api.Tests.DeviceMapping;

/// <summary>
/// docs/camera-devices-architecture.md. This is the piece that lets a camera stop
/// being told a static address, so what it must not do is confidently return
/// something that is not an address.
/// </summary>
public class HaDeviceHostTests
{
    [Fact]
    public void Reads_the_host_out_of_a_reolink_configuration_url()
    {
        Assert.Equal("192.168.1.40", HaDeviceHost.FromConfigurationUrl("http://192.168.1.40"));
    }

    [Fact]
    public void Drops_the_web_ui_port()
    {
        // configuration_url says where the camera's web UI is. RTSP is on a
        // different port, so carrying this one forward would be worse than
        // carrying nothing.
        Assert.Equal("10.0.0.9", HaDeviceHost.FromConfigurationUrl("http://10.0.0.9:8000"));
    }

    [Fact]
    public void Drops_a_path_too()
    {
        Assert.Equal("10.0.0.9", HaDeviceHost.FromConfigurationUrl("https://10.0.0.9/admin/index.html"));
    }

    [Fact]
    public void Keeps_a_hostname_as_a_hostname()
    {
        Assert.Equal("front-door.lan", HaDeviceHost.FromConfigurationUrl("http://front-door.lan"));
    }

    [Fact]
    public void Brackets_an_ipv6_literal()
    {
        // Uri hands back the address without brackets, and it needs them the
        // moment something appends ":554".
        Assert.Equal("[fd00::1]", HaDeviceHost.FromConfigurationUrl("http://[fd00::1]/"));
    }

    [Fact]
    public void A_homeassistant_scheme_url_is_not_an_address()
    {
        // Several integrations point configuration_url at a page inside HA
        // itself. Its "host" is a config-flow handler name, which would be
        // stored as a camera's address and fail to resolve much later,
        // somewhere far from here.
        Assert.Null(HaDeviceHost.FromConfigurationUrl("homeassistant://navigate/config/reolink"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    public void Nothing_usable_is_null(string? url)
    {
        // The ordinary case: most integrations set no configuration_url, and a
        // device without one just means the operator types a host.
        Assert.Null(HaDeviceHost.FromConfigurationUrl(url));
    }
}
