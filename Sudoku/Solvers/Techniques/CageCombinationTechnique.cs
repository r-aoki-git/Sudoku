using Sudoku.Models;
using static Sudoku.Solvers.CageCombinatorics;

namespace Sudoku.Solvers.Techniques;

/// <summary>
/// レベル3: ケージ内の「合計値」「数字重複禁止」「各セルの現在の候補」を
/// 同時に満たす割り当てをすべて列挙し、どの割り当てにも現れない数字を
/// 各セルの候補から取り除く。
///
/// レベル1の <see cref="CageForcedComboTechnique"/> が
/// 「ケージ全体で使いうる数字の和集合」しか見ないのに対して、
/// こちらはセルごとに「そのセルに実際に置ける数字」まで絞り込む。
/// 他セルの候補との相互作用を追う必要があるため、人間にとっては明確に上位の推論。
/// </summary>
public class CageCombinationTechnique : ISolvingTechnique
{
    private readonly List<Cage> _cages;
    private readonly CageAnalysisCache _cache = new();

    public CageCombinationTechnique(List<Cage> cages)
    {
        _cages = cages;
    }

    public int Level => 3;
    public string Name => "Cage Combination";
    public bool PlacesValue => false;

    public bool TryApply(Board board, CandidateGrid candidates)
    {
        foreach (var cage in _cages)
        {
            var analysis =
                _cache.GetOrAnalyze(
                    cage,
                    board,
                    candidates,
                    cage.Cells,
                    cage.TargetSum);

            if (analysis.Remaining.Count == 0)
                continue;

            if (analysis.Assignments.Count == 0)
                continue;

            var allowed = CageCombinatorics.GetAllowedDigits(analysis);

            bool changed = false;

            for (int i = 0; i < analysis.Remaining.Count; i++)
            {
                var (row, col) = analysis.Remaining[i];

                foreach (int digit in candidates.GetCandidates(row, col).ToList())
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
