using Sudoku.Models;
using Sudoku.Solvers;
using System.Diagnostics;

namespace Sudoku.Generators;

/// <summary>
/// 完成盤面をキラーナンプレの合法なケージへ分割する生成器。
///
/// 重要な方針:
/// - ケージ生成と難易度判定を分離する。
/// - 盤面全体の巨大なバックトラックは行わない。
/// - 1回の分割失敗は「その試行だけ破棄」し、盤面全体を高速に再生成する。
/// - 1ケージ内部は frontier（ケージ全体に接する未使用セル）を使った DFS で厳密に探索する。
/// - 同一数字禁止と連結性は生成段階で常に保証する。
/// </summary>
public sealed class CageGenerator
{
    private const int MaxCageCandidates = 6;
    private static bool IsBudgetExceeded(
    Stopwatch stopwatch,
    int budgetMs)
    {
        return stopwatch.ElapsedMilliseconds >= budgetMs;
    }

    private readonly Random _random;

    private const int CellCount = Board.Size * Board.Size;
    private const int MaxCageSize = 8;

    // Hard はサイズ2～5を中心にする。
    // サイズ6～8を極端に増やすと Cage Forced Combination 側の探索量が増えやすい。
    private static readonly Dictionary<Difficulty, int[]> SizeWeights = new()
    {
        [Difficulty.Easy] = new[] { 35, 35, 20, 7, 3, 0, 0, 0 },
        [Difficulty.Normal] = new[] { 20, 35, 25, 12, 6, 2, 0, 0 },
        [Difficulty.Hard] = new[] { 2, 14, 28, 30, 22, 12, 0, 0 },
        [Difficulty.Expert] = new[] { 0, 8, 15, 18, 20, 18, 13, 8 },
        [Difficulty.Master] = new[] { 0, 3, 8, 14, 18, 20, 20, 17 },
    };

    private static readonly int[][] Neighbors = BuildNeighbors();

    // CageGenerator 1回の呼び出しで、自力再試行する上限。
    // 外側の10秒タイムアウトとは別管理。
    private const int MaxPartitionAttempts = 32;

    public CageGenerator(Random? random = null)
    {
        _random = random ?? new Random();
    }

    public List<Cage> GenerateCages(
    Board solvedBoard,
    Difficulty difficulty,
    int budgetMs = 500,
    CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(solvedBoard);

        var digits = ReadDigits(solvedBoard);
        ValidateDigits(digits);

        if (budgetMs <= 0)
            throw new ArgumentOutOfRangeException(nameof(budgetMs));

        var weights = SizeWeights[difficulty];
        var stopwatch = Stopwatch.StartNew();
        int effectiveBudgetMs = Math.Max(1, budgetMs);
        int maxSingles = GetMaxSingles(difficulty);

        cancellationToken.ThrowIfCancellationRequested();

        // サイズ計画を固定しない。
        // 各時点の残り領域に対して、実行可能なサイズだけを重み付きで選ぶ。
        // これが盤面形状による詰みを大幅に減らす。
        for (int attempt = 1; attempt <= MaxPartitionAttempts; attempt++)
        {
            if (IsBudgetExceeded(stopwatch, effectiveBudgetMs))
                break;

            var unassigned = new bool[CellCount];
            Array.Fill(unassigned, true);

            var cages = new List<List<int>>();

            if (TryPartition(
                unassigned,
                digits,
                weights,
                cages,
                stopwatch,
                effectiveBudgetMs,
                maxSingles,
                cancellationToken))
            {
                var result = CreateCages(cages, digits);
                WriteDebugInfo(result);
                return result;
            }
        }

        throw new InvalidOperationException(
            $"ケージ分割に失敗しました。{MaxPartitionAttempts}回の再試行でも合法な分割を作れませんでした。");
    }

    private int[] ReadDigits(Board solvedBoard)
    {
        var digits = new int[CellCount];

        for (int row = 0; row < Board.Size; row++)
        {
            for (int col = 0; col < Board.Size; col++)
            {
                var value = solvedBoard.GetCell(row, col).Value;
                if (!value.HasValue)
                    throw new InvalidOperationException("完成盤面に空セルがあります。");

                digits[ToIndex(row, col)] = value.Value;
            }
        }

        return digits;
    }

