using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Eto.Forms;
using Rhino;
using Rhino.PlugIns;

[assembly: AssemblyTitle("BetterMake2D UI")]
[assembly: AssemblyCompany("BetterMake2D")]
[assembly: AssemblyDescription("Toolbar and Drafting menu integration for BetterMake2D.")]
[assembly: AssemblyVersion("1.6.0.0")]
[assembly: AssemblyFileVersion("1.6.0.0")]
[assembly: AssemblyInformationalVersion("1.6.0")]
[assembly: Guid("08E16F61-63FD-483F-9B56-683EFDAA43E0")]
[assembly: ComVisible(false)]
[assembly: TargetFramework(".NETCoreApp,Version=v10.0")]

namespace BetterMake2D.Ui;

[Guid("08E16F61-63FD-483F-9B56-683EFDAA43E0")]
public sealed class DesktopUiPlugin : PlugIn
{
    private static readonly Guid DrawingPlugin = new("4A30881F-DC1F-4FA2-80E6-F5E233255108");
    private UITimer timer;
    private ToolbarIntegration toolbar;
    private DraftingMenu menu;
    private bool reportedError;

    public override PlugInLoadTime LoadTime => PlugInLoadTime.AtStartup;

    protected override LoadReturnCode OnLoad(ref string errorMessage)
    {
        if (OperatingSystem.IsWindows() && !RhinoApp.IsRunningUnattended && !Rhino.Runtime.HostUtils.RunningAsRhinoInside)
        {
            RhinoApp.Idle += Start;
            RhinoApp.Closing += Close;
        }
        return LoadReturnCode.Success;
    }

    private void Start(object sender, EventArgs args)
    {
        RhinoApp.Idle -= Start;
        toolbar = new ToolbarIntegration();
        menu = new DraftingMenu();
        timer = new UITimer { Interval = 0.75 };
        timer.Elapsed += Refresh;
        timer.Start();
        Refresh(sender, args);
    }

    private void Refresh(object sender, EventArgs args)
    {
        if (RhinoApp.InCommand != 0)
            return;
        bool enabled = PlugIn.PlugInExists(DrawingPlugin, out _, out bool disabled) && !disabled;
        try
        {
            menu.Update(enabled);
            toolbar.Update(enabled);
        }
        catch (Exception error)
        {
            if (!reportedError)
                RhinoApp.WriteLine("BetterMake2D UI: " + error.GetBaseException().Message);
            reportedError = true;
        }
    }

    private void Close(object sender, EventArgs args)
    {
        RhinoApp.Idle -= Start;
        RhinoApp.Closing -= Close;
        if (timer == null)
            return;
        timer.Stop();
        timer.Elapsed -= Refresh;
        timer = null;
        try { menu.Dispose(); }
        finally { toolbar.Dispose(); }
    }

    protected override void OnShutdown()
    {
        Close(this, EventArgs.Empty);
        base.OnShutdown();
    }
}
