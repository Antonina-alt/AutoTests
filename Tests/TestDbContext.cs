using System.Collections.Generic;
using System.Threading.Tasks;
using Framework;

namespace Tests;

public sealed class TestDbContext : ISharedContext
{
    public List<string> Logs { get; } = new();

    public Task SetUpAsync()
    {
        Logs.Clear();
        Logs.Add("SharedContext: SetUp");
        return Task.CompletedTask;
    }

    public Task TearDownAsync()
    {
        Logs.Add("SharedContext: TearDown");
        return Task.CompletedTask;
    }
}