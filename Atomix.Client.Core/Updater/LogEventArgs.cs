using System;

namespace Atomix.Updater
{
    public class LogEventArgs : EventArgs
    {
        public LogLevel Level { get; private set; }
        public string Message { get; private set; }

        public LogEventArgs(LogLevel level, string message)
        {
            Level = level;
            Message = message;
        }
    }

    public enum LogLevel
    {
        Error,
        Warning,
        Debug
    }
}
