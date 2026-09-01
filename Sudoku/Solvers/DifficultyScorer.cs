namespace Sudoku.Solvers;

public enum Difficulty { Easy, Normal, Hard, Expert, Master }

public enum DifficultySolveStatus
{
    Solved,
    Stuck,
    Timeout,
    Invalid
}

public record DifficultyResult(
    Difficulty Label,
    int Score,
    DifficultySolveStatus Status,
    int MaxLevel,
    int Remaining,
    bool UsedFallback);

/// <summary>
/// HumanSolverの結果からスコアと難易度ラベルを算出する。
///
/// Levelだけではなく、キラー固有テクニックの使用回数も評価する
/// また、フォールバック使用を即Masterにはせず、「人間解法では未完了」として扱う
/// </summary>
public class DifficultyScorer
{
    private static readonly Dictionary<int, int> LevelBasePoints = new()
    {
        [1] = 0,
        [2] = 20,
        [3] = 50,
        [4] = 100,
    };

    private static readonly Dictionary<string, int> TechniquePoints = new()
    {
        // Level 1
        ["Naked Single"] = 0,
        ["Hidden Single"] = 1,

        // キラー固有 Level 1
        ["Cage Forced Combination"] = 0,

        // Level 2
        ["45 Rule (Single Unit)"] = 5,

        // Level 3
        ["Locked Candidates"] = 3,
        ["Killer Locked Candidates"] = 5,
        ["Innie / Outie"] = 6,
        ["Killer Pair / Triple / Quad"] = 10,

        // Subsets
        ["Naked Pair"] = 3,
        ["Naked Triple"] = 5,
        ["Hidden Pair"] = 4,
        ["Hidden Triple"] = 6,

        // Level 4
        ["X-Wing"] = 20,
        ["Swordfish"] = 35,
    };

    public DifficultyResult Evaluate(HumanSolveResult result)
    {
        int score = ComputeScore(result.MaxLevelUsed, result.TechniqueUsageByName);

        // 人間解法で最後まで解けなかった場合。
        // これはMaster確定ではなく「Stuck」とする
        if (result.RequiredFallback)
        {
            return new DifficultyResult(
                Difficulty.Master,
                score,
                DifficultySolveStatus.Stuck,
                result.MaxLevelUsed,
                result.RemainingCells,
                true);
        }

        var label = GetDifficultyLabel(score, result.MaxLevelUsed);

        if (SolverDiagnostics.VerboseLogging)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[DifficultyScorer] " +
                $"MaxLevel={result.MaxLevelUsed}, " +
                $"Score={score}, " +
                $"Label={label}, " +
                $"Fallback={result.RequiredFallback}");
        }

        return new DifficultyResult(label, score, DifficultySolveStatus.Solved, result.MaxLevelUsed, result.RemainingCells, false);
    }

    /// <summary>
    /// 求解の途中経過（現時点のMaxLevel・テクニック使用回数）から、
    /// 目標難易度へまだ到達しうるかを判定する。
    ///
    /// スコア・MaxLevelはどちらも求解が進むほど単調非減少（後から減ることはない）ため、
    /// 「現時点で既に目標難易度の上限を超えている」と判明した時点で、
    /// その後どれだけ求解を続けても目標難易度には一致しえないと確定できる。
    /// HumanSolver側はこれを使って、見込みのない求解を早期に打ち切る（枝刈り）。
    /// </summary>
    public static bool CanStillReach(
        Difficulty target,
        int maxLevelUsedSoFar,
        IReadOnlyDictionary<string, int> usageByNameSoFar)
    {
        // レベル4のテクニックを1回でも使った時点でMaster確定。
        // Master以外を狙っている場合、その時点で絶対に一致しない。
        if (maxLevelUsedSoFar >= 4)
            return target == Difficulty.Master;

        int? upperBound = GetScoreUpperBoundExclusive(target);

        // 上限が存在しない（Expert / Master）場合は、まだ否定できない。
        if (upperBound is null)
            return true;

        int scoreSoFar = ComputeScore(maxLevelUsedSoFar, usageByNameSoFar);
        return scoreSoFar < upperBound.Value;
    }

    private static int ComputeScore(int maxLevelUsed, IReadOnlyDictionary<string, int> usageByName)
    {
        int score = maxLevelUsed switch
        {
            <= 1 => 0,
            2 => LevelBasePoints[2],
            3 => LevelBasePoints[3],
            _ => LevelBasePoints[4],
        };

        foreach (var (name, count) in usageByName)
        {
            if (count <= 0)
                continue;

            if (TechniquePoints.TryGetValue(name, out int points))
                score += count * points;
        }

        return score;
    }

    /// <summary>
    /// GetDifficultyLabelのスコア境界（未満）に対応する、各難易度のスコア上限（排他的）。
    /// Expert/Masterはスコアに上限がないため null を返す。
    /// GetDifficultyLabelと必ず整合させること（このメソッドを使って導出しているため自動的に整合する）。
    /// </summary>
    private static int? GetScoreUpperBoundExclusive(Difficulty difficulty) => difficulty switch
    {
        Difficulty.Easy => 30,
        Difficulty.Normal => 80,
        Difficulty.Hard => 400,
        _ => null,
    };

    private static Difficulty GetDifficultyLabel(int score, int maxLevel)
    {
        if (maxLevel >= 4)
            return Difficulty.Master;

        foreach (var candidate in new[] { Difficulty.Easy, Difficulty.Normal, Difficulty.Hard })
        {
            var bound = GetScoreUpperBoundExclusive(candidate);
            if (bound is not null && score < bound.Value)
                return candidate;
        }

        return Difficulty.Expert;
    }
}