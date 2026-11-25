using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Threading;
using Rhino.Runtime.InProcess;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length is not (3 or 4) || args.Length == 4 && args[3] is not ("ai" or "svg")) return 64;
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        string system = args[0];
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            foreach (string root in new[] { Path.Combine(system, "netcore"), system })
            {
                string path = Path.Combine(root, name.Name + ".dll");
                if (File.Exists(path)) return AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
            }
            return null;
        };
        using var mutex = new Mutex(false, @"Local\BetterMake2D_HeadlessValidation");
        bool locked = false;
        try
        {
            if (args.Length == 4 && args[3] == "ai") return Run(args[1], args[2], "BetterMake2D.DesktopAiExport");
            try { locked = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { locked = true; }
            if (!locked) throw new InvalidOperationException("Another BetterMake2D geometry worker is active.");
            return Execute(args[1], args[2], args.Length == 4);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.ToString());
            return 1;
        }
        finally { if (locked) mutex.ReleaseMutex(); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Execute(string plugin, string directory, bool vectors)
    {
        Console.WriteLine("P|startup");
        using var core = new RhinoCore(new[] { "/nosplash", "/notemplate", "/scheme=BetterMake2DWorker" },
            WindowStyle.NoWindow, IntPtr.Zero);
        return Run(plugin, directory, "BetterMake2D.DesktopWorker", vectors ? "ExportSvg" : "Run");
    }

    private static int Run(string plugin, string directory, string type, string method = "Run")
    {
        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(plugin);
        var run = assembly.GetType(type, true).GetMethod(method,
            BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
        return (int)run.Invoke(null, new object[] { directory });
    }
}
