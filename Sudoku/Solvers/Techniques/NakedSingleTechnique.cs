using Sudoku.Models;

namespace Sudoku.Solvers.Techniques;

/// <summary>
/// レベル1: そのマスに入りうる候補が1つしかない場合、その数字に確定する。
/// </summary>
public class NakedSingleTechnique : ISolvingTechnique
{
    public int Level => 1;
    public string Name => "Naked Single";
    public bool PlacesValue => true;

    public bool TryApply(Board board, CandidateGrid candidates)
    {
        for (int r = 0; r < Board.Size; r++)
        {
            for (int c = 0; c < Board.Size; c++)
            {
                var cell = board.GetCell(r, c);
                if (cell.HasValue) continue;

                var possible = candidates.GetCandidates(r, c);
                if (possible.Count == 1)
                {
                    cell.SetValue(possible.First());
                    return true;
                }
            }
        }
        return false;
    }
}