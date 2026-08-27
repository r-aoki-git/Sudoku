using Sudoku.Models;

namespace Sudoku.Solvers.Techniques;

/// <summary>
/// レベル3: Hidden Pair(size=2) / Hidden Triple(size=3)。
/// 同じユニット内で、k種類の数字が候補として現れるマスがちょうどk個に限られる場合、
/// そのk個のマスの候補を、そのk種類の数字だけに絞り込む。
/// </summary>
public class HiddenSubsetTechnique : ISolvingTechnique
{
    private readonly int _size;

    public HiddenSubsetTechnique(int size)
    {
        if (size < 2 || size > 3)
            throw new ArgumentOutOfRangeException(nameof(size), "size は2または3を指定してください。");
        _size = size;
    }

    public int Level => 3;
    public string Name => _size == 2 ? "Hidden Pair" : "Hidden Triple";
    public bool PlacesValue => false;

    public bool TryApply(Board board, CandidateGrid candidates)
    {
        foreach (var unit in BoardUnits.All())
        {
            var emptyCells = unit.Where(p => !board.GetCell(p.row, p.col).HasValue).ToList();
            if (emptyCells.Count < _size) continue;

            var remainingDigits = Enumerable.Range(1, 9)
                .Where(d => emptyCells.Any(p => candidates.GetCandidates(p.row, p.col).Contains(d)))
                .ToList();

            foreach (var digitCombo in Combinations(remainingDigits, _size))
            {
                var digitSet = new HashSet<int>(digitCombo);

                var cellsContainingAny = emptyCells
                    .Where(p => candidates.GetCandidates(p.row, p.col).Overlaps(digitSet))
                    .ToList();

                if (cellsContainingAny.Count != _size) continue;

                bool changed = false;
                foreach (var (row, col) in cellsContainingAny)
                {
                    var toRemove = candidates.GetCandidates(row, col)
                        .Where(d => !digitSet.Contains(d))
                        .ToList();

                    foreach (var d in toRemove)
                        if (candidates.EliminateCandidate(row, col, d))
                            changed = true;
                }

                if (changed) return true;
            }
        }
        return false;
    }

    private static IEnumerable<List<int>> Combinations(List<int> items, int k)
        => CombinationsRecursive(items, k, 0);

    private static IEnumerable<List<int>> CombinationsRecursive(List<int> items, int k, int start)
    {
        if (k == 0)
        {
            yield return new List<int>();
            yield break;
        }

        for (int i = start; i <= items.Count - k; i++)
        {
            foreach (var rest in CombinationsRecursive(items, k - 1, i + 1))
            {
                var combo = new List<int> { items[i] };
                combo.AddRange(rest);
                yield return combo;
            }
        }
    }
}