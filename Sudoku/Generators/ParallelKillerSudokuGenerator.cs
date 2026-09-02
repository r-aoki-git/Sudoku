using Sudoku.Models;
using Sudoku.Solvers;
using System.Diagnostics;

namespace Sudoku.Generators;

/// <summary>
/// KillerSudokuGeneratorを複数スレッドで並列に走らせ、最初に成功した結果を採用する。
///
/// 【設計上の注意】
/// 各WorkerはKillerSudokuGeneratorを1つだけ保持する。
/// KillerSudokuGenerator内部では、1つの完成盤面に対してケージ構造を何度も試行し、
/// 一定回数に達した場合のみ完成盤面を作り直す。
///
/// Worker単位のGenerate()呼び出しにはPerAttemptBudgetMsを設定するが、
/// Generate()が時間切れになってもGenerator自体は破棄しない。
/// そのため、次のGenerate()呼び出しでは前回の完成盤面をそのまま再利用できる。
///
/// これにより、完成盤面生成という高コストな処理を毎回やり直すことを防ぎ、
/// Workerごとの試行効率を維持する。
///
/// 【並列実行】
/// 各Workerは独立したRandom・Generator・完成盤面を持つ。
/// いずれか1つが成功した時点でCancellationTokenをキャンセルし、
/// 残りのWorkerを停止する。
///
/// 【エスカレーション】
/// 指定したoverallTimeoutMs以内に成功しなかった場合はRoundを終了し、
/// 次のRoundではoverallTimeoutMsを1.5倍にして再挑戦する。
/// </summary>
public static class ParallelKillerSudokuGenerator
{
    private const int DefaultOverallTimeoutMs = 20000;
    private const int DefaultPerAttemptBudgetMs = 4000;
    private const int DefaultMaxEscalations = 1;
    private const double EscalationMultiplier = 1.5;

    // 実行環境のCPUコア数に応じて並列度を決定する。
    // メインスレッド分を除いたコア数を基本とし、1〜8の範囲に収める。
    private static readonly int DefaultWorkerCount =
        Math.Clamp(Environment.ProcessorCount - 1, 1, 8);

