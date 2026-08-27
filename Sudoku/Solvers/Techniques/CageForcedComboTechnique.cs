using Sudoku.Models;

namespace Sudoku.Solvers.Techniques;

/// <summary>
/// レベル1: ケージの合計値から、残りマスに入りうる数字の組み合わせを求め、
/// その組み合わせに一切現れない数字を候補から取り除く。
/// （例：2マスで合計3のケージ → {1, 2}以外の数字はどちらのマスにも入りえない）
/// </summary>
public class CageForcedComboTechnique : ISolvingTechnique
{
    private readonly List<Cage> _cages;

    public CageForcedComboTechnique(List<Cage> cages)
    {
        _cages = cages;
    }

    public int Level => 1;
    public string Name => "Cage Forced Combination";
    public bool PlacesValue => false;

    public bool TryApply(Board board, CandidateGrid candidates)
    {
        foreach (var cage in _cages)
        {
            var analysis = CageCombinatorics.AnalyzeCage(board, candidates, cage.Cells, cage.TargetSum);

            if (analysis.Remaining.Count == 0)
                continue;

            if (analysis.Assignments.Count == 0)
                continue;

            var allowed = CageCombinatorics.GetAllowedDigits(analysis);

            bool changed = false;

            for (int i = 0; i < analysis.Remaining.Count; i++)
            {
                var (row, col) = analysis.Remaining[i];

                var currentCandidates =
                    candidates.GetCandidates(row, col).ToList();

                foreach (int digit in currentCandidates)
                {
                    if (allowed[i].Contains(digit))
                        continue;

                    if (candidates.EliminateCandidate(row, col, digit))
                        changed = true;
                }
            }

            if (changed)
                return true;
        }
        return false;
    }
}