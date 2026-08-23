using Aerie.Api.Services.DeviceMapping;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aerie.Api.Tests.DeviceMapping;

/// <summary>
/// Covers MotionEventDispatcher - the Phase 5 seam between the Home Assistant
/// listener and everything that reacts to motion. No HA, no DB, no clock: the
/// whole point of the type is that it holds only in-memory state.
/// </summary>
public class MotionEventDispatcherTests
{
    private static MotionEventDispatcher Dispatcher() => new(NullLogger<MotionEventDispatcher>.Instance);

    /// <summary>Subscribes and collects, the way a Phase 6 SSE connection will.</summary>
    private static List<MotionStateChange> Record(MotionEventDispatcher dispatcher)
    {
        var seen = new List<MotionStateChange>();
        dispatcher.Changed += seen.Add;
        return seen;
    }

    [Fact]
    public void SetMotionState_Active_RaisesChangeAndMarksDeviceActive()
    {
        var dispatcher = Dispatcher();
        var seen = Record(dispatcher);
        var device = Guid.NewGuid();

        dispatcher.SetMotionState(device, true);

        Assert.Equal([new MotionStateChange(device, true)], seen);
        Assert.Equal([device], dispatcher.ActiveDeviceIds);
    }

    [Fact]
    public void SetMotionState_BackToInactive_RaisesChangeAndClearsDevice()
    {
        var dispatcher = Dispatcher();
        var device = Guid.NewGuid();
        dispatcher.SetMotionState(device, true);
        var seen = Record(dispatcher);

        dispatcher.SetMotionState(device, false);

        Assert.Equal([new MotionStateChange(device, false)], seen);
        Assert.Empty(dispatcher.ActiveDeviceIds);
    }

    /// <summary>The listener can see repeats - two devices on one sensor, a reconnect replaying - and a repeat must not re-open a modal the user dismissed.</summary>
    [Fact]
    public void SetMotionState_RepeatedActive_RaisesNothingTheSecondTime()
    {
        var dispatcher = Dispatcher();
        var device = Guid.NewGuid();
        dispatcher.SetMotionState(device, true);
        var seen = Record(dispatcher);

        dispatcher.SetMotionState(device, true);

        Assert.Empty(seen);
        Assert.Equal([device], dispatcher.ActiveDeviceIds);
    }

    /// <summary>A device that was never active going inactive is not news; a fresh install would otherwise announce "no motion" for every camera on the first frame.</summary>
    [Fact]
    public void SetMotionState_InactiveOnUnknownDevice_RaisesNothing()
    {
        var dispatcher = Dispatcher();
        var seen = Record(dispatcher);

        dispatcher.SetMotionState(Guid.NewGuid(), false);

        Assert.Empty(seen);
        Assert.Empty(dispatcher.ActiveDeviceIds);
    }

    [Fact]
    public void SetMotionState_TwoDevices_TracksThemIndependently()
    {
        var dispatcher = Dispatcher();
        var front = Guid.NewGuid();
        var back = Guid.NewGuid();
        var seen = Record(dispatcher);

        dispatcher.SetMotionState(front, true);
        dispatcher.SetMotionState(back, true);
        dispatcher.SetMotionState(front, false);

        Assert.Equal(
            [new MotionStateChange(front, true), new MotionStateChange(back, true), new MotionStateChange(front, false)],
            seen);
        Assert.Equal([back], dispatcher.ActiveDeviceIds);
    }

    /// <summary>Every subscriber is a kiosk; a change has to reach all of them.</summary>
    [Fact]
    public void Changed_MultipleSubscribers_AllReceiveTheChange()
    {
        var dispatcher = Dispatcher();
        var first = Record(dispatcher);
        var second = Record(dispatcher);
        var device = Guid.NewGuid();

        dispatcher.SetMotionState(device, true);

        Assert.Equal([new MotionStateChange(device, true)], first);
        Assert.Equal([new MotionStateChange(device, true)], second);
    }

    /// <summary>A closed SSE connection unsubscribes; it must not keep getting events after that.</summary>
    [Fact]
    public void Changed_AfterUnsubscribe_StopsReceiving()
    {
        var dispatcher = Dispatcher();
        var seen = new List<MotionStateChange>();
        void Handler(MotionStateChange change) => seen.Add(change);

        dispatcher.Changed += Handler;
        dispatcher.SetMotionState(Guid.NewGuid(), true);
        dispatcher.Changed -= Handler;
        dispatcher.SetMotionState(Guid.NewGuid(), true);

        Assert.Single(seen);
    }

    /// <summary>One kiosk whose connection died between the raise and its own cleanup must not cost the others their event.</summary>
    [Fact]
    public void Changed_SubscriberThrows_OtherSubscribersStillReceiveTheChange()
    {
        var dispatcher = Dispatcher();
        dispatcher.Changed += _ => throw new InvalidOperationException("connection is gone");
        var survivor = Record(dispatcher);
        var device = Guid.NewGuid();

        dispatcher.SetMotionState(device, true);

        Assert.Equal([new MotionStateChange(device, true)], survivor);
    }

    /// <summary>A subscriber that connects mid-motion reads this to render what is already happening rather than waiting for the next transition.</summary>
    [Fact]
    public void ActiveDeviceIds_IsASnapshot_NotALiveView()
    {
        var dispatcher = Dispatcher();
        var device = Guid.NewGuid();
        dispatcher.SetMotionState(device, true);

        var snapshot = dispatcher.ActiveDeviceIds;
        dispatcher.SetMotionState(device, false);

        Assert.Equal([device], snapshot);
        Assert.Empty(dispatcher.ActiveDeviceIds);
    }

    /// <summary>Nothing serializes callers today, but the interface is public and the state has to survive concurrent use.</summary>
    [Fact]
    public void SetMotionState_ConcurrentDevices_RaisesExactlyOneChangeEach()
    {
        var dispatcher = Dispatcher();
        var devices = Enumerable.Range(0, 64).Select(_ => Guid.NewGuid()).ToArray();
        var seen = new List<MotionStateChange>();
        dispatcher.Changed += change => seen.Add(change);

        Parallel.ForEach(devices, device =>
        {
            dispatcher.SetMotionState(device, true);
            dispatcher.SetMotionState(device, true);
        });

        Assert.Equal(devices.Length, seen.Count);
        Assert.Equal([.. devices.Order()], [.. dispatcher.ActiveDeviceIds.Order()]);
    }
}
