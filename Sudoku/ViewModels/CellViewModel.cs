using System.Collections.ObjectModel;
using System.Linq;
using Sudoku.Models;

namespace Sudoku.ViewModels;

/// <summary>1マス分の表示用ViewModel。Cellをラップし、UIバインド用のプロパティを公開する。</summary>
public class CellViewModel : ViewModelBase
{
    private readonly Cell _cell;

    public int Row { get; }
    public int Col { get; }

    public CellViewModel(Cell cell, int row, int col, CageCellInfo? cageInfo = null)
    {
        _cell = cell;
        Row = row;
        Col = col;

        CageSumText = cageInfo?.SumText ?? "";
        IsKillerCell = cageInfo is not null;

        for (int digit = 1; digit <= 9; digit++)
            Candidates.Add(new CandidateSlotViewModel(digit));

        UpdateCandidateTexts();
    }

    public bool HasValue => _cell.HasValue;
    public int? Value => _cell.Value;
    public string DisplayText => _cell.HasValue ? _cell.Value!.Value.ToString() : string.Empty;
    public bool IsGiven => _cell.IsGiven;

    ///<summary>
    ///キラーナンプレの盤面のマスかどうか。キラーでは全マスがいずれかのケージに属するため、
    ///81マスすべてでtrueになる（通常モードでは全マスfalse）。
    ///
    ///ケージ合計値は左上に描かれ、候補値メモの「1」の位置と重なるため、
    ///キラーではメモの3×3グリッド全体を合計値ラベルの分だけ下げる。
    ///合計値ラベルを持つマスだけを下げると、マスごとにメモの位置がずれて読みにくくなるので、
    ///盤面全体で揃える。
    ///</summary>
    public bool IsKillerCell { get; }

    ///<summary>キラーナンプレ：ケージ合計値ラベル（表示するマスのみ非空）。
    ///ケージの枠線はマス単位ではなく、BoardViewModel.CageOutlineが盤面全体で1本のPathとして描く。</summary>
    public string CageSumText { get; }

    /// <summary>候補値メモの3×3ミニグリッド</summary>
    public ObservableCollection<CandidateSlotViewModel> Candidates { get; } = new();

    private int? _highlightedDigit;
    public void SetHighlightedDigit(int? digit)
    {
        _highlightedDigit = digit;
        foreach (var slot in Candidates)
            slot.IsHighlighted = slot.Digit == digit && _cell.CandidateMarks.Contains(slot.Digit);
    }

    private void UpdateCandidateTexts()
    {
        foreach (var slot in Candidates)
            slot.Text = _cell.CandidateMarks.Contains(slot.Digit) ? slot.Digit.ToString() : "";
    }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    private bool _isRelatedHighlight;
    public bool IsRelatedHighlight
    {
        get => _isRelatedHighlight;
        set => SetProperty(ref _isRelatedHighlight, value);
    }

    private bool _isConflict;
    public bool IsConflict
    {
        get => _isConflict;
        set => SetProperty(ref _isConflict, value);
    }

    /// <summary>隠しモード中に、ホバー中のマスの行・列であることを示す。</summary>
    private bool _isHoverHighlight;
    public bool IsHoverHighlight
    {
        get => _isHoverHighlight;
        set => SetProperty(ref _isHoverHighlight, value);
    }

    ///<summary>選択したマスと同じ数字が確定しているマスであることを示す。</summary>
    private bool _isSameNumberHighlight;
    public bool IsSameNumberHighlight
    {
        get => _isSameNumberHighlight;
        set => SetProperty(ref _isSameNumberHighlight, value);
    }

    ///<summary>Shift＋クリック時、同じ数字のマスの行・列であることを示す。</summary>
    private bool _isMatchRowColHighlight;
    public bool IsMatchRowColHighlight
    {
        get => _isMatchRowColHighlight;
        set => SetProperty(ref _isMatchRowColHighlight, value);
    }

    /// <summary>Cellの値・候補値メモが変わった後に呼ぶ。</summary>
    public void Refresh()
    {
        OnPropertyChanged(string.Empty);
        UpdateCandidateTexts();
        SetHighlightedDigit(_highlightedDigit); // ハイライト状態も候補値メモの変化に追従させる
    }
}