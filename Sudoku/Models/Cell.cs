namespace Sudoku.Models;

/// <summary>
/// 盤面上の1マスを表す。
/// 確定値・初期配置かどうかのフラグ・候補値メモを保持する。
/// </summary>
public class Cell
{
    /// <summary>確定値（未入力の場合はnull）。1〜9の範囲。</summary>
    public int? Value { get; private set; }

    /// <summary>初期配置（問題として与えられた数字）かどうか。true の場合は編集不可。</summary>
    public bool IsGiven { get; }

    /// <summary>候補値メモ（プレイヤーが書き込む「候補の数字」）。</summary>
    public HashSet<int> CandidateMarks { get; } = new();

    public bool HasValue => Value.HasValue;

    public Cell(int? value = null, bool isGiven = false)
    {
        if (value.HasValue && (value.Value < 1 || value.Value > 9))
            throw new ArgumentOutOfRangeException(nameof(value), "セルの値は1〜9の範囲で指定してください。");

        Value = value;
        IsGiven = isGiven;
    }

    /// <summary>確定値を設定する。設定すると候補値メモは自動的にクリアされる。</summary>
    public void SetValue(int value)
    {
        EnsureEditable();
        if (value < 1 || value > 9)
            throw new ArgumentOutOfRangeException(nameof(value), "セルの値は1〜9の範囲で指定してください。");

        Value = value;
        CandidateMarks.Clear();
    }

    /// <summary>確定値を消去する。</summary>
    public void ClearValue()
    {
        EnsureEditable();
        Value = null;
    }

    /// <summary>候補値メモをトグルする（あれば消し、なければ追加する）。</summary>
    public void ToggleCandidate(int number)
    {
        EnsureEditable();
        if (number < 1 || number > 9)
            throw new ArgumentOutOfRangeException(nameof(number), "候補値は1〜9の範囲で指定してください。");

        if (!CandidateMarks.Remove(number))
            CandidateMarks.Add(number);
    }

    /// <summary>候補値メモから指定した数字を取り除く（存在しない場合は何もしない）。</summary>
    public void RemoveCandidate(int number) => CandidateMarks.Remove(number);

    /// <summary>このセルの複製を作成する（Undo/Redoやソルバーでの盤面複製に使用）。</summary>
    public Cell Clone()
    {
        var clone = new Cell(Value, IsGiven);
        foreach (var candidate in CandidateMarks)
            clone.CandidateMarks.Add(candidate);
        return clone;
    }

    private void EnsureEditable()
    {
        if (IsGiven)
            throw new InvalidOperationException("初期配置のマスは編集できません。");
    }

    public override string ToString() => Value?.ToString() ?? ".";
}