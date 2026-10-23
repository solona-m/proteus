using System;
using System.IO;
using Proteus.Services;

namespace Proteus.Refit;

/// <summary>
/// The folders a run extracts the game's bodies into (<see cref="VanillaBodyCatalog.Dir"/>) and bends bodies into
/// (<see cref="BakedBodyCatalog.Dir"/>), made this run's own: their defaults are shared by every Proteus on the machine,
/// and a game loading the plugin, or a second batch process, clears them under a run. Only <c>Main</c> sets them — a
/// test process has set its own once for every test (<c>TestAssemblySetup</c>), and a command run inside it must leave
/// them alone.
/// </summary>
internal static class TempFolders
{
    /// <summary>Where a run's folders go: under the temp folder, named for the process and the run.</summary>
    internal static (string Root, string Vanilla, string Baked) Paths(int pid, Guid run)
    {
        string root = Path.Combine(Path.GetTempPath(), $"proteus-refit-{pid}-{run:N}");
        return (root, Path.Combine(root, "vanilla"), Path.Combine(root, "baked"));
    }

    /// <summary>Point both folders at this run's own until disposed; then put them back and delete the run's.</summary>
    internal static IDisposable Isolate()
    {
        var (root, vanilla, baked) = Paths(Environment.ProcessId, Guid.NewGuid());
        var before = (VanillaBodyCatalog.Dir, BakedBodyCatalog.Dir);
        VanillaBodyCatalog.Dir = vanilla;
        BakedBodyCatalog.Dir = baked;
        return new Restore(() =>
        {
            (VanillaBodyCatalog.Dir, BakedBodyCatalog.Dir) = before;
            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Left in the temp folder: only this run ever used it.
            }
        });
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        private Action? undo = undo;

        public void Dispose()
        {
            undo?.Invoke();
            undo = null;
        }
    }
}
