using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Framework;

namespace Tests;

public sealed class TestDbContext : ISharedContext
{
    private readonly object _sync = new();
    private readonly List<string> _logs = new();

    public IReadOnlyList<string> Logs
    {
        get
        {
            lock (_sync)
            {
                return _logs.ToList();
            }
        }
    }

    public void AddLog(string message)
    {
        lock (_sync)
        {
            _logs.Add(message);
        }
    }

    public Task SetUpAsync()
    {
        lock (_sync)
        {
            _logs.Clear();
            _logs.Add("SharedContext: SetUp");
        }

        return Task.CompletedTask;
    }

    public Task TearDownAsync()
    {
        AddLog("SharedContext: TearDown");
        return Task.CompletedTask;
    }
}