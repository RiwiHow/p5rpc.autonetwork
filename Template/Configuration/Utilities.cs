using System.Diagnostics;

namespace p5rpc.autonetwork.Template.Configuration;

public class Utilities
{
    public static T TryGetValue<T>(Func<T> getValue, int timeout, int sleepTime, CancellationToken token = default) where T : new()
    {
        Stopwatch watch = new();
        watch.Start();
        bool valueSet = false;
        T value = new();

        while (watch.ElapsedMilliseconds < timeout)
        {
            if (token.IsCancellationRequested)
                return value;

            try
            {
                value = getValue();
                valueSet = true;
                break;
            }
            catch (Exception) { /* Ignored */ }

            Thread.Sleep(sleepTime);
        }

        if (!valueSet)
            throw new Exception($"Timeout limit {timeout} exceeded.");

        return value;
    }
}
