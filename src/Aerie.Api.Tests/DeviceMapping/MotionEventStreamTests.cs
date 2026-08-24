using System.Text.Json;
using Aerie.Api.Services.DeviceMapping;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aerie.Api.Tests.DeviceMapping;

/// <summary>
/// Covers MotionEventStream - what one kiosk's SSE connection sees
/// (docs/camera-devices-architecture.md). No HTTP here: the controller around this
/// only turns each item into an SSE frame, so everything worth asserting -
/// the snapshot, delivery, the heartbeat, and unsubscribing when the kiosk
/// goes - is reachable from the sequence itself.
/// </summary>
public class MotionEventStreamTests
{
    private static readonly TimeSpan Heartbeat = TimeSpan.FromMilliseconds(25);

    /// <summary>Long enough that a broken await fails as a failed assertion rather than a hung test run.</summary>
    private static CancellationTokenSource Deadline() => new(TimeSpan.FromSeconds(5));

    private static MotionEventDispatcher Dispatcher() => new(NullLogger<MotionEventDispatcher>.Instance);

    /// <summary>
    /// Every test on an idle dispatcher starts by pulling the opening
    /// heartbeat, because the enumerator does not subscribe until the first
    /// MoveNextAsync - raising before that is a race, not a test.
    /// </summary>
    private static async Task<IAsyncEnumerator<MotionStateChange?>> SubscribedStream(
        IMotionEventDispatcher dispatcher,
        CancellationToken ct)
    {
        var stream = MotionEventStream.ReadAsync(dispatcher, Heartbeat, ct).GetAsyncEnumerator(ct);
        Assert.True(await stream.MoveNextAsync());
        Assert.Null(stream.Current);

        return stream;
    }

    private static async Task<MotionStateChange?> Next(IAsyncEnumerator<MotionStateChange?> stream)
    {
        Assert.True(await stream.MoveNextAsync());
        return stream.Current;
    }

    /// <summary>A kiosk that connects mid-motion has to render what is already happening, not wait for the next transition.</summary>
    [Fact]
    public async Task ReadAsync_YieldsEveryAlreadyActiveDeviceFirst()
    {
        using var deadline = Deadline();
        var dispatcher = Dispatcher();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        dispatcher.SetMotionState(first, true);
        dispatcher.SetMotionState(second, true);

        await using var stream = MotionEventStream.ReadAsync(dispatcher, Heartbeat, deadline.Token).GetAsyncEnumerator(deadline.Token);

        var snapshot = new List<MotionStateChange?> { await Next(stream), await Next(stream) };

        // Asserted without an order: the snapshot comes off a set, and a kiosk
        // applies each item on its own anyway.
        Assert.Contains(new MotionStateChange(first, true), snapshot);
        Assert.Contains(new MotionStateChange(second, true), snapshot);
    }

    /// <summary>
    /// The opening item is a heartbeat rather than nothing, because an SSE
    /// response writes no headers until its first frame: a kiosk connecting on
    /// a quiet night would otherwise sit there with an EventSource that has not
    /// opened yet and no way to tell that from a broken API.
    /// </summary>
    [Fact]
    public async Task ReadAsync_NoActiveDevices_StillOpensWithAHeartbeat()
    {
        using var deadline = Deadline();
        var dispatcher = Dispatcher();

        await using var stream = MotionEventStream.ReadAsync(dispatcher, Heartbeat, deadline.Token).GetAsyncEnumerator(deadline.Token);

        Assert.True(await stream.MoveNextAsync());
        Assert.Null(stream.Current);
    }

    [Fact]
    public async Task ReadAsync_ChangeAfterSubscribing_IsYielded()
    {
        using var deadline = Deadline();
        var dispatcher = Dispatcher();
        var stream = await SubscribedStream(dispatcher, deadline.Token);
        await using var _stream = stream;
        var device = Guid.NewGuid();

        dispatcher.SetMotionState(device, true);

        Assert.Equal(new MotionStateChange(device, true), await Next(stream));
    }

