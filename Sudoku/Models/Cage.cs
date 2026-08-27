namespace Sudoku.Models;

/// <summary>
/// キラーナンプレにおける1つのケージ（マスの集合+合計値）を表す。
/// </summary>
public class Cage
{
    public IReadOnlyList<(int Row, int Col)> Cells { get; }
    public int TargetSum { get; }

    public Cage(IReadOnlyList<(int Row, int Col)> cells, int targetSum)
    {
        Cells = cells;
        TargetSum = targetSum;
    }
}