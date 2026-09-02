using Sudoku.Models;
using System.Collections.Concurrent;
using System.Numerics;

namespace Sudoku.Solvers;

/// <summary>
/// ケージの合計値制約と通常のナンプレ制約を組み合わせて解く、キラーナンプレ専用の解法エンジン。
/// 唯一解かどうかの判定（生成時の検証）専用に使う。
///
/// 「制約伝播（ケージの組み合わせ推論＋行・列・ブロック）で確定できることを先に全部確定させ、
/// 本当にどうしても絞り込めない部分だけバックトラッキングする」という設計にすることで、
/// 完全に空の盤面から唯一解を証明するという重い処理を高速化している。
/// </summary>
public class KillerBacktrackingSolver
{
    private const int FullMask = 0b1_1111_1111; // digit 1 ～ 9 → bit 0 ～ 8

    private readonly List<Cage> _cages;
    private CancellationToken _cancellationToken;

    private static readonly ConcurrentDictionary<(int Size, int Sum), List<int>> ComboMaskCache = new();

    private int[,] _grid = new int[Board.Size, Board.Size];
    private int[,] _candidates = new int[Board.Size, Board.Size];

    private readonly int[] _rowUsed =
    new int[Board.Size];

    private readonly int[] _colUsed =
        new int[Board.Size];

    private readonly int[] _boxUsed =
        new int[Board.Size];

    // ------------------------------------------------------------
    // ApplyCageConstraints() の結果キャッシュ。
    //
    // Propagate() は固定点に達するまで毎回全ケージを再解析するが、
    // 深い探索の1手先などでは、実際に候補・確定値が変化したのは
    // ごく一部のセルだけであることが多い。そこで、各ケージについて
    // 「最後に解析した時点の確定値・候補マスク」をシグネチャとして
    // 保持し、シグネチャが変化していなければ再解析をスキップする。
    // ------------------------------------------------------------
    private readonly Dictionary<Cage, long> _cageStableSignature = new();

    private System.Diagnostics.Stopwatch? _stopwatch;
    private int _timeBudgetMs;
    private bool _aborted;

    // 一意解検証時に使用する既知の完成盤面。
    // 「この盤面とは異なる解」が存在するかを探索する。
    private int[,]? _knownSolution;

    private readonly struct GridChange
    {
        public readonly int Row;
        public readonly int Col;
        public readonly int OldValue;

        public GridChange(
            int row,
            int col,
            int oldValue)
        {
            Row = row;
            Col = col;
            OldValue = oldValue;
        }
    }

    private readonly List<GridChange> _trail = new();

    private readonly struct CandidateChange
    {
        public readonly int Row;
        public readonly int Col;
        public readonly int OldMask;

        public CandidateChange(
            int row,
            int col,
            int oldMask)
        {
            Row = row;
            Col = col;
            OldMask = oldMask;
        }
    }

    private readonly List<CandidateChange> _candidateTrail = new();

    // ------------------------------------------------------------
    // ApplyCageConstraints() 用の再利用バッファ。
    //
    // Expert / Master の探索では制約伝播が大量に呼ばれるため、
    // 毎回 List / Dictionary / int[] を new するとGC負荷が増える。
    // 各ケージは最大9セルなので、盤面サイズ固定のバッファを再利用する。
    // ------------------------------------------------------------
    private readonly (int Row, int Col)[] _emptyCellBuffer =
        new (int Row, int Col)[Board.Size];

    private readonly int[] _allowedPerCellBuffer =
        new int[Board.Size];

    private readonly int[] _comboAllowedBuffer =
        new int[Board.Size];

    private void SetGridValue(int row, int col, int value)
    {
        if (_grid[row, col] == value)
            return;

        _trail.Add(new GridChange(row, col, _grid[row, col]));
        _grid[row, col] = value;
    }

    private void SetCandidates(int row, int col, int mask)
    {
        if (_candidates[row, col] == mask)
            return;

        _candidateTrail.Add(new CandidateChange(row, col, _candidates[row, col]));
        _candidates[row, col] = mask;
    }

    private readonly struct Checkpoint
    {
        public readonly int GridTrailCount;
        public readonly int CandidateTrailCount;

        public Checkpoint(
            int gridTrailCount,
            int candidateTrailCount)
        {
            GridTrailCount = gridTrailCount;
            CandidateTrailCount = candidateTrailCount;
        }
    }

