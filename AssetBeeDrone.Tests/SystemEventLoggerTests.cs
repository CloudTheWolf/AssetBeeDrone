using AssetBeeDrone.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace AssetBeeDrone.Tests;

public sealed class SystemEventLoggerTests
{
    [Fact]
    public void WriteError_does_not_throw_for_current_os()
    {
        SystemEventLogger logger = new(NullLogger<SystemEventLogger>.Instance);

        Exception? exception = Record.Exception(() =>
            logger.WriteError(
                "Inventory sync failed; the service will retry next interval.",
                "System.InvalidOperationException: boom"));

        Assert.Null(exception);
    }

    [Fact]
    public void WriteError_rejects_blank_title()
    {
        SystemEventLogger logger = new(NullLogger<SystemEventLogger>.Instance);

        Assert.ThrowsAny<ArgumentException>(() => logger.WriteError(" ", "details"));
    }
}
