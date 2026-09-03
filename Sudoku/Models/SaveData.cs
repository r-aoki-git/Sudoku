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

///<summary>セーブファイルに書き出す1ケージ分のデータ（キラーナンプレ用）</summary>
public class CageSaveData
{
    ///<summary>ケージに属するマスのインデックス（row * 9 + col）の一覧</summary>
    public List<int> CellIndexes { get; set; } = new();
    public int TargetSum { get; set; }
}

/// <summary>セーブファイル全体のデータ構造</summary>
public class SaveData
{
    public Sudoku.Solvers.Difficulty Difficulty { get; set; }

    /// <summary>ゲームモード（通常 or キラー）</summary>
    public Sudoku.Solvers.GameMode Mode { get; set; } = Sudoku.Solvers.GameMode.Classic;

    public double ElapsedSeconds { get; set; }
    public List<CellSaveData> Puzzle { get; set; } = new();

    /// <summary>正解の盤面。81マス分、行優先で値のみを保持</summary>
    public List<int> Solution { get; set; } = new();

    /// <summary>キラーナンプレ用のケージ一覧。通常モードでは空のまま。</summary>
    public List<CageSaveData> Cages { get; set; } = new();
}