    private Checkpoint CreateCheckpoint()
    {
        return new Checkpoint(
            _trail.Count,
            _candidateTrail.Count);
    }

    private void Restore(Checkpoint checkpoint)
    {
        for (int i = _candidateTrail.Count - 1;
             i >= checkpoint.CandidateTrailCount;
             i--)
        {
            var change = _candidateTrail[i];

            _candidates[change.Row, change.Col] =
                change.OldMask;
        }

        _candidateTrail.RemoveRange(
            checkpoint.CandidateTrailCount,
            _candidateTrail.Count -
            checkpoint.CandidateTrailCount);

        for (int i = _trail.Count - 1;
             i >= checkpoint.GridTrailCount;
             i--)
        {
            var change = _trail[i];

            _grid[change.Row, change.Col] =
                change.OldValue;
        }

        _trail.RemoveRange(
            checkpoint.GridTrailCount,
            _trail.Count -
            checkpoint.GridTrailCount);
    }

    public KillerBacktrackingSolver(
        List<Cage> cages,
        CancellationToken cancellationToken = default)
    {
        _cages = cages;
        _cancellationToken = cancellationToken;
    }

    /// <summary>盤面を解いて埋める。解けたらtrue。</summary>
    public bool TrySolve(
        Board board,
        int timeBudgetMs = 5000,
        CancellationToken cancellationToken = default)
    {
        _cancellationToken = cancellationToken;

        LoadFromBoard(board);
        _stopwatch = System.Diagnostics.Stopwatch.StartNew();
        _timeBudgetMs = timeBudgetMs;
        _aborted = false;

        _cancellationToken.ThrowIfCancellationRequested();

        bool solved = SolveOne();

        if (solved)
            WriteBackToBoard(board);

        return solved;
    }

    /// <summary>
    /// 解の個数をlimit件までカウントする。
    ///
    /// 戻り値:
    ///   0〜limit = 実際に確認できた解の個数
    ///   -1 = 時間切れまたはキャンセル
    ///
    /// 制約伝播後の未確定セル数を理由に
    /// 探索を打ち切ることはしない。
    /// </summary>
    public int CountSolutions(
        Board board,
        int limit = 2,
        int timeBudgetMs = 5000,
        CancellationToken cancellationToken = default)
    {
        _cancellationToken = cancellationToken;

        LoadFromBoard(board);
        _stopwatch = System.Diagnostics.Stopwatch.StartNew();
        _timeBudgetMs = timeBudgetMs;
        _aborted = false;

        _cancellationToken.ThrowIfCancellationRequested();

        if (!Propagate())
            return _aborted ? -1 : 0; // 矛盾（そもそも成立しないケージ配置）

        int filled =
            CountFilledCells();

        if (filled == Board.Size * Board.Size)
            return 1;

        int count = 0;

        CountAll(
            limit,
            ref count);

        return _aborted
            ? -1
            : count;
    }

