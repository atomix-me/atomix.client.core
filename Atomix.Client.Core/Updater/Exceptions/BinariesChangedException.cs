using System;

namespace Atomix.Updater.Exceptions
{
    public class BinariesChangedException : Exception
    {
        public BinariesChangedException()
            : base("Loaded binaries has been changed")
        {

        }
    }
}
