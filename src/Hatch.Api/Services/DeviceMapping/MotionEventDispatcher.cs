namespace Hatch.Api.Services.DeviceMapping;

/// <summary>One device crossing the motion/no-motion line. IsActive is the state it landed in, not a toggle - a repeated value is safe to apply twice.</summary>
public readonly record struct MotionStateChange(Guid DeviceId, bool IsActive);

/// <summary>
/// The seam between "motion happened" and "something reacts to it"
/// (docs/camera-devices-architecture.md). v1 has exactly one reaction - the
/// kiosk camera modal, driven off MotionEventsController's SSE stream - but
/// everything that knows about Home Assistant stops here, so a real
/// trigger/action registry can take this interface's place later without
/// touching the listener.
/// </summary>
public interface IMotionEventDispatcher
{
    /// <summary>
    /// Raised once per actual change, never for a repeat of the state a device
    /// is already in. Handlers run on the caller's thread - the WebSocket
    /// listener's receive loop - so they must not block: an SSE
    /// subscriber hands the change to its own connection and returns.
    /// </summary>
    event Action<MotionStateChange>? Changed;

    /// <summary>The devices with motion active right now. A subscriber reads this after subscribing to Changed, so it can render what is already happening instead of waiting for the next transition.</summary>
    IReadOnlyList<Guid> ActiveDeviceIds { get; }

    /// <summary>Records where a device now stands and raises <see cref="Changed"/> if that differs from where it stood before.</summary>
    void SetMotionState(Guid deviceId, bool isActive);
}

/// <inheritdoc />
public class MotionEventDispatcher(ILogger<MotionEventDispatcher> logger) : IMotionEventDispatcher
{
    /// <summary>
    /// Only the active devices are stored - membership is the state - so
    /// setting a device to the value it already holds is detected by the set
    /// operation itself rather than by a separate read.
    ///
    /// A lock rather than a ConcurrentDictionary because the mutation and the
    /// raise have to be one step: with them separate, two transitions on one
    /// device can be applied in order and then announced out of order, which
    /// leaves a kiosk showing a modal for motion that has already ended. Today
    /// the only caller is a single receive loop, so the lock is never
    /// contended; it is here so that stays true if a second caller appears.
    /// </summary>
    private readonly HashSet<Guid> active = [];
    private readonly Lock gate = new();

    public event Action<MotionStateChange>? Changed;

    public IReadOnlyList<Guid> ActiveDeviceIds
    {
        get
        {
            lock (gate) return [.. active];
        }
    }

    public void SetMotionState(Guid deviceId, bool isActive)
    {
        lock (gate)
        {
            var changed = isActive ? active.Add(deviceId) : active.Remove(deviceId);
            if (!changed) return;

            Raise(new MotionStateChange(deviceId, isActive));
        }
    }

    /// <summary>
    /// Each handler is called inside its own try so that one failing subscriber
    /// can't cost the others their event. Without this, a single kiosk whose
    /// connection died between the raise and its own cleanup would blind every
    /// other kiosk on this replica for that change.
    /// </summary>
    private void Raise(MotionStateChange change)
    {
        if (Changed is not { } handlers) return;

        foreach (var handler in handlers.GetInvocationList().Cast<Action<MotionStateChange>>())
        {
            try
            {
                handler(change);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "A motion subscriber threw handling {Change}; the remaining subscribers still got it", change);
            }
        }
    }
}