    /// <summary>
    /// 既知の完成盤面以外に解が存在するかを探索する。
    ///
    /// 戻り値:
    ///   1  = 既知の完成盤面が唯一解
    ///   2  = 既知の完成盤面とは異なる別解を発見
    ///  -1  = 時間切れまたはキャンセル
    ///   0  = 制約上成立する解が存在しない
    ///
    /// KillerSudokuGeneratorでは、元の完成盤面が必ず1つの既知解として
    ///存在するため、通常は1または2を返す。
    /// </summary>
    public int CheckUniqueAgainstKnownSolution(
        Board board,
        Board knownSolution,
        int timeBudgetMs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(board);
        ArgumentNullException.ThrowIfNull(knownSolution);

        if (timeBudgetMs <= 0)
            throw new ArgumentOutOfRangeException(nameof(timeBudgetMs));

        _cancellationToken = cancellationToken;

        LoadFromBoard(board);

        _stopwatch =
            System.Diagnostics.Stopwatch.StartNew();

        _timeBudgetMs =
            timeBudgetMs;

        _aborted = false;

        _knownSolution =
            new int[Board.Size, Board.Size];

        // ------------------------------------------------------------
        // 既知解を読み込む。
        // ------------------------------------------------------------
        for (int r = 0; r < Board.Size; r++)
        {
            for (int c = 0; c < Board.Size; c++)
            {
                var cell =
                    knownSolution.GetCell(r, c);

                if (!cell.HasValue)
                {
                    _knownSolution = null;

                    throw new InvalidOperationException(
                        "一意解検証に渡された完成盤面に空セルがあります。");
                }

                int value =
                    cell.Value!.Value;

                if (value < 1 || value > Board.Size)
                {
                    _knownSolution = null;

                    throw new InvalidOperationException(
                        $"一意解検証に渡された完成盤面の値が不正です: {value}");
                }

                _knownSolution[r, c] =
                    value;
            }
        }

        _cancellationToken.ThrowIfCancellationRequested();

        // ------------------------------------------------------------
        // 制約伝播。
        // ここで状態を一度だけ正規化する。
        // ------------------------------------------------------------
        if (!Propagate())
        {
            int result =
                _aborted
                    ? -1
                    : 0;

            _knownSolution = null;

            return result;
        }

        // ------------------------------------------------------------
        // 制約伝播後に既知解と異なる状態になっている場合。
        // そこから1解見つかれば、それが別解になる。
        // ------------------------------------------------------------
        if (IsDifferentFromKnownSolution())
        {
            bool solved =
                SolveOne();

            if (_aborted)
            {
                _knownSolution = null;
                return -1;
            }

            _knownSolution = null;

            return solved ? 2 : 1;
        }

        // ------------------------------------------------------------
        // 完成しており、しかも既知解と一致している。
        // 別解は存在しない。
        // ------------------------------------------------------------
        if (FindMrvCell() is null)
        {
            _knownSolution = null;
            return 1;
        }

        // ------------------------------------------------------------
        // ここでは既にPropagate済み。
        // SearchForAlternativeSolution() は
        // 探索開始時にもう一度Propagateしない。
        // ------------------------------------------------------------
        bool alternativeFound =
            SearchForAlternativeSolution(propagated: true);

        if (_aborted)
        {
            _knownSolution = null;
            return -1;
        }

        _knownSolution = null;

        return alternativeFound ? 2 : 1;
    }

    /// <summary>
    /// 現在の盤面から、既知解とは異なる解を探す。
    ///
    /// 現在の状態が既知解と矛盾した場合は、
    /// そこから通常のSolveOne()で完成解を1つ探す。
    ///
    /// まだ既知解と一致している場合は、
    /// 分岐時に既知解と異なる数字を優先的に探索する。
    /// </summary>
    private bool SearchForAlternativeSolution(
        bool propagated = false)
    {
        if (TimeIsUp())
        {
            _aborted = true;
            return false;
        }

        if (_knownSolution is null)
            throw new InvalidOperationException(
                "既知解が設定されていません。");

        var checkpoint =
            CreateCheckpoint();

        // 親から既に制約伝播済みの場合は、
        // 同じPropagateを二重実行しない。
        if (!propagated)
        {
            if (!Propagate() || _aborted)
            {
                Restore(checkpoint);
                return false;
            }
        }

        // ------------------------------------------------------------
        // 現在の状態ですでに既知解から分岐している場合、
        // この状態から完成解を1つ見つければ、それが別解になる。
        // ------------------------------------------------------------
        if (IsDifferentFromKnownSolution())
        {
            // 現在状態は既知解とは異なることが確定している。
            //
            // ここからは「既知解かどうか」を考える必要がない。
            // 制約伝播済み状態から、通常の解探索だけを行う。
            bool solved =
                SearchAnySolution();

            if (solved)
                return true;

            Restore(checkpoint);
            return false;
        }

        var target =
            FindMrvCell();

        // 完成しており、既知解と一致している。
        // これは「既知解そのもの」なので別解ではない。
        if (target is null)
        {
            Restore(checkpoint);
            return false;
        }

        var (row, col, mask) =
            target.Value;

        int knownDigit =
            _knownSolution[row, col];

        int knownBit =
            1 << (knownDigit - 1);

        // ------------------------------------------------------------
        // まず「既知解とは異なる数字」を試す。
        //
        // 別解が存在するなら、こちら側で即座に見つかる可能性が高い。
        // ------------------------------------------------------------
        foreach (int digit in IterateDigits(mask))
        {
            if (_aborted)
                break;

            if (digit == knownDigit)
                continue;

            var branchCheckpoint =
                CreateCheckpoint();

            SetGridValue(
                row,
                col,
                digit);

            if (SolveOne())
                return true;

            Restore(branchCheckpoint);

            if (TimeIsUp())
            {
                _aborted = true;
                break;
            }
        }

        // ------------------------------------------------------------
        // 異なる数字の分岐ですべて解が存在しなかった場合のみ、
        // 既知解と同じ数字の分岐を進める。
        //
        // この先のどこかで別のセルが既知解と異なる可能性があるため、
        // 再帰的に調べる。
        // ------------------------------------------------------------
        if (!_aborted &&
            (mask & knownBit) != 0)
        {
            var knownBranchCheckpoint =
                CreateCheckpoint();

            SetGridValue(
                row,
                col,
                knownDigit);

            // knownDigitを配置したことで候補状態が変化したため、
            // この分岐で必要な制約伝播を1回だけ実行する。
            if (Propagate())
            {
                if (SearchForAlternativeSolution(
                        propagated: true))
                {
                    return true;
                }
            }

            Restore(knownBranchCheckpoint);
        }

        Restore(checkpoint);

        return false;
    }

