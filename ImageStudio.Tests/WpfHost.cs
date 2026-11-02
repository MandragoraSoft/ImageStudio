using System.Runtime.ExceptionServices;
using System.Windows.Threading;

namespace ImageStudio.Tests;

// Jeden wątek UI z prawdziwą aplikacją WPF (App.xaml) dla testów z oknami: Application może istnieć tylko raz na proces
internal static class WpfHost
{
    private static readonly Lazy<Dispatcher> Ui = new(Start);

    private static Dispatcher Start()
    {
        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            var app = new ImageStudio.App();
            app.InitializeComponent();
            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        return dispatcher!;
    }

    // Asynchroniczny test na wątku UI: kontynuacje wracają na ten sam wątek, więc ViewModel i widoki (ICollectionView) żyją na jednym
    public static Task RunAsync(Func<Task> action) => Ui.Value.InvokeAsync(action).Task.Unwrap();

    public static void Run(Action action)
    {
        Exception? error = null;
        Ui.Value.Invoke(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        });
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
