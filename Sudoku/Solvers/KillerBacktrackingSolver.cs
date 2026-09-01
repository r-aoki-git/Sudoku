using Sudoku.Models;
using System.Collections.Concurrent;
using System.Threading;
using System.Windows.Controls;

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
    private System.Diagnostics.Stopwatch? _stopwatch;
    private int _timeBudgetMs;
    private bool _aborted;

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
    /// 戻り値: 実際の解の個数（0〜limit）／ -1=時間切れ／ -2=制約伝播だけでは全然埋まらず、
    /// このケージ配置は見込みが薄いと判断して深い探索を行わずに見切った場合。
    /// -1・-2どちらも、呼び出し側は「不合格・リトライ」として扱えばよい。
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

    private void LoadFromBoard(Board board)
    {
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

    // ====== 唯一解の探索（見つけたらそこで終了） ======
    private bool SolveOne()
    {
        if (_stopwatch is null)
            throw new InvalidOperationException("Stopwatch is not initialized.");

        if (TimeIsUp())
        {
            _aborted = true;
            return false;
        }

        if (_cancellationToken.IsCancellationRequested)
        {
            _aborted = true;
            return false;
        }

        var checkpoint = CreateCheckpoint();

        if (!Propagate() || _aborted)
        {
            Restore(checkpoint);
            return false;
        }

        var target = FindMrvCell();
        if (target is null) return true; // 全マス確定 = 解けた（この状態を保持したまま返す）

        var (row, col, mask) = target.Value;

        foreach (int digit in IterateDigits(mask))
        {
            if (_aborted) break;

            var branchCheckpoint = CreateCheckpoint();

            SetGridValue(row, col, digit);

            if (SolveOne()) return true;

            Restore(branchCheckpoint);

            if (TimeIsUp()) { _aborted = true; break; }
        }

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
        for (int r = 0; r < Board.Size; r++)
        {
            for (int c = 0; c < Board.Size; c++)
            {
                if (_grid[r, c] != 0)
                {
                    SetCandidates(r, c, 0);
                    continue;
                }

                int used = 0;
                for (int i = 0; i < Board.Size; i++)
                {
                    if (_grid[r, i] != 0)
                        used |= 1 << (_grid[r, i] - 1);
                    if (_grid[i, c] != 0)
                        used |= 1 << (_grid[i, c] - 1);
                }

                int boxRow = (r / Board.BoxSize) * Board.BoxSize;
                int boxCol = (c / Board.BoxSize) * Board.BoxSize;
                for (int rr = boxRow; rr < boxRow + Board.BoxSize; rr++)
                    for (int cc = boxCol; cc < boxCol + Board.BoxSize; cc++)
                        if (_grid[rr, cc] != 0) used |= 1 << (_grid[rr, cc] - 1);
                SetCandidates(r, c, FullMask & ~used);
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
            int usedMask = 0;
            int usedSum = 0;
            int filledCount = 0;

            // ====== 既に確定している数字を調べる ======
            foreach (var (row, col) in cage.Cells)
            {
                int value = _grid[row, col];

                if (value == 0)
                    continue;

                int bit = 1 << (value - 1);

                // ケージ内重複
                if ((usedMask & bit) != 0)
                    return false;

                usedMask |= bit;
                usedSum += value;
                filledCount++;
            }

            int remainingCount = cage.Cells.Count - filledCount;

            // ====== ケージが完成している場合 ======
            if (remainingCount == 0)
            {
                if (usedSum != cage.TargetSum)
                    return false;

                continue;
            }

            // ====== 合計値から候補となる数字の組み合わせを取得 ======
            var combos = GetComboMasks(cage.Cells.Count, cage.TargetSum);

            var emptyCells = new List<(int Row, int Col)>();

            foreach (var cell in cage.Cells)
            {
                if (_grid[cell.Row, cell.Col] == 0)
                    emptyCells.Add(cell);
            }

            // 各セルに最終的に許可する数字
            var allowedPerCell = new Dictionary<(int Row, int Col), int>();

            foreach (var cell in emptyCells)
                allowedPerCell[cell] = 0;

            bool anyValidCombo = false;

            // ====== 各組み合わせを検証 ======
            foreach (int comboMask in combos)
            {
                // 既に配置されている数字を含んでいない組み合わせは無効
                if ((comboMask & usedMask) != usedMask)
                    continue;

                int remainingMask = comboMask & ~usedMask;

                // 念のため、空きセル数と数字数が一致しているか確認
                if (PopCount(remainingMask) != emptyCells.Count)
                    continue;

                // ====== この組み合わせを実際にセルへ割り当てられるか確認 ======
                int[] comboAllowed = GetComboAllowedMasks(emptyCells, remainingMask);

                bool comboIsValid = false;

                for (int i = 0; i < emptyCells.Count; i++)
                {
                    if (comboAllowed[i] == 0)
                        continue;

                    comboIsValid = true;

                    var cell = emptyCells[i];

                    allowedPerCell[cell] |= comboAllowed[i];
                }

                if (comboIsValid)
                    anyValidCombo = true;
            }

            // ====== 有効な組み合わせが1つもない ======
            if (!anyValidCombo)
                return false;

            // ====== 各セルの候補をさらに絞り込む ======
            foreach (var cell in emptyCells)
            {
                int newMask = _candidates[cell.Row, cell.Col] & allowedPerCell[cell];

                if (newMask == 0)
                    return false;

                SetCandidates(cell.Row, cell.Col, newMask);
            }
        }

        return true;
    }

    private int[] GetComboAllowedMasks(List<(int Row, int Col)> cells, int remainingMask)
    {
        int[] allowed = new int[cells.Count];

        for (int i = 0; i < cells.Count; i++)
        {
            var cell = cells[i];

            int possible = _candidates[cell.Row, cell.Col] & remainingMask;

            foreach (int digit in IterateDigits(possible))
            {
                int bit = 1 << (digit - 1);

                // このセルにこの数字を置いた状態で、残りの数字を残りのセルへ割り当てられるか確認
                var remainingCells = new List<(int Row, int Col)>(cells);

                remainingCells.RemoveAt(i);

                int remainingDigits = remainingMask & ~bit;

                if (CanCompleteAssignment(remainingCells, remainingDigits))
                    allowed[i] |= bit;
            }
        }

        return allowed;
    }

    private bool CanCompleteAssignment(List<(int Row, int Col)> cells, int remainingMask)
    {
        // 全セルに割り当て終わった
        if (cells.Count == 0)
            return remainingMask == 0;

        // セル数と数字数が一致していない
        if (PopCount(remainingMask) != cells.Count)
            return false;

        int bestIndex = -1;
        int bestMask = 0;
        int bestCount = int.MaxValue;

        // MRV: 残っているセルのうち、候補数が最も少ないセルを選ぶ
        for (int i = 0; i < cells.Count; i++)
        {
            var cell = cells[i];

            int possible = _candidates[cell.Row, cell.Col] & remainingMask;

            int count = PopCount(possible);

            if (count == 0)
                return false;

            if (count < bestCount)
            {
                bestCount = count;
                bestIndex = i;
                bestMask = possible;

                if (count == 1)
                    break;
            }
        }

        var selectedCell = cells[bestIndex];

        var nextCells = new List<(int Row, int Col)>(cells);

        nextCells.RemoveAt(bestIndex);

        foreach (int digit in IterateDigits(bestMask))
        {
            int bit = 1 << (digit - 1);

            if (CanCompleteAssignment(nextCells, remainingMask & ~bit))
                return true;
        }
        return false;
    }

    private bool ApplyHiddenSingles()
    {
        for (int r = 0; r < Board.Size; r++)
        {
            if (ApplyHiddenSingleToUnit(Enumerable.Range(0, Board.Size)
                .Select(c => (r, c))))
            {
                return true;
            }
        }

        for (int c = 0; c < Board.Size; c++)
        {
            if (ApplyHiddenSingleToUnit(
                Enumerable.Range(0, Board.Size)
                .Select(r => (r, c))))
            {
                return true;
            }
        }

        for (int boxRow = 0; boxRow < Board.Size; boxRow += Board.BoxSize)
        {
            for (int boxCol = 0; boxCol < Board.Size; boxCol += Board.BoxSize)
            {
                var cells = new List<(int Row, int Col)>();

                for (int r = boxRow; r < boxRow + Board.BoxSize; r++)
                {
                    for (int c = boxCol; c < boxCol + Board.BoxSize; c++)
                    {
                        cells.Add((r, c));
                    }
                }

                if (ApplyHiddenSingleToUnit(cells))
                    return true;
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
        int count = 0;
        while (mask != 0) { count += mask & 1; mask >>= 1; }
        return count;
    }

    private static IEnumerable<int> IterateDigits(int mask)
    {
        for (int digit = 1; digit <= 9; digit++)
            if ((mask & (1 << (digit - 1))) != 0)
                yield return digit;
    }
}