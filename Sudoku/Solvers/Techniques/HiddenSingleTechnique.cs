using Sudoku.Models;

namespace Sudoku.Solvers.Techniques;

/// <summary>
/// レベル1: 行・列・ブロックのいずれかで、ある数字が入りうるマスが1箇所しかない場合、その数字に確定する。
/// </summary>
public class HiddenSingleTechnique : ISolvingTechnique
{
    public int Level => 1;
    public string Name => "Hidden Single";
    public bool PlacesValue => true;

    public bool TryApply(Board board, CandidateGrid candidates)
    {
        for (int i = 0; i < Board.Size; i++)
        {
            if (TryApplyToUnit(board, candidates, RowPositions(i))) return true;
            if (TryApplyToUnit(board, candidates, ColumnPositions(i))) return true;
        }

        for (int boxRow = 0; boxRow < Board.Size; boxRow += Board.BoxSize)
            for (int boxCol = 0; boxCol < Board.Size; boxCol += Board.BoxSize)
                if (TryApplyToUnit(board, candidates, BoxPositions(boxRow, boxCol))) return true;

        return false;
    }

    private static bool TryApplyToUnit(Board board, CandidateGrid candidates, IEnumerable<(int row, int col)> positions)
    {
        var positionList = positions.ToList();

        for (int digit = 1; digit <= 9; digit++)
        {
            (int row, int col)? onlyPosition = null;
            int count = 0;

            foreach (var (row, col) in positionList)
            {
                if (board.GetCell(row, col).HasValue) continue;
                if (!candidates.GetCandidates(row, col).Contains(digit)) continue;

                count++;
                onlyPosition = (row, col);
            }

            if (count == 1)
            {
                var (row, col) = onlyPosition!.Value;
                board.GetCell(row, col).SetValue(digit);
                return true;
            }
        }
        return false;
    }

    private static IEnumerable<(int, int)> RowPositions(int row)
    {
        for (int c = 0; c < Board.Size; c++) yield return (row, c);
    }

    private static IEnumerable<(int, int)> ColumnPositions(int col)
    {
        for (int r = 0; r < Board.Size; r++) yield return (r, col);
    }

    private static IEnumerable<(int, int)> BoxPositions(int boxRow, int boxCol)
    {
        for (int r = boxRow; r < boxRow + Board.BoxSize; r++)
            for (int c = boxCol; c < boxCol + Board.BoxSize; c++)
                yield return (r, c);
    }
}