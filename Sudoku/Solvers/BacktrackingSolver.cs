using Sudoku.Models;

namespace Sudoku.Solvers;

/// <summary>
/// バックトラッキングによる解法エンジン。
/// ・完成盤面のランダム生成（generateモード）
/// ・既存の問題を解く（solveモード）
/// ・唯一解かどうかの判定（解の個数を数える）
/// の3つの用途に使う。
/// </summary>
public class BacktrackingSolver
{
    private readonly Random _random;

    public BacktrackingSolver(Random? random = null)
    {
        _random = random ?? new Random();
    }

    /// <summary>
    /// 盤面を解いて埋める（既に入っている数字はそのまま使う）。
    /// 従来API互換版。
    /// </summary>
    public bool TrySolve(Board board)
        => TrySolve(board, CancellationToken.None);

    /// <summary>
    /// 盤面を解いて埋める。
    /// キャンセル要求が来た場合は OperationCanceledException を送出する。
    /// </summary>
    public bool TrySolve(
        Board board,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Solve(
            board,
            randomizeOrder: false,
            cancellationToken);
    }

    /// <summary>
    /// 空の盤面からランダムな完成盤面を1つ生成する。
    /// 従来API互換版。
    /// </summary>
    public bool TryGenerateFullGrid(Board board)
        => TryGenerateFullGrid(board, CancellationToken.None);

    /// <summary>
    /// 空の盤面からランダムな完成盤面を1つ生成する。
    ///
    /// ParallelKillerSudokuGeneratorからのキャンセルを
    /// 完成盤面生成の再帰探索まで伝播させる。
    /// </summary>
    public bool TryGenerateFullGrid(
        Board board,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Solve(
            board,
            randomizeOrder: true,
            cancellationToken);
    }

    /// <summary>
    /// 解の個数をlimit件までカウントする。
    /// 従来API互換版。
    /// </summary>
    public int CountSolutions(
        Board board,
        int limit = 2)
        => CountSolutions(
            board,
            limit,
            CancellationToken.None);

    /// <summary>
    /// 解の個数をlimit件までカウントする。
    /// キャンセル要求が来た場合は OperationCanceledException を送出する。
    /// </summary>
    public int CountSolutions(
        Board board,
        int limit,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        int count = 0;

        CountRecursive(
            board,
            limit,
            ref count,
            cancellationToken);

        return count;
    }

    private bool Solve(
        Board board,
        bool randomizeOrder,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var emptyCell = FindEmptyCell(board);

        if (emptyCell is null)
            return true;

        var (row, col) = emptyCell.Value;

        var candidates =
            CreateCandidateOrder(randomizeOrder);

        foreach (var number in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!CanPlace(
                    board,
                    row,
                    col,
                    number))
            {
                continue;
            }

            board
                .GetCell(row, col)
                .SetValue(number);

            try
            {
                if (Solve(
                        board,
                        randomizeOrder,
                        cancellationToken))
                {
                    return true;
                }
            }
            catch
            {
                // 通常の失敗は下で値を戻す。
                // OperationCanceledException もここに入るが、
                // 値を確実に戻してから再送出する。
                board
                    .GetCell(row, col)
                    .ClearValue();

                throw;
            }

            board
                .GetCell(row, col)
                .ClearValue();
        }

        return false;
    }

    private void CountRecursive(
        Board board,
        int limit,
        ref int count,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (count >= limit)
            return;

        var emptyCell = FindEmptyCell(board);

        if (emptyCell is null)
        {
            count++;
            return;
        }

        var (row, col) = emptyCell.Value;

        foreach (var number in CreateCandidateOrder(
                     randomizeOrder: false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (count >= limit)
                return;

            if (!CanPlace(
                    board,
                    row,
                    col,
                    number))
            {
                continue;
            }

            board
                .GetCell(row, col)
                .SetValue(number);

            try
            {
                CountRecursive(
                    board,
                    limit,
                    ref count,
                    cancellationToken);
            }
            catch
            {
                board
                    .GetCell(row, col)
                    .ClearValue();

                throw;
            }

            board
                .GetCell(row, col)
                .ClearValue();
        }
    }

    private static (int row, int col)? FindEmptyCell(
        Board board)
    {
        for (int r = 0; r < Board.Size; r++)
        {
            for (int c = 0; c < Board.Size; c++)
            {
                if (!board
                        .GetCell(r, c)
                        .HasValue)
                {
                    return (r, c);
                }
            }
        }

        return null;
    }

    private static bool CanPlace(
        Board board,
        int row,
        int col,
        int value)
    {
        foreach (var cell in board.GetRow(row))
        {
            if (cell.Value == value)
                return false;
        }

        foreach (var cell in board.GetColumn(col))
        {
            if (cell.Value == value)
                return false;
        }

        foreach (var cell in board.GetBox(row, col))
        {
            if (cell.Value == value)
                return false;
        }

        return true;
    }

    private List<int> CreateCandidateOrder(
        bool randomizeOrder)
    {
        var candidates =
            Enumerable
                .Range(1, 9)
                .ToList();

        if (!randomizeOrder)
            return candidates;

        for (int i = candidates.Count - 1; i > 0; i--)
        {
            int j =
                _random.Next(i + 1);

            (candidates[i], candidates[j]) =
                (candidates[j], candidates[i]);
        }

        return candidates;
    }
}