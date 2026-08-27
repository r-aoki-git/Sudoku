using System.Security.Policy;
using System.Windows.Input;
using System.Windows.Threading;
using Sudoku.Generators;
using Sudoku.Models;
using Sudoku.Services;
using Sudoku.Solvers;

namespace Sudoku.ViewModels;

/// <summary>ゲーム画面全体のViewModel。盤面・タイマー・ホームへの復帰・セーブ/ロードを管理する。</summary>
public class GameViewModel : ViewModelBase
{
    private readonly DispatcherTimer _timer;
    private readonly SaveDataService _saveService = new();
    private Difficulty _difficulty;
    private DateTime _startTime;

    public event EventHandler? HomeRequested;

    private BoardViewModel _board = null!;
    public BoardViewModel Board
    {
        get => _board;
        private set => SetProperty(ref _board, value);
    }

    private string _elapsedText = "00:00";
    public string ElapsedText
    {
        get => _elapsedText;
        private set => SetProperty(ref _elapsedText, value);
    }

    private bool _isClearPopupVisible;
    public bool IsClearPopupVisible
    {
        get => _isClearPopupVisible;
        private set => SetProperty(ref _isClearPopupVisible, value);
    }

    private string _clearTimeText = "";
    public string ClearTimeText
    {
        get => _clearTimeText;
        private set => SetProperty(ref _clearTimeText, value);
    }

    public ICommand NewGameCommand { get; }
    public ICommand HomeCommand { get; }

    ///<summary>新規ゲームとして開始する場合のコンストラクタ。</summary>
    public GameViewModel(Difficulty difficulty) : this(difficulty, null)
    {
    }

    /// <summary>セーブデータから再開する場合は、resumeDataに読み込んだ内容を渡す。</summary>
    private GameViewModel(Difficulty difficulty, (Board Puzzle, Board Solution, TimeSpan Elapsed)? resumeData)
    {
        _difficulty = difficulty;

        NewGameCommand = new RelayCommand(_ => StartNewGame());
        HomeCommand = new RelayCommand(_ => GoHome());

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => UpdateElapsedText();

        if (resumeData.HasValue)
            ResumeGame(resumeData.Value.Puzzle, resumeData.Value.Solution, resumeData.Value.Elapsed);
        else
            StartNewGame();
    }

    /// <summary>セーブデータから続きを再開するゲーム画面を作る（読み込みに失敗したらnullを返す）</summary>
    public static GameViewModel? CreateFromSave(SaveDataService saveService)
    {
        var loaded = saveService.Load();
        if (loaded is null) return null;

        return new GameViewModel(loaded.Value.Difficulty,
            (loaded.Value.Puzzle, loaded.Value.Solution, loaded.Value.Elapsed));
    }

    private void StartNewGame()
    {
        IsClearPopupVisible = false;

        var generator = new SudokuGenerator();
        int targetGivens = GetTargetGivens(_difficulty);
        var (puzzle, solution) = generator.GeneratePuzzle(targetGivens);

        AttachBoard(puzzle, solution);

        _startTime = DateTime.Now;
        UpdateElapsedText();
        _timer.Start();
    }

    private void ResumeGame(Board puzzle, Board solution, TimeSpan elapsed)
    {
        IsClearPopupVisible = false;

        AttachBoard(puzzle, solution);

        _startTime = DateTime.Now - elapsed;
        UpdateElapsedText();
        _timer.Start();
    }

    private void AttachBoard(Board puzzle, Board solution)
    {
        Board = new BoardViewModel(puzzle, solution);
        Board.PuzzleSolved += OnPuzzleSolved;
    }

    /// <summary>現在の進行状況を保存する。クリア済みの場合は、代わりにセーブデータを削除する。</summary>
    public void SaveCurrentGame()
    {
        if (IsClearPopupVisible)
        {
            _saveService.Delete();
            return;
        }

        _saveService.Save(_difficulty, DateTime.Now - _startTime, Board.GetBoardSnapshot(), Board.GetSolutionSnapshot());
    }

    private void GoHome()
    {
        SaveCurrentGame();
        _timer.Stop();
        HomeRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnPuzzleSolved(object? sender, EventArgs e)
    {
        _timer.Stop();
        ClearTimeText = ElapsedText;
        IsClearPopupVisible = true;
    }

    private void UpdateElapsedText()
    {
        var elapsed = DateTime.Now - _startTime;
        ElapsedText = elapsed.ToString(@"mm\:ss");
    }

    private static int GetTargetGivens(Difficulty difficulty) => difficulty switch
    {
        Difficulty.Easy => 40,
        Difficulty.Normal => 33,
        Difficulty.Hard => 29,
        Difficulty.Expert => 25,
        Difficulty.Master => 20,
        _ => 33,
    };
}