using System;
using System.Threading.Tasks;

namespace Atomix.Updater
{
    static class Wait
    {
        public static async Task While(Func<bool> expression, int timeout = 0)
        {
            timeout = timeout <= 0 ? int.MaxValue : timeout;

            while (expression())
            {
                if (timeout <= 0)
                    throw new TimeoutException();

                timeout -= 100;
                await Task.Delay(100);
            }
        }
    }
}
