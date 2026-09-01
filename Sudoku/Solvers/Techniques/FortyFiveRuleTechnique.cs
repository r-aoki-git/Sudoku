using Sudoku.Models;
using static Sudoku.Solvers.CageCombinatorics;

namespace Sudoku.Solvers.Techniques;

/// <summary>
/// レベル2: 45の法則（単一ユニット版）
/// ある行・列・ブロックの中で「完全に内包されたケージ」の合計を45から差し引くと、
/// 残り（範囲をまたぐケージの、ユニット内側の部分）に入りうる数字の組み合わせを
/// 絞り込める。またぐケージは1つとは限らない。複数のケージが同時にまたいでいても、
/// それらのユニット内セルをまとめて1つの仮想ケージとして扱えば同じ理屈で適用できる
/// （合計45という制約自体は、またぐケージの個数に依存しないため）。
/// </summary>
public class FortyFiveRuleTechnique : ISolvingTechnique
{
    private const int UnitSum = 45;
    private readonly Dictionary<(int Row, int Col), Cage> _cageByCell;
    private readonly List<VirtualCage> _virtualCages;
    private readonly CageAnalysisCache _cache = new();

    private sealed class VirtualCage
    {
        public List<(int Row, int Col)> Cells { get; }
        public int TargetSum { get; }

        public VirtualCage(List<(int Row, int Col)> cells, int targetSum)
        {
            Cells = cells;
            TargetSum = targetSum;
        }
    }

    public FortyFiveRuleTechnique(List<Cage> cages)
    {
        _cageByCell = new Dictionary<(int, int), Cage>();
        foreach (var cage in cages)
            foreach (var cell in cage.Cells)
                _cageByCell[cell] = cage;

        _virtualCages = BuildVirtualCages();
    }

    public int Level => 2;
    public string Name => "45 Rule (Single Unit)";
    public bool PlacesValue => false;

    public bool TryApply(Board board, CandidateGrid candidates)
    {
        foreach (var virtualCage in _virtualCages)
        {
            if (TryVirtualCage(board, candidates, virtualCage))
                return true;
        }
        return false;
    }

    private bool TryVirtualCage(Board board, CandidateGrid candidates, VirtualCage virtualCage)
    {
        var analysis = _cache.GetOrAnalyze(virtualCage, board, candidates, virtualCage.Cells, virtualCage.TargetSum);

        if (analysis.Remaining.Count == 0 || analysis.Assignments.Count == 0)
            return false;

        var allowed = CageCombinatorics.GetAllowedDigits(analysis);

        bool changed = false;

        for (int i = 0; i < analysis.Remaining.Count; i++)
        {
            var (row, col) = analysis.Remaining[i];
            var allowedDigits = allowed[i];

            foreach (var digit in candidates.GetCandidates(row, col).ToList())
            {
                if (!allowedDigits.Contains(digit) &&
                    candidates.EliminateCandidate(row, col, digit))
                {
                    changed = true;
                }
            }
        }

        if (changed && SolverDiagnostics.VerboseLogging)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[45Rule] Remaining={analysis.Remaining.Count}, " +
                $"Assignments={analysis.Assignments.Count}");
        }

        return changed;
    }

    private List<VirtualCage> BuildVirtualCages()
    {
        var result = new List<VirtualCage>();

        foreach (var unit in EnumerateUnits())
        {
            var virtualCage = TryBuildVirtualCage(unit);
            if (virtualCage != null)
                result.Add(virtualCage);
        }

        return result;
    }

    private VirtualCage? TryBuildVirtualCage(List<(int row, int col)> unitCells)
    {
        var cageGroups = unitCells.GroupBy(cell => _cageByCell[cell]);

        int fullyContainedSum = 0;
        List<(int row, int col)>? crossingCellsInUnit = null;

        foreach (var group in cageGroups)
        {
            var cage = group.Key;
            var cellsInUnit = group.ToList();

            if (cellsInUnit.Count == cage.Cells.Count)
            {
                fullyContainedSum += cage.TargetSum;
                continue;
            }

            if (crossingCellsInUnit != null)
                return null;

            crossingCellsInUnit = cellsInUnit;
        }

        if (crossingCellsInUnit is null)
            return null;

        int virtualTargetSum = UnitSum - fullyContainedSum;
        var cells = crossingCellsInUnit.Select(c => (Row: c.row, Col: c.col)).ToList();

        return new VirtualCage(cells, virtualTargetSum);
    }

    private static IEnumerable<List<(int row, int col)>> EnumerateUnits()
    {
        for (int i = 0; i < Board.Size; i++)
        {
            yield return BoardUnits.Row(i);
            yield return BoardUnits.Column(i);
        }

        for (int boxRow = 0; boxRow < Board.Size; boxRow += Board.BoxSize)
            for (int boxCol = 0; boxCol < Board.Size; boxCol += Board.BoxSize)
                yield return BoardUnits.Box(boxRow, boxCol);
    }
}