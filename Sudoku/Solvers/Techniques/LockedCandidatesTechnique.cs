using Sudoku.Models;

namespace Sudoku.Solvers.Techniques;

/// <summary>
/// レベル2: Locked Candidates（ポインティング／クレーミング）。
/// ・ポインティング: ブロック内で、ある数字の候補が1つの行(または列)だけに集中している場合、
///   その行(列)の、ブロック外のマスからその数字の候補を除去する。
/// ・クレーミング: 行(または列)内で、ある数字の候補が1つのブロックだけに集中している場合、
///   そのブロックの、行(列)外のマスからその数字の候補を除去する。
/// </summary>
public class LockedCandidatesTechnique : ISolvingTechnique
{
    public int Level => 2;
    public string Name => "Locked Candidates";
    public bool PlacesValue => false;

    public bool TryApply(Board board, CandidateGrid candidates)
    {
        if (TryPointing(board, candidates)) return true;
        if (TryClaiming(board, candidates)) return true;
        return false;
    }

    private static bool TryPointing(Board board, CandidateGrid candidates)
    {
        for (int boxRow = 0; boxRow < Board.Size; boxRow += Board.BoxSize)
        {
            for (int boxCol = 0; boxCol < Board.Size; boxCol += Board.BoxSize)
            {
                for (int digit = 1; digit <= 9; digit++)
                {
                    var cellsInBoxWithDigit = new List<(int row, int col)>();

                    for (int r = boxRow; r < boxRow + Board.BoxSize; r++)
                        for (int c = boxCol; c < boxCol + Board.BoxSize; c++)
                        {
                            if (board.GetCell(r, c).HasValue) continue;
                            if (candidates.GetCandidates(r, c).Contains(digit))
                                cellsInBoxWithDigit.Add((r, c));
                        }

                    if (cellsInBoxWithDigit.Count < 2) continue;

                    bool sameRow = cellsInBoxWithDigit.All(p => p.row == cellsInBoxWithDigit[0].row);
                    bool sameCol = cellsInBoxWithDigit.All(p => p.col == cellsInBoxWithDigit[0].col);

                    if (sameRow)
                    {
                        if (EliminateFromRowOutsideBox(board, candidates, cellsInBoxWithDigit[0].row, boxCol, digit))
                            return true;
                    }
                    else if (sameCol)
                    {
                        if (EliminateFromColumnOutsideBox(board, candidates, cellsInBoxWithDigit[0].col, boxRow, digit))
                            return true;
                    }
                }
            }
        }
        return false;
    }

    private static bool TryClaiming(Board board, CandidateGrid candidates)
    {
        for (int row = 0; row < Board.Size; row++)
            if (TryClaimingForUnit(board, candidates, RowPositions(row), isRow: true))
                return true;

        for (int col = 0; col < Board.Size; col++)
            if (TryClaimingForUnit(board, candidates, ColumnPositions(col), isRow: false))
                return true;

        return false;
    }

    private static bool TryClaimingForUnit(Board board, CandidateGrid candidates, IEnumerable<(int row, int col)> unit, bool isRow)
    {
        var positions = unit.ToList();

        for (int digit = 1; digit <= 9; digit++)
        {
            var cellsWithDigit = positions
                .Where(p => !board.GetCell(p.row, p.col).HasValue && candidates.GetCandidates(p.row, p.col).Contains(digit))
                .ToList();

            if (cellsWithDigit.Count < 2) continue;

            int boxRow0 = (cellsWithDigit[0].row / Board.BoxSize) * Board.BoxSize;
            int boxCol0 = (cellsWithDigit[0].col / Board.BoxSize) * Board.BoxSize;

            bool sameBox = cellsWithDigit.All(p =>
                (p.row / Board.BoxSize) * Board.BoxSize == boxRow0 &&
                (p.col / Board.BoxSize) * Board.BoxSize == boxCol0);

            if (!sameBox) continue;

            bool changed = false;
            for (int r = boxRow0; r < boxRow0 + Board.BoxSize; r++)
                for (int c = boxCol0; c < boxCol0 + Board.BoxSize; c++)
                {
                    bool inUnit = isRow ? r == cellsWithDigit[0].row : c == cellsWithDigit[0].col;
                    if (inUnit) continue;
                    if (board.GetCell(r, c).HasValue) continue;

                    if (candidates.EliminateCandidate(r, c, digit))
                        changed = true;
                }

            if (changed) return true;
        }
        return false;
    }

    private static bool EliminateFromRowOutsideBox(Board board, CandidateGrid candidates, int row, int boxCol, int digit)
    {
        bool changed = false;
        for (int c = 0; c < Board.Size; c++)
        {
            if (c >= boxCol && c < boxCol + Board.BoxSize) continue;
            if (board.GetCell(row, c).HasValue) continue;

            if (candidates.EliminateCandidate(row, c, digit))
                changed = true;
        }
        return changed;
    }

    private static bool EliminateFromColumnOutsideBox(Board board, CandidateGrid candidates, int col, int boxRow, int digit)
    {
        bool changed = false;
        for (int r = 0; r < Board.Size; r++)
        {
            if (r >= boxRow && r < boxRow + Board.BoxSize) continue;
            if (board.GetCell(r, col).HasValue) continue;

            if (candidates.EliminateCandidate(r, col, digit))
                changed = true;
        }
        return changed;
    }

    private static IEnumerable<(int, int)> RowPositions(int row)
    {
        for (int c = 0; c < Board.Size; c++) yield return (row, c);
    }

    private static IEnumerable<(int, int)> ColumnPositions(int col)
    {
        for (int r = 0; r < Board.Size; r++) yield return (r, col);
    }
}