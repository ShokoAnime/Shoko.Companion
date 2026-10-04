using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using NLog;

#pragma warning disable CA1416
namespace Shoko.Companion.Notifications;

/// <summary>
/// Per-user Windows toast prerequisite: ensures a Start Menu shortcut with a
/// stable AppUserModelID exists, and registers the current process's AUMID so
/// [Windows.UI.Notifications.ToastNotificationManager] works.
///
/// Called from <see cref="PlatformNotificationService"/> before the first toast
/// attempt. Idempotent — repeated calls are cheap no-ops.
/// </summary>
public static class WindowsAppUserModelId
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>Stable AUMID for Shoko Companion. Never localize.</summary>
    public const string AppId = "Shoko.Companion.1";

    /// <summary>Display name shown in the Start Menu and toast header.</summary>
    public const string FriendlyName = "Shoko Companion";

    private const string ShortcutRelativePath =
        @"Microsoft\Windows\Start Menu\Programs\Shoko Companion.lnk";

    /// <summary>
    /// Full path of the per-user Start Menu shortcut that carries the AUMID.
    /// </summary>
    public static string ShortcutPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            ShortcutRelativePath);

    /// <summary>
    /// The same shortcut under %LOCALAPPDATA% — the per-user Start Menu Programs
    /// folder that Windows actually scans for toast activation targets.
    /// </summary>
    private static string PerUserProgramsShortcutPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"Microsoft\Windows\Start Menu\Programs\Shoko Companion.lnk");

    /// <summary>
    /// Ensure the AUMID is set for the current process and a matching Start Menu
    /// shortcut exists. Safe to call repeatedly.
    /// </summary>
    public static void EnsureRegistered()
    {
        try
        {
            SetCurrentProcessAppId(AppId);
            EnsureShortcut();
            Logger.Info("Windows AppUserModelID registered: {AppId}", AppId);
        }
        catch (Exception ex)
        {
            // AUMID is a best-effort improvement. Never break toast fallback.
            Logger.Warn(ex, "Failed to register AppUserModelID; toast may not display");
        }
    }

    private static void EnsureShortcut()
    {
        EnsureShortcutAt(ShortcutPath);
        EnsureShortcutAt(PerUserProgramsShortcutPath);
    }

    private static void EnsureShortcutAt(string lnkPath)
    {
        if (File.Exists(lnkPath))
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(lnkPath)!);
        CreateShortcut(lnkPath, exeFile: UrlSchemeExecutablePath(), appId: AppId, name: FriendlyName);
    }

    private static string UrlSchemeExecutablePath()
    {
        // Mirrors UrlSchemeRegistrar.ExecutablePath logic without taking a dependency
        // on the internal accessor. Keep in sync.
        return Environment.ProcessPath
            ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
            ?? throw new InvalidOperationException("Cannot determine executable path");
    }

    private static void CreateShortcut(string lnkPath, string exeFile, string appId, string name)
    {
        // Hand-rolled IShellLinkW + IPersistFile. Avoids IWshRuntimeLibrary COM
        // dependency and works on .NET 10 single-file publish.
        var shellLink = (IShellLinkW)new CShellLink();
        try
        {
            shellLink.SetPath(exeFile);
            shellLink.SetWorkingDirectory(Path.GetDirectoryName(exeFile) ?? string.Empty);
            shellLink.SetDescription(name);
            // Property store for AppUserModelID
            var propertyStore = (IPropertyStore)shellLink;
            var appIdKey = PROPERTYKEY.AppUserModel_ID;
            var propVariant = new PROPVARIANT(appId);
            try
            {
                propertyStore.SetValue(ref appIdKey, ref propVariant);
            }
            finally
            {
                propVariant.Dispose();
            }
            propertyStore.Commit();
            var persistFile = (IPersistFile)shellLink;
            persistFile.Save(lnkPath, fRemember: true);
            Logger.Info("Created Start Menu shortcut with AUMID at {Path}", lnkPath);
        }
        finally
        {
            Marshal.FinalReleaseComObject(shellLink);
        }
    }

    // ── P/Invoke ────────────────────────────────────────────────────────

    [DllImport("shell32.dll", PreserveSig = false)]
    private static extern void SetCurrentProcessExplicitAppUserModelID(
        [MarshalAs(UnmanagedType.LPWStr)] string AppID);

    private static void SetCurrentProcessAppId(string appId)
    {
        // Available since Windows 7. No-op if the API is missing (older Windows).
        try { SetCurrentProcessExplicitAppUserModelID(appId); }
        catch (EntryPointNotFoundException) { }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class CShellLink { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport, Guid("0000010B-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        // IPersist first
        void GetClassID(out Guid pClassID);
        // IPersistFile
        void IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([Out, MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint cProps);
        void GetAt(uint iProp, out PROPERTYKEY pkey);
        void GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
        void SetValue(ref PROPERTYKEY key, ref PROPVARIANT pv);
        void Commit();
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;

        // System.AppUserModel.ID (PKEY_AppUserModel_ID from propsys.h)
        public static PROPERTYKEY AppUserModel_ID => new()
        {
            fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
            pid = 5,
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPVARIANT : IDisposable
    {
        public ushort vt;
        public ushort wReserved1, wReserved2, wReserved3;
        public IntPtr p;
        public int p2;

        public PROPVARIANT(string value)
        {
            vt = 31; // VT_LPWSTR
            wReserved1 = wReserved2 = wReserved3 = 0;
            p = Marshal.StringToCoTaskMemUni(value ?? string.Empty);
            p2 = 0;
        }

        public void Dispose()
        {
            if (p != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(p);
                p = IntPtr.Zero;
            }
        }
    }
}