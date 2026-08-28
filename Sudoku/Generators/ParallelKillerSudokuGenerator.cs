using Sudoku.Models;
using Sudoku.Solvers;
using System.Diagnostics;

namespace Sudoku.Generators;

/// <summary>
/// KillerSudokuGeneratorを複数スレッドで並列に走らせ、最初に成功した結果を採用する。
///
/// 【設計上の注意】
/// KillerSudokuGeneratorは本来、「1つの完成盤面に対してケージ構造を何十〜何百回も
/// 振り直す」ことで効率よく試行回数を稼ぐ設計になっている（内部のRegenerateSolutionAfter
/// ロジック）。完成盤面のバックトラック生成はケージ振り直しよりコストが高いため、
/// 1つの盤面をなるべく使い回すことが速度上重要になる。
///
/// そのためこのクラスは、各ワーカーに「ある程度まとまった」予算（PerAttemptBudgetMs）を
/// 与えたKillerSudokuGeneratorを繰り返し使い捨てにする。予算が短すぎると、1回のミニ試行が
/// 「完成盤面を1つ作って、ケージ振り直しをほんの数回試しただけで時間切れ」になり、
/// 盤面再利用のメリットをほとんど活かせないまま高コストな盤面生成を繰り返すことになる。
/// PerAttemptBudgetMsは、この「使い回しの効率」と「他ワーカーの成功への追従速度
/// （cts.Cancelへの反応の速さ）」のトレードオフを取るパラメータ。
///
/// 【エスカレーション】
/// 出現率が低い難易度・パラメータの組み合わせでは、指定したoverallTimeoutMsを
/// 使い切っても見つからないことがある（確率的な事象なので、時間を伸ばせば
/// 成功率は上がるが、ゼロにはならない）。そのため、1回目のタイムアウトで
/// 諦めず、予算を広げて自動的に再挑戦する。典型的な成功パターンでは
/// 1回目の（短い）予算内であっさり見つかるため、エスカレーションは
/// 「保険」として働き、通常ケースの速度には影響しない。
/// </summary>
public static class ParallelKillerSudokuGenerator
{
    private const int DefaultOverallTimeoutMs = 20000;
    private const int DefaultPerAttemptBudgetMs = 4000;
    private const int DefaultMaxEscalations = 1;
    private const double EscalationMultiplier = 1.5;

    private const int DefaultWorkerCount = 6;

    public static (Board Solution, List<Cage> Cages) Generate(
        Difficulty difficulty,
        int? workerCount = null,
        int overallTimeoutMs = DefaultOverallTimeoutMs,
        int perAttemptBudgetMs = DefaultPerAttemptBudgetMs,
        int maxEscalations = DefaultMaxEscalations)
    {
        if (maxEscalations < 0)
            throw new ArgumentOutOfRangeException(nameof(maxEscalations));

        int workers = Math.Max(1, workerCount ?? DefaultWorkerCount);

        int currentTimeout = overallTimeoutMs;
        Exception? lastFailure = null;

        for (int round = 0; round <= maxEscalations; round++)
        {
            var roundStopwatch = Stopwatch.StartNew();

            Debug.WriteLine(
                $"[ParallelKillerSudokuGenerator] Round={round}, " +
                $"Difficulty={difficulty}, Workers={workers}, " +
                $"TimeoutMs={currentTimeout}, PerAttemptBudgetMs={perAttemptBudgetMs}");

            try
            {
                var result =
                    GenerateOnce(
                        difficulty,
                        workers,
                        currentTimeout,
                        perAttemptBudgetMs);

                roundStopwatch.Stop();

                Debug.WriteLine(
                    $"[ParallelRoundSuccess] " +
                    $"Round={round}, " +
                    $"Elapsed={roundStopwatch.ElapsedMilliseconds}ms");

                return result;
            }
            catch (InvalidOperationException ex)
            {
                roundStopwatch.Stop();

                lastFailure = ex;

                Debug.WriteLine(
                    $"[ParallelKillerSudokuGenerator] " +
                    $"Round={round} failed: {ex.Message}, " +
                    $"Elapsed={roundStopwatch.ElapsedMilliseconds}ms");

                currentTimeout =
                    (int)(currentTimeout * EscalationMultiplier);
            }
        }

        throw new InvalidOperationException(
            $"キラーナンプレの生成に失敗しました（{maxEscalations + 1}回の試行、" +
            $"最終タイムアウト設定{currentTimeout}ms でも成功しませんでした）。",
            lastFailure);
    }

