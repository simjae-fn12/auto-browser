namespace PluckWindowsNative;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        var links = BrowserForm.LinksFromArgs(args);
        using var instance = new Mutex(true, @"Local\" + BrowserForm.InstanceName, out var first);
        if (!first)
        {
            if (BrowserForm.HandOff(links)) return;
            // The running pluck may be closing; take over once it is gone.
            try { if (!instance.WaitOne(TimeSpan.FromSeconds(15))) return; }
            catch (AbandonedMutexException) { }
        }
        SetCurrentProcessExplicitAppUserModelID("pluck.browser");
        ApplicationConfiguration.Initialize();
        BrowserForm.LaunchLinks.AddRange(links);
        Application.Run(new BrowserForm());
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
}
