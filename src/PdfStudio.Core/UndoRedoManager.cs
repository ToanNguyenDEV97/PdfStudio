namespace PdfStudio.Core;

public interface IUndoableAction
{
    void Execute();
    void Undo();
}

public sealed class UndoRedoManager
{
    private readonly Stack<IUndoableAction> _undoStack = new();
    private readonly Stack<IUndoableAction> _redoStack = new();

    public event Action? StateChanged;

    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;

    public void Execute(IUndoableAction action)
    {
        action.Execute();
        _undoStack.Push(action);
        _redoStack.Clear();
        StateChanged?.Invoke();
    }

    public void Undo()
    {
        if (_undoStack.Count == 0) return;
        var action = _undoStack.Pop();
        action.Undo();
        _redoStack.Push(action);
        StateChanged?.Invoke();
    }

    public void Redo()
    {
        if (_redoStack.Count == 0) return;
        var action = _redoStack.Pop();
        action.Execute();
        _undoStack.Push(action);
        StateChanged?.Invoke();
    }

    public void Clear()
    {
        _undoStack.Clear();
        _redoStack.Clear();
        StateChanged?.Invoke();
    }
}