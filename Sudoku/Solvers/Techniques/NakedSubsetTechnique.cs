using Sudoku.Models;

namespace Sudoku.Solvers;

/// <summary>
/// レベル3: Naked Pair(size=2) / Naked Triple(size=3)
/// 同じユニット内で、k個のマスの候補をすべて合わせるとちょうどk種類の数字になる場合、
/// その組み合わせは他の数字が入る余地がないとみなし、同じユニットの他のマスからその数字を除去する。
/// </summary>
public class NakedSubsetTechnique : ISolvingTechnique
{
    private readonly int _size;

    public NakedSubsetTechnique(int size)
    {
        if (size < 2 || size > 3)
            throw new ArgumentOutOfRangeException(nameof(size), "size は2または3を指定してください。");
        _size = size;
    }

    public int Level => 3;
    public string Name => _size == 2 ? "Naked Pair" : "Naked Triple";
    public bool PlacesValue => false;

    public bool TryApply(Board board, CandidateGrid candidates)
    {
        foreach (var unit in BoardUnits.All())
        {
            var candidateCells = unit
                .Where(p => !board.GetCell(p.row, p.col).HasValue)
                .Where(p =>
                {
                    int count = candidates.GetCandidates(p.row, p.col).Count;
                    return count >= 2 && count <= _size;
                })
                .ToList();

            if (candidateCells.Count < _size) continue;

            foreach (var combo in Combinations(candidateCells, _size))
            {
                var unionDigits = new HashSet<int>();
                foreach (var cell in combo)
                    unionDigits.UnionWith(candidates.GetCandidates(cell.row, cell.col));

                if (unionDigits.Count != _size) continue;

                bool changed = false;
                foreach (var (row, col) in unit)
                {
                    if (combo.Contains((row, col))) continue;
                    if (board.GetCell(row, col).HasValue) continue;

                    foreach (var digit in unionDigits)
                        if (candidates.EliminateCandidate(row, col, digit))
                            changed = true;
                }

                if (changed) return true;
            }
        }
        return false;
    }

    private static IEnumerable<List<(int row, int col)>> Combinations(List<(int row, int col)> items, int k)
        => CombinationsRecursive(items, k, 0);

    private static IEnumerable<List<(int row, int col)>> CombinationsRecursive(List<(int row, int col)> items, int k, int start)
    {
        if (k == 0)
        {
            yield return new List<(int, int)>();
            yield break;
        }

        for (int i = start; i <= items.Count - k; i++)
        {
            foreach (var rest in CombinationsRecursive(items, k - 1, i + 1))
            {
                var combo = new List<(int, int)> { items[i] };
                combo.AddRange(rest);
                yield return combo;
            }
        }
    }
}