using System;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

using Atomix.Updater.Abstract;

namespace Atomix.Updater
{
    public class LocalVersionProvider : IVersionProvider
    {
        readonly string FilePath;
        public LocalVersionProvider(string filePath)
        {
            FilePath = filePath;
        }

        public async Task<Version> GetLatestVersionAsync()
        {
            await Task.Delay(100);
            var json = JToken.Parse(File.ReadAllText(FilePath));
            return Version.Parse(json["version"].Value<string>());
        }
    }

    public static class LocalVersionProviderExt
    {
        public static Updater UseLocalVersionProvider(this Updater updater, string filePath)
        {
            return updater.UseVersionProvider(new LocalVersionProvider(filePath));
        }
    }
}
