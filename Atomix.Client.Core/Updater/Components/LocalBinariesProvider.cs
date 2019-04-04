using System.IO;
using System.Threading.Tasks;

using Atomix.Updater.Abstract;

namespace Atomix.Updater
{
    public class LocalBinariesProvider : IBinariesProvider
    {
        readonly string FilePath;
        public LocalBinariesProvider(string filePath)
        {
            FilePath = filePath;
        }

        public async Task<Stream> GetLatestBinariesAsync()
        {
            await Task.Delay(100);
            return File.OpenRead(FilePath);
        }
    }

    public static class LocalBinariesProviderExt
    {        
        public static AppUpdater UseLocalBinariesProvider(this AppUpdater updater, string filePath)
        {
            return updater.UseBinariesProvider(new LocalBinariesProvider(filePath));
        }
    }
}
