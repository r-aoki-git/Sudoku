using Sudoku.Models;
using Sudoku.Solvers;
using System.Diagnostics;

namespace Sudoku.Generators;

/// <summary>
/// キラーナンプレの問題生成。完成盤面の生成→ケージ分割→唯一解の検証、をリトライしながら行う。
/// 生成処理全体で使える時間に上限（OverallBudgetMs）を設け、その残り時間を毎回の検証に配分することで、
/// リトライが重なってもトータルの待ち時間が青天井にならないようにしている。
///
/// OverallBudgetMsはコンストラクタで指定可能。ParallelKillerSudokuGeneratorが
/// 「短い予算で何度も使い捨てる」ワーカーを構成する際に利用する。
/// </summary>
public class KillerSudokuGenerator
{
    public const int DefaultOverallBudgetMs = 10000;
    private const int MinAttemptBudgetMs = 50;

    // Union-Find方式のケージ生成は非常に高速（< 1ms）なので、
    // ケージ生成に割り当てる予算はごく小さくてよい。
    private const int CageBudgetMs = 100;

    // ------------------------------------------------------------
    // 難易度別の検証予算
    //
    // Expert / Master はケージ数が少なく、平均ケージサイズが大きいため、
    // 「既知解とは異なる解」の探索コストが Hard 以下より大きくなる。
    //
    // ここでは生成時間を無制限に増やすのではなく、
    // 難易度そのものに応じて唯一解検証へ与える予算を増やす。
    // ------------------------------------------------------------
    private static int GetUniquenessBudgetMs(
        Difficulty difficulty)
    {
        return difficulty switch
        {
            Difficulty.Easy => 500,
            Difficulty.Normal => 700,
            Difficulty.Hard => 1200,
            Difficulty.Expert => 6000,
            Difficulty.Master => 8000,
            _ => 1200
        };
    }

    // ------------------------------------------------------------
    // 人間解法の予算
    //
    // Expert / Master は難しいケージ構造を扱うため、
    // Hard と同じ400msでは判定途中で切れる可能性がある。
    // ------------------------------------------------------------
    private static int GetHumanBudgetMs(
        Difficulty difficulty)
    {
        return difficulty switch
        {
            Difficulty.Easy => 250,
            Difficulty.Normal => 300,
            Difficulty.Hard => 400,
            Difficulty.Expert => 800,
            Difficulty.Master => 1200,
            _ => 400
        };
    }

    private readonly Random _random;
    private readonly BacktrackingSolver _solver;
    private readonly CageGenerator _cageGenerator;
    private readonly DifficultyScorer _difficultyScorer;

    private Board? _solution;
    private int _attemptsSinceNewSolution;
    private int _totalAttempts;
    private int _solutionRegenerations;

    // 新しいCageGeneratorは高速に多様なケージ構造を生成できるため、
    // 同じ完成盤面でより多くのケージ構造を試行できる。
    private const int RegenerateSolutionAfter = 500;

    public KillerSudokuGenerator(Random? random = null)
    {
        _random = random ?? new Random();
        _solver = new BacktrackingSolver(_random);
        _cageGenerator = new CageGenerator(_random);
        _difficultyScorer = new DifficultyScorer();
    }

