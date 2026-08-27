using System.Text;

namespace Sudoku.Models;

/// <summary>
/// 9x9のナンプレ盤面。行・列・ブロック単位でのアクセス、妥当性チェック、複製を提供する。
/// </summary>
public class Board
{
    public const int Size = 9;
    public const int BoxSize = 3;

    private readonly Cell[,] _cells;

    public Board()
    {
        _cells = new Cell[Size, Size];
        for (int r = 0; r < Size; r++)
            for (int c = 0; c < Size; c++)
                _cells[r, c] = new Cell();
    }

    /// <summary>指定した行・列のセルを取得する（0始まり）。</summary>
    public Cell GetCell(int row, int col)
    {
        ValidateIndex(row, col);
        return _cells[row, col];
    }

    /// <summary>指定した行のセルを列挙する。</summary>
    public IEnumerable<Cell> GetRow(int row)
    {
        for (int c = 0; c < Size; c++)
            yield return _cells[row, c];
    }

    /// <summary>指定した列のセルを列挙する。</summary>
    public IEnumerable<Cell> GetColumn(int col)
    {
        for (int r = 0; r < Size; r++)
            yield return _cells[r, col];
    }

    /// <summary>指定したマスが属する3x3ブロックのセルを列挙する。</summary>
    public IEnumerable<Cell> GetBox(int row, int col)
    {
        int boxRow = (row / BoxSize) * BoxSize;
        int boxCol = (col / BoxSize) * BoxSize;
        for (int r = boxRow; r < boxRow + BoxSize; r++)
            for (int c = boxCol; c < boxCol + BoxSize; c++)
                yield return _cells[r, c];
    }

    /// <summary>盤面が現時点でナンプレのルール（行・列・ブロックに重複がない）を満たしているか判定する。</summary>
    public bool IsValid()
    {
        for (int i = 0; i < Size; i++)
        {
            if (HasDuplicate(GetRow(i))) return false;
            if (HasDuplicate(GetColumn(i))) return false;
        }

        for (int boxRow = 0; boxRow < Size; boxRow += BoxSize)
            for (int boxCol = 0; boxCol < Size; boxCol += BoxSize)
                if (HasDuplicate(GetBox(boxRow, boxCol))) return false;

        return true;
    }

    /// <summary>全マスが埋まっており、かつルールを満たしているか（クリア判定）。</summary>
    public bool IsComplete()
    {
        for (int r = 0; r < Size; r++)
            for (int c = 0; c < Size; c++)
                if (!_cells[r, c].HasValue)
                    return false;

        return IsValid();
    }

    private static bool HasDuplicate(IEnumerable<Cell> cells)
    {
        var seen = new HashSet<int>();
        foreach (var cell in cells)
        {
            if (!cell.HasValue) continue;
            if (!seen.Add(cell.Value!.Value))
                return true;
        }
        return false;
    }

    /// <summary>盤面全体を複製する（Undo/Redoやソルバーでの探索に使用）。</summary>
    public Board Clone()
    {
        var clone = new Board();
        for (int r = 0; r < Size; r++)
            for (int c = 0; c < Size; c++)
                clone._cells[r, c] = _cells[r, c].Clone();
        return clone;
    }

    /// <summary>指定したマスの初期配置状態を解除し、空マスに戻す（問題生成時の間引き専用）。</summary>
    public void ClearGivenAt(int row, int col)
    {
        ValidateIndex(row, col);
        _cells[row, col] = new Cell();
    }

    /// <summary>指定した値を初期配置として設定する（完成盤面から問題を組み立てる際に使用）。</summary>
    public void SetGivenAt(int row, int col, int value)
    {
        ValidateIndex(row, col);
        _cells[row, col] = new Cell(value, isGiven: true);
    }

    /// <summary>
    /// 81文字の文字列から盤面を読み込む。'0' または '.' は空マス、それ以外は初期配置の数字として扱う。
    /// </summary>
    public static Board LoadFromString(string puzzle)
    {
        if (puzzle.Length != Size * Size)
            throw new ArgumentException($"盤面文字列は{Size * Size}文字である必要があります。", nameof(puzzle));

        var board = new Board();
        for (int i = 0; i < puzzle.Length; i++)
        {
            int row = i / Size;
            int col = i % Size;
            char ch = puzzle[i];

            board._cells[row, col] = (ch == '0' || ch == '.')
                ? new Cell()
                : new Cell(ch - '0', isGiven: true);
        }
        return board;
    }

    /// <summary>盤面をコンソール向けの整形済み文字列として出力する。</summary>
    public override string ToString()
    {
        var sb = new StringBuilder();
        for (int r = 0; r < Size; r++)
        {
            if (r != 0 && r % BoxSize == 0)
                sb.AppendLine("------+-------+------");

            for (int c = 0; c < Size; c++)
            {
                if (c != 0 && c % BoxSize == 0)
                    sb.Append("| ");

                sb.Append(_cells[r, c]);
                sb.Append(' ');
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static void ValidateIndex(int row, int col)
    {
        if (row < 0 || row >= Size)
            throw new ArgumentOutOfRangeException(nameof(row));
        if (col < 0 || col >= Size)
            throw new ArgumentOutOfRangeException(nameof(col));
    }
}