    /// <summary>Order is the whole contract: an on/off pair applied backwards leaves a modal open for motion that ended.</summary>
    [Fact]
    public async Task ReadAsync_SeveralChanges_ArriveInOrder()
    {
        using var deadline = Deadline();
        var dispatcher = Dispatcher();
        var stream = await SubscribedStream(dispatcher, deadline.Token);
        await using var _stream = stream;
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        dispatcher.SetMotionState(first, true);
        dispatcher.SetMotionState(second, true);
        dispatcher.SetMotionState(first, false);

        Assert.Equal(new MotionStateChange(first, true), await Next(stream));
        Assert.Equal(new MotionStateChange(second, true), await Next(stream));
        Assert.Equal(new MotionStateChange(first, false), await Next(stream));
    }

    /// <summary>A repeat the dispatcher swallows must not reach the kiosk either - it would re-open a modal the user just dismissed.</summary>
    [Fact]
    public async Task ReadAsync_RepeatedState_YieldsHeartbeatInsteadOfAChange()
    {
        using var deadline = Deadline();
        var dispatcher = Dispatcher();
        var stream = await SubscribedStream(dispatcher, deadline.Token);
        await using var _stream = stream;
        var device = Guid.NewGuid();
        dispatcher.SetMotionState(device, true);
        Assert.Equal(new MotionStateChange(device, true), await Next(stream));

        dispatcher.SetMotionState(device, true);

        Assert.Null(await Next(stream));
    }

    /// <summary>The heartbeat is what eventually writes to - and so fails on - a connection whose kiosk quietly disappeared.</summary>
    [Fact]
    public async Task ReadAsync_Quiet_YieldsHeartbeats()
    {
        using var deadline = Deadline();
        var dispatcher = Dispatcher();
        var stream = await SubscribedStream(dispatcher, deadline.Token);
        await using var _stream = stream;

        Assert.Null(await Next(stream));
        Assert.Null(await Next(stream));
    }

    [Fact]
    public async Task ReadAsync_Cancelled_EndsWithoutAFinalFrame()
    {
        using var deadline = Deadline();
        using var aborted = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var dispatcher = Dispatcher();
        var stream = await SubscribedStream(dispatcher, aborted.Token);
        await using var _stream = stream;

        var pending = stream.MoveNextAsync();
        await aborted.CancelAsync();

        Assert.False(await pending);
    }

    /// <summary>
    /// The dispatcher outlives every connection, so a stream that failed to
    /// detach would leave the process feeding a kiosk that is gone - one more
    /// dead handler per reconnect, forever.
    /// </summary>
    [Fact]
    public async Task ReadAsync_Disposed_UnsubscribesFromTheDispatcher()
    {
        using var deadline = Deadline();
        var dispatcher = new CountingDispatcher();
        var stream = MotionEventStream.ReadAsync(dispatcher, Heartbeat, deadline.Token).GetAsyncEnumerator(deadline.Token);

        Assert.Null(await Next(stream));
        Assert.Equal(1, dispatcher.SubscriberCount);

        await stream.DisposeAsync();

        Assert.Equal(0, dispatcher.SubscriberCount);
    }

    /// <summary>
    /// Pins the shape the kiosk parses out of each `data:` line. The SSE
    /// result serializes with the web defaults - Program.cs configures MVC's
    /// JSON options but not Http.Json's, so nothing overrides them here - and
    /// a property rename on MotionStateChange would otherwise break the
    /// dashboard silently instead of breaking this build.
    /// </summary>
    [Fact]
    public void MotionStateChange_SerializesAsTheKioskReadsIt()
    {
        var change = new MotionStateChange(Guid.Parse("2b4c9f3a-0000-4000-8000-000000000001"), true);

        var json = JsonSerializer.Serialize<MotionStateChange?>(change, JsonSerializerOptions.Web);

        Assert.Equal("""{"deviceId":"2b4c9f3a-0000-4000-8000-000000000001","isActive":true}""", json);
    }

    /// <summary>Exposes the subscriber count the real dispatcher deliberately keeps to itself.</summary>
    private sealed class CountingDispatcher : IMotionEventDispatcher
    {
        public event Action<MotionStateChange>? Changed;

        public int SubscriberCount => Changed?.GetInvocationList().Length ?? 0;

        public IReadOnlyList<Guid> ActiveDeviceIds => [];

        public void SetMotionState(Guid deviceId, bool isActive) => Changed?.Invoke(new MotionStateChange(deviceId, isActive));
    }
}
