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

    public CellViewModel(Cell cell, int row, int col)
    {
        _cell = cell;
        Row = row;
        Col = col;

        for (int digit = 1; digit <= 9; digit++)
            Candidates.Add(new CandidateSlotViewModel(digit));

        UpdateCandidateTexts();
    }

    public bool HasValue => _cell.HasValue;
    public int? Value => _cell.Value;
    public string DisplayText => _cell.HasValue ? _cell.Value!.Value.ToString() : string.Empty;
    public bool IsGiven => _cell.IsGiven;

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