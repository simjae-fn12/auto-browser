namespace PluckWindowsNative;

static class Program
{
    [STAThread]
    static void Main()
    {
        SetCurrentProcessExplicitAppUserModelID("pluck.browser");
        ApplicationConfiguration.Initialize();
        Application.Run(new BrowserForm());
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
}
