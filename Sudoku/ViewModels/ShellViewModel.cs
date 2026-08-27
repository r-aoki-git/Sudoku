using Sudoku.Services;

namespace Sudoku.ViewModels;

///<summary>
///アプリ全体のナビゲーションを管理するトップレベルのViewModel。
///現在表示中の画面をCurrentViewModelとして公開する。
///</summary>
public class ShellViewModel : ViewModelBase
{
    private readonly SaveDataService _saveService = new();

    private object _currentViewModel;
    public object CurrentViewModel
    {
        get => _currentViewModel;
        private set => SetProperty(ref _currentViewModel, value);
    }

    public ShellViewModel()
    {
        _currentViewModel = CreateTitleViewModel();
    }

    private TitleViewModel CreateTitleViewModel()
    {
        var vm = new TitleViewModel(_saveService.HasSaveData);
        vm.StartGameRequested += (_, difficulty) => ShowNewGame(difficulty);
        vm.ContinueGameRequested += (_, _) => ShowContinuedGame();
        return vm;
    }

    private void ShowNewGame(Sudoku.Solvers.Difficulty difficulty)
    {
        var gameViewModel = new GameViewModel(difficulty);
        AttachHomeNavigation(gameViewModel);
        CurrentViewModel = gameViewModel;
    }

    private void ShowContinuedGame()
    {
        var gameViewModel = GameViewModel.CreateFromSave(_saveService);
        if (gameViewModel is null) return; // ボタンはIsContinueAvailableで無効化されているはずだが、念のため

        AttachHomeNavigation(gameViewModel);
        CurrentViewModel = gameViewModel;
    }

    private void AttachHomeNavigation(GameViewModel gameViewModel)
    {
        gameViewModel.HomeRequested += (_, _) => CurrentViewModel = CreateTitleViewModel();
    }
}