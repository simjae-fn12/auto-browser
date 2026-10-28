namespace OinziBrowser;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // The uninstaller's last step: take Oinzi out of the default-browser list and Windows startup.
        if (args is ["--unregister"]) { BrowserForm.UnregisterBrowser(); return; }
        if (!Rebrand.Migrate())
        {
            MessageBox.Show("이전 이름(pluck)으로 실행 중인 브라우저가 있습니다. 그 창을 닫은 뒤 Oinzi를 다시 실행하세요.", "Oinzi",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var links = BrowserForm.LinksFromArgs(args);
        using var instance = new Mutex(true, @"Local\" + BrowserForm.InstanceName, out var first);
        if (!first)
        {
            if (BrowserForm.HandOff(links)) return;
            // The running Oinzi may be closing; take over once it is gone.
            try { if (!instance.WaitOne(TimeSpan.FromSeconds(15))) return; }
            catch (AbandonedMutexException) { }
        }
        SetCurrentProcessExplicitAppUserModelID("oinzi.browser");
        ApplicationConfiguration.Initialize();
        BrowserForm.LaunchLinks.AddRange(links);
        Application.Run(new BrowserForm());
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
}