    private static void ValidateDigits(int[] digits)
    {
        if (digits.Length != CellCount)
            throw new InvalidOperationException("盤面サイズが不正です。");

        for (int i = 0; i < digits.Length; i++)
        {
            if (digits[i] < 1 || digits[i] > Board.Size)
                throw new InvalidOperationException($"セル {i} の数字が不正です: {digits[i]}");
        }
    }

    /// <summary>
    /// 残り領域に応じてサイズを動的に決めながら盤面を分割する。
    /// 1回の試行ではバックトラックせず、失敗したら盤面全体を再生成する。
    /// </summary>
    private bool TryPartition(
        bool[] unassigned,
        int[] digits,
        int[] weights,
        List<List<int>> cages,
        Stopwatch stopwatch,
        int budgetMs,
        int maxSingles,
        CancellationToken cancellationToken)
    {

        while (!IsBudgetExceeded(stopwatch, budgetMs))
        {
            cancellationToken.ThrowIfCancellationRequested();

            int remaining = unassigned.Count(x => x);

            if (remaining == 0)
                return true;

            int currentSingles =
                cages.Count(c => c.Count == 1);

            int maxSize = GetMaxTargetSize(remaining);

            bool placed = false;

            // ---------------------------------------------------------
            // 1. まず size >= 2 だけを試す。
            // ---------------------------------------------------------
            var sizeCandidates =
                BuildWeightedSizeCandidates(
                    remaining,
                    maxSize,
                    weights,
                    allowSingle: false);

            var seedCandidates =
                FindSeedCandidates(
                    unassigned,
                    digits);

            foreach (int targetSize in sizeCandidates)
            {
                if (IsBudgetExceeded(stopwatch, budgetMs))
                    return false;

                foreach (int seed in seedCandidates)
                {
                    if (IsBudgetExceeded(stopwatch, budgetMs))
                        return false;

                    var cageCandidates =
                        GenerateCageCandidates(
                            seed,
                            targetSize,
                            unassigned,
                            digits,
                            stopwatch,
                            budgetMs,
                            cancellationToken);

                    foreach (var cage in cageCandidates)
                    {
                        if (IsBudgetExceeded(stopwatch, budgetMs))
                            return false;

                        if (!WouldKeepRemainingUseful(
                                cage,
                                unassigned,
                                digits,
                                cages.Count(c => c.Count == 1),
                                maxSingles))
                        {
                            continue;
                        }

                        Apply(cage, unassigned, false);
                        cages.Add(cage);
                        placed = true;
                        break;
                    }

                    if (placed)
                        break;
                }

                if (placed)
                    break;
            }

            if (placed)
                continue;

            // ---------------------------------------------------------
            // 2. size >= 2 が置けなかった場合。
            //    単セルにできるのは「他セルと合法に接続できないセル」だけ。
            // ---------------------------------------------------------
            if (currentSingles < maxSingles)
            {
                var forcedSingleSeeds =
                    FindForcedSingleCandidates(unassigned, digits);

                foreach (int seed in forcedSingleSeeds)
                {
                    if (IsBudgetExceeded(stopwatch, budgetMs))
                        return false;

                    var cage = new List<int>(1)
                    {
                        seed
                    };

                    Apply(cage, unassigned, false);
                    cages.Add(cage);
                    placed = true;
                    break;
                }
            }

            if (!placed)
                return false;
        }

        return false;
    }

    private static int GetMaxTargetSize(int remaining)
    {
        if (remaining <= 1)
            return 1;

        if (remaining <= 3)
            return remaining;

        if (remaining <= 8)
            return 3;

        if (remaining <= 18)
            return 4;

        if (remaining <= 30)
            return 5;

        return Math.Min(MaxCageSize, remaining);
    }

    private static int GetMaxSingles(Difficulty difficulty)
    {
        return difficulty switch
        {
            Difficulty.Easy => 15,
            Difficulty.Normal => 10,
            Difficulty.Hard => 7,
            Difficulty.Expert => 5,
            Difficulty.Master => 3,
            _ => 7
        };
    }

