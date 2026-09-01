using Sudoku.Models;

namespace Sudoku.Solvers;

/// <summary>
/// ケージの合計条件と各セルの候補を組み合わせて、
/// 実際に成立可能な数字配置を列挙するためのロジック。
/// </summary>
public static class CageCombinatorics
{
    public sealed record CageAnalysis(
        IReadOnlyList<(int Row, int Col)> Remaining,
        IReadOnlyList<IReadOnlyList<int>> Assignments);

    /// <summary>
/// 既存の45の法則・イニー/アウティー・キラーペア/トリプル向けの
/// 旧API互換版。
/// 各セルの候補は考慮せず、ケージの合計条件だけから
/// 数字の組み合わせを求める。
/// </summary>
public static (
    List<(int Row, int Col)> Remaining,
    List<HashSet<int>> Combos)
    AnalyzeCage(
        Board board,
        IReadOnlyList<(int Row, int Col)> cells,
        int targetSum)
{
    var remaining = new List<(int Row, int Col)>();
    var usedDigits = new HashSet<int>();
    int usedSum = 0;

    foreach (var (row, col) in cells)
    {
        var cell = board.GetCell(row, col);

        if (cell.HasValue)
        {
            int value = cell.Value!.Value;

            if (value < 1 || value > Board.Size)
                return (remaining, new List<HashSet<int>>());

            if (!usedDigits.Add(value))
                return (remaining, new List<HashSet<int>>());

            usedSum += value;
        }
        else
        {
            remaining.Add((row, col));
        }
    }

    int remainingSum = targetSum - usedSum;

    var availableDigits = Enumerable
        .Range(1, Board.Size)
        .Where(d => !usedDigits.Contains(d))
        .ToList();

    var combos = new List<HashSet<int>>();

    if (remaining.Count > 0 && remainingSum >= 0)
    {
        GenerateCombos(
            availableDigits,
            0,
            remaining.Count,
            remainingSum,
            new HashSet<int>(),
            combos);
    }

    return (remaining, combos);
}

    /// <summary>
    /// 数字の組み合わせ群に登場する全数字の和集合を返す。
    /// 既存の45の法則・イニー/アウティー・キラーペア/トリプルで使用。
    /// </summary>
    public static HashSet<int> UnionDigits(
        IReadOnlyList<HashSet<int>> combos)
    {
        var union = new HashSet<int>();

        foreach (var combo in combos)
            union.UnionWith(combo);

        return union;
    }

    private static void GenerateCombos(
    List<int> digits,
    int start,
    int size,
    int remainingSum,
    HashSet<int> current,
    List<HashSet<int>> results)
    {
        if (current.Count == size)
        {
            if (remainingSum == 0)
            {
                results.Add(new HashSet<int>(current));
            }

            return;
        }

        // 残りマス数より候補が足りない
        if (digits.Count - start < size - current.Count)
            return;

        for (int i = start; i < digits.Count; i++)
        {
            int digit = digits[i];

            // 昇順なので、これ以降も target を超える
            if (digit > remainingSum)
                break;

            current.Add(digit);

            GenerateCombos(
                digits,
                i + 1,
                size,
                remainingSum - digit,
                current,
                results);

            current.Remove(digit);
        }
    }


    /// <summary>
    /// ケージ内の未確定セルについて、
    /// 「各セルの候補」「数字重複禁止」「合計値」を同時に満たす
    /// 全配置を列挙する。
    ///
    /// Assignment の各要素は Remaining と同じ順番で対応する。
    /// </summary>
    public static CageAnalysis AnalyzeCage(
        Board board,
        CandidateGrid candidates,
        IReadOnlyList<(int Row, int Col)> cells,
        int targetSum,
        CancellationToken cancellationToken = default)
    {
        var remaining = new List<(int Row, int Col)>();

        int fixedSum = 0;
        var usedDigits = new bool[Board.Size + 1];

        foreach (var (row, col) in cells)
        {
            var cell = board.GetCell(row, col);

            if (cell.HasValue)
            {
                int value = cell.Value!.Value;

                if (value < 1 || value > Board.Size)
                    return new CageAnalysis(remaining, []);

                if (usedDigits[value])
                    return new CageAnalysis(remaining, []);

                usedDigits[value] = true;
                fixedSum += value;
            }
            else
            {
                remaining.Add((row, col));
            }
        }

        if (remaining.Count == 0)
            return new CageAnalysis(remaining, []);

        int remainingSum = targetSum - fixedSum;

        if (remainingSum <= 0)
            return new CageAnalysis(remaining, []);

        // 各セルの候補を取得。
        var cellCandidates = new List<int[]>(remaining.Count);

        foreach (var (row, col) in remaining)
        {
            var values = candidates
                .GetCandidates(row, col)
                .Where(d => !usedDigits[d])
                .OrderBy(d => d)
                .ToArray();

            if (values.Length == 0)
                return new CageAnalysis(remaining, []);

            cellCandidates.Add(values);
        }

        // 候補数が少ないセルから処理することで探索量を減らす。
        var order = Enumerable
            .Range(0, remaining.Count)
            .OrderBy(i => cellCandidates[i].Length)
            .ToArray();

        var orderedCandidates = order
            .Select(i => cellCandidates[i])
            .ToArray();

        var assignments = new List<IReadOnlyList<int>>();

        var current = new int[remaining.Count];

        Search(
            depth: 0,
            currentSum: 0,
            remainingSum,
            usedDigits,
            orderedCandidates,
            order,
            current,
            assignments,
            cancellationToken);

        return new CageAnalysis(remaining, assignments);
    }

