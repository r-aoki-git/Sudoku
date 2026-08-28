using Sudoku.Models;
using System.Windows.Controls;

namespace Sudoku.Solvers.Techniques;

/// <summary>
/// レベル2: 45の法則（単一ユニット版）
/// ある行・列・ブロックの中で「完全に内包されたケージ」の合計を45から差し引くと、
/// 範囲をまたぐケージが1つだけ・かつユニット内のマスが1個だけの場合、
/// そのマスの値を直接確定できる。
/// </summary>
public class FortyFiveRuleTechnique : ISolvingTechnique
{
    private const int UnitSum = 45;
    private readonly Dictionary<(int Row, int Col), Cage> _cageByCell;

    public FortyFiveRuleTechnique(List<Cage> cages)
    {
        _cageByCell = new Dictionary<(int, int), Cage>();
        foreach (var cage in cages)
            foreach (var cell in cage.Cells)
                _cageByCell[cell] = cage;
    }

    public int Level => 2;
    public string Name => "45 Rule (Single Unit)";
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

            // ケージ全体がこのユニット内に収まっている
            if (cellsInUnit.Count == cage.Cells.Count)
            {
                fullyContainedSum += cage.TargetSum;
                continue;
            }

            // このユニットを跨ぐケージ
            if (crossingCellsInUnit != null)
                return false;

            crossingCellsInUnit = cellsInUnit;
        }

        // 跨ぐケージが存在しない
        if (crossingCellsInUnit is null)
            return false;

        // ユニット内に残っている数字の合計
        int virtualTargetSum = UnitSum - fullyContainedSum;

        var virtualCells = crossingCellsInUnit.Select(c => (Row: c.row, Col: c.col)).ToList();

        var analysis = CageCombinatorics.AnalyzeCage(
            board,
            candidates,
            virtualCells,
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

        // ここは候補が1つ絞られるたびに呼ばれるホットパス。
        // 既定では出力しない（SolverDiagnostics.VerboseLoggingを参照）。
        if (changed && SolverDiagnostics.VerboseLogging)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[45Rule] Remaining={analysis.Remaining.Count}, " +
                $"Assignments={analysis.Assignments.Count}");
        }

        return changed;
    }
}