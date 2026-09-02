using Sudoku.Models;
using static Sudoku.Solvers.CageCombinatorics;

namespace Sudoku.Solvers.Techniques;

/// <summary>
/// レベル1: ケージの合計値だけを見て、残りマスに入りうる数字の和集合を求め、
/// そこに一切現れない数字を候補から取り除く。
/// （例：2マスで合計3のケージ → {1, 2}以外の数字はどちらのマスにも入りえない）
///
/// これは初心者が「組み合わせ表」を引いて行う推論に相当する。
/// 各セルの現在の候補は考慮せず、ケージ内の確定値と合計値だけを使う。
///
/// 各セルの候補まで突き合わせた厳密な割り当て解析は、
/// 人間にとっては明確に上位の推論なので <see cref="CageCombinationTechnique"/>（レベル3）
/// として分離している。両者を同じレベル1に混ぜると、レベル1だけで
/// ほぼ全ての盤面が解けてしまい、難易度の階段が成立しなくなる。
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
            var (remaining, combos) =
                CageCombinatorics.AnalyzeCage(
                    board,
                    cage.Cells,
                    cage.TargetSum);

            if (remaining.Count == 0 || combos.Count == 0)
                continue;

            var allowed = CageCombinatorics.UnionDigits(combos);

            bool changed = false;

            foreach (var (row, col) in remaining)
            {
                foreach (int digit in candidates.GetCandidates(row, col).ToList())
                {
                    if (allowed.Contains(digit))
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
