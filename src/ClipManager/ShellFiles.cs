using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace ClipManager;

/// <summary>Windows-Funktionen rund um Dateien: Papierkorb, Explorer, Standard-Player.</summary>
internal static class ShellFiles
{
    private const uint FoDelete = 0x0003;
    private const ushort FofSilent = 0x0004;
    private const ushort FofNoConfirmation = 0x0010;
    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofNoErrorUi = 0x0400;

    /// <summary>Verschiebt eine Datei in den Papierkorb (wiederherstellbar).</summary>
    public static void MoveToRecycleBin(string path)
    {
        var op = new ShFileOpStruct
        {
            wFunc = FoDelete,
            pFrom = path + "\0\0",
            fFlags = (ushort)(FofAllowUndo | FofNoConfirmation | FofSilent | FofNoErrorUi),
        };

        var result = SHFileOperation(ref op);
        if (result != 0 || op.fAnyOperationsAborted)
        {
            throw new IOException($"Die Datei konnte nicht in den Papierkorb verschoben werden (Fehler {result}). Ist sie noch in einem anderen Programm offen?");
        }
    }

    public static void ShowInExplorer(string path) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });

    public static void OpenWithDefaultApp(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
            ShowInExplorer(path);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref ShFileOpStruct lpFileOp);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }
}
