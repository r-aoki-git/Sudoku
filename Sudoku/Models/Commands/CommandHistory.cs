using System.Windows.Input;

namespace Sudoku.Models.Commands;

/// <summary>実行済みコマンドの履歴を管理し、Undo/Redoを提供する。</summary>
public class CommandHistory
{
    private readonly Stack<ICellCommand> _undoStack = new();
    private readonly Stack<ICellCommand> _redoStack = new();

    public void Execute(ICellCommand command)
    {
        command.Execute();
        _undoStack.Push(command);
        _redoStack.Clear();
    }

    public void Undo()
    {
        if (_undoStack.Count == 0) return;
        var command = _undoStack.Pop();
        command.Undo();
        _redoStack.Push(command);
    }

    public void Redo()
    {
        if (_redoStack.Count == 0) return;
        var command = _redoStack.Pop();
        command.Execute();
        _undoStack.Push(command);
    }
}