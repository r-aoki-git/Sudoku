using Sudoku.Models;

namespace Sudoku.Solvers;

/// <summary>
/// 盤面の現在の状態から、各マスに入りうる候補数字を計算する。
/// （プレイヤーが書き込む候補値メモとは別物。ソルバー内部で使う計算結果。）
/// </summary>
public class CandidateGrid
{
    private readonly HashSet<int>[,] _candidates = new HashSet<int>[Board.Size, Board.Size];

    public static CandidateGrid Calculate(Board board)
    {
        var grid = new CandidateGrid();

        for (int r = 0; r < Board.Size; r++)
        {
            for (int c = 0; c < Board.Size; c++)
            {
                var cell = board.GetCell(r, c);
                if (cell.HasValue)
                {
                    grid._candidates[r, c] = new HashSet<int> { cell.Value!.Value };
                    continue;
                }

                var used = new HashSet<int>();
                foreach (var rowCell in board.GetRow(r))
                    if (rowCell.HasValue) used.Add(rowCell.Value!.Value);
                foreach (var colCell in board.GetColumn(c))
                    if (colCell.HasValue) used.Add(colCell.Value!.Value);
                foreach (var boxCell in board.GetBox(r, c))
                    if (boxCell.HasValue) used.Add(boxCell.Value!.Value);

                var possible = new HashSet<int>();
                for (int v = 1; v <= 9; v++)
                    if (!used.Contains(v)) possible.Add(v);

                grid._candidates[r, c] = possible;
            }
        }

        return grid;
    }

    public HashSet<int> GetCandidates(int row, int col) => _candidates[row, col];

    /// <summary>指定したマスの候補から数字を1つ取り除く。取り除けたら true。</summary>
    public bool EliminateCandidate(int row, int col, int digit) => _candidates[row, col].Remove(digit);
}