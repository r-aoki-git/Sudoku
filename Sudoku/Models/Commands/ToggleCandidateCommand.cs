namespace Sudoku.Models.Commands;

/// <summary>候補値メモを1つトグルする操作。もう一度トグルすれば元に戻る。</summary>
public class ToggleCandidateCommand : ICellCommand
{
    private readonly Cell _cell;
    private readonly int _digit;

    public ToggleCandidateCommand(Cell cell, int digit)
    {
        _cell = cell;
        _digit = digit;
    }

    public void Execute() => _cell.ToggleCandidate(_digit);
    public void Undo() => _cell.ToggleCandidate(_digit);
}