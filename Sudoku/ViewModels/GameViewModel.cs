using System.Security.Policy;
using System.Windows;
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
    private GameMode _mode;
    private List<Cage>? _cages;
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

    /// <summary>問題を生成中かどうか。trueの間はGameView側でオーバーレイを表示し、全操作をブロックする</summary>
    private bool _isGenerating;
    public bool IsGenerating
    {
        get => _isGenerating;
        private set => SetProperty(ref _isGenerating, value);
    }

    public ICommand NewGameCommand { get; }
    public ICommand HomeCommand { get; }

    ///<summary>新規ゲームとして開始する場合のコンストラクタ。</summary>
    public GameViewModel(Difficulty difficulty, GameMode mode) : this(difficulty, mode, null)
    {
    }

    /// <summary>セーブデータから再開する場合は、resumeDataに読み込んだ内容を渡す。</summary>
    private GameViewModel(Difficulty difficulty, GameMode mode, (Board Puzzle, Board Solution, List<Cage>? Cages, TimeSpan Elapsed)? resumeData)
    {
        _difficulty = difficulty;
        _mode = mode;

        NewGameCommand = new RelayCommand(_ => StartNewGame(), _ => !IsGenerating);
        HomeCommand = new RelayCommand(_ => GoHome(), _ => !IsGenerating);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => UpdateElapsedText();

        if (resumeData.HasValue)
            ResumeGame(resumeData.Value.Puzzle, resumeData.Value.Solution, resumeData.Value.Cages, resumeData.Value.Elapsed);
        else
            StartNewGame();
    }

    /// <summary>セーブデータから続きを再開するゲーム画面を作る（読み込みに失敗したらnullを返す）</summary>
    public static GameViewModel? CreateFromSave(SaveDataService saveService)
    {
        var loaded = saveService.Load();
        if (loaded is null) return null;

        return new GameViewModel(
            loaded.Value.Difficulty,
            loaded.Value.Mode,
            (loaded.Value.Puzzle, loaded.Value.Solution, loaded.Value.Cages, loaded.Value.Elapsed));
    }

    private void StartNewGame() => _ = StartNewGameAsync();

    /// <summary>
    /// 新しい問題を生成する。キラーナンプレモードは生成に数秒かかることがあるため、
    /// バックグラウンドスレッドで実行しIsGeneratingでUIをブロックする。
    /// </summary>
    private async Task StartNewGameAsync()
    {
        if (IsGenerating) return;

        IsGenerating = true;
        IsClearPopupVisible = false;
        _timer.Stop();

        try
        {
            Board puzzle;
            Board solution;
            List<Cage>? cages;

            if (_mode == GameMode.Killer)
            {
                var (killerSolution, killerCages) = await Task.Run(()
                    => ParallelKillerSudokuGenerator.Generate(_difficulty));

                solution = killerSolution;
                cages = killerCages;
                puzzle = new Board(); // キラーナンプレは初期配置なし（ケージのみが手がかり）
            }
            else
            {
                int targetGivens = GetTargetGivens(_difficulty);

                var (classicPuzzle, classicSolution) = await Task.Run(() =>
                {
                    var generator = new SudokuGenerator();
                    return generator.GeneratePuzzle(targetGivens);
                });

                puzzle = classicPuzzle;
                solution = classicSolution;
                cages = null;
            }

            AttachBoard(puzzle, solution, cages);

            _startTime = DateTime.Now;
            UpdateElapsedText();
            _timer.Start();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"問題の生成に失敗しました。もう一度お試しください。\n({ex.Message})",
                "エラー",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            HomeRequested?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            IsGenerating = false;
        }
    }

    private void ResumeGame(Board puzzle, Board solution, List<Cage>? cages, TimeSpan elapsed)
    {
        IsClearPopupVisible = false;

        AttachBoard(puzzle, solution, cages);

        _startTime = DateTime.Now - elapsed;
        UpdateElapsedText();
        _timer.Start();
    }

    private void AttachBoard(Board puzzle, Board solution, List<Cage>? cages)
    {
        _cages = cages;
        Board = new BoardViewModel(puzzle, solution, cages);
        Board.PuzzleSolved += OnPuzzleSolved;
    }

    /// <summary>現在の進行状況を保存する。クリア済みの場合は、代わりにセーブデータを削除する。</summary>
    public void SaveCurrentGame()
    {
        //生成中（Boardが未確定）は保存しない
        if (IsGenerating || Board is null)
            return;

        if (IsClearPopupVisible)
        {
            _saveService.Delete();
            return;
        }

        _saveService.Save(
            _difficulty,
            _mode,
            DateTime.Now - _startTime,
            Board.GetBoardSnapshot(),
            Board.GetSolutionSnapshot(),
            _cages);
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