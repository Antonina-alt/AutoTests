using System.Threading.Tasks;

namespace Framework;

public interface ISharedContext
{
    Task SetUpAsync();
    Task TearDownAsync();
}