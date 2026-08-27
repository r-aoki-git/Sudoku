using Sudoku.Models;

namespace Sudoku.Solvers.Techniques;

/// <summary>
/// レベル3：Killer Pair / Killer Triple
///
/// ケージの残りセルについて、
/// 「候補」「数字重複禁止」「ケージ合計」を同時に考慮した結果、
/// 使用可能な数字がセル数と同数に閉じた場合に、
/// その数字を同じ行・列・ブロックの他セルから除去する。
/// </summary>
public class KillerPairTripleTechnique : ISolvingTechnique
{
    private readonly List<Cage> _cages;

    public KillerPairTripleTechnique(List<Cage> cages)
    {
        _cages = cages;
    }

    public int Level => 3;

    public string Name => "Killer Pair / Triple";

    public bool PlacesValue => false;

    public bool TryApply(Board board, CandidateGrid candidates)
    {
        foreach (var cage in _cages)
        {
            var analysis = CageCombinatorics.AnalyzeCage(
                board,
                candidates,
                cage.Cells,
                cage.TargetSum);

            var remaining = analysis.Remaining;

            if (remaining.Count != 2 && remaining.Count != 3)
                continue;

            if (analysis.Assignments.Count == 0)
                continue;

            var allowed = CageCombinatorics.GetAllowedDigits(analysis);

            var unionDigits = new HashSet<int>();

            foreach (var digits in allowed)
                unionDigits.UnionWith(digits);

            if (unionDigits.Count != remaining.Count)
                continue;

            // 現在の候補から実際に削除できるものがない場合は、
            // このペア/トリプルは既に適用済みなので再処理しない。
            bool hasCandidateReduction = false;

            for (int i = 0; i < allowed.Length; i++)
            {
                var (row, col) = remaining[i];

                foreach (int digit in candidates.GetCandidates(row, col))
                {
                    if (!allowed[i].Contains(digit))
                    {
                        hasCandidateReduction = true;
                        break;
                    }
                }

                if (hasCandidateReduction)
                    break;
            }

            if (!hasCandidateReduction)
            {
                // 各セル自身は既に絞り切られている。
                // ユニット外候補だけを確認する。
                bool hasSharedUnitReduction =
                    HasSharedRowReduction(
                        candidates,
                        remaining,
                        unionDigits)
                    ||
                    HasSharedColumnReduction(
                        candidates,
                        remaining,
                        unionDigits)
                    ||
                    HasSharedBoxReduction(
                        candidates,
                        remaining,
                        unionDigits);

                if (!hasSharedUnitReduction)
                    continue;
            }

            bool changed = false;

            for (int i = 0; i < allowed.Length; i++)
            {
                // 各セルについて、ケージ上成立可能な数字以外を除去。
                var (row, col) = remaining[i];

                foreach (var digit in candidates.GetCandidates(row, col).ToList())
                {
                    if (!allowed[i].Contains(digit) &&
                        candidates.EliminateCandidate(row, col, digit))
                    {
                        changed = true;
                    }
                }
            }

            // ペア / トリプルとして閉じている数字を、
            // 同じユニットの他セルから除去する。
            if (TryEliminateFromSharedRow(
                    candidates,
                    remaining,
                    unionDigits))
            {
                changed = true;
            }

            if (TryEliminateFromSharedColumn(
                    candidates,
                    remaining,
                    unionDigits))
            {
                changed = true;
            }

            if (TryEliminateFromSharedBox(
                    candidates,
                    remaining,
                    unionDigits))
            {
                changed = true;
            }

            if (changed)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[KillerPairTriple] " +
                    $"Cells={remaining.Count}, " +
                    $"Assignments={analysis.Assignments.Count}, " +
                    $"Digits={string.Concat(unionDigits.OrderBy(x => x))}");

                return true;
            }
        }

        return false;
    }

    private static bool TryEliminateFromSharedRow(
        CandidateGrid candidates,
        IReadOnlyList<(int Row, int Col)> cells,
        HashSet<int> digits)
    {
        if (cells.Count == 0)
            return false;

        int row = cells[0].Row;

        if (!cells.All(c => c.Row == row))
            return false;

        bool changed = false;

        foreach (var (r, c) in BoardUnits.Row(row))
        {
            if (cells.Contains((r, c)))
                continue;

            foreach (var digit in digits)
            {
                if (candidates.EliminateCandidate(r, c, digit))
                    changed = true;
            }
        }

        return changed;
    }

    private static bool TryEliminateFromSharedColumn(
        CandidateGrid candidates,
        IReadOnlyList<(int Row, int Col)> cells,
        HashSet<int> digits)
    {
        if (cells.Count == 0)
            return false;

        int col = cells[0].Col;

        if (!cells.All(c => c.Col == col))
            return false;

        bool changed = false;

        foreach (var (r, c) in BoardUnits.Column(col))
        {
            if (cells.Contains((r, c)))
                continue;

            foreach (var digit in digits)
            {
                if (candidates.EliminateCandidate(r, c, digit))
                    changed = true;
            }
        }

        return changed;
    }

    private static bool TryEliminateFromSharedBox(
        CandidateGrid candidates,
        IReadOnlyList<(int Row, int Col)> cells,
        HashSet<int> digits)
    {
        if (cells.Count == 0)
            return false;

        int boxRow =
            (cells[0].Row / Board.BoxSize) * Board.BoxSize;

        int boxCol =
            (cells[0].Col / Board.BoxSize) * Board.BoxSize;

        bool sameBox = cells.All(c =>
            (c.Row / Board.BoxSize) * Board.BoxSize == boxRow &&
            (c.Col / Board.BoxSize) * Board.BoxSize == boxCol);

        if (!sameBox)
            return false;

        bool changed = false;

        foreach (var (r, c) in BoardUnits.Box(boxRow, boxCol))
        {
            if (cells.Contains((r, c)))
                continue;

            foreach (var digit in digits)
            {
                if (candidates.EliminateCandidate(r, c, digit))
                    changed = true;
            }
        }

        return changed;
    }

    private static bool HasSharedRowReduction(
    CandidateGrid candidates,
    IReadOnlyList<(int Row, int Col)> cells,
    HashSet<int> digits)
    {
        if (cells.Count == 0)
            return false;

        int row = cells[0].Row;

        if (!cells.All(c => c.Row == row))
            return false;

        foreach (var (r, c) in BoardUnits.Row(row))
        {
            if (cells.Contains((r, c)))
                continue;

            foreach (int digit in digits)
            {
                if (candidates.GetCandidates(r, c).Contains(digit))
                    return true;
            }
        }

        return false;
    }

    private static bool HasSharedColumnReduction(
        CandidateGrid candidates,
        IReadOnlyList<(int Row, int Col)> cells,
        HashSet<int> digits)
    {
        if (cells.Count == 0)
            return false;

        int col = cells[0].Col;

        if (!cells.All(c => c.Col == col))
            return false;

        foreach (var (r, c) in BoardUnits.Column(col))
        {
            if (cells.Contains((r, c)))
                continue;

            foreach (int digit in digits)
            {
                if (candidates.GetCandidates(r, c).Contains(digit))
                    return true;
            }
        }

        return false;
    }

    private static bool HasSharedBoxReduction(
        CandidateGrid candidates,
        IReadOnlyList<(int Row, int Col)> cells,
        HashSet<int> digits)
    {
        if (cells.Count == 0)
            return false;

        int boxRow =
            (cells[0].Row / Board.BoxSize) * Board.BoxSize;

        int boxCol =
            (cells[0].Col / Board.BoxSize) * Board.BoxSize;

        bool sameBox = cells.All(c =>
            (c.Row / Board.BoxSize) * Board.BoxSize == boxRow &&
            (c.Col / Board.BoxSize) * Board.BoxSize == boxCol);

        if (!sameBox)
            return false;

        foreach (var (r, c) in BoardUnits.Box(boxRow, boxCol))
        {
            if (cells.Contains((r, c)))
                continue;

            foreach (int digit in digits)
            {
                if (candidates.GetCandidates(r, c).Contains(digit))
                    return true;
            }
        }

        return false;
    }
}