    /// <summary>
    /// 現在の状態が、既知の完成盤面からすでに分岐しているかを判定する。
    ///
    /// 次のいずれかに該当した場合、既知解とは異なる:
    ///   1. 確定値が既知解と異なる
    ///   2. 未確定セルの候補から既知解の数字が消えている
    ///
    /// 2の場合は、この状態から既知解へ戻ることが不可能なので、
    /// 完成できれば必ず別解になる。
    /// </summary>
    private bool IsDifferentFromKnownSolution()
    {
        if (_knownSolution is null)
            throw new InvalidOperationException(
                "既知解が設定されていません。");

        for (int r = 0; r < Board.Size; r++)
        {
            for (int c = 0; c < Board.Size; c++)
            {
                int current =
                    _grid[r, c];

                int known =
                    _knownSolution[r, c];

                if (current != 0)
                {
                    if (current != known)
                        return true;

                    continue;
                }

                int knownBit =
                    1 << (known - 1);

                if ((_candidates[r, c] & knownBit) == 0)
                    return true;
            }
        }

        return false;
    }

    private void LoadFromBoard(Board board)
    {
        _knownSolution = null;
        _grid = new int[Board.Size, Board.Size];

        for (int r = 0; r < Board.Size; r++)
            for (int c = 0; c < Board.Size; c++)
            {
                var cell = board.GetCell(r, c);
                _grid[r, c] = cell.HasValue ? cell.Value!.Value : 0;
            }
        _candidates = new int[Board.Size, Board.Size];

        _trail.Clear();
        _candidateTrail.Clear();
        _cageStableSignature.Clear();
    }

    private void WriteBackToBoard(Board board)
    {
        for (int r = 0; r < Board.Size; r++)
            for (int c = 0; c < Board.Size; c++)
                if (!board.GetCell(r, c).HasValue && _grid[r, c] != 0)
                    board.GetCell(r, c).SetValue(_grid[r, c]);
    }

    private int CountFilledCells()
    {
        int count = 0;
        for (int r = 0; r < Board.Size; r++)
            for (int c = 0; c < Board.Size; c++)
                if (_grid[r, c] != 0) count++;
        return count;
    }

    // ================================================================
    // 既に制約伝播済みの状態から解を1つ探す。
    // SolveOne() と違い、開始時に Propagate() を実行しない。
    // ================================================================
    private bool SearchAnySolution()
    {
        if (_aborted)
            return false;

        if (TimeIsUp())
        {
            _aborted = true;
            return false;
        }

        var target =
            FindMrvCell();

        // 全セル確定
        if (target is null)
            return true;

        var (row, col, mask) =
            target.Value;

        foreach (int digit in IterateDigits(mask))
        {
            if (_aborted)
                return false;

            if (TimeIsUp())
            {
                _aborted = true;
                return false;
            }

            var checkpoint =
                CreateCheckpoint();

            SetGridValue(
                row,
                col,
                digit);

            if (Propagate())
            {
                if (SearchAnySolution())
                    return true;
            }

            Restore(checkpoint);
        }

        return false;
    }

    // ================================================================
    // 通常の解探索。
    // 開始時に制約伝播を1回だけ行い、
    // その後は SearchAnySolution() に渡す。
    // ================================================================
    private bool SolveOne()
    {
        if (_stopwatch is null)
            throw new InvalidOperationException(
                "Stopwatch is not initialized.");

        if (TimeIsUp())
        {
            _aborted = true;
            return false;
        }

        var checkpoint =
            CreateCheckpoint();

        if (!Propagate() || _aborted)
        {
            Restore(checkpoint);
            return false;
        }

        if (SearchAnySolution())
            return true;

        Restore(checkpoint);

        return false;
    }

