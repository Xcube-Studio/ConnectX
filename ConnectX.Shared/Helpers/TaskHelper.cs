namespace ConnectX.Shared.Helpers;

public static class TaskHelper
{
    public static void Forget(this Task t){}
    public static void Forget<T>(this Task<T> t){}
    public static void Forget(this ValueTask t){}
    public static void Forget<T>(this ValueTask<T> t){}
    
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
                Console.WriteLine(e);
                break;
            }
        }
    }
}