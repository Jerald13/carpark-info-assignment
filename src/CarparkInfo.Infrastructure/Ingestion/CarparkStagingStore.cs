using CarparkInfo.Application.Abstractions;
using CarparkInfo.Application.Ingestion;
using CarparkInfo.Domain.Carparks;
using CarparkInfo.Domain.Ingestion;
using CarparkInfo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CarparkInfo.Infrastructure.Ingestion;

/// <summary>EF Core implementation of staging and the atomic merge.</summary>
public sealed class CarparkStagingStore : ICarparkStagingStore
{
    private readonly CarparkDbContext _db;
    private readonly AtomicMergeService _merge;

    /// <summary>Creates the store.</summary>
    /// <param name="db">The database context.</param>
    /// <param name="merge">The atomic merge service.</param>
    public CarparkStagingStore(CarparkDbContext db, AtomicMergeService merge)
    {
        _db = db;
        _merge = merge;
    }

    /// <inheritdoc />
    public async Task StageBatchAsync(int jobRunId, IReadOnlyList<ValidatedCarparkRecord> records,
        ILookupResolver lookups, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(lookups);

        foreach (var record in records)
        {
            _db.CarparkStaging.Add(new CarparkStagingRow(
                jobRunId: jobRunId,
                carParkNo: record.CarParkNo,
                address: record.Address,
                svy21X: record.Location.Svy21X,
                svy21Y: record.Location.Svy21Y,
                latitude: record.Location.Latitude,
                longitude: record.Location.Longitude,
                carParkTypeId: lookups.CarParkTypeId(record.CarParkTypeCode),
                parkingSystemTypeId: lookups.ParkingSystemTypeId(record.ParkingSystemCode),
                shortTermParkingTypeId: lookups.ShortTermParkingTypeId(record.ShortTermParkingCode),
                freeParkingTypeId: lookups.FreeParkingTypeId(record.FreeParkingCode),
                hasNightParking: record.HasNightParking,
                deckCount: record.DeckCount,
                gantryHeightMetres: record.HeightRestriction.MaximumVehicleHeightMetres,
                hasHeightRestriction: record.HeightRestriction.IsRestricted,
                gantryHeightRaw: record.HeightRestriction.RawSourceValue,
                hasBasement: record.HasBasement,
                sourceRowHash: record.SourceRowHash,
                lineNumber: record.LineNumber));
        }

        // Each batch is its own transaction against a table no reader queries, so the API stays
        // fully available while an arbitrarily large file is absorbed.
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Release the tracked entities: keeping 2,000,000 of them would make DetectChanges
        // quadratic and defeat the point of batching.
        _db.ChangeTracker.Clear();
    }

    /// <inheritdoc />
    public async Task<IngestionCounts> MergeAsync(int jobRunId, IngestionMode mode,
        DateTimeOffset observedAt, double maximumDeactivationRatio,
        CancellationToken cancellationToken)
    {
        var counts = await _merge
            .MergeAsync(jobRunId, mode, observedAt, maximumDeactivationRatio, cancellationToken)
            .ConfigureAwait(false);

        return new IngestionCounts(
            Read: 0,
            Inserted: counts.Inserted,
            Updated: counts.Updated,
            Unchanged: counts.Unchanged,
            Deactivated: counts.Deactivated,
            Rejected: 0);
    }

    /// <inheritdoc />
    public async Task TruncateAsync(int jobRunId, CancellationToken cancellationToken) =>
        await _merge.TruncateStagingAsync(jobRunId, cancellationToken).ConfigureAwait(false);
}

/// <summary>
/// EF Core implementation of lookup resolution, with auto-registration of unseen values.
/// </summary>
/// <remarks>
/// Codes are cached for the run so resolution costs a dictionary hit per row rather than a query.
/// A value the feed has not used before is registered rather than rejected: HDB introducing an
/// eighth carpark type must not stop the nightly job.
/// </remarks>
public sealed class LookupResolver : ILookupResolver
{
    private readonly CarparkDbContext _db;

    private readonly Dictionary<string, int> _carParkTypes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _parkingSystems = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _shortTermParking = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _freeParking = new(StringComparer.Ordinal);

