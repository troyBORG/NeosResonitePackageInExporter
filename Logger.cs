using BaseX;

namespace NeosResonitePackageInExporter
{
    internal class Logger
    {
        internal static void Log(string message) => UniLog.Log($"[NeosResonitePackageInExporter]  {message}");
        internal static void Log(object obj) => Log(obj.ToString());
     
        internal static void Error(string message) => UniLog.Error($"[NeosResonitePackageInExporter]  {message}");
        internal static void Error(object obj) => Error(obj.ToString());

        internal static void Warning(string message) => UniLog.Warning($"[NeosResonitePackageInExporter]  {message}");
        internal static void Warning(object obj) => Warning(obj.ToString());
    }
}