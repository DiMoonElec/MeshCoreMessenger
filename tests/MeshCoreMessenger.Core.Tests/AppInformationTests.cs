using MeshCoreMessenger.Core;
using Xunit;

namespace MeshCoreMessenger.Core.Tests;

public sealed class AppInformationTests
{
    [Fact]
    public void ProductNameIsStable() =>
        Assert.Equal("MeshCoreMessenger", AppInformation.ProductName);
}
