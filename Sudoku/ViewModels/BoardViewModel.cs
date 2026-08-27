using System.Collections.ObjectModel;
using System.Linq;
using Sudoku.Models;
using Sudoku.Models.Commands;
using System.Windows.Input;

namespace Sudoku.ViewModels;

/// <summary>盤面全体のViewModel。81個のCellViewModelを保持し、マス選択とハイライトを管理する。</summary>
public class BoardViewModel : ViewModelBase
{
    private readonly Board _board;
    private readonly Board _solution;
    private readonly CommandHistory _history = new();

    public event EventHandler? PuzzleSolved;

    public ObservableCollection<CellViewModel> Cells { get; } = new();

    private CellViewModel? _selectedCell;
    public CellViewModel? SelectedCell
    {
        get => _selectedCell;
        private set => SetProperty(ref _selectedCell, value);
    }

    private bool _memoMode;
    public bool MemoMode
    {
        get => _memoMode;
        private set => SetProperty(ref _memoMode, value);
    }

    private bool _isDigit1Available = true;
    public bool IsDigit1Available { get => _isDigit1Available; private set => SetProperty(ref _isDigit1Available, value); }
    private bool _isDigit2Available = true;
    public bool IsDigit2Available { get => _isDigit2Available; private set => SetProperty(ref _isDigit2Available, value); }
    private bool _isDigit3Available = true;
    public bool IsDigit3Available { get => _isDigit3Available; private set => SetProperty(ref _isDigit3Available, value); }
    private bool _isDigit4Available = true;
    public bool IsDigit4Available { get => _isDigit4Available; private set => SetProperty(ref _isDigit4Available, value); }
    private bool _isDigit5Available = true;
    public bool IsDigit5Available { get => _isDigit5Available; private set => SetProperty(ref _isDigit5Available, value); }
    private bool _isDigit6Available = true;
    public bool IsDigit6Available { get => _isDigit6Available; private set => SetProperty(ref _isDigit6Available, value); }
    private bool _isDigit7Available = true;
    public bool IsDigit7Available { get => _isDigit7Available; private set => SetProperty(ref _isDigit7Available, value); }
    private bool _isDigit8Available = true;
    public bool IsDigit8Available { get => _isDigit8Available; private set => SetProperty(ref _isDigit8Available, value); }
    private bool _isDigit9Available = true;
    public bool IsDigit9Available { get => _isDigit9Available; private set => SetProperty(ref _isDigit9Available, value); }

    public ICommand EnterDigitCommand { get; }
    public ICommand ClearCommand { get; }

    public ICommand ToggleMemoModeCommand { get; }

    /// <summary>隠しモード：ONのときはホバー中のマスの行・列全体をハイライトする（右クリックで切り替え）。</summary>
    private bool _hoverAssistMode;
    public bool HoverAssistMode
    {
        get => _hoverAssistMode;
        private set => SetProperty(ref _hoverAssistMode, value);
    }

    public BoardViewModel(Board board, Board solution)
    {
        ToggleMemoModeCommand = new RelayCommand(_ => ToggleMemoMode());

        EnterDigitCommand = new RelayCommand(param =>
        {
            if (param is string text && int.TryParse(text, out int digit))
                EnterDigit(digit);
        });
        ClearCommand = new RelayCommand(_ => ClearSelectedCell());

        _board = board;
        _solution = solution;

        for (int r = 0; r < Board.Size; r++)
            for (int c = 0; c < Board.Size; c++)
                Cells.Add(new CellViewModel(_board.GetCell(r, c), r, c));

        RefreshConflicts();
    }

    public void ToggleMemoMode() => MemoMode = !MemoMode;

    public void ToggleHoverAssistMode()
    {
        HoverAssistMode = !HoverAssistMode;
        ClearHover();
    }

    public void HoverCell(int row, int col)
    {
        if (!HoverAssistMode) return;

        foreach (var cell in Cells)
            cell.IsHoverHighlight = cell.Row == row || cell.Col == col;
    }