    private List<int> BuildWeightedSizeCandidates(
        int remaining,
        int maxSize,
        int[] weights,
        bool allowSingle)
    {
        var weighted = new List<(int Size, int Weight)>();

        for (int size = 1; size <= maxSize; size++)
        {
            // size1はallowSingle=falseなら候補に入れない
            if (size == 1 && !allowSingle)
                continue;

            int weight = weights[size - 1];

            if (weight <= 0)
                continue;

            // 最後に1セルだけ残すのは許可するが、
            // 巨大サイズで端数を作るのを少し抑える。
            int next = remaining - size;

            if (next == 1 && remaining > 3)
                weight = Math.Max(1, weight / 4);

            weighted.Add((size, weight));
        }

        // size1を許可する場合、
        // weight=0の難易度でもsize1を最後の候補として使えるようにする。
        if (allowSingle &&
            !weighted.Any(x => x.Size == 1))
        {
            weighted.Add((1, 1));
        }

        // remaining == 1 はsize1しか選択肢がない。
        if (remaining == 1 &&
            !weighted.Any(x => x.Size == 1))
        {
            weighted.Add((1, 1));
        }

        var result = new List<int>(weighted.Count);

        while (weighted.Count > 0)
        {
            int total = weighted.Sum(x => x.Weight);

            int roll = _random.Next(total);

            int index = 0;

            for (; index < weighted.Count; index++)
            {
                roll -= weighted[index].Weight;

                if (roll < 0)
                    break;
            }

            if (index >= weighted.Count)
                index = weighted.Count - 1;

            result.Add(weighted[index].Size);
            weighted.RemoveAt(index);
        }

        return result;
    }

    /// <summary>
    /// 低次数セルを優先してseed候補を返す。
    /// 実際にケージが作れるかは、候補生成時に判定する。
    /// </summary>
    private List<int> FindSeedCandidates(
        bool[] unassigned,
        int[] digits)
    {
        var candidates = new List<(int Cell, int Score)>();

        for (int cell = 0; cell < CellCount; cell++)
        {
            if (!unassigned[cell])
                continue;

            int expandableNeighbors = 0;
            int distinctNeighborDigits = 0;
            int neighborMask = 0;

            foreach (int neighbor in Neighbors[cell])
            {
                if (!unassigned[neighbor])
                    continue;

                if (digits[neighbor] == digits[cell])
                    continue;

                expandableNeighbors++;

                int bit = 1 << (digits[neighbor] - 1);
                if ((neighborMask & bit) == 0)
                {
                    neighborMask |= bit;
                    distinctNeighborDigits++;
                }
            }

            int score =
                expandableNeighbors * 10 +
                distinctNeighborDigits * 3;

            candidates.Add((cell, score));
        }

        candidates.Sort((a, b) =>
            a.Score.CompareTo(b.Score));

        int take = Math.Min(12, candidates.Count);

        var result = candidates
            .Take(take)
            .Select(x => x.Cell)
            .ToList();

        Shuffle(result);

        return result;
    }

    private List<int> FindForcedSingleCandidates(
    bool[] unassigned,
    int[] digits)
    {
        var result = new List<int>();

        for (int cell = 0; cell < CellCount; cell++)
        {
            if (!unassigned[cell])
                continue;

            bool hasCompatibleNeighbor = false;

            foreach (int neighbor in Neighbors[cell])
            {
                if (!unassigned[neighbor])
                    continue;

                if (digits[neighbor] == digits[cell])
                    continue;

                hasCompatibleNeighbor = true;
                break;
            }

            if (!hasCompatibleNeighbor)
                result.Add(cell);
        }

        Shuffle(result);

        return result;
    }

