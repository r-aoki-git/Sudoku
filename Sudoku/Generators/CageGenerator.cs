using Sudoku.Models;
using Sudoku.Solvers;
using System.Diagnostics;

namespace Sudoku.Generators;

/// <summary>
/// 完成盤面をキラーナンプレの合法なケージへ分割する生成器。
///
/// 重要な方針:
/// - 「ランダムマージ（Union-Find）」方式。
/// - 全81セルを個別ケージとして初期化し、隣接ペアをランダム順に統合していく。
/// - バックトラック不要。マージできないペアはスキップするだけなのでデッドロックしない。
/// - O(n) でグリッド全体のパーティションが完了（n は隣接ペア数 = 144）。
/// - 同一数字禁止と連結性は統合段階で常に保証する。
///
/// 【ブロック跨ぎバイアス】
/// 平均ケージサイズを上げるほど盤面は難しくなるが、同時に唯一解になる確率が急落する。
/// 実測（各300回のパーティションのうち、唯一解になった数）:
///
///   平均サイズ | バイアスなし | バイアスあり
///        2.2  |      69     |     114
///        2.5  |      38     |      69
///        2.8  |      22     |      68
///        3.0  |      10     |      33
///        3.2  |       3     |      15
///
/// 1つのブロック内で閉じたケージは、そのブロックに対する制約をほとんど生まない。
/// 逆にブロックを跨ぐケージは「45の法則」に効くため、盤面を強く拘束する。
/// そこで、同一ブロック内で閉じてしまうマージを確率的に見送ることで、
/// 同じ平均サイズのまま唯一解率を2〜5倍に引き上げている。
/// 唯一解判定にかかる時間も同時に短くなる（強く拘束されるほど探索が早く終わるため）。
/// </summary>
public sealed class CageGenerator
{
    private readonly Random _random;

    private const int CellCount = Board.Size * Board.Size; // 81
    private const int EdgeCount = 144;                     // 横72 + 縦72

    /// <summary>
    /// 難易度ごとのケージ分割パラメータ。
    ///   MaxSize       : 1つのケージに含められるセルの上限
    ///   TargetAvg     : 目標平均ケージサイズ（81 / ケージ数）
    ///   AvgTolerance  : TargetAvg からの許容誤差
    ///   MaxSingles    : 単セルケージの上限数
    ///   BoxCrossBias  : 同一ブロック内で閉じるマージを見送る確率（0〜1）
    ///
    /// TargetAvg は「その難易度のスコア帯が最も出やすい平均サイズ」を実測して決めている。
    /// DifficultyScorer のスコア境界と対で調整すること。
    /// </summary>
    private readonly record struct PartitionParams(
        int MaxSize,
        double TargetAvg,
        double AvgTolerance,
        int MaxSingles,
        double BoxCrossBias);

    /// <summary>
    /// 各難易度のパラメータは、(平均サイズ × バイアス) を振って
    /// 「その難易度の盤面が1件出るまでの実測時間」が最小になる点を選んでいる
    /// （シングルスレッド、1セルあたり400回のパーティション）。
    ///
    ///   平均 | バイアス | Easy  Normal  Hard  Expert  Master   (1件あたりms)
    ///   1.8 |   0.70  |   15     -      -      -       -
    ///   2.2 |   0.85  |   75     65    156      -       -
    ///   2.4 |   0.85  |  238     95     90    371       -
    ///   2.6 |   0.85  | 2974    991    248    135     330
    ///   2.8 |   0.85  |    -   5613    936    468     374
    ///   3.0 |   0.85  |    -      -  12331   3083    6166
    ///
    /// 平均3.0以上は唯一解率が1割を切るうえ、唯一解判定そのものが重くなるため、
    /// どの難易度にとっても割に合わない。以前の実装が Expert=3.4 / Master=3.8 を
    /// 狙っていたのは、まさにこの領域であり、生成が事実上成立していなかった。
    /// 上位難易度は「ケージを大きくする」のではなく
    /// 「ブロックを跨がせてスコア帯で選別する」ことで作る。
    /// </summary>
    private static readonly Dictionary<Difficulty, PartitionParams> DifficultyParams = new()
    {
        [Difficulty.Easy] = new(
            MaxSize: 4,
            TargetAvg: 1.8,
            AvgTolerance: 0.15,
            MaxSingles: 26,
            BoxCrossBias: 0.70),

        [Difficulty.Normal] = new(
            MaxSize: 5,
            TargetAvg: 2.2,
            AvgTolerance: 0.15,
            MaxSingles: 18,
            BoxCrossBias: 0.85),

        [Difficulty.Hard] = new(
            MaxSize: 5,
            TargetAvg: 2.4,
            AvgTolerance: 0.15,
            MaxSingles: 14,
            BoxCrossBias: 0.85),

        [Difficulty.Expert] = new(
            MaxSize: 5,
            TargetAvg: 2.6,
            AvgTolerance: 0.15,
            MaxSingles: 11,
            BoxCrossBias: 0.85),

        [Difficulty.Master] = new(
            MaxSize: 5,
            TargetAvg: 2.8,
            AvgTolerance: 0.15,
            MaxSingles: 9,
            BoxCrossBias: 0.85),
    };