    public void ClearHover()
    {
        foreach (var cell in Cells)
            cell.IsHoverHighlight = false;
    }

    /// <summary>マスを選択する。extendMatchHighlight=true（Shift押下時）は、同じ数字のマスの行・列も追加でハイライトする。</summary>
    public void SelectCell(int row, int col, bool extendMatchHighlight = false)
    {
        var targetCell = _board.GetCell(row, col);
        int? matchDigit = targetCell.HasValue ? targetCell.Value : null;

        foreach (var cell in Cells)
        {
            cell.IsSelected = cell.Row == row && cell.Col == col;
            cell.IsRelatedHighlight = !cell.IsSelected && IsRelated(cell.Row, cell.Col, row, col);
            cell.IsSameNumberHighlight = matchDigit.HasValue && cell.HasValue && cell.Value == matchDigit;
            cell.SetHighlightedDigit(matchDigit);
            cell.IsMatchRowColHighlight = false;
        }

        if (extendMatchHighlight && matchDigit.HasValue)
        {
            var matchPositions = Cells.Where(c => c.IsSameNumberHighlight).ToList();
            foreach (var cell in Cells)
            {
                if (cell.IsSameNumberHighlight) continue;
                cell.IsMatchRowColHighlight = matchPositions.Any(m =>
                    m.Row == cell.Row || m.Col == cell.Col || IsSameBox(m.Row, m.Col, cell.Row, cell.Col));
            }
        }

        SelectedCell = Cells.First(c => c.Row == row && c.Col == col);
    }

    private IEnumerable<Cell> GetRelatedCells(int row, int col)
    {
        var result = new HashSet<Cell>();
        foreach (var c in _board.GetRow(row)) result.Add(c);
        foreach (var c in _board.GetColumn(col)) result.Add(c);
        foreach (var c in _board.GetBox(row, col)) result.Add(c);
        result.Remove(_board.GetCell(row, col));
        return result;
    }

    public void MoveSelection(int rowDelta, int colDelta)
    {
        int currentRow = SelectedCell?.Row ?? 0;
        int currentCol = SelectedCell?.Col ?? 0;

        int newRow = Math.Clamp(currentRow + rowDelta, 0, Board.Size - 1);
        int newCol = Math.Clamp(currentCol + colDelta, 0, Board.Size - 1);

        SelectCell(newRow, newCol);
    }

    public void EnterDigit(int digit)
    {
        if (SelectedCell is null || SelectedCell.IsGiven) return;

        var cell = _board.GetCell(SelectedCell.Row, SelectedCell.Col);

        if (MemoMode)
        {
            if (cell.HasValue) return; // 確定値のあるマスにはメモを付けない
            _history.Execute(new ToggleCandidateCommand(cell, digit));
        }
        else if (cell.HasValue && cell.Value == digit)
        {
            _history.Execute(new ClearValueCommand(cell));
        }
        else
        {
            var relatedCells = GetRelatedCells(SelectedCell.Row, SelectedCell.Col);
            _history.Execute(new SetValueCommand(cell, digit, relatedCells));
        }

        RefreshAllCells();
        RefreshConflicts();
        SelectCell(SelectedCell.Row, SelectedCell.Col);
    }

    public void ClearSelectedCell()
    {
        if (SelectedCell is null || SelectedCell.IsGiven) return;

        var cell = _board.GetCell(SelectedCell.Row, SelectedCell.Col);
        _history.Execute(new ClearValueCommand(cell));

        RefreshAllCells();
        RefreshConflicts();
        SelectCell(SelectedCell.Row, SelectedCell.Col);
    }

    public void Undo()
    {
        _history.Undo();
        RefreshAllCells();
        RefreshConflicts();
        if (SelectedCell is not null) SelectCell(SelectedCell.Row, SelectedCell.Col);
    }

