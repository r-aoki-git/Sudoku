using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Sudoku.ViewModels;

namespace Sudoku.Views;

public partial class GameView : UserControl
{
    public GameView()
    {
        InitializeComponent();
        Focusable = true;
        Loaded += (_, _) => Focus();
        PreviewKeyDown += MainWindow_PreviewKeyDown;
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not GameViewModel vm) return;
        var board = vm.Board;

        switch (e.Key)
        {
            case Key.Up: board.MoveSelection(-1, 0); e.Handled = true; break;
            case Key.Down: board.MoveSelection(1, 0); e.Handled = true; break;
            case Key.Left: board.MoveSelection(0, -1); e.Handled = true; break;
            case Key.Right: board.MoveSelection(0, 1); e.Handled = true; break;
            case Key.M: board.ToggleMemoMode(); e.Handled = true; break;
            case Key.Delete:
            case Key.Back:
                board.ClearSelectedCell();
                e.Handled = true;
                break;
            case Key.Z when Keyboard.Modifiers == ModifierKeys.Control:
                board.Undo();
                e.Handled = true;
                break;
            case Key.Y when Keyboard.Modifiers == ModifierKeys.Control:
                board.Redo();
                e.Handled = true;
                break;
            default:
                int? digit = GetDigit(e.Key);
                if (digit.HasValue)
                {
                    board.EnterDigit(digit.Value);
                    e.Handled = true;
                }
                break;
        }
    }

    private static int? GetDigit(Key key)
    {
        if (key >= Key.D1 && key <= Key.D9) return key - Key.D0;
        if (key >= Key.NumPad1 && key <= Key.NumPad9) return key - Key.NumPad0;
        return null;
    }

    private void Cell_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (fe.DataContext is not CellViewModel cell) return;
        if (DataContext is not GameViewModel vm) return;

        bool shiftHeld = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        vm.Board.SelectCell(cell.Row, cell.Col, shiftHeld);
        e.Handled = true;
    }

    private void Cell_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (fe.DataContext is not CellViewModel cell) return;
        if (DataContext is not GameViewModel vm) return;

        vm.Board.HoverCell(cell.Row, cell.Col);
    }

    private void Cell_MouseLeave(object sender, MouseEventArgs e)
    {
        if (DataContext is not GameViewModel vm) return;
        vm.Board.ClearHover();
    }

    private void Board_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not GameViewModel vm) return;
        vm.Board.ToggleHoverAssistMode();
        e.Handled = true;
    }
}