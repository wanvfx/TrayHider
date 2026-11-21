using System;

namespace TrayHider
{
    /// <summary>Windows 版本判定。Win11 与 Win10 的通知区域实现完全不同。</summary>
    internal static class OSInfo
    {
        public static readonly bool IsWindows11 = Detect();

        private static bool Detect()
        {
            var v = Environment.OSVersion.Version;
            // Windows 11 与 Windows 10 的主版本号都是 10，用 build 号区分（Win11 >= 22000）。
            return v.Major == 10 && v.Build >= 22000;
        }
    }
}
