using RemoteFlattener.Network;
using Xunit;

namespace RemoteFlattener.Tests.Network;

public class MachineEndpointTests
{
    [Theory]
    [InlineData("server.domain.test")]
    [InlineData("10.0.0.42")]
    public void Host_PreservesExactConnectionAddress(string host)
    {
        var endpoint = new MachineEndpoint(host, 8765);

        Assert.Equal(host, endpoint.Host);
        Assert.Equal($"{host}:8765", endpoint.ToString());
    }
}