    /// <summary>
    /// ケージ内部の候補生成。
    /// currentだけではなく「ケージ全体のfrontier」から次セルを選ぶ。
    /// これによりL字・分岐型など、正しい連結形状を漏らしにくくする。
    /// </summary>
    private List<List<int>> GenerateCageCandidates(
        int seed,
        int targetSize,
        bool[] unassigned,
        int[] digits,
        Stopwatch stopwatch,
        int budgetMs,
        CancellationToken cancellationToken)
    {
        var results = new List<List<int>>(MaxCageCandidates);
        var seen = new HashSet<string>();

        var cage = new List<int>(targetSize);
        var usedCells = new bool[CellCount];
        var usedDigits = new bool[Board.Size + 1];

        CollectCageCandidates(
            seed,
            targetSize,
            unassigned,
            digits,
            cage,
            usedCells,
            usedDigits,
            results,
            seen,
            stopwatch,
            budgetMs,
            randomize: true,
            cancellationToken);

        // ランダム探索で見つからなかった場合のみ決定的探索
        if (results.Count == 0 &&
            !IsBudgetExceeded(stopwatch, budgetMs))
        {
            cage.Clear();
            Array.Clear(usedCells);
            Array.Clear(usedDigits);

            CollectCageCandidates(
                seed,
                targetSize,
                unassigned,
                digits,
                cage,
                usedCells,
                usedDigits,
                results,
                seen,
                stopwatch,
                budgetMs,
                randomize: false,
                cancellationToken);
        }

        results.Sort((a, b) =>
            ScoreRemainingBoundary(b, unassigned)
                .CompareTo(
                    ScoreRemainingBoundary(a, unassigned)));

        return results;
    }

    private void CollectCageCandidates(
        int current,
        int targetSize,
        bool[] unassigned,
        int[] digits,
        List<int> cage,
        bool[] usedCells,
        bool[] usedDigits,
        List<List<int>> results,
        HashSet<string> seen,
        Stopwatch stopwatch,
        int budgetMs,
        bool randomize,
        CancellationToken cancellationToken)
    {
        if (results.Count >= MaxCageCandidates)
            return;

        cancellationToken.ThrowIfCancellationRequested();

        if (IsBudgetExceeded(stopwatch, budgetMs))
            return;

        if (!unassigned[current] || usedCells[current])
            return;

        int digit = digits[current];

        if (usedDigits[digit])
            return;

        cage.Add(current);
        usedCells[current] = true;
        usedDigits[digit] = true;

        if (cage.Count == targetSize)
        {
            var result = new List<int>(cage);
            result.Sort();

            string key = string.Join(',', result);

            if (seen.Add(key))
                results.Add(result);

            cage.RemoveAt(cage.Count - 1);
            usedCells[current] = false;
            usedDigits[digit] = false;
            return;
        }

        var frontier = new HashSet<int>();

        foreach (int cell in cage)
        {
            if (IsBudgetExceeded(stopwatch, budgetMs))
                break;

            foreach (int neighbor in Neighbors[cell])
            {
                if (unassigned[neighbor] &&
                    !usedCells[neighbor])
                {
                    frontier.Add(neighbor);
                }
            }
        }

        var candidates = frontier
            .Where(c => !usedDigits[digits[c]])
            .ToList();

        if (randomize)
        {
            Shuffle(candidates);

            candidates.Sort((a, b) =>
            {
                int aScore =
                    CountExpandableNeighbors(
                        a,
                        unassigned,
                        usedCells,
                        usedDigits,
                        digits);

                int bScore =
                    CountExpandableNeighbors(
                        b,
                        unassigned,
                        usedCells,
                        usedDigits,
                        digits);

                return bScore.CompareTo(aScore);
            });
        }
        else
        {
            candidates.Sort();
        }

        foreach (int candidate in candidates)
        {
            if (results.Count >= MaxCageCandidates)
                break;

            if (IsBudgetExceeded(stopwatch, budgetMs))
                break;

            CollectCageCandidates(
                candidate,
                targetSize,
                unassigned,
                digits,
                cage,
                usedCells,
                usedDigits,
                results,
                seen,
                stopwatch,
                budgetMs,
                randomize,
                cancellationToken);
        }

        cage.RemoveAt(cage.Count - 1);
        usedCells[current] = false;
        usedDigits[digit] = false;
    }