    // ====== 解の個数を数える（limit件まで） ======
    private void CountAll(int limit, ref int count)
    {
        if (_aborted || count >= limit)
            return;

        if (_cancellationToken.IsCancellationRequested)
        {
            _aborted = true;
            return;
        }

        if (TimeIsUp())
        {
            _aborted = true;
            return;
        }

        var checkpoint = CreateCheckpoint();

        if (!Propagate() || _aborted)
        {
            Restore(checkpoint);
            return;
        }

        var target = FindMrvCell();

        if (target is null)
        {
            count++; // 全マス確定 = 解を1つ発見
            Restore(checkpoint);
            return;
        }

        var (row, col, mask) = target.Value;

        foreach (int digit in IterateDigits(mask))
        {
            if (_aborted || count >= limit) break;

            var branchCheckpoint = CreateCheckpoint();

            SetGridValue(row, col, digit);

            CountAll(limit, ref count);

            Restore(branchCheckpoint);
        }

        Restore(checkpoint);
    }

    private bool TimeIsUp()
    {
        if (_cancellationToken.IsCancellationRequested)
        {
            _aborted = true;
            return true;
        }

        if (_stopwatch is null)
            return false;

        if (_stopwatch.ElapsedMilliseconds >= _timeBudgetMs)
        {
            _aborted = true;
            return true;
        }

        return false;
    }

    /// <summary>
    /// 「候補が1つに絞れたマスを確定する」を、これ以上進展しなくなるまで繰り返す（制約伝播）。
    /// 途中で矛盾（候補0のマス）を見つけたらfalseを返す
    /// </summary>
    private bool Propagate()
    {
        while (true)
        {
            if (TimeIsUp())
            {
                _aborted = true;
                return false;
            }

            // 通常のナンプレ候補を再計算
            RecomputeBasicCandidates();

            // ケージによる候補制限
            if (!ApplyCageConstraints())
                return false;

            bool placedSomething = false;

            // ====== Naked Single =======
            for (int r = 0; r < Board.Size; r++)
            {
                for (int c = 0; c < Board.Size; c++)
                {
                    if (_grid[r, c] != 0)
                        continue;

                    int mask = _candidates[r, c];
                    if (mask == 0)
                        return false; // 矛盾：入る数字がない

                    if (IsSingleBit(mask))
                    {
                        SetGridValue(r, c, BitToDigit(mask));
                        placedSomething = true;
                    }
                }
            }

            // Naked Single で盤面が変わった → 候補を再計算する
            if (placedSomething)
                continue;

            // ====== Hidden Single ======
            if (ApplyHiddenSingles())
                continue;

            // これ以上確定できることがないv
            return true;
        }
    }

    /// <summary>通常のナンプレ制約（行・列・ブロック）だけから、各空きマスの候補を計算する。</summary>
    private void RecomputeBasicCandidates()
    {
        Array.Clear(
            _rowUsed,
            0,
            _rowUsed.Length);

        Array.Clear(
            _colUsed,
            0,
            _colUsed.Length);

        Array.Clear(
            _boxUsed,
            0,
            _boxUsed.Length);

        // ------------------------------------------------------------
        // 行・列・ブロックの使用数字を一度だけ作る。
        // ------------------------------------------------------------
        for (int r = 0; r < Board.Size; r++)
        {
            for (int c = 0; c < Board.Size; c++)
            {
                int value =
                    _grid[r, c];

                if (value == 0)
                    continue;

                int bit =
                    1 << (value - 1);

                _rowUsed[r] |= bit;
                _colUsed[c] |= bit;

                int box =
                    (r / Board.BoxSize) * Board.BoxSize +
                    (c / Board.BoxSize);

                _boxUsed[box] |= bit;
            }
        }

        // ------------------------------------------------------------
        // 各セルの候補を、
        // 行・列・ブロックの3マスクから一発で求める。
        // ------------------------------------------------------------
        for (int r = 0; r < Board.Size; r++)
        {
            for (int c = 0; c < Board.Size; c++)
            {
                if (_grid[r, c] != 0)
                {
                    SetCandidates(
                        r,
                        c,
                        0);

                    continue;
                }

                int box =
                    (r / Board.BoxSize) * Board.BoxSize +
                    (c / Board.BoxSize);

                int used =
                    _rowUsed[r] |
                    _colUsed[c] |
                    _boxUsed[box];

                SetCandidates(
                    r,
                    c,
                    FullMask & ~used);
            }
        }
    }

