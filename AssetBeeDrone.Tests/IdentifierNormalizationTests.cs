using AssetBeeDrone.Collectors;

namespace AssetBeeDrone.Tests;

public sealed class IdentifierNormalizationTests
{
    [Theory]
    [InlineData("Dell Inc.", "dellInc")]
    [InlineData("Dell_Inc.", "dellInc")]
    [InlineData("DELL_INC", "dellInc")]
    [InlineData("Latitude_5520", "latitude5520")]
    [InlineData("Latitude 5520", "latitude5520")]
    [InlineData("  ", null)]
    [InlineData(null, null)]
    public void ToCamelCaseIdentifier_normalizes_manufacturer_and_sku(string? input, string? expected)
    {
        Assert.Equal(expected, TestCollector.Normalize(input));
    }

    private sealed class TestCollector : InventoryCollectorBase
    {
        public static string? Normalize(string? value) => ToCamelCaseIdentifier(value);
    }
}
