namespace Sudoku.Models.Commands;

/// <summary>1マスに対する1操作を表す。Undo/Redoの単位になる。</summary>
public interface ICellCommand
{
    void Execute();
    void Undo();
}