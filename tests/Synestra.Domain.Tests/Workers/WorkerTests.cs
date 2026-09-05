using Synestra.Domain.Workers;
using Xunit;

namespace Synestra.Domain.Tests.Workers;

public sealed class WorkerTests
{
    private static readonly DateTime Now = new(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Registration_PreservesIdentityAndReplacesDesiredState()
    {
        var id = Guid.CreateVersion7();
        var session = Guid.CreateVersion7();
        var worker = new Worker(new(id, session, "first", 4, ["a", "B"]), Now);
        Assert.Equal(id, worker.Id);
        Assert.Equal(session, worker.SessionId);
        Assert.Equal(Now, worker.RegisteredAtUtc);
        Assert.Equal(Now, worker.SessionStartedAtUtc);
        Assert.Equal(Now, worker.LastSeenAtUtc);

        worker.UpdateRegistration(new(id, session, "second", 2, ["B", "c"]), Now.AddSeconds(10));
        Assert.Equal("second", worker.Name);
        Assert.Equal(2, worker.Capacity);
        Assert.Equal(["B", "c"], Types(worker));
        Assert.Equal(Now, worker.SessionStartedAtUtc);

        var nextSession = Guid.CreateVersion7();
        worker.UpdateRegistration(new(id, nextSession, "third", 1, ["d"]), Now.AddSeconds(20));
        Assert.Equal(nextSession, worker.SessionId);
        Assert.Equal(Now.AddSeconds(20), worker.SessionStartedAtUtc);
        Assert.Equal(Now, worker.RegisteredAtUtc);
        Assert.Equal(["d"], Types(worker));
    }

    [Fact]
    public void Heartbeat_IsMonotonicAndRejectsStaleSessionWithoutMutation()
    {
        var worker = NewWorker();
        Assert.True(worker.RecordHeartbeat(worker.SessionId!.Value, Now.AddSeconds(10)));
        Assert.True(worker.RecordHeartbeat(worker.SessionId.Value, Now.AddSeconds(-10)));
        Assert.False(worker.RecordHeartbeat(Guid.CreateVersion7(), Now.AddDays(1)));
        Assert.Equal(Now.AddSeconds(10), worker.LastSeenAtUtc);
        worker.UpdateRegistration(new(worker.Id, Guid.CreateVersion7(), "updated", 1, ["new"]), Now);
        Assert.Equal(Now.AddSeconds(10), worker.LastSeenAtUtc);
    }

    [Fact]
    public void InvalidUpdate_DoesNotPartiallyMutateWorker()
    {
        var worker = NewWorker();
        Assert.Throws<ArgumentException>(() => worker.UpdateRegistration(
            new(Guid.CreateVersion7(), Guid.CreateVersion7(), "other", 2, ["other"]), Now));
        Assert.Throws<ArgumentException>(() => worker.UpdateRegistration(
            new(worker.Id, Guid.CreateVersion7(), "other", 2, ["other"]), DateTime.SpecifyKind(Now, DateTimeKind.Local)));
        Assert.Equal("worker", worker.Name);
        Assert.Equal(["type"], Types(worker));
        Assert.Throws<ArgumentException>(() => worker.RecordHeartbeat(Guid.Empty, Now));
        Assert.Throws<ArgumentException>(() => worker.RecordHeartbeat(worker.SessionId!.Value, DateTime.SpecifyKind(Now, DateTimeKind.Unspecified)));
    }

    public static IEnumerable<object[]> InvalidRegistrations()
    {
        var id = Guid.CreateVersion7();
        var session = Guid.CreateVersion7();
        yield return [Guid.Empty, session, "worker", 1, new[] { "a" }];
        yield return [Guid.NewGuid(), session, "worker", 1, new[] { "a" }];
        yield return [id, Guid.Empty, "worker", 1, new[] { "a" }];
        yield return [id, Guid.NewGuid(), "worker", 1, new[] { "a" }];
        yield return [Guid.Parse("019ec569-5a00-7000-0000-000000000001"), session, "worker", 1, new[] { "a" }];
        foreach (var name in new[] { null, "", " \t", new string('a', 201), "a\0b" })
            yield return [id, session, name!, 1, new[] { "a" }];
        yield return [id, session, "worker", 0, new[] { "a" }];
        yield return [id, session, "worker", -1, new[] { "a" }];
        foreach (var types in new string[][] { null!, [], ["a", "a"], [null!], [""], ["\u2003"], [new string('a', 101)], ["a\0b"], Enumerable.Range(0, 101).Select(x => x.ToString()).ToArray() })
            yield return [id, session, "worker", 1, types];
    }

    [Theory]
    [MemberData(nameof(InvalidRegistrations))]
    public void Registration_EnforcesInvariants(Guid id, Guid session, string name, int capacity, string[] types) =>
        Assert.ThrowsAny<ArgumentException>(() => new WorkerRegistration(id, session, name, capacity, types));

    [Fact]
    public void Registration_AcceptsLimitsAndOrdinalTypesAndCopiesCallerInput()
    {
        var types = new[] { "a", "A", new string('x', 100) };
        var registration = new WorkerRegistration(Guid.CreateVersion7(), Guid.CreateVersion7(), new string('n', 200), int.MaxValue, types);
        types[0] = "changed";
        Assert.Contains("a", registration.SupportedTypes);
        Assert.Equal("A", registration.SupportedTypes[0]);
        Assert.Equal(100, new WorkerRegistration(Guid.CreateVersion7(), Guid.CreateVersion7(), "worker", 1,
            Enumerable.Range(0, 100).Select(x => x.ToString()).ToArray()).SupportedTypes.Count);
    }

    [Fact]
    public void Entities_HaveNoPublicMutationPaths()
    {
        foreach (var type in new[] { typeof(Worker), typeof(WorkerSupportedType), typeof(WorkerRegistration) })
        {
            Assert.All(type.GetProperties(), property => Assert.False(property.SetMethod?.IsPublic ?? false));
            Assert.Empty(type.GetFields());
        }

        var worker = NewWorker();
        Assert.Throws<NotSupportedException>(() => ((ICollection<WorkerSupportedType>)worker.SupportedTypes).Clear());
        Assert.Empty(typeof(WorkerSupportedType).GetConstructors());
    }

    [Theory]
    [InlineData("019ec569-5a00-7000-8000-000000000001")]
    [InlineData("019ec569-5a00-7000-9000-000000000001")]
    [InlineData("019ec569-5a00-7000-a000-000000000001")]
    [InlineData("019ec569-5a00-7000-b000-000000000001")]
    public void Identity_AcceptsAllRfcVariantSuffixBits(string value) =>
        WorkerRegistration.ValidateIdentity(Guid.Parse(value), nameof(value));

    private static Worker NewWorker() => new(new(Guid.CreateVersion7(), Guid.CreateVersion7(), "worker", 1, ["type"]), Now);
    private static string[] Types(Worker worker) => worker.SupportedTypes.Select(x => x.Type).Order(StringComparer.Ordinal).ToArray();
}