    private static (Board Solution, List<Cage> Cages) GenerateOnce(
        Difficulty difficulty,
        int workers,
        int overallTimeoutMs,
        int perAttemptBudgetMs)
    {
        var overallStopwatch = Stopwatch.StartNew();
        using var cts = new CancellationTokenSource(overallTimeoutMs);

        var allTasks = new Task<(Board Solution, List<Cage> Cages)?>[workers];

        for (int i = 0; i < workers; i++)
        {
            allTasks[i] = Task.Factory.StartNew(
                () => RunWorker(
                    difficulty,
                    overallStopwatch,
                    overallTimeoutMs,
                    perAttemptBudgetMs,
                    cts.Token),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        try
        {
            var pending = allTasks.ToList();

            while (pending.Count > 0)
            {
                int index = Task.WaitAny(pending.ToArray());
                var completed = pending[index];

                if (completed.Result is { } result)
                {
                    // 勝者確定。
                    // ここで全ワーカーに停止要求を出す。
                    cts.Cancel();

                    // 重要：
                    // 勝者以外のLongRunningワーカーが完全終了するまで待つ。
                    // これを行わないと、次のRoundや次の生成処理とCPUを奪い合う。
                    try
                    {
                        Task.WaitAll(allTasks);
                    }
                    catch (AggregateException)
                    {
                        // OperationCanceledException / InvalidOperationException
                        // は各ワーカー側で処理済みなので、ここでは無視する。
                    }

                    Debug.WriteLine(
                        $"[ParallelRoundSuccess] " +
                        $"RoundElapsed={overallStopwatch.ElapsedMilliseconds}ms");

                    return result;
                }

                pending.RemoveAt(index);
            }
        }
        finally
        {
            // タイムアウト・例外時も全ワーカーを確実に停止する。
            cts.Cancel();

            try
            {
                Task.WaitAll(allTasks);
            }
            catch (AggregateException)
            {
                // ワーカー側のキャンセル・失敗例外はここでは無視する。
            }
        }

        throw new InvalidOperationException(
            $"並列 {workers} ワーカーとも {overallTimeoutMs}ms 以内に成功しませんでした。");
    }

    private static (Board Solution, List<Cage> Cages)? RunWorker(
        Difficulty difficulty,
        Stopwatch roundStopwatch,
        int overallTimeoutMs,
        int perAttemptBudgetMs,
        CancellationToken cancellationToken)
    {
        var workerStopwatch = Stopwatch.StartNew();

        long workerStartAt =
            roundStopwatch.ElapsedMilliseconds;

        int threadId =
            Environment.CurrentManagedThreadId;

        Debug.WriteLine(
            $"[WorkerStart] " +
            $"Thread={threadId}, " +
            $"RoundElapsed={workerStartAt}ms");

        try
        {
            while (roundStopwatch.ElapsedMilliseconds < overallTimeoutMs)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var generator =
                        new KillerSudokuGenerator(
                            overallBudgetMs: perAttemptBudgetMs);

                    var result =
                        generator.Generate(
                            difficulty,
                            cancellationToken);

                    workerStopwatch.Stop();

                    long workerElapsed =
                        workerStopwatch.ElapsedMilliseconds;

                    long roundElapsed =
                        roundStopwatch.ElapsedMilliseconds;

                    Debug.WriteLine(
                        $"[WorkerSuccess] " +
                        $"Thread={threadId}, " +
                        $"WorkerElapsed={workerElapsed}ms, " +
                        $"RoundElapsed={roundElapsed}ms");

                    return result;
                }
                catch (InvalidOperationException)
                {
                    if (SolverDiagnostics.VerboseLogging)
                    {
                        Debug.WriteLine(
                            $"[WorkerRetry] " +
                            $"Thread={threadId}, " +
                            $"WorkerElapsed={workerStopwatch.ElapsedMilliseconds}ms, " +
                            $"RoundElapsed={roundStopwatch.ElapsedMilliseconds}ms");
                    }
                }
            }

            workerStopwatch.Stop();

            Debug.WriteLine(
                $"[WorkerTimeout] " +
                $"Thread={threadId}, " +
                $"WorkerElapsed={workerStopwatch.ElapsedMilliseconds}ms, " +
                $"RoundElapsed={roundStopwatch.ElapsedMilliseconds}ms");

            return null;
        }
        catch (OperationCanceledException)
        {
            workerStopwatch.Stop();

            Debug.WriteLine(
                $"[WorkerCanceled] " +
                $"Thread={threadId}, " +
                $"WorkerElapsed={workerStopwatch.ElapsedMilliseconds}ms, " +
                $"RoundElapsed={roundStopwatch.ElapsedMilliseconds}ms");

            return null;
        }
    }
}