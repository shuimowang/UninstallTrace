using System.IO;
using System.Security.Cryptography;
using DeepUninstaller.Services;

internal static class PortableUpdateVerification
{
    public static void Run()
    {
        var work = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "UninstallTrace-Update-" + Guid.NewGuid().ToString("N")));
        var target = Path.Combine(work.FullName, "My renamed portable app.exe");
        var stage = Path.Combine(work.FullName, "stage.tmp");
        var helper = Path.Combine(work.FullName, "helper.exe");
        byte[] original = [11, 22, 33];
        byte[] next = [40, 50, 60, 70];
        var originalHash = SHA256.HashData(original);
        var nextHash = SHA256.HashData(next);

        try
        {
            File.WriteAllBytes(target, original);
            File.WriteAllBytes(helper, original);
            File.WriteAllBytes(stage, next);

            var restarts = 0;
            void Restart(string exe)
            {
                Require(exe == target, "The updater must launch the same executable path used before the update.");
                Require(File.ReadAllBytes(exe).SequenceEqual(next), "The updater must restart the verified downloaded bytes.");
                restarts++;
            }

            void Apply(byte[] hash, Action<string> restart) => PortableUpdateInstaller.ReplaceAndRestart(target, stage,
                next.Length, hash, helper, original.Length, originalHash, restart);

            Expect<InvalidDataException>(() => Apply(originalHash, Restart));
            Require(File.ReadAllBytes(target).SequenceEqual(original) && restarts == 0, "A bad digest cannot replace or start a binary.");

            byte[] unrelated = [2, 2, 2];
            File.WriteAllBytes(target, unrelated);
            Expect<InvalidDataException>(() => Apply(nextHash, Restart));
            Require(File.ReadAllBytes(target).SequenceEqual(unrelated) && restarts == 0, "The updater cannot overwrite a changed target.");
            File.WriteAllBytes(target, original);

            using (File.Open(target, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Expect<IOException>(() => Apply(nextHash, Restart));
                Require(File.ReadAllBytes(target).SequenceEqual(original) && restarts == 0, "An in-use target stays runnable after failure.");
            }

            Expect<InvalidOperationException>(() => Apply(nextHash, exe =>
            {
                Require(File.ReadAllBytes(exe).SequenceEqual(next), "The replacement cannot run until it is complete.");
                throw new InvalidOperationException("Synthetic restart error.");
            }));
            Require(File.ReadAllBytes(target).SequenceEqual(original), "A failed restart must restore the verified original.");
            Require(!Directory.EnumerateFiles(work.FullName, "*.restore").Any(), "Remove the temporary restore file.");

            File.WriteAllBytes(stage, next);
            Apply(nextHash, Restart);
            Require(!File.Exists(stage) && restarts == 1, "The staged replacement must be consumed and restarted once.");
            Require(File.ReadAllBytes(helper).SequenceEqual(original), "The source used for recovery remains intact until helper exit.");

            var scratch = Directory.CreateDirectory(Path.Combine(work.FullName, "private")).FullName;
            using (var staging = new PortableUpdateStaging(scratch))
            {
                Require(!File.Exists(staging.DownloadPath), "The download path begins unoccupied.");
                File.WriteAllBytes(staging.DownloadPath, next);
            }
            Require(!Directory.Exists(scratch), "Disposing unused staging must delete only the temporary package and directory.");
        }
        finally
        {
            work.Delete(recursive: true);
        }
    }

    private static void Expect<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
