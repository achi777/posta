using System.Collections.Concurrent;

namespace Posta.Outlook;

/// <summary>
/// A single-threaded apartment (STA) worker that owns all COM objects for one Outlook session.
/// Outlook's object model is apartment-threaded: every call on a COM object must run on the same
/// STA thread that created the Outlook.Application. All work is marshalled onto this thread.
/// </summary>
internal sealed class StaComContext : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;

    public StaComContext(string name)
    {
        _thread = new Thread(Pump) { IsBackground = true, Name = name };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    private void Pump()
    {
        foreach (var work in _queue.GetConsumingEnumerable())
        {
            try { work(); } catch { /* individual work items report through their own TCS */ }
        }
    }

    public Task<T> Run<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _queue.Add(() =>
            {
                try { tcs.TrySetResult(func()); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });
        }
        catch (InvalidOperationException)
        {
            tcs.TrySetException(new ObjectDisposedException(nameof(StaComContext)));
        }
        return tcs.Task;
    }

    public Task Run(Action action) => Run<object?>(() => { action(); return null; });

    public void Dispose()
    {
        try { _queue.CompleteAdding(); } catch { }
    }
}
