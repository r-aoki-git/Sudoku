using Sudoku.Generators;
using Sudoku.Models;
using Sudoku.Solvers;
using System.Diagnostics;

namespace Sudoku.Diagnostics;

public static class KillerSudokuGenerationBenchmark
{
    private static readonly Difficulty[] Difficulties =
    {
        Difficulty.Easy,
        Difficulty.Normal,
        Difficulty.Hard,
        Difficulty.Expert,
        Difficulty.Master
    };

    public static void Run(
        int samplesPerDifficulty = 10,
        int workers = 4,
        int overallTimeoutMs = 10000,
        int perAttemptBudgetMs = 2500)
    {
        if (samplesPerDifficulty <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(samplesPerDifficulty));

        Debug.WriteLine(
            "============================================================");

        Debug.WriteLine(
            "[KillerBenchmark] START");

        Debug.WriteLine(
            $"Samples={samplesPerDifficulty}, " +
            $"Workers={workers}, " +
            $"Timeout={overallTimeoutMs}ms, " +
            $"PerAttempt={perAttemptBudgetMs}ms");

        foreach (var difficulty in Difficulties)
        {
            RunDifficulty(
                difficulty,
                samplesPerDifficulty,
                workers,
                overallTimeoutMs,
                perAttemptBudgetMs);
        }

        Debug.WriteLine(
            "[KillerBenchmark] END");

        Debug.WriteLine(
            "============================================================");
    }

    private static void RunDifficulty(
        Difficulty difficulty,
        int samples,
        int workers,
        int overallTimeoutMs,
        int perAttemptBudgetMs)
    {
        int successCount = 0;
        int hardFailureCount = 0;
        int uniquenessFailureCount = 0;
        int actualDifficultyMismatchCount = 0;

        long totalElapsed = 0;
        long minElapsed = long.MaxValue;
        long maxElapsed = 0;

        var elapsedValues =
            new List<long>(samples);

        Debug.WriteLine(
            $"---------- {difficulty} ----------");

        for (int i = 1; i <= samples; i++)
        {
            var stopwatch =
                Stopwatch.StartNew();

            try
            {
                var result =
                    ParallelKillerSudokuGenerator.Generate(
                        difficulty,
                        workerCount: workers,
                        overallTimeoutMs: overallTimeoutMs,
                        perAttemptBudgetMs: perAttemptBudgetMs);

                stopwatch.Stop();

                long elapsed =
                    stopwatch.ElapsedMilliseconds;

                elapsedValues.Add(elapsed);

                totalElapsed += elapsed;
                minElapsed =
                    Math.Min(
                        minElapsed,
                        elapsed);

                maxElapsed =
                    Math.Max(
                        maxElapsed,
                        elapsed);

                successCount++;

                bool solutionValid =
                    ValidateSolution(
                        result.Solution);

                bool cagesValid =
                    ValidateCages(
                        result.Solution,
                        result.Cages);

                int uniqueResult =
                    ValidateUniqueness(
                        result.Solution,
                        result.Cages);

                if (uniqueResult != 1)
                    uniquenessFailureCount++;

                Difficulty actualDifficulty =
                    DetermineActualDifficulty(
                        result.Cages,
                        out bool fallback);

                if (actualDifficulty != difficulty)
                    actualDifficultyMismatchCount++;

                Debug.WriteLine(
                    $"[{difficulty}] {i}/{samples} " +
                    $"Elapsed={elapsed}ms, " +
                    $"SolutionValid={solutionValid}, " +
                    $"CagesValid={cagesValid}, " +
                    $"Unique={uniqueResult}, " +
                    $"Actual={actualDifficulty}, " +
                    $"Fallback={fallback}");

                if (!solutionValid)
                    hardFailureCount++;

                if (!cagesValid)
                    hardFailureCount++;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();

                hardFailureCount++;

                Debug.WriteLine(
                    $"[{difficulty}] {i}/{samples} " +
                    $"FAILED " +
                    $"Elapsed={stopwatch.ElapsedMilliseconds}ms, " +
                    $"Exception={ex.GetType().Name}: {ex.Message}");
            }
        }

        if (successCount == 0)
        {
            Debug.WriteLine(
                $"[{difficulty}] " +
                $"Success=0/{samples}");

            return;
        }

        double average =
            (double)totalElapsed /
            successCount;

        double median =
            CalculateMedian(
                elapsedValues);

        Debug.WriteLine(
            $"[{difficulty}] " +
            $"Success={successCount}/{samples}, " +
            $"Average={average:F1}ms, " +
            $"Median={median:F1}ms, " +
            $"Min={minElapsed}ms, " +
            $"Max={maxElapsed}ms, " +
            $"UniqueFailures={uniquenessFailureCount}, " +
            $"DifficultyMismatch={actualDifficultyMismatchCount}, " +
            $"HardFailures={hardFailureCount}");
    }

