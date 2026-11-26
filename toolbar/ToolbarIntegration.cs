using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Rhino;
using Rhino.UI;

namespace BetterMake2D.Ui;

internal sealed class ToolbarIntegration : IDisposable
{
    private const string TabPanels = "Rhino.UI.Internal.TabPanels.";
    private const string Io = TabPanels + "IO.";
    private const string Resource = "BetterMake2D.Toolbar.rui";
    private const string LegacyId = "36f53cad-5149-4c09-a1b4-88704c179d73";
    private static readonly Guid ButtonId = new("702664be-da37-4bc0-8306-18110b6b577a");
    private static readonly Guid DraftingId = new("90fd89fc-e41f-49cc-bcf0-29e0d58017a1");
    private static readonly Guid FallbackId = new("acd7541b-a90f-48a8-8a44-2f157d3326ff");
    private static readonly Guid RecentId = new("00211289-60a5-460b-88dc-cc667b234dbd");
    private static readonly Regex NativeMacro = new(@"^\s*!?\s*_?Make2D\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private readonly Assembly ui = typeof(Rhino.UI.Runtime.IToolbar).Assembly;
    private readonly Dictionary<(Type, string), MemberInfo> getters = new();
    private readonly Dictionary<(Type, string, int), MethodInfo> methods = new();
    private object attachedBar;
    private object attachedButton;
    private bool migrated;
    private bool reflectionFailed;
    private ToolbarFile fallback;
    private ToolbarGroup fallbackGroup;

    internal void Update(bool enabled)
    {
        if (!reflectionFailed)
        {
            try
            {
                if (!migrated) migrated = RemoveLegacyButtons();
                var anchor = enabled ? FindAnchor() : default;
                if (!ReferenceEquals(attachedBar, anchor.Bar) ||
                    (attachedBar != null && !AtAnchor(anchor)))
                {
                    bool changed = RemoveAttached();
                    if (anchor.Bar != null)
                    {
                        attachedButton ??= NewButton();
                        if (Edit(anchor.Bar, "TryInsertAt", attachedButton, anchor.Index + 1))
                        {
                            attachedBar = anchor.Bar;
                            changed = true;
                        }
                    }
                    if (changed) Notify();
                }
                if (attachedBar != null || !enabled)
                {
                    SetFallbackVisible(false);
                    return;
                }
            }
            catch (Exception exception)
            {
                reflectionFailed = true;
                RhinoApp.WriteLine("BetterMake2D toolbar: " + exception.GetBaseException().Message);
            }
        }
        SetFallbackVisible(enabled);
    }

    private (object Bar, int Index) FindAnchor()
    {
        var instances = Get(TypeOf(TabPanels + "UIInstance"), "Instances") as IDictionary;
        uint serial = RhinoDoc.ActiveDoc?.RuntimeSerialNumber ?? 0;
        object instance = instances?.Contains(serial) == true ? instances[serial] : instances?.Values.Cast<object>().FirstOrDefault();
        object workspace = Get(instance, "CurrentWorkspace");
        var bars = workspace == null ? VisibleGroupToolbars() : Nodes(workspace)
            .Where(node => node.GetType().Name == "ReferencedToolbar")
            .Select(node => Call(node, "GetReference"));
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        (object Bar, int Index) candidate = default;
        foreach (var bar in bars)
        {
            if (bar == null || !seen.Add(bar) || Id(bar) == FallbackId || Id(bar) == RecentId)
                continue;
            int i = 0;
            foreach (var item in Items(Get(bar, "Items")))
            {
                if (NativeMacro.IsMatch(Macro(item, "Left") ?? ""))
                {
                    if (Id(bar) == DraftingId)
                        return (bar, i);
                    if (candidate.Bar == null)
                        candidate = (bar, i);
                    break;
                }
                i++;
            }
        }
        return candidate;
    }

    private IEnumerable<object> VisibleGroupToolbars()
    {
        // The standard Rhino 9 toolbar layout uses groups when no named workspace is loaded.
        var cache = Get(TypeOf(Io + "RuiCache"), "GlobalInstance");
        foreach (var nativeFile in RhinoApp.ToolbarFiles)
        {
            var file = Items(Get(cache, "Files")).FirstOrDefault(item => Id(item) == nativeFile.Id);
            if (file == null) continue;
            var groups = Items(Get(file, "Groups")).ToDictionary(Id);
            for (int i = 0; i < nativeFile.GroupCount; i++)
            {
                var group = nativeFile.GetGroup(i);
                if (group?.Visible == true && groups.TryGetValue(group.Id, out var model))
                    foreach (var bar in Items(Get(model, "Toolbars")))
                        yield return bar;
            }
        }
    }

    private IEnumerable<object> Nodes(object root)
    {
        if (root == null)
            yield break;
        yield return root;
        foreach (var child in Items(Get(root, "Children")))
            foreach (var node in Nodes(child))
                yield return node;
    }

    private bool AtAnchor((object Bar, int Index) anchor) =>
        ReferenceEquals(Items(Get(attachedBar, "Items")).ElementAtOrDefault(anchor.Index + 1), attachedButton);

    private bool RemoveLegacyButtons()
    {
        var cache = Get(TypeOf(Io + "RuiCache"), "GlobalInstance");
        var file = Items(Get(cache, "Files")).FirstOrDefault(x => Get(x, "DefaultRui") is true);
        if (file == null) return false;
        object[] bars = Items(Get(file, "Toolbars")).ToArray();
        var ids = new HashSet<Guid>(bars.Select(bar => InstalledButtonId(Id(bar))));
        bool changed = false;
        foreach (var bar in bars)
        {
            foreach (var item in Items(Get(bar, "Items")).ToArray())
            {
                Guid id = Id(item);
                if (id != ButtonId && (Id(bar) == RecentId ? !ids.Contains(id) : id != InstalledButtonId(Id(bar))))
                    continue;
                if (Macro(item, "Left") is not ("! _BetterMake2D" or "! _P2VisibleLines"))
                    continue;
                string right = Macro(item, "Right");
                if (!string.IsNullOrEmpty(right) && right is not ("! _BetterMake2DHidden" or "! _P2HiddenLines"))
                    continue;
                changed |= Call(bar, "TryRemove", item) is true;
            }
        }
        if (changed) Notify();
        return true;
    }

    private static Guid InstalledButtonId(Guid bar) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes(bar + "/" + LegacyId)).AsSpan(0, 16));

    private bool RemoveAttached()
    {
        if (attachedBar == null) return false;
        bool removed = Edit(attachedBar, "TryRemove", attachedButton);
        attachedBar = null;
        var cache = Get(TypeOf(Io + "RuiCache"), "GlobalInstance");
        foreach (var file in Items(Get(cache, "Files")))
        {
            var recent = Items(Get(file, "Toolbars")).FirstOrDefault(bar => Id(bar) == RecentId);
            foreach (var item in Items(Get(recent, "Items")).Where(item => Id(item) == ButtonId).ToArray())
                removed |= Edit(recent, "TryRemove", item);
        }
        return removed;
    }

    private bool Edit(object bar, string method, params object[] arguments)
    {
        object owner = Get(bar, "Parent");
        object modified = Get(owner, "ModifiedFlag");
        try { return Call(bar, method, arguments) is true; }
        finally
        {
            // The transient button is excluded from ordinary Rhino toolbar saves on shutdown.
            if (modified is bool value)
                Set(owner, "ModifiedFlag", value);
        }
    }

    private object NewButton()
    {
        using var stream = OpenResource();
        var icons = XDocument.Load(stream).Root.Element("icons").Element("icon");
        Guid image = Guid.Parse((string)icons.Attribute("guid"));
        var icon = New(Io + "Structures.ToolbarIcon", image);
        Set(icon, "Light", New(Io + "SvgIcon", image, icons.Element("light").Elements().Single().ToString(SaveOptions.DisableFormatting)));
        Set(icon, "Dark", New(Io + "SvgIcon", image, icons.Element("dark").Elements().Single().ToString(SaveOptions.DisableFormatting)));
        var label = New(Io + "LocaleText");
        var locale = TypeOf(Io + "Locale");
        Call(label, "UpdateText", Enum.Parse(locale, "English_US"), "BetterMake2D");
        Call(label, "UpdateText", Enum.Parse(locale, "Chinese"), "BetterMake2D");
        var macro = New(Io + "Structures.ToolbarMacro", new Guid("91d305d0-a18f-47b1-ad90-6a99c11e5398"));
        Set(macro, "Script", "! _BetterMake2D");
        Set(macro, "PrivateTooltipText", label);
        var button = New(Io + "Structures.ToolbarItem", ButtonId);
        Set(button, "Name", label);
        Set(button, "Icon", icon);
        Set(button, "Left", macro);
        Set(button, "Visible", true);
        return button;
    }

    private void Notify()
    {
        object queue = Get(TypeOf("Rhino.UI.Internal.Queues.EventQueue"), "GlobalInstance");
        Call(queue, "Invoke", "RuiCache");
        // The cache event alone leaves the toolbar's displayed button controls intact.
        Call(TypeOf(TabPanels + "OldCodeExtensions"), "RefreshToolbars");
    }

    private void SetFallbackVisible(bool visible)
    {
        if (!visible)
        {
            if (fallback?.Close(false) == true)
            {
                fallback = null;
                fallbackGroup = null;
            }
            return;
        }
        if (fallbackGroup == null)
        {
            // A process-owned temporary source avoids restoring a toolbar when the drawing plug-in is disabled.
            string path = Path.Combine(Path.GetTempPath(), "BetterMake2D-" + Guid.NewGuid().ToString("N") + ".rui");
            try
            {
                using (var input = OpenResource())
                using (var output = File.Create(path)) input.CopyTo(output);
                fallback = RhinoApp.ToolbarFiles.Open(path);
                fallbackGroup = fallback?.GetGroup(0);
            }
            finally { File.Delete(path); }
        }
        if (fallbackGroup != null && !fallbackGroup.Visible)
            fallbackGroup.Visible = true;
    }

    private static Stream OpenResource() => typeof(ToolbarIntegration).Assembly.GetManifestResourceStream(Resource)
        ?? throw new InvalidOperationException("BetterMake2D toolbar resource is missing.");

    public void Dispose()
    {
        try { if (RemoveAttached()) Notify(); }
        finally { fallback?.Close(false); }
    }

    private Type TypeOf(string name) => ui.GetType(name, true);
    private object New(string name, params object[] args) =>
        Activator.CreateInstance(TypeOf(name), BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, args, null);
    private static IEnumerable<object> Items(object items) => items is IEnumerable collection ? collection.Cast<object>() : Enumerable.Empty<object>();
    private Guid Id(object node) => Get(node, "Id") is Guid id ? id : Guid.Empty;
    private string Macro(object item, string side) => Get(Get(item, side), "Script") as string;

    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private object Get(object target, string name)
    {
        if (target == null) return null;
        Type type = target as Type ?? target.GetType();
        if (!getters.TryGetValue((type, name), out var member))
            getters[(type, name)] = member = (MemberInfo)type.GetProperty(name, Members) ?? type.GetField(name, Members);
        object owner = target is Type ? null : target;
        return member is PropertyInfo property ? property.GetValue(owner) : (member as FieldInfo)?.GetValue(owner);
    }
    private static void Set(object target, string name, object value) => target.GetType().GetProperty(name, Members).SetValue(target, value);
    private object Call(object target, string name, params object[] args)
    {
        Type type = target as Type ?? target.GetType();
        if (!methods.TryGetValue((type, name, args.Length), out var method))
            methods[(type, name, args.Length)] = method = type.GetMethods(Members).First(x => x.Name == name &&
                !x.IsGenericMethod && x.GetParameters().Length == args.Length &&
                x.GetParameters().Select((parameter, i) => parameter.ParameterType.IsInstanceOfType(args[i])).All(x => x));
        return method.Invoke(target is Type ? null : target, args);
    }
}
