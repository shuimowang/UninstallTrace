using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

internal static class PathAttributeProbe
{
    [DllImport("kernel32.dll", EntryPoint = "GetFileAttributesW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint Attributes(string path);

    public static int Run(string manifest)
    {
        var paths = File.ReadAllLines(manifest);
        for (int round = 0; round < 20; round++)
        {
            ulong sum = 0;
            var start = Stopwatch.GetTimestamp();
            for (int repeat = 0; repeat < 8; repeat++)
                foreach (var path in paths)
                    sum += Attributes(path);
            var ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            if (round >= 5)
                Console.WriteLine(FormattableString.Invariant($"{ms:F6},{sum}"));
        }
        return 0;
    }
}