    /// <summary>
    /// CageGenerator 1回の呼び出しでのパーティション再試行上限。
    /// 1回の分割が 0.1ms 程度なので、多くの試行を許容できる。
    /// </summary>
    private const int MaxPartitionAttempts = 200;

    /// <summary>マージの試行パス数。1パスで全144辺をランダム順に1回ずつ検討する。</summary>
    private const int MaxMergePasses = 12;

    /// <summary>単セル解消専用パスの上限。</summary>
    private const int MaxSingleCleanupPasses = 8;

    /// <summary>全隣接ペア（辺を共有するセルの組み合わせ）を事前計算して保持。</summary>
    private static readonly (int A, int B)[] AllEdges = BuildAllEdges();

    /// <summary>各セルが属するブロックのビット。ブロック跨ぎ判定に使う。</summary>
    private static readonly int[] BoxBit = BuildBoxBits();

    // Union-Find の作業配列。
    // GenerateCages は1インスタンスにつき単一スレッドからしか呼ばれない
    // （各ワーカーが自分の CageGenerator を持つ）ため、毎回 new せず使い回す。
    private readonly int[] _parent = new int[CellCount];
    private readonly int[] _size = new int[CellCount];
    private readonly int[] _digitMask = new int[CellCount];
    private readonly int[] _boxMask = new int[CellCount];
    private readonly int[] _edgeOrder = new int[EdgeCount];

    public CageGenerator(Random? random = null)
    {
        _random = random ?? new Random();

        for (int i = 0; i < _edgeOrder.Length; i++)
            _edgeOrder[i] = i;
    }

