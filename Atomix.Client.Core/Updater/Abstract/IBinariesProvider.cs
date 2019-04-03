using System.IO;
using System.Threading.Tasks;

namespace Atomix.Updater.Abstract
{
    public interface IBinariesProvider
    {
        Task<Stream> GetLatestBinariesAsync();
    }
}
