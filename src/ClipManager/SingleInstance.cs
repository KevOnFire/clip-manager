using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using ClipManager.Core;

namespace ClipManager;

/// <summary>
/// Sorgt dafür, dass Clippy nur einmal läuft. Startet man es ein zweites Mal, gibt der zweite Start
/// dem laufenden Bescheid (das holt sein Fenster nach vorne) und beendet sich gleich wieder.
/// </summary>
internal static class SingleInstance
{
    // Pro Windows-Benutzer eindeutig ("Local\" = nur in dieser Anmeldesitzung).
    private const string MutexName = @"Local\Clippy.ClipManager.SingleInstance";
    private const string EventName = @"Local\Clippy.ClipManager.ShowWindow";
    private const int AsfwAny = -1;

    private static Mutex? _mutex;
    private static EventWaitHandle? _showEvent;

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    /// <summary>
    /// True, wenn dies der erste Start ist. Sonst wird das laufende Clippy nach vorne geholt und false zurückgegeben.
    /// </summary>
    public static bool TryStart(Action showWindow)
    {
        try
        {
            _mutex = new Mutex(true, MutexName, out var createdNew);
            if (!createdNew)
            {
                // Der laufende Prozess darf sein Fenster in den Vordergrund holen (sonst blinkt es nur in der Taskleiste).
                AllowSetForegroundWindow(AsfwAny);
                if (EventWaitHandle.TryOpenExisting(EventName, out var existing))
                {
                    existing.Set();
                    existing.Dispose();
                }

                _mutex.Dispose();
                _mutex = null;
                return false;
            }

            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
            var dispatcher = Application.Current.Dispatcher;
            var thread = new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        _showEvent.WaitOne();
                        dispatcher.BeginInvoke(showWindow);
                    }
                    catch (ObjectDisposedException)
                    {
                        return;
                    }
                }
            })
            {
                IsBackground = true,
                Name = "Clippy-Einzelstart",
            };
            thread.Start();
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException or IOException or AbandonedMutexException)
        {
            // Im Zweifel lieber normal starten als gar nicht.
            Log.Error("Prüfung auf laufendes Clippy fehlgeschlagen", ex);
            return true;
        }
    }

    /// <summary>Holt ein Fenster nach vorne (auch aus der Taskleiste, wenn es minimiert ist).</summary>
    public static void BringToFront(Window? window)
    {
        if (window == null)
        {
            return;
        }

        if (!window.IsVisible)
        {
            window.Show();
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
        // Kurz "immer oben", damit es sicher vor anderen Fenstern landet.
        window.Topmost = true;
        window.Topmost = false;
        window.Focus();
    }
}