    public List<Cage>? GenerateCages(
        Board solvedBoard,
        Difficulty difficulty,
        int budgetMs = 500,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(solvedBoard);

        if (budgetMs <= 0)
            throw new ArgumentOutOfRangeException(nameof(budgetMs));

        var digits = ReadDigits(solvedBoard);
        var param = DifficultyParams[difficulty];
        var stopwatch = Stopwatch.StartNew();

        cancellationToken.ThrowIfCancellationRequested();

        for (int attempt = 1; attempt <= MaxPartitionAttempts; attempt++)
        {
            if (stopwatch.ElapsedMilliseconds >= budgetMs)
                break;

            cancellationToken.ThrowIfCancellationRequested();

            var cageIndices = TryRandomMergePartition(digits, param);

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
    /// 全81セルを個別ケージとして初期化し、隣接ペアをランダム順に統合していく。
    ///
    /// 統合の条件:
    ///   1. 統合後のサイズが MaxSize 以下
    ///   2. 統合後のケージ内に同じ数字が存在しない
    ///   3. 同一ブロック内で閉じるマージは BoxCrossBias の確率で見送る
    ///
    /// 最終的に平均サイズと単セル数を検証し、条件を満たさなければ
    /// null を返して呼び出し側に再試行させる。
    /// </summary>
    private List<List<int>>? TryRandomMergePartition(
        int[] digits,
        PartitionParams param)
    {
        for (int i = 0; i < CellCount; i++)
        {
            _parent[i] = i;
            _size[i] = 1;
            _digitMask[i] = 1 << (digits[i] - 1);
            _boxMask[i] = BoxBit[i];
        }

        int cageCount = CellCount;

        // 81セルなので average = 81 / cageCount。
        // TargetAvg = 2.8 なら 81 / 2.8 ≒ 29 ケージを目標にする。
        int targetCageCount =
            Math.Max(
                1,
                (int)Math.Round(
                    CellCount / param.TargetAvg,
                    MidpointRounding.AwayFromZero));

        // ------------------------------------------------------------
        // メインのマージパス。
        // 1回のランダム順では統合可能な辺を取り逃がすため、
        // 目標ケージ数に届くまで複数パス回す。
        // ------------------------------------------------------------
        for (int pass = 0; pass < MaxMergePasses && cageCount > targetCageCount; pass++)
        {
            ShuffleEdgeOrder();

            bool mergedThisPass = false;

            foreach (int edgeIdx in _edgeOrder)
            {
                if (cageCount <= targetCageCount)
                    break;

                var (a, b) = AllEdges[edgeIdx];

                int rootA = Find(a);
                int rootB = Find(b);

                if (!CanMerge(rootA, rootB, param.MaxSize))
                    continue;

                // 単セルを含むマージは最優先で通す。
                // 単セルは実質的な「ヒント」なので、残すほど難易度が上がらない。
                bool hasSingle =
                    _size[rootA] == 1 ||
                    _size[rootB] == 1;

                if (!hasSingle &&
                    ShouldSkipForBoxBias(rootA, rootB, param.BoxCrossBias))
                {
                    continue;
                }

                Union(rootA, rootB);

                cageCount--;
                mergedThisPass = true;
            }

            if (!mergedThisPass)
                break;
        }

        // ------------------------------------------------------------
        // 単セル解消専用パス。
        //
        // メインループは cageCount <= targetCageCount に達した時点で
        // 打ち切られるため、目標ケージ数へ到達していても単セルが
        // 大量に残ることがある。その状態は下の検証で必ず弾かれ、
        // 再試行を無駄に繰り返す原因になるので、
        // 目標ケージ数とは無関係に、残った単セルを追加でマージする。
        // ------------------------------------------------------------
        for (int pass = 0; pass < MaxSingleCleanupPasses; pass++)
        {
            if (CountSingles() <= param.MaxSingles)
                break;

            ShuffleEdgeOrder();

            bool mergedThisPass = false;

            foreach (int edgeIdx in _edgeOrder)
            {
                var (a, b) = AllEdges[edgeIdx];

                int rootA = Find(a);
                int rootB = Find(b);

                if (!CanMerge(rootA, rootB, param.MaxSize))
                    continue;

                // 単セルを含まないマージはここでは行わない。
                // 平均サイズを目標から押し上げてしまうため。
                if (_size[rootA] != 1 && _size[rootB] != 1)
                    continue;

                Union(rootA, rootB);

                cageCount--;
                mergedThisPass = true;

                if (CountSingles() <= param.MaxSingles)
                    break;
            }

            if (!mergedThisPass)
                break;
        }

        // ------------------------------------------------------------
        // 検証して抽出
        // ------------------------------------------------------------
        double avgSize = (double)CellCount / cageCount;

        if (Math.Abs(avgSize - param.TargetAvg) > param.AvgTolerance)
            return null;

        if (CountSingles() > param.MaxSingles)
            return null;

        return ExtractCages();
    }

    /// <summary>ケージ2つを統合できるか（別ケージ・サイズ上限・数字重複）。</summary>
    private bool CanMerge(int rootA, int rootB, int maxSize)
    {
        if (rootA == rootB)
            return false;

        if (_size[rootA] + _size[rootB] > maxSize)
            return false;

        return (_digitMask[rootA] & _digitMask[rootB]) == 0;
    }

    /// <summary>
    /// 同一ブロック内で閉じたままになるマージを、BoxCrossBias の確率で見送る。
    /// ブロックを跨ぐケージは「45の法則」に効くため盤面を強く拘束し、
    /// 同じ平均サイズでも唯一解になりやすくなる。
    /// </summary>
    private bool ShouldSkipForBoxBias(int rootA, int rootB, double boxCrossBias)
    {
        if (boxCrossBias <= 0)
            return false;

        bool staysInsideOneBox =
            _boxMask[rootA] == _boxMask[rootB] &&
            IsSingleBit(_boxMask[rootA]);

        if (!staysInsideOneBox)
            return false;

        return _random.NextDouble() < boxCrossBias;
    }

    /// <summary>Union-Find: Find（経路圧縮付き）</summary>
    private int Find(int x)
    {
        while (_parent[x] != x)
        {
            _parent[x] = _parent[_parent[x]];
            x = _parent[x];
        }

        return x;
    }

    /// <summary>
    /// Union-Find: Union。
    /// サイズの大きい方を親にすることで木の深さを抑えつつ、
    /// サイズ・数字マスク・ブロックマスクを親へ集約する。
    /// </summary>
    private void Union(int rootA, int rootB)
    {
        if (_size[rootA] < _size[rootB])
            (rootA, rootB) = (rootB, rootA);

        _parent[rootB] = rootA;
        _size[rootA] += _size[rootB];
        _digitMask[rootA] |= _digitMask[rootB];
        _boxMask[rootA] |= _boxMask[rootB];
    }

    /// <summary>現在の単セルケージ（サイズ1）の数。</summary>
    private int CountSingles()
    {
        int count = 0;

        for (int i = 0; i < CellCount; i++)
        {
            // 各ケージの代表元（root）でだけ数える。
            if (_parent[i] == i && _size[i] == 1)
                count++;
        }

        return count;
    }

    /// <summary>Union-Find の結果から、ケージごとのセル一覧を組み立てる。</summary>
    private List<List<int>> ExtractCages()
    {
        var cageByRoot = new Dictionary<int, List<int>>();

        for (int i = 0; i < CellCount; i++)
        {
            int root = Find(i);

            if (!cageByRoot.TryGetValue(root, out var list))
            {
                list = new List<int>();
                cageByRoot[root] = list;
            }

            list.Add(i);
        }

        return cageByRoot.Values.ToList();
    }

    /// <summary>辺の並びをその場でシャッフルする（Fisher-Yates）。</summary>
    private void ShuffleEdgeOrder()
    {
        for (int i = _edgeOrder.Length - 1; i > 0; i--)
        {
            int j = _random.Next(i + 1);
            (_edgeOrder[i], _edgeOrder[j]) = (_edgeOrder[j], _edgeOrder[i]);
        }
    }

    // ================================================================
    // ヘルパー
    // ================================================================

    private static bool IsSingleBit(int mask)
        => mask != 0 && (mask & (mask - 1)) == 0;

    private static int[] ReadDigits(Board solvedBoard)
    {
        var digits = new int[CellCount];

        for (int row = 0; row < Board.Size; row++)
        {
            for (int col = 0; col < Board.Size; col++)
            {
                var value = solvedBoard.GetCell(row, col).Value;

                if (!value.HasValue)
                    throw new InvalidOperationException("完成盤面に空セルがあります。");

                if (value.Value < 1 || value.Value > Board.Size)
                    throw new InvalidOperationException(
                        $"完成盤面のセル({row},{col})の数字が不正です: {value.Value}");

                digits[ToIndex(row, col)] = value.Value;
            }
        }

        return digits;
    }

    private static List<Cage> CreateCages(List<List<int>> cageIndexes, int[] digits)
    {
        var cages = new List<Cage>(cageIndexes.Count);

        foreach (var indexes in cageIndexes)
        {
            int sum = 0;
            var cells = new List<(int Row, int Col)>(indexes.Count);

            foreach (int index in indexes)
            {
                sum += digits[index];
                cells.Add(FromIndex(index));
            }

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
        var edges = new List<(int, int)>(EdgeCount);

        for (int row = 0; row < Board.Size; row++)
        {
            for (int col = 0; col < Board.Size; col++)
            {
                int index = ToIndex(row, col);

                if (col < Board.Size - 1)
                    edges.Add((index, ToIndex(row, col + 1)));

                if (row < Board.Size - 1)
                    edges.Add((index, ToIndex(row + 1, col)));
            }
        }

        return edges.ToArray();
    }

    private static int[] BuildBoxBits()
    {
        var bits = new int[CellCount];

        for (int row = 0; row < Board.Size; row++)
        {
            for (int col = 0; col < Board.Size; col++)
            {
                int box =
                    (row / Board.BoxSize) * Board.BoxSize +
                    (col / Board.BoxSize);

                bits[ToIndex(row, col)] = 1 << box;
            }
        }

        return bits;
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

        Debug.WriteLine(
            "[CageGenerator] " +
            string.Join(", ", sizeCounts) +
            $", CageCount={cages.Count}" +
            $", AvgSize={(double)CellCount / cages.Count:F2}" +
            $", Singles={cages.Count(c => c.Cells.Count == 1)}");
    }
}
