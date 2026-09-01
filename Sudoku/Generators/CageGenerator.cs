using Sudoku.Models;
using Sudoku.Solvers;
using System.Diagnostics;

namespace Sudoku.Generators;

/// <summary>
/// 完成盤面をキラーナンプレの合法なケージへ分割する生成器。
///
/// 重要な方針:
/// - 「ランダムマージ（Union-Find）」方式を採用。
/// - 全81セルを個別ケージとして初期化し、隣接ペアをランダム順に統合していく。
/// - バックトラック不要。マージできないペアはスキップするだけ。
/// - デッドロックなし。マージに失敗しても元のケージがそのまま残る。
/// - O(n) でグリッド全体のパーティションが完了（n は隣接ペア数 ≈ 144）。
/// - 同一数字禁止と連結性は統合段階で常に保証する。
/// </summary>
public sealed class CageGenerator
{
    private readonly Random _random;

    private const int CellCount = Board.Size * Board.Size; // 81
    private const int MaxCageSize = 9;

    /// <summary>
    /// 難易度ごとのケージ分割パラメータ。
    ///   MaxSize    : 1つのケージに含められるセルの上限
    ///   MinAvg     : ケージの平均サイズ（下限）。これを下回ったら再試行
    ///   MaxAvg     : ケージの平均サイズ（上限）。これを上回ったら再試行
    ///   MaxSingles : 単セルケージの上限数
    /// </summary>
    private static readonly Dictionary<
        Difficulty,
        (
            int MaxSize,
            double MinAvg,
            double TargetAvg,
            double MaxAvg,
            int MaxSingles,
            int MaxSize5
        )>
        DifficultyParams = new()
        {
            [Difficulty.Easy] =
                (
                    MaxSize: 4,
                    MinAvg: 1.5,
                    TargetAvg: 1.8,
                    MaxAvg: 2.5,
                    MaxSingles: 20,
                    MaxSize5: 0
                ),

            [Difficulty.Normal] =
                (
                    MaxSize: 5,
                    MinAvg: 2.0,
                    TargetAvg: 2.3,
                    MaxAvg: 3.0,
                    MaxSingles: 14,
                    MaxSize5: 3
                ),

            [Difficulty.Hard] =
                (
                    MaxSize: 5,
                    MinAvg: 2.2,
                    TargetAvg: 2.6,
                    MaxAvg: 3.4,
                    MaxSingles: 9,
                    MaxSize5: 4
                ),

            [Difficulty.Expert] =
                (
                    MaxSize: 7,
                    MinAvg: 3.0,
                    TargetAvg: 3.8,
                    MaxAvg: 5.0,
                    MaxSingles: 5,
                    MaxSize5: 8
                ),

            [Difficulty.Master] =
                (
                    MaxSize: 8,
                    MinAvg: 3.5,
                    TargetAvg: 4.2,
                    MaxAvg: 5.5,
                    MaxSingles: 3,
                    MaxSize5: 12
                ),
        };

    /// <summary>
    /// CageGenerator 1回の呼び出しで、パーティションの再試行上限。
    /// Union-Find方式は1回の分割が非常に高速（< 1ms）なので、多くの試行が可能。
    /// </summary>
    private const int MaxPartitionAttempts = 200;

    /// <summary>
    /// 全隣接ペア（辺を共有するセルの組み合わせ）を事前計算して保持。
    /// </summary>
    private static readonly (int A, int B)[] AllEdges = BuildAllEdges();

    public CageGenerator(Random? random = null)
    {
        _random = random ?? new Random();
    }

    public List<Cage>? GenerateCages(
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

        var param = DifficultyParams[difficulty];
        var stopwatch = Stopwatch.StartNew();

        cancellationToken.ThrowIfCancellationRequested();

        for (int attempt = 1; attempt <= MaxPartitionAttempts; attempt++)
        {
            if (stopwatch.ElapsedMilliseconds >= budgetMs)
                break;

            cancellationToken.ThrowIfCancellationRequested();

            var cageIndices = TryRandomMergePartition(
                digits,
                param.MaxSize,
                param.MinAvg,
                param.TargetAvg,
                param.MaxAvg,
                param.MaxSingles,
                param.MaxSize5);

            if (cageIndices is null)
                continue;

            var result = CreateCages(cageIndices, digits);
            WriteDebugInfo(result);
            return result;
        }

        return null;
    }

