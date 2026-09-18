namespace Gauge.Ingest.Services;

public sealed record ReadingDto(string MeterId, DateTime Timestamp, decimal Kwh);

public sealed record IngestBatchRequest(
    string CollectorId,
    IReadOnlyList<ReadingDto> Readings,
    Guid BatchId = default);

public sealed record IngestResult(int Accepted, int Rejected, IReadOnlyList<string> Errors);
