using Aerie.Api.Ef;
using Aerie.Api.Models.Panels;
using Aerie.Api.Services.DeviceMapping;
using Aerie.Api.Services.Routines;
using Microsoft.EntityFrameworkCore;

namespace Aerie.Api.Services.Panels;

public interface IPanelService
{
    /// <summary>The kiosk's tile row - included panels in display order.</summary>
    Task<IReadOnlyList<PanelSummary>> GetPanelsAsync(CancellationToken ct);

    /// <summary>Live state for one open panel, or null when no panel has that id.</summary>
    Task<PanelStateDto?> GetStateAsync(Guid panelId, CancellationToken ct);
}

/// <summary>
/// The kiosk's read path for Panels. Two calls with deliberately different
/// costs: GetPanelsAsync rides the 60s dashboard snapshot and touches no
/// channel state at all, while GetStateAsync is what an open overlay polls
/// every ~5s and is scoped to that one panel's channels.
///
/// GetPanelsAsync mirrors RoutineService.GetRoutinesAsync's Included/SortOrder
/// filtering, which mirrors ZoneService's.
/// </summary>
public class PanelService(IDbContextFactory<AerieContext> dbFactory) : IPanelService
{
    public async Task<IReadOnlyList<PanelSummary>> GetPanelsAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Panels.AsNoTracking()
            .Where(p => p.Included)
            .OrderBy(p => p.SortOrder)
            .ThenBy(p => p.Name)
            .Select(p => new PanelSummary(p.Id, p.Name, p.Icon, p.Color))
            .ToListAsync(ct);
    }

    public async Task<PanelStateDto?> GetStateAsync(Guid panelId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var panel = await db.Panels.AsNoTracking()
            .Include(p => p.Items).ThenInclude(i => i.Bindings)
            .Include(p => p.Items).ThenInclude(i => i.Routine!).ThenInclude(r => r.Actions)
            .FirstOrDefaultAsync(p => p.Id == panelId, ct);
        if (panel is null) return null;

        var items = panel.Items.OrderBy(i => i.SortOrder).ToList();

        // Every channel this panel needs, in one GetLatestAsync call: the
        // controls' bound channels, plus the power channels behind any toggle
        // routine on the panel. Scoped to this panel and no wider - that
        // scoping is the whole reason panel state is its own endpoint rather
        // than a field on the dashboard snapshot. Non-toggle routines
        // contribute nothing, exactly as in RoutineService, because their
        // IsActive is null regardless of what their channels read.
        var channelIds = items
            .SelectMany(i => i.Bindings.Select(b => b.ChannelId))
            .Concat(items
                .Where(i => i.Kind == PanelItemKind.Routine && i.Routine is { IsToggle: true })
                .SelectMany(i => RoutineToggleState.PowerChannelIds(i.Routine!)))
            .Distinct()
            .ToList();
        var latest = await ChannelLatestValues.GetLatestAsync(db, channelIds, ct);

        return new PanelStateDto(panel.Id, panel.Name, items.Select(i => ToState(i, latest)).ToList());
    }

    private static PanelItemStateDto ToState(EfPanelItem item, IReadOnlyDictionary<Guid, ChannelLatestValue> latest)
    {
        if (item.Kind == PanelItemKind.Routine)
        {
            // A routine item shows the routine's own name, icon and colour -
            // the item's Label/Icon/Color columns belong to controls. The
            // cascade on RoutineId means Routine is only null for a row written
            // outside the API, and a tile with no label says so more usefully
            // than a missing tile would.
            var routine = item.Routine;
            return new PanelItemStateDto(
                Id: item.Id,
                Kind: PanelItemKind.Routine,
                Label: routine?.Name,
                Icon: routine?.Icon,
                Color: routine?.Color,
                ControlKind: null,
                IsOn: null,
                SetpointF: null,
                AmbientF: null,
                Mode: null,
                MinF: null,
                MaxF: null,
                StepF: null,
                IsToggle: routine?.IsToggle,
                IsActive: routine is null ? null : RoutineToggleState.IsActive(routine, latest));
        }

        var isThermostat = item.ControlKind == Ef.ControlKind.Thermostat;
        var power = Read(item, ControlRole.Power, latest);
        var mode = Read(item, ControlRole.Mode, latest);

        return new PanelItemStateDto(
            Id: item.Id,
            Kind: PanelItemKind.Control,
            Label: item.Label,
            Icon: item.Icon,
            Color: item.Color,
            ControlKind: item.ControlKind,
            // A Power binding is the direct answer; a thermostat without one
            // reads its mode instead, where anything but "off" counts as on
            // (which mode means "on" is OnMode's business, on the write path).
            // A channel that has never reported leaves this null rather than
            // false - "we don't know yet" is not "it's off".
            IsOn: power?.State is { } powerState ? powerState == "on"
                : mode?.State is { } modeState ? modeState != "off"
                : null,
            SetpointF: Read(item, ControlRole.Setpoint, latest)?.Value,
            AmbientF: Read(item, ControlRole.Ambient, latest)?.Value,
            Mode: mode?.State,
            // Resolved here rather than on the client so the kiosk and the
            // write path clamp against the same numbers.
            MinF: isThermostat ? item.MinF ?? PanelDefaults.MinF : null,
            MaxF: isThermostat ? item.MaxF ?? PanelDefaults.MaxF : null,
            StepF: isThermostat ? item.StepF ?? PanelDefaults.StepF : null,
            IsToggle: null,
            IsActive: null);
    }

    /// <summary>The latest sample behind one role, or null when the role isn't bound at all - which the caller must tell apart from a bound channel that has never reported.</summary>
    private static ChannelLatestValue? Read(EfPanelItem item, ControlRole role, IReadOnlyDictionary<Guid, ChannelLatestValue> latest)
    {
        var binding = item.Bindings.FirstOrDefault(b => b.Role == role);
        if (binding is null) return null;
        return latest.TryGetValue(binding.ChannelId, out var value) ? value : new ChannelLatestValue(null, null, null);
    }
}