    private static void Search(
        int depth,
        int currentSum,
        int targetSum,
        bool[] usedDigits,
        int[][] candidates,
        int[] order,
        int[] current,
        List<IReadOnlyList<int>> results,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (depth == candidates.Length)
        {
            if (currentSum == targetSum)
            {
                var assignment = new int[current.Length];

                for (int i = 0; i < order.Length; i++)
                {
                    assignment[order[i]] = current[i];
                }

                results.Add(assignment);
            }

            return;
        }

        var options = candidates[depth];

        foreach (int digit in options)
        {
            if (usedDigits[digit])
                continue;

            int nextSum = currentSum + digit;

            if (nextSum > targetSum)
                continue;

            // 残りセルで作れる最小・最大値による枝刈り。
            int minPossible = nextSum;
            int maxPossible = nextSum;

            for (int i = depth + 1; i < candidates.Length; i++)
            {
                int min = int.MaxValue;
                int max = int.MinValue;

                foreach (int d in candidates[i])
                {
                    if (usedDigits[d] || d == digit)
                        continue;

                    min = Math.Min(min, d);
                    max = Math.Max(max, d);
                }

                if (min == int.MaxValue)
                {
                    minPossible = int.MaxValue;
                    break;
                }

                minPossible += min;
                maxPossible += max;
            }

            if (minPossible > targetSum)
                continue;

            if (maxPossible < targetSum)
                continue;

            usedDigits[digit] = true;
            current[depth] = digit;

            Search(
                depth + 1,
                nextSum,
                targetSum,
                usedDigits,
                candidates,
                order,
                current,
                results,
                cancellationToken);

            usedDigits[digit] = false;
        }
    }

    /// <summary>
    /// 各セルごとに、成立するAssignmentに登場する数字だけを返す。
    /// </summary>
    public static HashSet<int>[] GetAllowedDigits(
        CageAnalysis analysis)
    {
        var allowed = new HashSet<int>[analysis.Remaining.Count];

        for (int i = 0; i < allowed.Length; i++)
            allowed[i] = new HashSet<int>();

        foreach (var assignment in analysis.Assignments)
        {
            for (int i = 0; i < assignment.Count; i++)
                allowed[i].Add(assignment[i]);
        }

        return allowed;
    }

    /// <summary>
    /// AnalyzeCage（候補ベース版）の呼び出し結果をキャッシュする。
    ///
    /// HumanSolverのメインループは候補を1つ絞り込むたびに全テクニックを最初から
    /// 試行し直すため、内容が変わっていない同一ケージ（または仮想ケージ）に対して
    /// AnalyzeCageが極めて高頻度に呼び出される。
    /// 対象セルの確定値・候補バージョンが前回と一致する場合は再計算をスキップする。
    /// </summary>
    public sealed class CageAnalysisCache
    {
        private readonly Dictionary<object, (int InstanceId, long VersionKey, CageCombinatorics.CageAnalysis Result)> _cache = new();

        public CageCombinatorics.CageAnalysis GetOrAnalyze(
            object cacheKey,
            Board board,
            CandidateGrid candidates,
            IReadOnlyList<(int Row, int Col)> cells,
            int targetSum)
        {
            long versionKey = ComputeVersionKey(board, candidates, cells);

            if (_cache.TryGetValue(cacheKey, out var cached) &&
                cached.InstanceId == candidates.InstanceId &&
                cached.VersionKey == versionKey)
            {
                return cached.Result;
            }

            var result = CageCombinatorics.AnalyzeCage(board, candidates, cells, targetSum);
            _cache[cacheKey] = (candidates.InstanceId, versionKey, result);
            return result;
        }

        private static long ComputeVersionKey(
            Board board,
            CandidateGrid candidates,
            IReadOnlyList<(int Row, int Col)> cells)
        {
            unchecked
            {
                long hash = 17;
                foreach (var (row, col) in cells)
                {
                    var cell = board.GetCell(row, col);
                    int valuePart = cell.HasValue ? cell.Value!.Value : 0;
                    int versionPart = candidates.GetVersion(row, col);
                    hash = hash * 31 + valuePart;
                    hash = hash * 31 + versionPart;
                }
                return hash;
            }
        }
    }
}