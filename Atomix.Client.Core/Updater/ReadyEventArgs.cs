using System;

namespace Atomix.Updater
{
    public class ReadyEventArgs : EventArgs
    {
        public Version Version { get; private set; }
        public string Installer { get; private set; }

        public ReadyEventArgs(Version version, string installer)
        {
            Version = version;
            Installer = installer;
        }
    }
}
