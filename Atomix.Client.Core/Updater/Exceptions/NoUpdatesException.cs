using System;

namespace Atomix.Updater.Exceptions
{
    public class NoUpdatesException : Exception
    {
        public NoUpdatesException()
            : base("Application up to date")
        {

        }
    }
}
