using Sudoku.Models;

namespace Sudoku.Solvers;


/// <summary>
/// 盤面の「ユニット」（行・列・ブロック）の座標一覧を提供する共通ヘルパー
/// </summary>
public static class BoardUnits
{
    /// <summary>盤面上の27ユニット（行9 + 列9 + ブロック9）すべてを列挙する。</summary>
    public static IEnumerable<List<(int row, int col)>> All()
    {
        for (int r = 0; r < Board.Size; r++)
            yield return Row(r);

        for (int c = 0; c < Board.Size; c++)
            yield return Column(c);

        for (int boxRow = 0; boxRow < Board.Size; boxRow += Board.BoxSize)
            for (int boxCol = 0; boxCol < Board.Size; boxCol += Board.BoxSize)
                yield return Box(boxRow, boxCol);
    }

    public static List<(int row, int col)> Row(int row)
    {
        var positions = new List<(int, int)>();
        for (int c = 0; c < Board.Size; c++) positions.Add((row, c));
        return positions;
    }

    public static List<(int row, int col)> Column(int col)
    {
        var positions = new List<(int, int)>();
        for (int r = 0; r < Board.Size; r++) positions.Add((r, col));
        return positions;
    }

    public static List<(int row, int col)> Box(int boxRow, int boxCol)
    {
        var positions = new List<(int, int)>();
        for (int r = boxRow; r < boxRow + Board.BoxSize; r++)
            for (int c = boxCol; c < boxCol + Board.BoxSize; c++)
                positions.Add((r, c));
        return positions;
    }
}