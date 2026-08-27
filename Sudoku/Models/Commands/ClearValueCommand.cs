namespace Sudoku.Models.Commands;

/// <summary>マスの確定値を消去する操作。</summary>
public class ClearValueCommand : ICellCommand
{
    private readonly Cell _cell;
    private int? _previousValue;

    public ClearValueCommand(Cell cell)
    {
        _cell = cell;
    }

    public void Execute()
    {
        _previousValue = _cell.Value;
        _cell.ClearValue();
    }

    public void Undo()
    {
        if (_previousValue.HasValue)
            _cell.SetValue(_previousValue.Value);
    }
}