    /// <summary>
    /// 指定予算内で1回の生成を試みる。
    /// 予算切れは通常の失敗なので例外を投げず、nullを返す。
    /// CancellationTokenのキャンセルだけは例外として扱う。
    /// </summary>
    public (Board Solution, List<Cage> Cages)? TryGenerate(
        Difficulty difficulty,
        int budgetMs,
        CancellationToken cancellationToken = default)
    {
        if (budgetMs <= 0)
            throw new ArgumentOutOfRangeException(nameof(budgetMs));

        var overallStopwatch =
            System.Diagnostics.Stopwatch.StartNew();

        cancellationToken.ThrowIfCancellationRequested();

        while (overallStopwatch.ElapsedMilliseconds < budgetMs)
        {
            var attemptStopwatch =
                Stopwatch.StartNew();

            cancellationToken.ThrowIfCancellationRequested();

            if (_solution is null ||
                _attemptsSinceNewSolution >= RegenerateSolutionAfter)
            {
                _solution = new Board();

                _solutionRegenerations++;

                if (!_solver.TryGenerateFullGrid(
                        _solution,
                        cancellationToken))
                {
                    _solution = null;
                    continue;
                }

                _attemptsSinceNewSolution = 0;
            }

            long remaining =
                budgetMs -
                overallStopwatch.ElapsedMilliseconds;

            if (remaining < MinAttemptBudgetMs)
                break;

            int uniquenessBudgetLimit =
                GetUniquenessBudgetMs(difficulty);

            int humanBudgetLimit =
                GetHumanBudgetMs(difficulty);

            int cageBudget =
                (int)Math.Min(
                    CageBudgetMs,
                    Math.Max(
                        MinAttemptBudgetMs,
                        remaining -
                        uniquenessBudgetLimit -
                        humanBudgetLimit -
                        MinAttemptBudgetMs));

            List<Cage>? cages;

            cancellationToken.ThrowIfCancellationRequested();

            var cageStopwatch =
                Stopwatch.StartNew();

            cages =
                _cageGenerator.GenerateCages(
                    _solution,
                    difficulty,
                    cageBudget,
                    cancellationToken);

            cageStopwatch.Stop();

            if (cages is null)
            {
                attemptStopwatch.Stop();

                _attemptsSinceNewSolution++;
                _totalAttempts++;

                if (SolverDiagnostics.VerboseLogging)
                {
                    Debug.WriteLine(
                        $"[CageReject] " +
                        $"Attempts={_totalAttempts}, " +
                        $"AttemptElapsed={attemptStopwatch.ElapsedMilliseconds}ms, " +
                        $"Cage={cageStopwatch.ElapsedMilliseconds}ms");
                }

                continue;
            }

            _attemptsSinceNewSolution++;
            _totalAttempts++;

            // ---------------------------------------------------------
            // ① 先に人間解法による難易度判定を行う。
            //
            // 一意解検証（②）は分岐を伴う探索で、特にExpert/Masterでは
            // 数百ms〜数秒かかる。要求難易度と一致しないケージ構成に
            // その時間を費やすのは無駄なので、humanBudgetLimitで頭打ち
            // される軽い難易度判定を先に行い、一致しないものは
            // 一意解検証に進む前に棄却する。
            // ---------------------------------------------------------
            remaining =
                budgetMs -
                overallStopwatch.ElapsedMilliseconds;

            if (remaining < MinAttemptBudgetMs)
                break;

            int humanBudget =
                (int)Math.Min(
                    humanBudgetLimit,
                    remaining);

            var humanStopwatch =
                Stopwatch.StartNew();

            var humanSolver =
                new KillerHumanSolver(cages);

            var humanResult =
                humanSolver.Solve(
                    new Board(),
                    timeBudgetMs: humanBudget,
                    targetDifficulty: difficulty,
                    cancellationToken: cancellationToken);

            humanStopwatch.Stop();

            if (humanResult.EarlyRejected)
            {
                attemptStopwatch.Stop();

                if (SolverDiagnostics.VerboseLogging)
                {
                    Debug.WriteLine(
                        $"[EarlyReject] " +
                        $"Difficulty={difficulty}, " +
                        $"MaxLevel={humanResult.MaxLevelUsed}, " +
                        $"Attempts={_totalAttempts}, " +
                        $"AttemptElapsed={attemptStopwatch.ElapsedMilliseconds}ms, " +
                        $"Cage={cageStopwatch.ElapsedMilliseconds}ms, " +
                        $"Human={humanStopwatch.ElapsedMilliseconds}ms");
                }

                continue;
            }

            var difficultyResult =
                _difficultyScorer.Evaluate(humanResult);

            // Masterは「人間解法だけでは解ききれない」ことこそが判定条件のため、
            // RequiredFallback=true自体を理由に破棄してはならない。
            // フォールバックでも実際には解けなかった場合だけ破棄する。
            if (humanResult.RequiredFallback)
            {
                if (difficulty != Difficulty.Master || !humanResult.FallbackSolved)
                {
                    attemptStopwatch.Stop();

                    if (SolverDiagnostics.VerboseLogging)
                    {
                        Debug.WriteLine(
                            $"[DifficultyReject] " +
                            $"Requested={difficulty}, " +
                            $"Status=Stuck, " +
                            $"Score={difficultyResult.Score}, " +
                            $"Attempts={_totalAttempts}, " +
                            $"AttemptElapsed={attemptStopwatch.ElapsedMilliseconds}ms, " +
                            $"Human={humanStopwatch.ElapsedMilliseconds}ms");
                    }

                    continue;
                }
            }
            else if (difficultyResult.Label != difficulty)
            {
                attemptStopwatch.Stop();

                if (SolverDiagnostics.VerboseLogging)
                {
                    Debug.WriteLine(
                        $"[DifficultyReject] " +
                        $"Requested={difficulty}, " +
                        $"Actual={difficultyResult.Label}, " +
                        $"Score={difficultyResult.Score}, " +
                        $"Attempts={_totalAttempts}, " +
                        $"AttemptElapsed={attemptStopwatch.ElapsedMilliseconds}ms, " +
                        $"Human={humanStopwatch.ElapsedMilliseconds}ms");
                }

                continue;
            }

            // ---------------------------------------------------------
            // ② 難易度が一致したケージ構成についてのみ、一意解を検証する。
            // ---------------------------------------------------------
            if (_solution is null)
                throw new InvalidOperationException(
                    "一意解検証に必要な完成盤面が存在しません。");

            remaining =
                budgetMs -
                overallStopwatch.ElapsedMilliseconds;

            if (remaining < MinAttemptBudgetMs)
                break;

            long uniquenessBudget =
                Math.Min(
                    uniquenessBudgetLimit,
                    Math.Max(
                        MinAttemptBudgetMs,
                        remaining));

            if (uniquenessBudget < MinAttemptBudgetMs)
                break;

            var killerSolver =
                new KillerBacktrackingSolver(
                    cages,
                    cancellationToken);

            var uniquenessStopwatch =
                Stopwatch.StartNew();

            int uniquenessResult =
                killerSolver.CheckUniqueAgainstKnownSolution(
                    new Board(),
                    _solution,
                    timeBudgetMs: (int)uniquenessBudget,
                    cancellationToken: cancellationToken);

            uniquenessStopwatch.Stop();

            attemptStopwatch.Stop();

            if (uniquenessResult != 1)
            {
                if (SolverDiagnostics.VerboseLogging)
                {
                    Debug.WriteLine(
                        $"[UniquenessReject] " +
                        $"Result={uniquenessResult}, " +
                        $"Attempts={_totalAttempts}, " +
                        $"AttemptElapsed={attemptStopwatch.ElapsedMilliseconds}ms, " +
                        $"Cage={cageStopwatch.ElapsedMilliseconds}ms, " +
                        $"Human={humanStopwatch.ElapsedMilliseconds}ms, " +
                        $"Unique={uniquenessStopwatch.ElapsedMilliseconds}ms, " +
                        $"Budget={uniquenessBudget}ms");
                }

                continue;
            }

            if (SolverDiagnostics.VerboseLogging)
            {
                Debug.WriteLine(
                    $"[AttemptTiming] " +
                    $"Attempt={_totalAttempts}, " +
                    $"AttemptElapsed={attemptStopwatch.ElapsedMilliseconds}ms, " +
                    $"Cage={cageStopwatch.ElapsedMilliseconds}ms, " +
                    $"Human={humanStopwatch.ElapsedMilliseconds}ms, " +
                    $"Unique={uniquenessStopwatch.ElapsedMilliseconds}ms, " +
                    $"HumanBudget={humanBudget}ms, " +
                    $"UniqueBudget={uniquenessBudget}ms, " +
                    $"UniqueResult={uniquenessResult}, " +
                    $"Solved={humanResult.Solved}, " +
                    $"Fallback={humanResult.RequiredFallback}, " +
                    $"Remaining={humanResult.RemainingCells}, " +
                    $"Actual={difficultyResult.Label}, " +
                    $"Score={difficultyResult.Score}");

                Debug.WriteLine(
                    $"[成功] " +
                    $"経過{overallStopwatch.ElapsedMilliseconds}ms, " +
                    $"今回試行={attemptStopwatch.ElapsedMilliseconds}ms, " +
                    $"試行{_totalAttempts}回, " +
                    $"完成盤面の作り直し{_solutionRegenerations}回");
            }

            return (_solution, cages);
        }

        return null;
    }

    /// <summary>完成盤面（正解）とケージ分割の両方を返す。</summary>
    public (Board Solution, List<Cage> Cages) Generate(
        Difficulty difficulty,
        int budgetMs,
        CancellationToken cancellationToken = default)
    {
        var result =
            TryGenerate(
                difficulty,
                budgetMs,
                cancellationToken);

        if (result is { } success)
            return success;

        throw new InvalidOperationException(
            $"キラーナンプレの生成に失敗しました。実際の経過時間: " +
            $"{budgetMs}ms (予算{budgetMs}ms), " +
            $"試行回数: {_totalAttempts}回, " +
            $"完成盤面の作り直し回数: {_solutionRegenerations}回");
    }
}