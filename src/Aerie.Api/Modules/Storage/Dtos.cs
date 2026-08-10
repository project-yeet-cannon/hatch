namespace Aerie.Api.Modules.Storage;

// The API-facing shapes for Storage Helper, kept separate from the entities so
// the family shell has a stable contract. Codes travel in both forms: Code is
// what's stored and what URLs use, DisplayCode is what a label prints.

public record LocationDto(Guid Id, string Name, string? Description, int CrateCount, DateTimeOffset CreatedAt);

public record LocationWriteRequest(string Name, string? Description);

/// <summary>A crate without its contents - the shape a list screen needs.</summary>
public record CrateDto(
    Guid Id, string Code, string DisplayCode, string? Label,
    Guid? LocationId, string? LocationName, string? Notes,
    int ItemCount, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>
/// A crate and everything in it, in one response - this is what a scanned QR
/// lands on, and it should never take two round trips to draw.
/// </summary>
public record CrateDetailDto(CrateDto Crate, IReadOnlyList<ItemDto> Items);

public record CrateWriteRequest(string? Label, Guid? LocationId, string? Notes);

/// <summary>How many blank crates to mint for a label sheet - see StorageController.CreateCrateBatch.</summary>
public record CrateBatchRequest(int Count);

public record ItemDto(Guid Id, Guid CrateId, string Name, int Quantity, string? Notes, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>
/// One row of the flat "where is the drill" index: the item plus the full
/// answer to where it is, so the list needs no follow-up lookups.
/// </summary>
public record ItemIndexRow(
    Guid Id, string Name, int Quantity, string? Notes,
    Guid CrateId, string CrateCode, string CrateDisplayCode, string? CrateLabel,
    Guid? LocationId, string? LocationName);

public record ItemWriteRequest(Guid CrateId, string Name, int Quantity, string? Notes);
