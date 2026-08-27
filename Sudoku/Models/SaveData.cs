namespace Sudoku.Models;

/// <summary>セーブファイルに書き出す1マス分のデータ</summary>
public class CellSaveData
{
    public int Row { get; set; }
    public int Col { get; set; }
    public int? Value { get; set; }
    public bool IsGiven { get; set; }
    public List<int> Candidates { get; set; } = new();
}

/// <summary>セーブファイル全体のデータ構造</summary>
public class SaveData
{
    public Sudoku.Solvers.Difficulty Difficulty { get; set; }
    public double ElapsedSeconds { get; set; }
    public List<CellSaveData> Puzzle { get; set; } = new();

    /// <summary>正解の盤面。81マス分、行優先で値のみを保持</summary>
    public List<int> Solution { get; set; } = new();
}