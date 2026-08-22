namespace Aerie.Api.Modules.Gather;

// The API-facing shapes for Gather, kept separate from the entities so the two
// clients - the family shell and the kiosk overlay - have one stable contract.
// NameNormalized never appears here: it is the server's business.

/// <summary>
/// A list without its items - what a lists screen draws. Both counts travel
/// because "4 to get" and "and 11 already in the cart" are different facts and
/// a card shows the first without a second request for the second.
/// </summary>
public record ListSummaryDto(
    Guid Id, string Name, string? Icon, string? Color,
    int OpenCount, int CheckedCount, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>
/// A list and everything on it, in one response - what opening a list lands on,
/// and what the poll loop re-fetches. Items arrive in display order; see
/// <see cref="GatherService.InDisplayOrder"/>.
/// </summary>
public record ListDto(ListSummaryDto List, IReadOnlyList<ItemDto> Items);

public record ItemDto(
    Guid Id, Guid ListId, string Name, string? Quantity, string? Note,
    bool IsChecked, DateTimeOffset? CheckedAt, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public record ListWriteRequest(string Name, string? Icon, string? Color);

/// <summary>
/// Adding an item, which is an upsert on the normalized name - see
/// <see cref="IGatherService.AddItemAsync"/>. Separate from
/// <see cref="ItemWriteRequest"/> because the two mean different things: this
/// one merges, that one overwrites.
/// </summary>
public record ItemAddRequest(string Name, string? Quantity, string? Note);

public record ItemWriteRequest(string Name, string? Quantity, string? Note);

/// <summary>How many items the sweep removed, so the client can say so.</summary>
public record ClearCheckedResult(int Deleted);
