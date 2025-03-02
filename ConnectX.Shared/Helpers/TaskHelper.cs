using System.Diagnostics;

namespace ConnectX.Shared.Helpers;

public static class TaskHelper
{
    public static void Forget(this Task t)
    {
        t.ContinueWith(r =>
        {
            Debug.WriteLine(r.Exception);
        }, TaskContinuationOptions.OnlyOnFaulted);
    }

    public static void Forget<T>(this Task<T> t)
    {
        t.ContinueWith(r =>
        {
            Debug.WriteLine(r.Exception);
        }, TaskContinuationOptions.OnlyOnFaulted);
    }

    public static void Forget(this ValueTask t)
    {
        t.AsTask().ContinueWith(r =>
        {
            Debug.WriteLine(r.Exception);
        }, TaskContinuationOptions.OnlyOnFaulted);
    }

    public static void Forget<T>(this ValueTask<T> t)
    {
        t.AsTask().ContinueWith(r =>
        {
            Debug.WriteLine(r.Exception);
        }, TaskContinuationOptions.OnlyOnFaulted);
    }

    public static async ValueTask WaitUntilAsync(Func<bool> predicate, CancellationToken cancellationToken = default)
    {
        while (!predicate())
        {
            try
            {
                await Task.Delay(100, cancellationToken);
            }
            catch (TaskCanceledException e)
            {
                break;
            }
        }
    }
}