    // ================================================================
    // ランダムマージ（Union-Find）方式
    // ================================================================

    /// <summary>
    /// 全81セルを個別ケージとして初期化し、
    /// 隣接ペアをランダム順に統合していくことでケージを構築する。
    ///
    /// 統合の条件:
    ///   1. 統合後のサイズが MaxSize 以下
    ///   2. 統合後のケージ内に同じ数字が存在しない
    ///
    /// 統合中に難易度ごとの目標平均サイズへ近づける。
    /// 単セルを含む統合を優先し、終盤では小さいケージ同士を優先して
    /// サイズ上限による行き詰まりを抑える。
    ///
    /// 最終的に平均サイズ・単セル数・目標平均サイズを検証し、
    /// 条件を満たさなければ null を返して再試行させる。
    /// </summary>
    private List<List<int>>? TryRandomMergePartition(
        int[] digits,
        int maxSize,
        double minAvg,
        double targetAvg,
        double maxAvg,
        int maxSingles,
        int maxSize5)
    {
        // ----- Union-Find 初期化 -----
        var parent = new int[CellCount];
        var rank = new int[CellCount];
        var size = new int[CellCount];
        var digitMask = new int[CellCount];

        for (int i = 0; i < CellCount; i++)
        {
            parent[i] = i;
            rank[i] = 0;
            size[i] = 1;
            digitMask[i] = 1 << (digits[i] - 1);
        }

        int cageCount = CellCount;

        // ------------------------------------------------------------
        // 目標:
        //   平均サイズを TargetAvg 付近まで上げる。
        //
        // 81セルなので、
        //   average = 81 / cageCount
        //
        // TargetAvg = 3.1 なら、
        //   81 / 3.1 ≒ 26.1
        //
        // したがって、およそ26ケージを目標にする。
        // ------------------------------------------------------------
        int targetCageCount =
            Math.Max(
                1,
                (int)Math.Round(
                    CellCount / targetAvg,
                    MidpointRounding.AwayFromZero));

        // ------------------------------------------------------------
        // 同じ辺だけで一度しか試さないと、
        // ランダム順序によってはマージ可能な辺を取り逃がす。
        //
        // そこで、目標に届くまで最大8パスする。
        // 144辺 × 8パスでも十分小さい。
        // ------------------------------------------------------------
        const int MaxMergePasses = 8;

        for (int pass = 0; pass < MaxMergePasses; pass++)
        {
            if (cageCount <= targetCageCount)
                break;

            var edgeIndices =
                ShuffledEdgeIndices();

            bool mergedThisPass = false;

            foreach (int edgeIdx in edgeIndices)
            {
                if (cageCount <= targetCageCount)
                    break;

                var (a, b) = AllEdges[edgeIdx];

                int rootA = Find(parent, a);
                int rootB = Find(parent, b);

                if (rootA == rootB)
                    continue;

                int newSize =
                    size[rootA] +
                    size[rootB];

                if (newSize > maxSize)
                    continue;

                if ((digitMask[rootA] & digitMask[rootB]) != 0)
                    continue;

                if (newSize == 5)
                {
                    int currentSize5Count =
                        CountCagesOfSize(
                            parent,
                            size,
                            5);

                    if (currentSize5Count >= maxSize5)
                        continue;
                }

                // --------------------------------------------------------
                // 単セルを優先的に減らす。
                //
                // どちらかが単セルなら、そのマージを優先する。
                // 現在のforeach順自体がランダムなので、
                // 「単セルを含む辺」を見つけた場合は即マージする。
                // --------------------------------------------------------
                // --------------------------------------------------------
                // 単セルを優先的に減らす。
                //
                // どちらかが単セルなら、そのマージを優先する。
                // 現在のforeach順自体がランダムなので、
                // 「単セルを含む辺」を見つけた場合は即マージする。
                // --------------------------------------------------------
                bool hasSingle =
                    size[rootA] == 1 ||
                    size[rootB] == 1;

                if (hasSingle)
                {
                    Union(
                        parent,
                        rank,
                        size,
                        digitMask,
                        rootA,
                        rootB);

                    cageCount--;
                    mergedThisPass = true;
                    continue;
                }

                // --------------------------------------------------------
                // すでに単セルが少なくなった後は、
                // 小さなケージ同士を優先して統合する。
                //
                // 大きなケージを先に作りすぎると、
                // 終盤でサイズ上限に引っ掛かりやすくなる。
                // --------------------------------------------------------
                if (newSize >= 5 &&
                    cageCount > targetCageCount + 2)
                {
                    continue;
                }

                if (newSize == 5)
                {
                    int currentSize5Count = CountCagesOfSize(
                        parent,
                        size,
                        5);

                    if (currentSize5Count >= maxSize5)
                        continue;
                }

                Union(
                    parent,
                    rank,
                    size,
                    digitMask,
                    rootA,
                    rootB);

                cageCount--;
                mergedThisPass = true;
            }

            if (!mergedThisPass)
                break;
        }

        // ------------------------------------------------------------
        // 単セル解消専用パス。
        //
        // 上のメインループは cageCount <= targetCageCount に達した時点で
        // 即座に打ち切られるため、目標ケージ数へ到達していても
        // 単セルが大量に残存するケースがある。この状態は下のValidateAndExtract
        // の singles 上限チェックで必ず弾かれ、再試行を無駄に繰り返す原因になる。
        //
        // そこで、目標ケージ数への到達可否とは無関係に、
        // 残っている単セルを maxSingles を満たすまで追加でマージする。
        // ------------------------------------------------------------
        const int MaxSingleCleanupPasses = 8;

        for (int cleanupPass = 0; cleanupPass < MaxSingleCleanupPasses; cleanupPass++)
        {
            if (CountSingles(parent, size) <= maxSingles)
                break;

            var edgeIndices = ShuffledEdgeIndices();
            bool mergedThisPass = false;

            foreach (int edgeIdx in edgeIndices)
            {
                var (a, b) = AllEdges[edgeIdx];

                int rootA = Find(parent, a);
                int rootB = Find(parent, b);

                if (rootA == rootB)
                    continue;

                bool hasSingle = size[rootA] == 1 || size[rootB] == 1;
                if (!hasSingle)
                    continue;

                int newSize =
                    size[rootA] +
                    size[rootB];

                if (newSize > maxSize)
                    continue;

                if ((digitMask[rootA] & digitMask[rootB]) != 0)
                    continue;

                if (newSize == 5)
                {
                    int currentSize5Count =
                        CountCagesOfSize(
                            parent,
                            size,
                            5);

                    if (currentSize5Count >= maxSize5)
                        continue;
                }

                Union(parent, rank, size, digitMask, rootA, rootB);
                cageCount--;
                mergedThisPass = true;

                if (CountSingles(parent, size) <= maxSingles)
                    break;
            }

            if (!mergedThisPass)
                break;
        }

        // ------------------------------------------------------------
        // 最終結果を抽出
        // ------------------------------------------------------------
        var cages =
            ValidateAndExtract(
                parent,
                size,
                maxSingles,
                minAvg,
                maxAvg);

        if (cages is null)
            return null;

        double avgSize =
            (double)CellCount / cages.Count;

        int singles =
            cages.Count(c => c.Count == 1);

        int size5Count =
            cages.Count(c => c.Count == 5);

        const double TargetTolerance = 0.5;

        if (Math.Abs(avgSize - targetAvg) > TargetTolerance)
            return null;

        if (singles > maxSingles)
            return null;

        if (size5Count > maxSize5)
            return null;

        // 直前の2条件を通過している以上、この時点でAvgSize・Singlesは
        // 必ず許容範囲内のはず。生成ログの実測値がこの前提と食い違う場合、
        // 呼び出し元でparamの取り違えや競合が起きている可能性があるため、
        // デバッグビルドで即座に検知できるようにしておく。
        System.Diagnostics.Debug.Assert(
            Math.Abs(avgSize - targetAvg) <= TargetTolerance,
            $"AvgSize({avgSize:F2})がTargetAvg({targetAvg:F2})±{TargetTolerance}の範囲外です。");
        System.Diagnostics.Debug.Assert(
            singles <= maxSingles,
            $"Singles({singles})がMaxSingles({maxSingles})を超えています。");

        return cages;
    }

