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

    // 難易度判定に使う予算
    private const int HumanBudgetMs = 1500;

    // 唯一解検証（バックトラッキング）に使う予算。
    // 難易度判定より先に実行し、複数解になる無駄なケージ構成に対して
    // コストの高い人間解法テクニック群を走らせないようにする。
    // 制約伝播による早期終了（KillerBacktrackingSolver.minFilledAfterPropagation）が
    // あるため、多くの場合はこの予算を使い切る前に判定が完了する。
    private const int UniquenessBudgetMs = 1000;

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

            int cageBudget =
                (int)Math.Min(
                    CageBudgetMs,
                    Math.Max(
                        MinAttemptBudgetMs,
                        remaining -
                        UniquenessBudgetMs -
                        HumanBudgetMs -
                        MinAttemptBudgetMs));

            List<Cage>? cages;

            cancellationToken.ThrowIfCancellationRequested();

            cages =
                _cageGenerator.GenerateCages(
                    _solution,
                    difficulty,
                    cageBudget,
                    cancellationToken);

            if (cages is null)
            {
                _attemptsSinceNewSolution++;
                _totalAttempts++;
                continue;
            }

            _attemptsSinceNewSolution++;
            _totalAttempts++;

            // ---------------------------------------------------------
            // ① 先に唯一解を検証する。
            // 複数解になるケージ構成はここで破棄し、
            // コストの高い人間解法の判定（②）を無駄に行わないようにする。
            // ---------------------------------------------------------
            remaining =
                budgetMs -
                overallStopwatch.ElapsedMilliseconds;

            if (remaining < MinAttemptBudgetMs)
                break;

            int uniquenessBudget =
                (int)Math.Min(
                    UniquenessBudgetMs,
                    remaining);

            var killerSolver =
                new KillerBacktrackingSolver(
                    cages,
                    cancellationToken);

            // 難易度が上がるほど、制約伝播だけでは埋まりにくい構造になりやすい。
            // 閾値を下げすぎず、かつ高難易度側で不当に早期棄却しないよう
            // 難易度に応じて可変にする。
            int solutionCount =
                killerSolver.CountSolutions(
                    new Board(),
                    limit: 2,
                    timeBudgetMs: uniquenessBudget,
                    cancellationToken: cancellationToken);

            if (solutionCount != 1)
            {
                if (SolverDiagnostics.VerboseLogging)
                {
                    Debug.WriteLine(
                        $"[UniquenessReject] " +
                        $"Result={solutionCount}, " +
                        $"Attempts={_totalAttempts}, " +
                        $"Budget={uniquenessBudget}ms");
                }

                continue;
            }

            // ---------------------------------------------------------
            // ② 唯一解が確認できたケージ構成についてのみ、難易度判定を行う。
            // ---------------------------------------------------------
            remaining =
                budgetMs -
                overallStopwatch.ElapsedMilliseconds;

            if (remaining < MinAttemptBudgetMs)
                break;

            int humanBudget =
                (int)Math.Min(
                    HumanBudgetMs,
                    remaining);

            var humanSolver =
                new KillerHumanSolver(cages);

            var humanResult =
                humanSolver.Solve(
                    new Board(),
                    timeBudgetMs: humanBudget,
                    targetDifficulty: difficulty,
                    cancellationToken: cancellationToken);

            if (humanResult.EarlyRejected)
            {
                if (SolverDiagnostics.VerboseLogging)
                {
                    Debug.WriteLine(
                        $"[EarlyReject] " +
                        $"Difficulty={difficulty}, " +
                        $"MaxLevel={humanResult.MaxLevelUsed}, " +
                        $"Attempts={_totalAttempts}, " +
                        $"Elapsed={overallStopwatch.ElapsedMilliseconds}ms");
                }

                continue;
            }

            var difficultyResult =
                _difficultyScorer.Evaluate(humanResult);

            if (SolverDiagnostics.VerboseLogging)
            {
                Debug.WriteLine(
                    $"[DifficultyCheck] " +
                    $"Requested={difficulty}, " +
                    $"Actual={difficultyResult.Label}, " +
                    $"Status={difficultyResult.Status}, " +
                    $"Score={difficultyResult.Score}, " +
                    $"MaxLv={difficultyResult.MaxLevel}, " +
                    $"Fallback={difficultyResult.UsedFallback}, " +
                    $"Remaining={difficultyResult.Remaining}");
            }

            // Masterは「人間解法だけでは解ききれない」ことこそが判定条件のため、
            // RequiredFallback=true自体を理由に破棄してはならない。
            // フォールバックでも実際には解けなかった場合だけ破棄する。
            if (humanResult.RequiredFallback)
            {
                if (difficulty != Difficulty.Master || !humanResult.FallbackSolved)
                    continue;
            }
            else if (difficultyResult.Label != difficulty)
            {
                continue;
            }

            if (SolverDiagnostics.VerboseLogging)
            {
                Debug.WriteLine(
                    $"[成功] " +
                    $"経過{overallStopwatch.ElapsedMilliseconds}ms, " +
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