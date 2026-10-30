using PricingTool.NopIntegration;

namespace PricingTool.Tests;

/// <summary>Product.HasDiscountsAppliedStoreIds: add our store, keep every other, sorted, colon-separated.</summary>
public class StoreIdListTests
{
    [Theory]
    [InlineData(null, 2, "2")]
    [InlineData("", 2, "2")]
    [InlineData("2", 3, "2:3")]
    [InlineData("3", 2, "2:3")]
    [InlineData("1:3", 2, "1:2:3")]
    [InlineData("1:2:3", 2, "1:2:3")]
    [InlineData("10:2", 3, "2:3:10")]
    [InlineData(" 3 : 1 ", 2, "1:2:3")]
    public void Our_store_is_merged_and_none_is_dropped(string? existing, int store, string expected) =>
        Assert.Equal(expected, StoreIdList.Merge(existing, store));

    [Fact]
    public void A_list_that_would_not_fit_nvarchar_40_is_refused()
    {
        var long14 = string.Join(':', Enumerable.Range(100, 10));               // 39 chars
        Assert.Throws<InvalidOperationException>(() => StoreIdList.Merge(long14, 999));
    }
}