    /// <summary>
    /// Union-Find: Find（経路圧縮付き）
    /// </summary>
    private static int Find(int[] parent, int x)
    {
        while (parent[x] != x)
        {
            parent[x] = parent[parent[x]]; // 経路圧縮
            x = parent[x];
        }
        return x;
    }

    /// <summary>
    /// Union-Find: Union（ランクによる統合 + サイズ・数字マスクの伝播）
    /// </summary>
    private static void Union(
        int[] parent,
        int[] rank,
        int[] size,
        int[] digitMask,
        int rootA,
        int rootB)
    {
        if (rank[rootA] < rank[rootB])
            (rootA, rootB) = (rootB, rootA);

        parent[rootB] = rootA;
        size[rootA] += size[rootB];
        digitMask[rootA] |= digitMask[rootB];

        if (rank[rootA] == rank[rootB])
            rank[rootA]++;
    }

    /// <summary>
    /// 現在のUnion-Find状態における単セルケージ（サイズ1のケージ）の数を数える。
    /// </summary>
    private static int CountSingles(int[] parent, int[] size)
    {
        int count = 0;
        var seenRoots = new HashSet<int>();

        for (int i = 0; i < CellCount; i++)
        {
            int root = Find(parent, i);
            if (seenRoots.Add(root) && size[root] == 1)
                count++;
        }

        return count;
    }

