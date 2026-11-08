using System;
using System.Threading;
using System.Threading.Tasks;

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
await Report.PrintAsync(timeout.Token);

static class Report
{
    public static async Task PrintAsync(CancellationToken cancellationToken)
    {
        var pending = ReadCountAsync();
        var count = await pending;
        Console.WriteLine(await FormatAsync(count, token: cancellationToken));
    }

    public static async Task RefreshAsync()
    {
        await Task.Yield();
        Console.WriteLine("Refreshed");
    }

    private static Task<int> ReadCountAsync() => Task.FromResult(42);

    private static Task<string> FormatAsync(int count, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult($"Processed: {count}");
    }
}
