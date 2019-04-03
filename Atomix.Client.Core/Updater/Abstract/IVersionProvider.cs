using System;
using System.Threading.Tasks;

namespace Atomix.Updater.Abstract
{
    public interface IVersionProvider
    {
        Task<Version> GetLatestVersionAsync();
    }
}