    /// <summary>
    /// 各ケージについて「現在置かれている数字と矛盾しない合計値の組み合わせ」だけに絞り込み、
    /// その組み合わせで使われうる数字だけに、ケージ内の空きマスの候補を制限する。
    /// </summary>
    private bool ApplyCageConstraints()
    {
        foreach (var cage in _cages)
        {
            long signature = ComputeCageSignature(cage);

            if (_cageStableSignature.TryGetValue(cage, out long cachedSignature) &&
                cachedSignature == signature)
            {
                // 前回このケージを解析した時点から、関係するマスの
                // 確定値・候補マスクが変化していない。
                // 結果は既に候補へ反映済みのため、再解析をスキップする。
                continue;
            }

            int usedMask = 0;
            int usedSum = 0;
            int filledCount = 0;

            int emptyCount = 0;

            // ------------------------------------------------------------
            // 確定セルと空セルを分離する。
            // ------------------------------------------------------------
            foreach (var (row, col) in cage.Cells)
            {
                int value =
                    _grid[row, col];

                if (value == 0)
                {
                    _emptyCellBuffer[emptyCount++] =
                        (row, col);

                    continue;
                }

                int bit =
                    1 << (value - 1);

                // ケージ内重複
                if ((usedMask & bit) != 0)
                    return false;

                usedMask |= bit;
                usedSum += value;
                filledCount++;
            }

            int remainingCount =
                cage.Cells.Count -
                filledCount;

            // ------------------------------------------------------------
            // 完成済みケージ
            // ------------------------------------------------------------
            if (remainingCount == 0)
            {
                if (usedSum != cage.TargetSum)
                    return false;

                _cageStableSignature[cage] = signature;
                continue;
            }

            var combos =
                GetComboMasks(
                    cage.Cells.Count,
                    cage.TargetSum);

            Array.Clear(
                _allowedPerCellBuffer,
                0,
                emptyCount);

            bool anyValidCombo =
                false;

            // ------------------------------------------------------------
            // 合計値から可能な数字集合を調べる。
            // ------------------------------------------------------------
            foreach (int comboMask in combos)
            {
                if ((comboMask & usedMask) != usedMask)
                    continue;

                int remainingMask =
                    comboMask &
                    ~usedMask;

                if (BitOperations.PopCount(
                        (uint)remainingMask) != emptyCount)
                {
                    continue;
                }

                Array.Clear(
                    _comboAllowedBuffer,
                    0,
                    emptyCount);

                GetComboAllowedMasks(
                    _emptyCellBuffer,
                    emptyCount,
                    remainingMask,
                    _comboAllowedBuffer);

                bool comboIsValid =
                    false;

                for (int i = 0; i < emptyCount; i++)
                {
                    int allowed =
                        _comboAllowedBuffer[i];

                    if (allowed == 0)
                        continue;

                    comboIsValid = true;

                    _allowedPerCellBuffer[i] |=
                        allowed;
                }

                if (comboIsValid)
                    anyValidCombo = true;
            }

            if (!anyValidCombo)
                return false;

            // ------------------------------------------------------------
            // 各セルの候補をケージ制約で絞り込む。
            // ------------------------------------------------------------
            for (int i = 0; i < emptyCount; i++)
            {
                var (row, col) =
                    _emptyCellBuffer[i];

                int newMask =
                    _candidates[row, col] &
                    _allowedPerCellBuffer[i];

                if (newMask == 0)
                    return false;

                SetCandidates(
                    row,
                    col,
                    newMask);
            }

            // このケージについて絞り込みが完了した状態のシグネチャを記録する。
            _cageStableSignature[cage] = ComputeCageSignature(cage);
        }

        return true;
    }

    /// <summary>
    /// ケージ内の各セルの「確定値、または候補マスク」から、
    /// 現在の状態を表すシグネチャを計算する。
    /// 同じシグネチャが得られる限り、ApplyCageConstraintsの結果は
    /// 必ず同じになる（純粋にグリッドと候補の現在値だけで決まるため）。
    /// </summary>
    private long ComputeCageSignature(Cage cage)
    {
        unchecked
        {
            long hash = 17;

            foreach (var (row, col) in cage.Cells)
            {
                int value = _grid[row, col];
                int part = value != 0 ? value : (1000 + _candidates[row, col]);
                hash = hash * 31 + part;
            }

            return hash;
        }
    }

