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
    /// 盤面を解いて埋める（既に入っている数字はそのまま使う）。解けたらtrue。
    /// 「解答を見る」機能や、唯一解が保証された問題を解くのに使う。
    /// </summary>
    public bool TrySolve(Board board) => Solve(board, randomizeOrder: false);

    /// <summary>
    /// 空の盤面からランダムな完成盤面を1つ生成する（問題自動生成の最初のステップで使う）。
    /// </summary>
    public bool TryGenerateFullGrid(Board board) => Solve(board, randomizeOrder: true);

    /// <summary>
    /// 解の個数を limit 件までカウントする。唯一解かどうかの判定に使う
    /// （2件見つかった時点で打ち切るので、limit=2 のとき戻り値が1なら「唯一解」）。
    /// </summary>
    public int CountSolutions(Board board, int limit = 2)
    {
        int count = 0;
        CountRecursive(board, limit, ref count);
        return count;
    }

    private bool Solve(Board board, bool randomizeOrder)
    {
        var emptyCell = FindEmptyCell(board);
        if (emptyCell is null)
            return true; // 空きマスがなければ完成

        var (row, col) = emptyCell.Value;
        var candidates = CreateCandidateOrder(randomizeOrder);

        foreach (var number in candidates)
        {
            if (!CanPlace(board, row, col, number)) continue;

            board.GetCell(row, col).SetValue(number);

            if (Solve(board, randomizeOrder))
                return true;

            board.GetCell(row, col).ClearValue();
        }

        return false; // どの数字でも進めなかった → 手前の選択が誤り
    }

    private void CountRecursive(Board board, int limit, ref int count)
    {
        if (count >= limit) return;

        var emptyCell = FindEmptyCell(board);
        if (emptyCell is null)
        {
            count++;
            return;
        }

        var (row, col) = emptyCell.Value;

        foreach (var number in CreateCandidateOrder(randomizeOrder: false))
        {
            if (count >= limit) return;
            if (!CanPlace(board, row, col, number)) continue;

            board.GetCell(row, col).SetValue(number);
            CountRecursive(board, limit, ref count);
            board.GetCell(row, col).ClearValue();
        }
    }

    private static (int row, int col)? FindEmptyCell(Board board)
    {
        for (int r = 0; r < Board.Size; r++)
            for (int c = 0; c < Board.Size; c++)
                if (!board.GetCell(r, c).HasValue)
                    return (r, c);

        return null;
    }

    private static bool CanPlace(Board board, int row, int col, int value)
    {
        foreach (var cell in board.GetRow(row))
            if (cell.Value == value) return false;

        foreach (var cell in board.GetColumn(col))
            if (cell.Value == value) return false;

        foreach (var cell in board.GetBox(row, col))
            if (cell.Value == value) return false;

        return true;
    }

    private List<int> CreateCandidateOrder(bool randomizeOrder)
    {
        var candidates = Enumerable.Range(1, 9).ToList();
        if (!randomizeOrder) return candidates;

        for (int i = candidates.Count - 1; i > 0; i--)
        {
            int j = _random.Next(i + 1);
            (candidates[i], candidates[j]) = (candidates[j], candidates[i]);
        }
        return candidates;
    }
}