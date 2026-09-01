using Sudoku.Models;
using static Sudoku.Solvers.CageCombinatorics;

namespace Sudoku.Solvers.Techniques;

/// <summary>
/// レベル3：イニー/アウティー。45の法則を、複数マスにまたがるケージにも一般化したもの。
/// あるユニットで、はみ出すケージがちょうど1つ・かつユニット内のマスが2個以上の場合、
/// それらを「仮想ケージ（合計=45 - 完全内包ケージの合計）」とみなして候補を絞り込む
///
/// ケージ構造は生成後不変なので、対象となる仮想ケージはコンストラクタで一度だけ
/// 洗い出しておく。TryApply側は毎回のグルーピング処理を行わず、
/// 事前計算済みリストのみを走査する。
/// </summary>
public class InnieOutieTechnique : ISolvingTechnique
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

    public InnieOutieTechnique(List<Cage> cages)
    {
        _cageByCell = new Dictionary<(int, int), Cage>();
        foreach (var cage in cages)
            foreach (var cell in cage.Cells)
                _cageByCell[cell] = cage;

        _virtualCages = BuildVirtualCages();
    }

    public int Level => 3;
    public string Name => "Innie / Outie";
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
                $"[InnieOutie] Remaining={analysis.Remaining.Count}, " +
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

            // イニー/アウティーは「ユニット内のマスが2個以上」の場合のみ対象
            if (virtualCage != null && virtualCage.Cells.Count >= 2)
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