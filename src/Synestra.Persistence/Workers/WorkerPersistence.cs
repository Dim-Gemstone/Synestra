using System.Buffers.Binary;
using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Synestra.Application.Workers;
using Synestra.Domain.Workers;

namespace Synestra.Persistence.Workers;

internal sealed class WorkerPersistence(SynestraDbContext dbContext) : IWorkerPersistence
{
    public async Task<IWorkerTransaction> BeginTransactionAsync(CancellationToken cancellationToken)
    {
        var transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        return new WorkerTransaction(dbContext, transaction);
    }

    private sealed class WorkerTransaction(SynestraDbContext dbContext, IDbContextTransaction transaction) : IWorkerTransaction
    {
        private Worker? _worker;
        private readonly List<WorkerSupportedType> _originalTypes = [];
        private readonly List<WorkerSessionRecord> _addedSessions = [];

        public async Task<Worker?> LockRegistrationAsync(Guid workerId, CancellationToken cancellationToken)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes("synestra:register-worker:" + workerId.ToString("D")));
            var lockId = BinaryPrimitives.ReadInt64BigEndian(hash);
            await dbContext.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({lockId})", cancellationToken);

            // The statement after the advisory lock sees a registration committed while waiting.
            _worker = await dbContext.Workers
                .FromSqlInterpolated($"SELECT * FROM workers WHERE id = {workerId} FOR UPDATE")
                .Include(worker => worker.SupportedTypes).AsSplitQuery().AsNoTracking()
                .SingleOrDefaultAsync(cancellationToken);
            TrackWorker();
            return _worker;
        }

        public async Task<Worker?> FindForHeartbeatAsync(Guid workerId, CancellationToken cancellationToken)
        {
            _worker = await dbContext.Workers
                .FromSqlInterpolated($"SELECT * FROM workers WHERE id = {workerId} FOR UPDATE")
                .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
            TrackWorker();
            return _worker;
        }

        private void TrackWorker()
        {
            if (_worker is not null)
            {
                _originalTypes.AddRange(_worker.SupportedTypes);
                dbContext.Attach(_worker);
            }
        }

        public Task<bool> HasRegisteredSessionAsync(Guid workerId, Guid sessionId, CancellationToken cancellationToken) =>
            dbContext.Set<WorkerSessionRecord>().AsNoTracking()
                .AnyAsync(session => session.WorkerId == workerId && session.SessionId == sessionId, cancellationToken);

        public void AddSession(Guid workerId, Guid sessionId)
        {
            var session = new WorkerSessionRecord { WorkerId = workerId, SessionId = sessionId };
            dbContext.Add(session);
            _addedSessions.Add(session);
        }

        public void Add(Worker worker)
        {
            _worker = worker;
            dbContext.Workers.Add(worker);
        }

        public async Task CommitAsync(CancellationToken cancellationToken)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await transaction.DisposeAsync();
            }
            finally
            {
                foreach (var session in _addedSessions)
                {
                    dbContext.Entry(session).State = EntityState.Detached;
                }

                // Later operations in this scope must read committed state, including after rollback.
                if (_worker is not null)
                {
                    foreach (var type in _originalTypes.Concat(_worker.SupportedTypes).Distinct().ToArray())
                    {
                        dbContext.Entry(type).State = EntityState.Detached;
                    }

                    dbContext.Entry(_worker).State = EntityState.Detached;
                }
            }
        }
    }
}