    public void Redo()
    {
        _history.Redo();
        RefreshAllCells();
        RefreshConflicts();
        if (SelectedCell is not null) SelectCell(SelectedCell.Row, SelectedCell.Col);
    }

    private void RefreshAllCells()
    {
        foreach (var cell in Cells)
            cell.Refresh();
    }

    /// <summary>セーブ機能用に、内部で保持している盤面（問題）を取得する。</summary>
    public Board GetBoardSnapshot() => _board;

    /// <summary>セーブ機能用に、内部で保持している正解盤面を取得する。</summary>
    public Board GetSolutionSnapshot() => _solution;

    private void RefreshConflicts()
    {
        foreach (var cell in Cells)
            cell.IsConflict = false;

        for (int i = 0; i < Board.Size; i++)
        {
            MarkConflicts(Cells.Where(c => c.Row == i));
            MarkConflicts(Cells.Where(c => c.Col == i));
        }

        for (int boxRow = 0; boxRow < Board.Size; boxRow += Board.BoxSize)
            for (int boxCol = 0; boxCol < Board.Size; boxCol += Board.BoxSize)
                MarkConflicts(Cells.Where(c =>
                    c.Row >= boxRow && c.Row < boxRow + Board.BoxSize &&
                    c.Col >= boxCol && c.Col < boxCol + Board.BoxSize));

        // 重複がなくても、正解と異なる数字が入っているマスは赤く表示する。
        foreach (var cell in Cells)
        {
            if (cell.IsGiven || !cell.HasValue) continue;
            if (cell.Value != _solution.GetCell(cell.Row, cell.Col).Value)
                cell.IsConflict = true;
        }

        UpdateNumberPadAvailability();

        if (_board.IsComplete())
            PuzzleSolved?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>各数字が盤面に9個すべて置かれているかを判定し、数字パッドの有効/無効に反映する。</summary>
    private void UpdateNumberPadAvailability()
    {
        var counts = new int[10];
        foreach (var cell in Cells)
            if (cell.HasValue) counts[cell.Value!.Value]++;

        IsDigit1Available = counts[1] < 9;
        IsDigit2Available = counts[2] < 9;
        IsDigit3Available = counts[3] < 9;
        IsDigit4Available = counts[4] < 9;
        IsDigit5Available = counts[5] < 9;
        IsDigit6Available = counts[6] < 9;
        IsDigit7Available = counts[7] < 9;
        IsDigit8Available = counts[8] < 9;
        IsDigit9Available = counts[9] < 9;
    }

    private static void MarkConflicts(IEnumerable<CellViewModel> unit)
    {
        var groups = unit.Where(c => c.HasValue).GroupBy(c => c.Value);
        foreach (var group in groups)
            if (group.Count() > 1)
                foreach (var cell in group)
                    cell.IsConflict = true;
    }

    private static bool IsRelated(int row, int col, int selectedRow, int selectedCol)
    {
        if (row == selectedRow) return true;
        if (col == selectedCol) return true;

        int boxRow = (row / Board.BoxSize) * Board.BoxSize;
        int boxCol = (col / Board.BoxSize) * Board.BoxSize;
        int selectedBoxRow = (selectedRow / Board.BoxSize) * Board.BoxSize;
        int selectedBoxCol = (selectedCol / Board.BoxSize) * Board.BoxSize;

        return boxRow == selectedBoxRow && boxCol == selectedBoxCol;
    }

    private static bool IsSameBox(int row1, int col1, int row2, int col2)
    {
        int boxRow1 = (row1 / Board.BoxSize) * Board.BoxSize;
        int boxCol1 = (col1 / Board.BoxSize) * Board.BoxSize;
        int boxRow2 = (row2 / Board.BoxSize) * Board.BoxSize;
        int boxCol2 = (col2 / Board.BoxSize) * Board.BoxSize;

        return boxRow1 == boxRow2 && boxCol1 == boxCol2;
    }
}