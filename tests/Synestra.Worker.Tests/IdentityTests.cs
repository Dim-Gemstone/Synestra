using Xunit;

namespace Synestra.Worker.Tests;

public sealed class IdentityTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "synestra-worker-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void IdentityIsDurableAndExclusiveUntilOwnerExits()
    {
        Guid id;
        using (var first = WorkerIdentity.Open(_directory))
        {
            id = first.WorkerId;
            Assert.Equal(7, id.Version);
            Assert.Equal(0b1000, id.Variant & 0b1100);
            Assert.Equal(id.ToString("D"), File.ReadAllText(Path.Combine(_directory, "worker-id")));
            Assert.Throws<IOException>(() => WorkerIdentity.Open(_directory));
        }
        using var restarted = WorkerIdentity.Open(_directory);
        Assert.Equal(id, restarted.WorkerId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("partial")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("019ec569-5a00-4000-8000-000000000001")]
    [InlineData("019ec569-5a00-7000-0000-000000000001")]
    public void CorruptIdentityFailsWithoutReplacementAndReleasesLock(string contents)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "worker-id");
        File.WriteAllText(path, contents);
        Assert.Throws<InvalidDataException>(() => WorkerIdentity.Open(_directory));
        Assert.Throws<InvalidDataException>(() => WorkerIdentity.Open(_directory));
        Assert.Equal(contents, File.ReadAllText(path));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
