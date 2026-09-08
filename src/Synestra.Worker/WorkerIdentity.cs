using System.Text;

namespace Synestra.Worker;

internal sealed class WorkerIdentity(FileStream ownership, Guid workerId) : IDisposable
{
    public Guid WorkerId { get; } = workerId;

    public static WorkerIdentity Open(string stateDirectory)
    {
        Directory.CreateDirectory(stateDirectory);
        // Keep the lock file: deleting it would allow a second owner to lock another inode.
        var ownership = new FileStream(Path.Combine(stateDirectory, "worker.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        try
        {
            var path = Path.Combine(stateDirectory, "worker-id");
            Guid id;
            if (File.Exists(path))
            {
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (file.Length != 36) throw new InvalidDataException("Invalid stored worker identity.");
                Span<byte> bytes = stackalloc byte[36];
                file.ReadExactly(bytes);
                if (!Guid.TryParseExact(Encoding.ASCII.GetString(bytes), "D", out id)
                    || id.Version != 7 || (id.Variant & 0b1100) != 0b1000)
                    throw new InvalidDataException("Invalid stored worker identity.");
            }
            else
            {
                id = Guid.CreateVersion7();
                using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                file.Write(Encoding.ASCII.GetBytes(id.ToString("D")));
                // Registration must never precede persisting identity; partial writes fail closed next launch.
                file.Flush(flushToDisk: true);
            }
            return new WorkerIdentity(ownership, id);
        }
        catch
        {
            ownership.Dispose();
            throw;
        }
    }

    public void Dispose() => ownership.Dispose();
}
