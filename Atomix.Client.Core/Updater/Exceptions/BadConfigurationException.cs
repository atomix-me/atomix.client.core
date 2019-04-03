using System;

namespace Atomix.Updater.Exceptions
{
    public class BadConfigurationException : Exception
    {
        public BadConfigurationException()
            : base("Bad Updater configuration")
        {

        }
    }
}
