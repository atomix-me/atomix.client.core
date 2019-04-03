using System;

namespace Atomix.Updater
{
    public class ReadyEventArgs : EventArgs
    {
        public Version Version { get; private set; }

        public ReadyEventArgs(Version version)
        {
            Version = version;
        }
    }
}
