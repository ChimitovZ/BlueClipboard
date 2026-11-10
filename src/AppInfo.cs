using System;
using System.IO;
using System.Reflection;

[assembly: AssemblyTitle("BlueClipboard")]
[assembly: AssemblyProduct("BlueClipboard")]
[assembly: AssemblyVersion("1.1.0.0")]
[assembly: AssemblyFileVersion("1.1.0.0")]

namespace BlueClipboard
{
    internal static class AppInfo
    {
        public const string VersionText = "1.1.0";
        public const string Repository = "ChimitovZ/BlueClipboard";
        public const string AssetName = "BlueClipboard-Setup-x64.exe";
        public static bool IsInstalled { get { return File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "installed.flag")); } }
    }
}
