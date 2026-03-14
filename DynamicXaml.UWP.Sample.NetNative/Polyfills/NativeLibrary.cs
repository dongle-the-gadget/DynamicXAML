using System.Runtime.InteropServices;

namespace DynamicXaml.UWP.Sample.NetNative.Polyfills
{
    internal static class NativeLibrary
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern nint LoadLibraryW(string lpFileName);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern nint GetProcAddress(nint hModule, string procName);

        public static bool TryLoad(string libraryPath, out nint handle)
        {
            handle = LoadLibraryW(libraryPath);
            return handle != 0;
        }

        public static bool TryGetExport(nint handle, string name, out nint address)
        {
            address = GetProcAddress(handle, name);
            return address != 0;
        }
    }
}