    private void GetComboAllowedMasks(
        (int Row, int Col)[] cells,
        int cellCount,
        int remainingMask,
        int[] allowed)
    {
        Array.Clear(
            allowed,
            0,
            cellCount);

        int allCellMask =
            (1 << cellCount) - 1;

        for (int i = 0; i < cellCount; i++)
        {
            var cell =
                cells[i];

            int possible =
                _candidates[cell.Row, cell.Col] &
                remainingMask;

            int cellBit =
                1 << i;

            while (possible != 0)
            {
                int digitBit =
                    possible & -possible;

                possible &=
                    possible - 1;

                int nextCellMask =
                    allCellMask &
                    ~cellBit;

                int nextDigitMask =
                    remainingMask &
                    ~digitBit;

                if (CanCompleteAssignment(
                    cells,
                    nextCellMask,
                    nextDigitMask))
                {
                    allowed[i] |= digitBit;
                }
            }
        }
    }

    private bool CanCompleteAssignment(
        (int Row, int Col)[] cells,
        int remainingCellMask,
        int remainingDigitMask)
    {
        if (remainingCellMask == 0)
            return remainingDigitMask == 0;

        int cellCount =
            BitOperations.PopCount(
                (uint)remainingCellMask);

        if (BitOperations.PopCount(
                (uint)remainingDigitMask) != cellCount)
        {
            return false;
        }

        int bestIndex = -1;
        int bestCandidates = 0;
        int bestCount = int.MaxValue;

        int cellMask =
            remainingCellMask;

        while (cellMask != 0)
        {
            int cellBit =
                cellMask & -cellMask;

            int index =
                BitOperations.TrailingZeroCount(
                    (uint)cellBit);

            var cell =
                cells[index];

            int possible =
                _candidates[cell.Row, cell.Col] &
                remainingDigitMask;

            if (possible == 0)
                return false;

            int count =
                BitOperations.PopCount(
                    (uint)possible);

            if (count < bestCount)
            {
                bestCount =
                    count;

                bestIndex =
                    index;

                bestCandidates =
                    possible;

                if (count == 1)
                    break;
            }

            cellMask &=
                cellMask - 1;
        }

        int selectedCellBit =
            1 << bestIndex;

        int nextCellMask =
            remainingCellMask &
            ~selectedCellBit;

        int candidates =
            bestCandidates;

        while (candidates != 0)
        {
            int digitBit =
                candidates & -candidates;

            candidates &=
                candidates - 1;

            if (CanCompleteAssignment(
                cells,
                nextCellMask,
                remainingDigitMask &
                ~digitBit))
            {
                return true;
            }
        }

        return false;
    }

    private bool ApplyHiddenSingles()
    {
        // ------------------------------------------------------------
        // 行
        // ------------------------------------------------------------
        for (int r = 0; r < Board.Size; r++)
        {
            for (int digit = 1; digit <= Board.Size; digit++)
            {
                int bit =
                    1 << (digit - 1);

                int foundRow = -1;
                int count = 0;

                for (int c = 0; c < Board.Size; c++)
                {
                    if (_grid[r, c] != 0)
                        continue;

                    if ((_candidates[r, c] & bit) == 0)
                        continue;

                    count++;

                    if (count == 1)
                        foundRow = c;
                    else
                        break;
                }

                if (count == 1)
                {
                    SetGridValue(
                        r,
                        foundRow,
                        digit);

                    return true;
                }
            }
        }

        // ------------------------------------------------------------
        // 列
        // ------------------------------------------------------------
        for (int c = 0; c < Board.Size; c++)
        {
            for (int digit = 1; digit <= Board.Size; digit++)
            {
                int bit =
                    1 << (digit - 1);

                int foundRow = -1;
                int count = 0;

                for (int r = 0; r < Board.Size; r++)
                {
                    if (_grid[r, c] != 0)
                        continue;

                    if ((_candidates[r, c] & bit) == 0)
                        continue;

                    count++;

                    if (count == 1)
                        foundRow = r;
                    else
                        break;
                }

                if (count == 1)
                {
                    SetGridValue(
                        foundRow,
                        c,
                        digit);

                    return true;
                }
            }
        }

        // ------------------------------------------------------------
        // ブロック
        // ------------------------------------------------------------
        for (int boxRow = 0;
             boxRow < Board.Size;
             boxRow += Board.BoxSize)
        {
            for (int boxCol = 0;
                 boxCol < Board.Size;
                 boxCol += Board.BoxSize)
            {
                for (int digit = 1;
                     digit <= Board.Size;
                     digit++)
                {
                    int bit =
                        1 << (digit - 1);

                    int foundRow = -1;
                    int foundCol = -1;
                    int count = 0;

                    for (int r = boxRow;
                         r < boxRow + Board.BoxSize;
                         r++)
                    {
                        for (int c = boxCol;
                             c < boxCol + Board.BoxSize;
                             c++)
                        {
                            if (_grid[r, c] != 0)
                                continue;

                            if ((_candidates[r, c] & bit) == 0)
                                continue;

                            count++;

                            if (count == 1)
                            {
                                foundRow = r;
                                foundCol = c;
                            }
                            else
                            {
                                break;
                            }
                        }

                        if (count >= 2)
                            break;
                    }

                    if (count == 1)
                    {
                        SetGridValue(
                            foundRow,
                            foundCol,
                            digit);

                        return true;
                    }
                }
            }
        }

        return false;
    }

