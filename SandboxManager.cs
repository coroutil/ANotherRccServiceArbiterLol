using Arbiter;
using System.Diagnostics;

public static class SandboxManager
{
    private static readonly List<Sandbox> _sandboxes = new();

    public static Sandbox Create()
    {
        var s = new Sandbox();
        lock (_sandboxes) _sandboxes.Add(s);
        return s;
    }

    public static void DisposeEverything()
    {
        lock (_sandboxes)
        {
            foreach (var s in _sandboxes)
                s.Dispose();

            _sandboxes.Clear();
        }
    }
}