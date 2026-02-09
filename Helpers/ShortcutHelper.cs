using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace AdbExplorer.Helpers
{
    public static class ShortcutHelper
    {
        public static void CreateAppLaunchShortcut(string shortcutPath, string packageName,
            string appLabel, string? deviceName = null, string? iconPath = null)
        {
            string exePath = Environment.ProcessPath
                ?? System.Reflection.Assembly.GetExecutingAssembly().Location;

            // Build arguments: -launch <package> [deviceName]
            string args = $"-launch {packageName}";
            if (!string.IsNullOrEmpty(deviceName))
                args += $" {deviceName}";

            var link = (IShellLinkW)new ShellLink();
            link.SetPath(exePath);
            link.SetArguments(args);
            link.SetDescription($"Launch {appLabel} on Android device");
            link.SetWorkingDirectory(Path.GetDirectoryName(exePath) ?? "");

            // Use custom icon if available, otherwise use exe icon
            string? iconToUse = null;
            if (!string.IsNullOrEmpty(iconPath) && File.Exists(iconPath))
            {
                iconToUse = iconPath;
                if (string.Equals(Path.GetExtension(iconPath), ".png", StringComparison.OrdinalIgnoreCase))
                {
                    string icoPath = Path.ChangeExtension(iconPath, ".ico");
                    bool needsRefresh = !File.Exists(icoPath) ||
                        File.GetLastWriteTimeUtc(icoPath) < File.GetLastWriteTimeUtc(iconPath);

                    if (needsRefresh && IconHelper.TryCreateIcoFromPng(iconPath, icoPath))
                        iconToUse = icoPath;
                    else if (File.Exists(icoPath))
                        iconToUse = icoPath;
                }
            }

            if (!string.IsNullOrEmpty(iconToUse))
                link.SetIconLocation(iconToUse, 0);
            else
                link.SetIconLocation(exePath, 0);

            var file = (IPersistFile)link;
            file.Save(shortcutPath, false);
        }

        [ComImport]
        [Guid("00021401-0000-0000-C000-000000000046")]
        private class ShellLink { }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("000214F9-0000-0000-C000-000000000046")]
        private interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszFile,
                int cch, IntPtr pfd, int fFlags);
            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszName,
                int cch);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszDir,
                int cch);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszArgs,
                int cch);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
            void GetHotkey(out ushort pwHotkey);
            void SetHotkey(ushort wHotkey);
            void GetShowCmd(out int piShowCmd);
            void SetShowCmd(int iShowCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszIconPath,
                int cch, out int piIcon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
            void Resolve(IntPtr hwnd, int fFlags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }
    }
}
