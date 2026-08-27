namespace Sudoku.ViewModels;

///<summary>候補値メモの3×3ミニグリッド内、1つの数字分の表示情報</summary>
public class CandidateSlotViewModel : ViewModelBase
{
    public int Digit { get; }

    public CandidateSlotViewModel(int digit)
    {
        Digit = digit;
    }

    private string _text = "";
    public string Text
    {
        get => _text;
        set => SetProperty(ref _text, value);
    }

    private bool _isHighlighted;
    public bool IsHighlighted
    {
        get => _isHighlighted;
        set => SetProperty(ref _isHighlighted, value);
    }
}