    private static int CountCagesOfSize(
    int[] parent,
    int[] size,
    int targetSize)
    {
        int count = 0;
        var seenRoots = new HashSet<int>();

        for (int i = 0; i < CellCount; i++)
        {
            int root = Find(parent, i);

            if (!seenRoots.Add(root))
                continue;

            if (size[root] == targetSize)
                count++;
        }

        return count;
    }

    /// <summary>
    /// Union-Find の結果からケージリストを抽出し、構造を検証する。
    /// 基準を満たさなければ null を返す。
    /// </summary>
    private static List<List<int>>? ValidateAndExtract(
        int[] parent,
        int[] size,
        int maxSingles,
        double minAvg,
        double maxAvg)
    {
        // ケージごとにセルを集める
        var cageMap = new Dictionary<int, List<int>>();

        for (int i = 0; i < CellCount; i++)
        {
            int root = Find(parent, i);

            if (!cageMap.TryGetValue(root, out var list))
            {
                list = new List<int>();
                cageMap[root] = list;
            }

            list.Add(i);
        }

        var cages = cageMap.Values.ToList();

        // ----- 構造チェック -----
        int singles = cages.Count(c => c.Count == 1);

        if (singles > maxSingles)
            return null;

        double avgSize = (double)CellCount / cages.Count;

        if (avgSize < minAvg || avgSize > maxAvg)
            return null;

        return cages;
    }

    /// <summary>
    /// 辺のインデックス配列をシャッフルして返す。
    /// 毎回配列を新規作成してシャッフルする（Union-Find 方式では辺の順序がケージ構造を決定する）。
    /// </summary>
    private int[] ShuffledEdgeIndices()
    {
        var indices = new int[AllEdges.Length];
        for (int i = 0; i < indices.Length; i++)
            indices[i] = i;

        for (int i = indices.Length - 1; i > 0; i--)
        {
            int j = _random.Next(i + 1);
            (indices[i], indices[j]) = (indices[j], indices[i]);
        }

        return indices;
    }

    // ================================================================
    // ヘルパー
    // ================================================================

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

    private static int ToIndex(int row, int col)
        => row * Board.Size + col;

    private static (int Row, int Col) FromIndex(int index)
        => (index / Board.Size, index % Board.Size);

    /// <summary>
    /// グリッド上の全隣接ペア（辺共有）を事前計算する。
    /// 9x9 グリッドでは水平方向 8*9=72、垂直方向 9*8=72、合計 144 ペア。
    /// </summary>
    private static (int A, int B)[] BuildAllEdges()
    {
        var edges = new List<(int, int)>();

        for (int row = 0; row < Board.Size; row++)
        {
            for (int col = 0; col < Board.Size; col++)
            {
                int index = ToIndex(row, col);

                // 右隣
                if (col < Board.Size - 1)
                    edges.Add((index, ToIndex(row, col + 1)));

                // 下隣
                if (row < Board.Size - 1)
                    edges.Add((index, ToIndex(row + 1, col)));
            }
        }

        return edges.ToArray();
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