    private bool ApplyHiddenSingleToUnit(IEnumerable<(int Row, int Col)> unit)
    {
        var cells = unit.ToList();

        for (int digit = 1; digit <= 9; digit++)
        {
            int bit = 1 << (digit - 1);

            (int Row, int Col)? onlyCell = null;
            int count = 0;

            foreach (var (row, col) in cells)
            {
                if (_grid[row, col] != 0)
                    continue;

                if ((_candidates[row, col] & bit) != 0)
                {
                    count++;

                    if (count == 1)
                        onlyCell = (row, col);
                    else
                        break;
                }
            }

            if (count == 1 && onlyCell.HasValue)
            {
                var (row, col) = onlyCell.Value;

                SetGridValue(row, col, digit);
                return true;
            }
        }

        return false;
    }

    /// <summary>指定したケージサイズ・合計値に対する「使いうる数字の組み合わせ」一覧をビットマスクで返す（キャッシュ付き）。</summary>
    private static List<int> GetComboMasks(int size, int sum)
    {
        var key = (size, sum);

        return ComboMaskCache.GetOrAdd(
            key,
            static k =>
            {
                var results = new List<int>();

                GenerateComboMasks(
                    start: 1,
                    size: k.Size,
                    remainingSum: k.Sum,
                    depth: 0,
                    currentMask: 0,
                    results);

                return results;
            });
    }

    private static void GenerateComboMasks(int start, int size, int remainingSum, int depth, int currentMask, List<int> results)
    {
        if (depth == size)
        {
            if (remainingSum == 0) results.Add(currentMask);
            return;
        }

        for (int d = start; d <= 9; d++)
        {
            if (d > remainingSum) break;
            GenerateComboMasks(d + 1, size, remainingSum - d, depth + 1, currentMask | (1 << (d - 1)), results);
        }
    }

    /// <summary>空きマスの中で、候補数が最も少ないマス（MRV）を探す。</summary>
    private (int Row, int Col, int Mask)? FindMrvCell()
    {
        int bestRow = -1, bestCol = -1, bestMask = 0, bestCount = int.MaxValue;

        for (int r = 0; r < Board.Size; r++)
        {
            for (int c = 0; c < Board.Size; c++)
            {
                if (_grid[r, c] != 0) continue;

                int count = PopCount(_candidates[r, c]);
                if (count < bestCount)
                {
                    bestCount = count;
                    bestRow = r; bestCol = c; bestMask = _candidates[r, c];
                }
            }
        }

        return bestRow == -1 ? null : (bestRow, bestCol, bestMask);
    }

    private static bool IsSingleBit(int mask) => mask != 0 && (mask & (mask - 1)) == 0;

    private static int BitToDigit(int mask)
    {
        int digit = 1;
        while ((mask & 1) == 0) { mask >>= 1; digit++; }
        return digit;
    }

    private static int PopCount(int mask)
    {
        return BitOperations.PopCount(
            (uint)mask);
    }

    private static IEnumerable<int> IterateDigits(int mask)
    {
        while (mask != 0)
        {
            int bit =
                mask & -mask;

            int digit =
                BitOperations.TrailingZeroCount(
                    (uint)bit) + 1;

            yield return digit;

            mask &=
                mask - 1;
        }
    }
}