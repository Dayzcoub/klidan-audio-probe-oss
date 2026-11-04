namespace KlidanAudioProbe.Gui.Core;

public sealed record WorkerUiEventBatch(IReadOnlyList<WorkerEventDto> Events, bool Overflowed, int CoalescedSnapshots);

public sealed class WorkerUiEventBuffer
{
    public const int ControlCapacity = 32;
    private readonly Queue<WorkerEventDto> _control = new();
    private WorkerEventDto? _latestSnapshot;
    private bool _overflowed;
    private int _coalescedSnapshots;

    public int PendingCount => _control.Count + (_latestSnapshot is null ? 0 : 1);
    public int CoalescedSnapshots => _coalescedSnapshots;

    public void Enqueue(WorkerEventDto value)
    {
        if (_overflowed) return;
        if (value.Name == "MeterSnapshot")
        {
            if (_latestSnapshot is not null) _coalescedSnapshots++;
            _latestSnapshot = value;
            return;
        }
        if (_control.Count == ControlCapacity)
        {
            _overflowed = true;
            _control.Clear();
            _latestSnapshot = null;
            return;
        }
        _control.Enqueue(value);
    }

    public WorkerUiEventBatch Drain()
    {
        if (_overflowed)
        {
            _overflowed = false;
            _coalescedSnapshots = 0;
            return new([], true, 0);
        }
        var items = _control.ToList();
        _control.Clear();
        if (_latestSnapshot is not null) items.Add(_latestSnapshot);
        _latestSnapshot = null;
        var coalesced = _coalescedSnapshots;
        _coalescedSnapshots = 0;
        items.Sort((a, b) => a.EventSequence.CompareTo(b.EventSequence));
        return new(items, false, coalesced);
    }
}