    private static bool WouldKeepRemainingUseful(
    List<int> cage,
    bool[] unassigned,
    int[] digits,
    int currentSingles,
    int maxSingles)
    {
        var temp = (bool[])unassigned.Clone();

        Apply(cage, temp, false);

        int remaining = temp.Count(x => x);

        if (remaining == 0)
            return true;

        int forcedSingles = 0;

        for (int cell = 0; cell < CellCount; cell++)
        {
            if (!temp[cell])
                continue;

            bool hasCompatibleNeighbor = false;

            foreach (int neighbor in Neighbors[cell])
            {
                if (!temp[neighbor])
                    continue;

                if (digits[neighbor] == digits[cell])
                    continue;

                hasCompatibleNeighbor = true;
                break;
            }

            if (!hasCompatibleNeighbor)
                forcedSingles++;
        }

        // この配置によって、将来的に必要になる単セル数が
        // 許容数を超えるなら、このケージ配置を捨てる。
        if (currentSingles + forcedSingles > maxSingles)
            return false;

        return true;
    }

    private static int ScoreRemainingBoundary(List<int> cage, bool[] unassigned)
    {
        var set = new HashSet<int>(cage);
        int score = 0;

        foreach (int cell in cage)
        {
            foreach (int neighbor in Neighbors[cell])
            {
                if (unassigned[neighbor] && !set.Contains(neighbor))
                    score++;
            }
        }

        return score;
    }

    private static int CountExpandableNeighbors(
        int cell,
        bool[] unassigned,
        bool[] usedCells,
        bool[] usedDigits,
        int[] digits)
    {
        int count = 0;
        foreach (int neighbor in Neighbors[cell])
        {
            if (unassigned[neighbor]
                && !usedCells[neighbor]
                && !usedDigits[digits[neighbor]])
            {
                count++;
            }
        }

        return count;
    }

    private static void Apply(List<int> cage, bool[] unassigned, bool makeUnassigned)
    {
        foreach (int cell in cage)
            unassigned[cell] = makeUnassigned;
    }

    private static List<Cage> CreateCages(List<List<int>> cageIndexes, int[] digits)
    {
        var cages = new List<Cage>(cageIndexes.Count);

        foreach (var indexes in cageIndexes)
        {
            int sum = indexes.Sum(i => digits[i]);
            var cells = indexes.Select(FromIndex).ToList();
            cages.Add(new Cage(cells, sum));
        }

        return cages;
    }

    private void Shuffle<T>(IList<T> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = _random.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    private static int ToIndex(int row, int col)
        => row * Board.Size + col;

    private static (int Row, int Col) FromIndex(int index)
        => (index / Board.Size, index % Board.Size);

    private static int[][] BuildNeighbors()
    {
        var result = new int[CellCount][];

        for (int row = 0; row < Board.Size; row++)
        {
            for (int col = 0; col < Board.Size; col++)
            {
                int index = ToIndex(row, col);
                var neighbors = new List<int>(4);

                if (row > 0)
                    neighbors.Add(ToIndex(row - 1, col));
                if (row < Board.Size - 1)
                    neighbors.Add(ToIndex(row + 1, col));
                if (col > 0)
                    neighbors.Add(ToIndex(row, col - 1));
                if (col < Board.Size - 1)
                    neighbors.Add(ToIndex(row, col + 1));

                result[index] = neighbors.ToArray();
            }
        }

        return result;
    }

    private static void WriteDebugInfo(List<Cage> cages)
    {
        if (!SolverDiagnostics.VerboseLogging)
            return;

        var sizeCounts =
            cages
                .GroupBy(c => c.Cells.Count)
                .OrderBy(g => g.Key)
                .Select(g => $"Size{g.Key}={g.Count()}");

        double averageSize =
            cages.Count == 0
                ? 0
                : cages.Average(c => c.Cells.Count);

        int singleCount =
            cages.Count(c => c.Cells.Count == 1);

        System.Diagnostics.Debug.WriteLine(
            "[CageGenerator] " +
            string.Join(", ", sizeCounts) +
            $", CageCount={cages.Count}" +
            $", AvgSize={averageSize:F2}" +
            $", Singles={singleCount}");
    }
}