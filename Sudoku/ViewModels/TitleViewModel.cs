using System.Windows.Input;
using Sudoku.Solvers;

namespace Sudoku.ViewModels;

///<summary>タイトル画面のViewModel。難易度選択・モード選択・新規ゲーム開始・続きから再開を扱う。</summary>
public class TitleViewModel : ViewModelBase
{
    public event EventHandler<(Difficulty Difficulty, GameMode Mode)>? StartGameRequested;
    public event EventHandler? ContinueGameRequested;

    private Difficulty _selectedDifficulty = Difficulty.Normal;
    public Difficulty SelectedDifficulty
    {
        get => _selectedDifficulty;
        set => SetProperty(ref _selectedDifficulty, value);
    }

    private GameMode _selectedGameMode = GameMode.Classic;
    public GameMode SelectedGameMode
    {
        get => _selectedGameMode;
        set => SetProperty(ref _selectedGameMode, value);
    }

    public IReadOnlyList<Difficulty> DifficultyOptions { get; } = Enum.GetValues<Difficulty>();
    public IReadOnlyList<GameMode> GameModeOptions { get; } = Enum.GetValues<GameMode>();

    public bool IsContinueAvailable { get; }

    public ICommand StartGameCommand { get; }
    public ICommand ContinueGameCommand { get; }

    public TitleViewModel(bool isContinueAvailable)
    {
        IsContinueAvailable = isContinueAvailable;

        StartGameCommand = new RelayCommand(_ => StartGameRequested?.Invoke(this, (SelectedDifficulty, SelectedGameMode)));
        ContinueGameCommand = new RelayCommand(
            _ => ContinueGameRequested?.Invoke(this, EventArgs.Empty),
            _ => IsContinueAvailable);
    }
}