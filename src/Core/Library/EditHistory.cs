using PPlusTracklistEditor.Core.Tlst;

namespace PPlusTracklistEditor.Core.Library;

/// <summary>Saved state of a <see cref="StageEditSession"/> (entries are private copies).</summary>
public sealed record StageSnapshot(TlstEntry[] Entries, PendingSong[] Pending);

/// <summary>
/// Undo/Redo for one stage edit session, using whole snapshots (a stage has at most a few dozen songs, so copying
/// them is cheap and far simpler than recording each kind of change).
///
/// Call <see cref="BeforeChange"/> right before every edit. Repeated changes with the same <c>mergeKey</c>
/// (e.g. dragging the volume slider of one song) become a single undo step.
/// </summary>
public sealed class EditHistory(StageEditSession session, int limit = 200)
{
    private readonly LinkedList<StageSnapshot> _undo = new();
    private readonly Stack<StageSnapshot> _redo = new();
    private string? _lastKey;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public void BeforeChange(string? mergeKey = null)
    {
        if (mergeKey is not null && mergeKey == _lastKey && _undo.Count > 0) return;
        _undo.AddLast(session.Snapshot());
        if (_undo.Count > limit) _undo.RemoveFirst();
        _redo.Clear();
        _lastKey = mergeKey;
    }

    public bool Undo()
    {
        if (_undo.Last is not { } node) return false;
        _redo.Push(session.Snapshot());
        _undo.RemoveLast();
        session.Restore(node.Value);
        _lastKey = null;
        return true;
    }

    public bool Redo()
    {
        if (!_redo.TryPop(out var next)) return false;
        _undo.AddLast(session.Snapshot());
        session.Restore(next);
        _lastKey = null;
        return true;
    }

    /// <summary>Ends merging, so the next change is its own step (e.g. when the user picks another song).</summary>
    public void Break() => _lastKey = null;

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        _lastKey = null;
    }
}