    private static bool ValidateSolution(
        Board solution)
    {
        for (int r = 0; r < Board.Size; r++)
        {
            int mask = 0;

            for (int c = 0; c < Board.Size; c++)
            {
                var cell =
                    solution.GetCell(r, c);

                if (!cell.HasValue)
                    return false;

                int value =
                    cell.Value!.Value;

                if (value < 1 ||
                    value > Board.Size)
                {
                    return false;
                }

                int bit =
                    1 << (value - 1);

                if ((mask & bit) != 0)
                    return false;

                mask |= bit;
            }

            if (mask != FullMask)
                return false;
        }

        for (int c = 0; c < Board.Size; c++)
        {
            int mask = 0;

            for (int r = 0; r < Board.Size; r++)
            {
                int value =
                    solution.GetCell(r, c)!.Value!.Value;

                int bit =
                    1 << (value - 1);

                if ((mask & bit) != 0)
                    return false;

                mask |= bit;
            }

            if (mask != FullMask)
                return false;
        }

        for (int boxRow = 0;
             boxRow < Board.Size;
             boxRow += Board.BoxSize)
        {
            for (int boxCol = 0;
                 boxCol < Board.Size;
                 boxCol += Board.BoxSize)
            {
                int mask = 0;

                for (int r = boxRow;
                     r < boxRow + Board.BoxSize;
                     r++)
                {
                    for (int c = boxCol;
                         c < boxCol + Board.BoxSize;
                         c++)
                    {
                        int value =
                            solution.GetCell(r, c)!
                                .Value!.Value;

                        int bit =
                            1 << (value - 1);

                        if ((mask & bit) != 0)
                            return false;

                        mask |= bit;
                    }
                }

                if (mask != FullMask)
                    return false;
            }
        }

        return true;
    }

    private static bool ValidateCages(
        Board solution,
        List<Cage> cages)
    {
        var seen =
            new bool[Board.Size * Board.Size];

        foreach (var cage in cages)
        {
            if (cage.Cells.Count == 0)
                return false;

            int sum = 0;

            var digitMask = 0;

            foreach (var cell in cage.Cells)
            {
                if (cell.Row < 0 ||
                    cell.Row >= Board.Size ||
                    cell.Col < 0 ||
                    cell.Col >= Board.Size)
                {
                    return false;
                }

                int index =
                    cell.Row * Board.Size +
                    cell.Col;

                if (seen[index])
                    return false;

                seen[index] = true;

                int value =
                    solution.GetCell(
                        cell.Row,
                        cell.Col)!
                    .Value!.Value;

                int bit =
                    1 << (value - 1);

                if ((digitMask & bit) != 0)
                    return false;

                digitMask |= bit;
                sum += value;
            }

            if (sum != cage.TargetSum)
                return false;
        }

        for (int i = 0; i < seen.Length; i++)
        {
            if (!seen[i])
                return false;
        }

        return true;
    }

    private static int ValidateUniqueness(
        Board solution,
        List<Cage> cages)
    {
        var solver =
            new KillerBacktrackingSolver(
                cages);

        return solver.CheckUniqueAgainstKnownSolution(
            new Board(),
            solution,
            timeBudgetMs: 5000);
    }

    private static Difficulty DetermineActualDifficulty(
        List<Cage> cages,
        out bool fallback)
    {
        var solver =
            new KillerHumanSolver(
                cages);

        var result =
            solver.Solve(
                new Board(),
                timeBudgetMs: 5000,
                targetDifficulty: Difficulty.Master);

        fallback =
            result.RequiredFallback;

        var scorer =
            new DifficultyScorer();

        var difficulty =
            scorer.Evaluate(result);

        return difficulty.Label;
    }

    private static double CalculateMedian(
        List<long> values)
    {
        if (values.Count == 0)
            return 0;

        values.Sort();

        int middle =
            values.Count / 2;

        if (values.Count % 2 == 1)
            return values[middle];

        return
            (values[middle - 1] +
             values[middle]) /
            2.0;
    }

    private const int FullMask =
        0b1_1111_1111;
}