    private readonly List<CarParkType> _newCarParkTypes = [];
    private readonly List<ParkingSystemType> _newParkingSystems = [];
    private readonly List<ShortTermParkingType> _newShortTermParking = [];
    private readonly List<FreeParkingType> _newFreeParking = [];

    /// <summary>Creates the resolver.</summary>
    /// <param name="db">The database context.</param>
    public LookupResolver(CarparkDbContext db) => _db = db;

    /// <inheritdoc />
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        _carParkTypes.Clear();
        _parkingSystems.Clear();
        _shortTermParking.Clear();
        _freeParking.Clear();

        foreach (var type in await _db.CarParkTypes.AsNoTracking()
            .ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            _carParkTypes[type.Code] = type.Id;
        }

        foreach (var system in await _db.ParkingSystemTypes.AsNoTracking()
            .ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            _parkingSystems[system.Code] = system.Id;
        }

        foreach (var policy in await _db.ShortTermParkingTypes.AsNoTracking()
            .ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            _shortTermParking[policy.Code] = policy.Id;
        }

        foreach (var policy in await _db.FreeParkingTypes.AsNoTracking()
            .ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            _freeParking[policy.Code] = policy.Id;
        }
    }

    /// <inheritdoc />
    public int CarParkTypeId(string code)
    {
        if (_carParkTypes.TryGetValue(code, out var id))
        {
            return id;
        }

        var entity = new CarParkType(code, code);
        _newCarParkTypes.Add(entity);
        _db.CarParkTypes.Add(entity);
        _db.SaveChanges();
        _carParkTypes[code] = entity.Id;

        return entity.Id;
    }

    /// <inheritdoc />
    public int ParkingSystemTypeId(string code)
    {
        if (_parkingSystems.TryGetValue(code, out var id))
        {
            return id;
        }

        var entity = new ParkingSystemType(code, code);
        _newParkingSystems.Add(entity);
        _db.ParkingSystemTypes.Add(entity);
        _db.SaveChanges();
        _parkingSystems[code] = entity.Id;

        return entity.Id;
    }

    /// <inheritdoc />
    public int ShortTermParkingTypeId(string code)
    {
        if (_shortTermParking.TryGetValue(code, out var id))
        {
            return id;
        }

        var entity = new ShortTermParkingType(code, code, null, null, false, true);
        _newShortTermParking.Add(entity);
        _db.ShortTermParkingTypes.Add(entity);
        _db.SaveChanges();
        _shortTermParking[code] = entity.Id;

        return entity.Id;
    }

    /// <inheritdoc />
    public int FreeParkingTypeId(string code)
    {
        if (_freeParking.TryGetValue(code, out var id))
        {
            return id;
        }

        // An unknown policy is assumed to OFFER free parking unless it is literally NONE.
        // Erring towards "offered" shows the carpark in results; erring the other way hides it.
        var entity = new FreeParkingType(code, code, null, null, false,
            !string.Equals(code, "NONE", StringComparison.Ordinal));

        _newFreeParking.Add(entity);
        _db.FreeParkingTypes.Add(entity);
        _db.SaveChanges();
        _freeParking[code] = entity.Id;

        return entity.Id;
    }

    /// <inheritdoc />
    public Task SaveNewlyRegisteredAsync(CancellationToken cancellationToken)
    {
        // Values are persisted as they are discovered, because staging rows need their ids
        // immediately. This method exists so the port stays honest about the lifecycle.
        _newCarParkTypes.Clear();
        _newParkingSystems.Clear();
        _newShortTermParking.Clear();
        _newFreeParking.Clear();

        return Task.CompletedTask;
    }
}

/// <summary>Supplies the current time and the host's identity.</summary>
public sealed class IngestionContext : IIngestionContext
{
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates the context.</summary>
    /// <param name="timeProvider">
    /// The clock. Injected rather than read from <c>DateTimeOffset.UtcNow</c> so lease expiry,
    /// heartbeats and timestamps are all deterministically testable.
    /// </param>
    public IngestionContext(TimeProvider timeProvider) => _timeProvider = timeProvider;

    /// <inheritdoc />
    public DateTimeOffset UtcNow => _timeProvider.GetUtcNow();

    /// <inheritdoc />
    public string HostName => Environment.MachineName;
}
