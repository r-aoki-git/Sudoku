using Sudoku.Models;
using static Sudoku.Solvers.CageCombinatorics;

namespace Sudoku.Solvers.Techniques;

/// <summary>
/// レベル3：イニー/アウティー（複数ユニット版）。
///
/// 45の法則を、隣接する2〜3本の行・列、および横3ブロック / 縦3ブロックの帯へ
/// 一般化したもの。k本のユニットの合計は必ず 45*k になるので、
/// 帯に完全に内包されるケージの合計を差し引けば、はみ出しているケージの
/// 「帯の内側にあるセル」の合計が求まる。
///
/// 単一ユニット版は <see cref="FortyFiveRuleTechnique"/>（レベル2）が担当する。
/// こちらは帯の幅が2以上のケースだけを対象にすることで、
/// 45の法則の単なる再実行にならないようにしている
/// （幅1の帯を含めてしまうと、レベル2で必ず先に処理されるため
/// このテクニックは一度も発火しなくなる）。
/// </summary>
public class InnieOutieTechnique : ISolvingTechnique
{
    private const int UnitSum = 45;

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
        var cageByCell = new Dictionary<(int Row, int Col), Cage>();

        foreach (var cage in cages)
            foreach (var cell in cage.Cells)
                cageByCell[cell] = cage;

        _virtualCages = BuildVirtualCages(cageByCell);
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
        var analysis =
            _cache.GetOrAnalyze(
                virtualCage,
                board,
                candidates,
                virtualCage.Cells,
                virtualCage.TargetSum);

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

        return changed;
    }

    private static List<VirtualCage> BuildVirtualCages(
        Dictionary<(int Row, int Col), Cage> cageByCell)
    {
        var result = new List<VirtualCage>();

        foreach (var (region, unitCount) in EnumerateRegions())
        {
            var virtualCage =
                TryBuildVirtualCage(
                    cageByCell,
                    region,
                    unitCount);

            if (virtualCage != null)
                result.Add(virtualCage);
        }

        return result;
    }

    /// <summary>
    /// 帯に完全内包されるケージの合計を 45*unitCount から差し引き、
    /// はみ出しているケージが1つだけなら仮想ケージを作る。
    /// </summary>
    private static VirtualCage? TryBuildVirtualCage(
        Dictionary<(int Row, int Col), Cage> cageByCell,
        List<(int Row, int Col)> regionCells,
        int unitCount)
    {
        int containedSum = 0;
        List<(int Row, int Col)>? crossingCellsInRegion = null;

        foreach (var group in regionCells.GroupBy(cell => cageByCell[cell]))
        {
            var cage = group.Key;
            var cellsInRegion = group.ToList();

            if (cellsInRegion.Count == cage.Cells.Count)
            {
                containedSum += cage.TargetSum;
                continue;
            }

            // はみ出しているケージが2つ以上ある帯は扱えない。
            if (crossingCellsInRegion != null)
                return null;

            crossingCellsInRegion = cellsInRegion;
        }

        if (crossingCellsInRegion is null)
            return null;

        // 仮想ケージは最大9セルまで（数字の重複禁止が成立する範囲）。
        if (crossingCellsInRegion.Count > Board.Size)
            return null;

        return new VirtualCage(
            crossingCellsInRegion,
            (UnitSum * unitCount) - containedSum);
    }

    /// <summary>
    /// 幅2以上の帯を列挙する。
    ///   ・隣接する2行 / 3行
    ///   ・隣接する2列 / 3列
    /// 3行・3列の帯はブロック帯（バンド / スタック）と一致するケースを含む。
    /// </summary>
    private static IEnumerable<(List<(int Row, int Col)> Cells, int UnitCount)> EnumerateRegions()
    {
        for (int width = 2; width <= 3; width++)
        {
            for (int start = 0; start + width <= Board.Size; start++)
            {
                var rows = new List<(int Row, int Col)>();

                for (int r = start; r < start + width; r++)
                    for (int c = 0; c < Board.Size; c++)
                        rows.Add((r, c));

                yield return (rows, width);

                var cols = new List<(int Row, int Col)>();

                for (int c = start; c < start + width; c++)
                    for (int r = 0; r < Board.Size; r++)
                        cols.Add((r, c));

                yield return (cols, width);
            }
        }
    }
}
