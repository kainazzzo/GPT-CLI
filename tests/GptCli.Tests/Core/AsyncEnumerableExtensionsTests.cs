using GPT.CLI;
using Xunit;

namespace GptCli.Tests.Core;

public sealed class AsyncEnumerableExtensionsTests
{
    [Fact]
    public async Task List_overload_yields_items_in_order()
    {
        var items = new List<int>();
        await foreach (var item in new List<int> { 1, 2, 3 }.ToAsyncEnumerable())
        {
            items.Add(item);
        }

        Assert.Equal(new[] { 1, 2, 3 }, items);
    }

    [Fact]
    public async Task Single_item_overload_yields_that_item()
    {
        var items = new List<int>();
        await foreach (var item in 42.ToAsyncEnumerable())
        {
            items.Add(item);
        }

        Assert.Equal(new[] { 42 }, items);
    }
}