    public static (Board Solution, List<Cage> Cages) Generate(
        Difficulty difficulty,
        int? workerCount = null,
        int overallTimeoutMs = DefaultOverallTimeoutMs,
        int perAttemptBudgetMs = DefaultPerAttemptBudgetMs,
        int maxEscalations = DefaultMaxEscalations)
    {
        if (maxEscalations < 0)
            throw new ArgumentOutOfRangeException(nameof(maxEscalations));

        int workers = Math.Max(
            1,
            workerCount ?? DefaultWorkerCount);

        // ------------------------------------------------------------
        // 難易度に必要な最低予算を保証する。
        // 呼び出し側がHard用の2500msを渡しても、
        // Expert / Master では必要な検証時間を確保する。
        // ------------------------------------------------------------
        int minimumPerAttemptBudget =
            GetMinimumPerAttemptBudget(
                difficulty);

        int effectivePerAttemptBudget =
            Math.Max(
                perAttemptBudgetMs,
                minimumPerAttemptBudget);

        // ------------------------------------------------------------
        // 難易度に必要な最低Round時間を保証する。
        // ------------------------------------------------------------
        int minimumOverallTimeout =
            GetMinimumOverallTimeout(
                difficulty);

        int currentTimeout =
            Math.Max(
                overallTimeoutMs,
                minimumOverallTimeout);

        Exception? lastFailure = null;

        for (int round = 0; round <= maxEscalations; round++)
        {
            var roundStopwatch = Stopwatch.StartNew();

            Debug.WriteLine(
                $"[ParallelKillerSudokuGenerator] Round={round}, " +
                $"Difficulty={difficulty}, Workers={workers}, " +
                $"TimeoutMs={currentTimeout}, " +
                $"PerAttemptBudgetMs={effectivePerAttemptBudget}");

            try
            {
                var result =
                    GenerateOnce(
                        difficulty,
                        workers,
                        currentTimeout,
                        effectivePerAttemptBudget);

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
        var overallStopwatch =
            Stopwatch.StartNew();

        using var cts =
            new CancellationTokenSource(overallTimeoutMs);

        var allTasks =
            new Task<(Board Solution, List<Cage> Cages)?>[workers];

        for (int i = 0; i < workers; i++)
        {
            allTasks[i] =
                Task.Factory.StartNew(
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

        var pending =
            allTasks.Cast<Task>().ToArray();

        while (pending.Length > 0)
        {
            int index =
                Task.WaitAny(
                    pending,
                    100);

            if (index < 0)
            {
                if (cts.IsCancellationRequested)
                    break;

                continue;
            }

            var completed =
                (Task<(Board Solution, List<Cage> Cages)?>)
                    pending[index];

            if (completed.IsCompletedSuccessfully &&
                completed.Result is { } result)
            {
                // ---------------------------------------------------------
                // 勝者確定。
                // ---------------------------------------------------------
                cts.Cancel();

                Debug.WriteLine(
                    $"[ParallelWinner] " +
                    $"RoundElapsed={overallStopwatch.ElapsedMilliseconds}ms");

                // ---------------------------------------------------------
                // 全Workerが完全終了するまで待つ。
                //
                // CancellationTokenSourceをDisposeする前に、
                // このラウンドで生成した全Workerを確実に終了させる。
                // ---------------------------------------------------------
                try
                {
                    Task.WaitAll(allTasks);
                }
                catch (AggregateException)
                {
                    // Worker側で処理済み。
                }

                return result;
            }

            pending =
                pending
                    .Where(task =>
                        !ReferenceEquals(task, completed))
                    .ToArray();
        }

        // -------------------------------------------------------------
        // タイムアウト。
        // -------------------------------------------------------------
        cts.Cancel();

        try
        {
            // CancellationTokenSourceをDisposeする前に
            // 全Workerを確実に終了させる。
            Task.WaitAll(allTasks);
        }
        catch (AggregateException)
        {
            // Worker側で処理済み。
        }

        throw new InvalidOperationException(
            $"並列 {workers} ワーカーとも " +
            $"{overallTimeoutMs}ms 以内に成功しませんでした。");
    }

    private static int GetMinimumPerAttemptBudget(
        Difficulty difficulty)
    {
        return difficulty switch
        {
            Difficulty.Easy => 1200,
            Difficulty.Normal => 1800,
            Difficulty.Hard => 2500,
            Difficulty.Expert => 6500,
            Difficulty.Master => 10000,
            _ => 2500
        };
    }

    private static int GetMinimumOverallTimeout(
        Difficulty difficulty)
    {
        return difficulty switch
        {
            Difficulty.Easy => 5000,
            Difficulty.Normal => 7500,
            Difficulty.Hard => 10000,
            Difficulty.Expert => 20000,
            Difficulty.Master => 30000,
            _ => 10000
        };
    }

    private static (Board Solution, List<Cage> Cages)? RunWorker(
        Difficulty difficulty,
        Stopwatch roundStopwatch,
        int overallTimeoutMs,
        int perAttemptBudgetMs,
        CancellationToken cancellationToken)
    {
        var workerStopwatch =
            Stopwatch.StartNew();

        var generator =
            new KillerSudokuGenerator();

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
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (roundStopwatch.ElapsedMilliseconds >= overallTimeoutMs)
                {
                    return null;
                }

                var result =
                    generator.TryGenerate(
                        difficulty,
                        perAttemptBudgetMs,
                        cancellationToken);

                if (result is { } success)
                {
                    workerStopwatch.Stop();

                    Debug.WriteLine(
                        $"[WorkerSuccess] " +
                        $"Thread={threadId}, " +
                        $"WorkerElapsed={workerStopwatch.ElapsedMilliseconds}ms, " +
                        $"RoundElapsed={roundStopwatch.ElapsedMilliseconds}ms");

                    return success;
                }
            }
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