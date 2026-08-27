using Sudoku.Models;

namespace Sudoku.Solvers.Techniques;

/// <summary>
/// レベル3：イニー/アウティー。45の法則を、複数マスにまたがるケージにも一般化したもの。
/// あるユニットで、はみ出すケージがちょうど1つ・かつユニット内のマスが2個以上の場合、
/// それらを「仮想ケージ（合計=45 - 完全内包ケージの合計）」とみなして候補を絞り込む
/// </summary>
public class InnieOutieTechnique : ISolvingTechnique
{
    private const int UnitSum = 45;
    private readonly Dictionary<(int Row, int Col), Cage> _cageByCell;

    public InnieOutieTechnique(List<Cage> cages)
    {
        _cageByCell = new Dictionary<(int, int), Cage>();
        foreach (var cage in cages)
            foreach (var cell in cage.Cells)
                _cageByCell[cell] = cage;
    }

    public int Level => 3;
    public string Name => "Innie / Outie";
    public bool PlacesValue => false;

    public bool TryApply(Board board, CandidateGrid candidates)
    {
        for (int i = 0; i < Board.Size; i++)
        {
            if (TryUnit(board, candidates, BoardUnits.Row(i))) return true;
            if (TryUnit(board, candidates, BoardUnits.Column(i))) return true;
        }

        for (int boxRow = 0; boxRow < Board.Size; boxRow += Board.BoxSize)
            for (int boxCol = 0; boxCol < Board.Size; boxCol += Board.BoxSize)
                if (TryUnit(board, candidates, BoardUnits.Box(boxRow, boxCol))) return true;

        return false;
    }

    private bool TryUnit(Board board, CandidateGrid candidates, List<(int row, int col)> unitCells)
    {
        var cageGroups = unitCells.GroupBy(cell => _cageByCell[cell]);

        int fullyContainedSum = 0;
        List<(int row, int col)>? crossingCellsInUnit = null;

        foreach (var group in cageGroups)
        {
            var cage = group.Key;
            var cellsInUnit = group.ToList();

            if (cellsInUnit.Count == cage.Cells.Count)
                fullyContainedSum += cage.TargetSum;
            else
            {
                if (crossingCellsInUnit != null) return false;
                crossingCellsInUnit = cellsInUnit;
            }
        }

        if (crossingCellsInUnit is null || crossingCellsInUnit.Count < 2) return false;

        int virtualTargetSum = UnitSum - fullyContainedSum;

        var castCells = crossingCellsInUnit.Select(c => (Row: c.row, Col: c.col)).ToList();

        var analysis = CageCombinatorics.AnalyzeCage(
            board,
            candidates,
            castCells,
            virtualTargetSum);

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

        if (changed)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[InnieOutie] Remaining={analysis.Remaining.Count}, " +
                $"Assignments={analysis.Assignments.Count}");
        }

        return changed;
    }
}