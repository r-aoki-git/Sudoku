using Sudoku.Models;

namespace Sudoku.Solvers.Techniques;

/// <summary>
/// レベル4: Fish系テクニック（size=2: X-Wing, size=3: Swordfish）。
/// ある数字について、size個の行（または列）だけを見たとき、その数字の候補がちょうどsize個の
/// 列（行）だけに収まっている場合、その列（行）の他の行（列）からその数字の候補を除去する。
/// </summary>
public class FishTechnique : ISolvingTechnique
{
    private readonly int _size;

    public FishTechnique(int size)
    {
        if (size < 2 || size > 3)
            throw new ArgumentOutOfRangeException(nameof(size), "size は2または3を指定してください。");
        _size = size;
    }

    public int Level => 4;
    public string Name => _size == 2 ? "X-Wing" : "Swordfish";
    public bool PlacesValue => false;

    public bool TryApply(Board board, CandidateGrid candidates)
    {
        if (TryFish(board, candidates, isRowBased: true)) return true;
        if (TryFish(board, candidates, isRowBased: false)) return true;
        return false;
    }

    private bool TryFish(Board board, CandidateGrid candidates, bool isRowBased)
    {
        for (int digit = 1; digit <= 9; digit++)
        {
            var lines = new List<(int index, HashSet<int> positions)>();

            for (int i = 0; i < Board.Size; i++)
            {
                var positions = new HashSet<int>();
                for (int j = 0; j < Board.Size; j++)
                {
                    var (row, col) = isRowBased ? (i, j) : (j, i);
                    if (board.GetCell(row, col).HasValue) continue;
                    if (candidates.GetCandidates(row, col).Contains(digit))
                        positions.Add(j);
                }

                if (positions.Count >= 2 && positions.Count <= _size)
                    lines.Add((i, positions));
            }

            if (lines.Count < _size) continue;

            foreach (var combo in Combinations(lines, _size))
            {
                var unionPositions = new HashSet<int>();
                foreach (var line in combo)
                    unionPositions.UnionWith(line.positions);

                if (unionPositions.Count != _size) continue;

                var comboIndices = new HashSet<int>(combo.Select(l => l.index));
                bool changed = false;

                foreach (var pos in unionPositions)
                {
                    for (int i = 0; i < Board.Size; i++)
                    {
                        if (comboIndices.Contains(i)) continue;

                        var (row, col) = isRowBased ? (i, pos) : (pos, i);
                        if (board.GetCell(row, col).HasValue) continue;

                        if (candidates.EliminateCandidate(row, col, digit))
                            changed = true;
                    }
                }

                if (changed) return true;
            }
        }
        return false;
    }

    private static IEnumerable<List<(int index, HashSet<int> positions)>> Combinations(
        List<(int index, HashSet<int> positions)> items, int k)
        => CombinationsRecursive(items, k, 0);

    private static IEnumerable<List<(int index, HashSet<int> positions)>> CombinationsRecursive(
        List<(int index, HashSet<int> positions)> items, int k, int start)
    {
        if (k == 0)
        {
            yield return new List<(int, HashSet<int>)>();
            yield break;
        }

        for (int i = start; i <= items.Count - k; i++)
        {
            foreach (var rest in CombinationsRecursive(items, k - 1, i + 1))
            {
                var combo = new List<(int, HashSet<int>)> { items[i] };
                combo.AddRange(rest);
                yield return combo;
            }
        }
    }
}